using System.Collections.Concurrent;
using System.Collections.Frozen;
using Microsoft.Extensions.AI;
using OpenClaw.Agent;
using OpenClaw.Agent.Integrations;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Contacts;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;
using OpenClaw.Core.Skills;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Extensions;
using OpenClaw.Gateway.Profiles;

namespace OpenClaw.Gateway.Composition;

internal static class RuntimeInitializationExtensions
{
    public static async Task<GatewayAppRuntime> InitializeOpenClawRuntimeAsync(
        this WebApplication app,
        GatewayStartupContext startup)
    {
        var config = startup.Config;
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

        var allowlistSemantics = app.Services.GetRequiredService<AllowlistSemantics>();
        var allowlists = app.Services.GetRequiredService<AllowlistManager>();
        var recentSenders = app.Services.GetRequiredService<RecentSendersStore>();
        var sessionManager = app.Services.GetRequiredService<SessionManager>();
        var retentionCoordinator = app.Services.GetRequiredService<IMemoryRetentionCoordinator>();
        var pairingManager = app.Services.GetRequiredService<PairingManager>();
        var commandProcessor = app.Services.GetRequiredService<ChatCommandProcessor>();
        var toolApprovalService = app.Services.GetRequiredService<ToolApprovalService>();
        var runtimeMetrics = app.Services.GetRequiredService<RuntimeMetrics>();
        var memoryStore = app.Services.GetRequiredService<IMemoryStore>();
        var pipeline = app.Services.GetRequiredService<MessagePipeline>();
        var wsChannel = app.Services.GetRequiredService<WebSocketChannel>();
        var nativeRegistry = app.Services.GetRequiredService<NativePluginRegistry>();

        var (smsChannel, smsWebhookHandler) = CreateTwilioResources(
            config,
            allowlists,
            recentSenders,
            allowlistSemantics);

        var channelAdapters = new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal)
        {
            ["websocket"] = wsChannel
        };

        if (smsChannel is not null)
            channelAdapters["sms"] = smsChannel;

        if (config.Channels.Telegram.Enabled)
            channelAdapters["telegram"] = app.Services.GetRequiredService<TelegramChannel>();

        if (config.Channels.WhatsApp.Enabled)
        {
            if (config.Channels.WhatsApp.Type == "bridge")
                channelAdapters["whatsapp"] = app.Services.GetRequiredService<WhatsAppBridgeChannel>();
            else
                channelAdapters["whatsapp"] = app.Services.GetRequiredService<WhatsAppChannel>();
        }

        if (config.Plugins.Native.Email.Enabled)
        {
            channelAdapters["email"] = new EmailChannel(
                config.Plugins.Native.Email,
                loggerFactory.CreateLogger<EmailChannel>());
        }

        channelAdapters["cron"] = new CronChannel(
            config.Memory.StoragePath,
            loggerFactory.CreateLogger<CronChannel>());

        var builtInTools = CreateBuiltInTools(config, memoryStore, sessionManager, pipeline, startup.WorkspacePath);

        PluginHost? pluginHost = null;
        NativeDynamicPluginHost? nativeDynamicPluginHost = null;
        IReadOnlyList<ITool> bridgeTools = [];
        IReadOnlyList<ITool> nativeDynamicTools = [];

        if (config.Plugins.Enabled)
        {
            var bridgeScript = Path.Combine(AppContext.BaseDirectory, "Plugins", "plugin-bridge.mjs");
            pluginHost = new PluginHost(
                config.Plugins,
                bridgeScript,
                loggerFactory.CreateLogger<PluginHost>(),
                startup.RuntimeState);
            bridgeTools = await pluginHost.LoadAsync(startup.WorkspacePath, app.Lifetime.ApplicationStopping);

            foreach (var adapter in pluginHost.ChannelAdapters)
            {
                if (!channelAdapters.ContainsKey(adapter.ChannelId))
                    channelAdapters[adapter.ChannelId] = adapter;
            }

            pluginHost.RegisterCommandsWith(commandProcessor);

            foreach (var (providerId, _, bridge) in pluginHost.ProviderRegistrations)
            {
                var bridgedProvider = new BridgedLlmProvider(
                    bridge,
                    providerId,
                    loggerFactory.CreateLogger<BridgedLlmProvider>());
                LlmClientFactory.RegisterProvider(providerId, bridgedProvider);
            }
        }

        if (config.Plugins.DynamicNative.Enabled)
        {
            nativeDynamicPluginHost = new NativeDynamicPluginHost(
                config.Plugins.DynamicNative,
                startup.RuntimeState,
                loggerFactory.CreateLogger<NativeDynamicPluginHost>());
            nativeDynamicTools = await nativeDynamicPluginHost.LoadAsync(startup.WorkspacePath, app.Lifetime.ApplicationStopping);

            foreach (var adapter in nativeDynamicPluginHost.ChannelAdapters)
            {
                if (!channelAdapters.ContainsKey(adapter.ChannelId))
                    channelAdapters[adapter.ChannelId] = adapter;
            }

            nativeDynamicPluginHost.RegisterCommandsWith(commandProcessor);

            foreach (var (providerId, _, client) in nativeDynamicPluginHost.ProviderRegistrations)
                LlmClientFactory.RegisterProvider(providerId, client);
        }

        IChatClient chatClient = LlmClientFactory.CreateChatClient(config.Llm);

        var resolveLogger = loggerFactory.CreateLogger("PluginResolver");
        IReadOnlyList<ITool> tools = NativePluginRegistry.ResolvePreference(
            builtInTools,
            nativeRegistry.Tools,
            [.. bridgeTools, .. nativeDynamicTools],
            config.Plugins,
            resolveLogger);

        var combinedPluginSkillRoots = new List<string>();
        if (pluginHost is not null)
            combinedPluginSkillRoots.AddRange(pluginHost.SkillRoots);
        if (nativeDynamicPluginHost is not null)
            combinedPluginSkillRoots.AddRange(nativeDynamicPluginHost.SkillRoots);

        var skillLogger = loggerFactory.CreateLogger("SkillLoader");
        var skills = SkillLoader.LoadAll(config.Skills, startup.WorkspacePath, skillLogger, combinedPluginSkillRoots);
        if (skills.Count > 0)
            skillLogger.LogInformation("{Summary}", SkillPromptBuilder.BuildSummary(skills));

        var hooks = CreateHooks(config, loggerFactory, pluginHost, nativeDynamicPluginHost);
        var (effectiveRequireToolApproval, effectiveApprovalRequiredTools) = ResolveApprovalMode(config);

        var agentLogger = loggerFactory.CreateLogger("AgentRuntime");
        var agentRuntime = CreateAgentRuntime(
            config,
            chatClient,
            tools,
            memoryStore,
            runtimeMetrics,
            skills,
            agentLogger,
            hooks,
            startup.WorkspacePath,
            effectiveRequireToolApproval,
            effectiveApprovalRequiredTools);

        var middlewarePipeline = CreateMiddlewarePipeline(config, loggerFactory);
        var skillWatcher = new SkillWatcherService(
            config.Skills,
            startup.WorkspacePath,
            combinedPluginSkillRoots,
            agentRuntime,
            app.Services.GetRequiredService<ILogger<SkillWatcherService>>());
        skillWatcher.Start(app.Lifetime.ApplicationStopping);

        var cronTask = StartCronIfEnabled(config, loggerFactory, pipeline, app.Lifetime.ApplicationStopping);
        StartNativeEventBridges(config, loggerFactory, pipeline, app.Lifetime.ApplicationStopping);

        var profile = app.Services.GetRequiredService<IRuntimeProfile>();
        var runtime = new GatewayAppRuntime
        {
            AgentRuntime = agentRuntime,
            Pipeline = pipeline,
            MiddlewarePipeline = middlewarePipeline,
            WebSocketChannel = wsChannel,
            ChannelAdapters = channelAdapters,
            SessionManager = sessionManager,
            RetentionCoordinator = retentionCoordinator,
            PairingManager = pairingManager,
            Allowlists = allowlists,
            AllowlistSemantics = allowlistSemantics,
            RecentSenders = recentSenders,
            CommandProcessor = commandProcessor,
            ToolApprovalService = toolApprovalService,
            RuntimeMetrics = runtimeMetrics,
            SkillWatcher = skillWatcher,
            PluginReports = GetCombinedPluginReports(pluginHost, nativeDynamicPluginHost),
            Skills = skills,
            EffectiveRequireToolApproval = effectiveRequireToolApproval,
            EffectiveApprovalRequiredTools = effectiveApprovalRequiredTools,
            NativeRegistry = nativeRegistry,
            SessionLocks = new ConcurrentDictionary<string, SemaphoreSlim>(),
            LockLastUsed = new ConcurrentDictionary<string, DateTimeOffset>(),
            AllowedOriginsSet = config.Security.AllowedOrigins.Length > 0
                ? config.Security.AllowedOrigins.ToFrozenSet(StringComparer.Ordinal)
                : null,
            CronTask = cronTask,
            TwilioSmsWebhookHandler = smsWebhookHandler,
            PluginHost = pluginHost,
            NativeDynamicPluginHost = nativeDynamicPluginHost
        };

        await profile.OnRuntimeInitializedAsync(app, startup, runtime);
        return runtime;
    }

    private static IReadOnlyList<ITool> CreateBuiltInTools(
        GatewayConfig config,
        IMemoryStore memoryStore,
        SessionManager sessionManager,
        MessagePipeline pipeline,
        string? workspacePath)
    {
        var projectId = config.Memory.ProjectId
            ?? Environment.GetEnvironmentVariable("OPENCLAW_PROJECT")
            ?? "default";

        var tools = new List<ITool>
        {
            new ShellTool(config.Tooling),
            new FileReadTool(config.Tooling),
            new FileWriteTool(config.Tooling),
            new MemoryNoteTool(memoryStore),
            new MemorySearchTool((IMemoryNoteSearch)memoryStore),
            new ProjectMemoryTool(memoryStore, projectId),
            new SessionsTool(sessionManager, pipeline.InboundWriter)
        };

        if (config.Tooling.EnableBrowserTool)
            tools.Add(new BrowserTool(config.Tooling));

        return tools;
    }

    private static IReadOnlyList<IToolHook> CreateHooks(
        GatewayConfig config,
        ILoggerFactory loggerFactory,
        PluginHost? pluginHost,
        NativeDynamicPluginHost? nativeDynamicPluginHost)
    {
        var hooks = new List<IToolHook>
        {
            new AuditLogHook(loggerFactory.CreateLogger("AuditLog")),
            new AutonomyHook(config.Tooling, loggerFactory.CreateLogger("AutonomyHook"))
        };

        if (pluginHost is not null)
            hooks.AddRange(pluginHost.ToolHooks);
        if (nativeDynamicPluginHost is not null)
            hooks.AddRange(nativeDynamicPluginHost.ToolHooks);

        return hooks;
    }

    private static (bool RequireApproval, IReadOnlyList<string> RequiredTools) ResolveApprovalMode(GatewayConfig config)
    {
        var autonomyMode = (config.Tooling.AutonomyMode ?? "full").Trim().ToLowerInvariant();
        var effectiveRequireToolApproval = config.Tooling.RequireToolApproval || autonomyMode == "supervised";
        var effectiveApprovalRequiredTools = config.Tooling.ApprovalRequiredTools;

        if (autonomyMode == "supervised")
        {
            var defaults = new[]
            {
                "shell", "write_file", "code_exec", "git", "home_assistant_write", "mqtt_publish",
                "database", "email", "inbox_zero", "calendar", "delegate_agent"
            };

            effectiveApprovalRequiredTools = effectiveApprovalRequiredTools
                .Concat(defaults)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return (effectiveRequireToolApproval, effectiveApprovalRequiredTools);
    }

    private static IAgentRuntime CreateAgentRuntime(
        GatewayConfig config,
        IChatClient chatClient,
        IReadOnlyList<ITool> tools,
        IMemoryStore memoryStore,
        RuntimeMetrics runtimeMetrics,
        IReadOnlyList<SkillDefinition> skills,
        ILogger logger,
        IReadOnlyList<IToolHook> hooks,
        string? workspacePath,
        bool requireToolApproval,
        IReadOnlyList<string> approvalRequiredTools)
    {
        IAgentRuntime agentRuntime = new AgentRuntime(
            chatClient,
            tools,
            memoryStore,
            config.Llm,
            config.Memory.MaxHistoryTurns,
            skills,
            skillsConfig: config.Skills,
            skillWorkspacePath: workspacePath,
            logger: logger,
            toolTimeoutSeconds: config.Tooling.ToolTimeoutSeconds,
            metrics: runtimeMetrics,
            parallelToolExecution: config.Tooling.ParallelToolExecution,
            enableCompaction: config.Memory.EnableCompaction,
            compactionThreshold: config.Memory.CompactionThreshold,
            compactionKeepRecent: config.Memory.CompactionKeepRecent,
            requireToolApproval: requireToolApproval,
            approvalRequiredTools: [.. approvalRequiredTools],
            hooks: hooks,
            sessionTokenBudget: config.SessionTokenBudget,
            recall: config.Memory.Recall);

        if (!config.Delegation.Enabled || config.Delegation.Profiles.Count == 0)
            return agentRuntime;

        var delegateTool = new DelegateTool(
            chatClient,
            tools,
            memoryStore,
            config.Llm,
            config.Delegation,
            currentDepth: 0,
            metrics: runtimeMetrics,
            logger: logger,
            recall: config.Memory.Recall);

        tools = [.. tools, delegateTool];
        return new AgentRuntime(
            chatClient,
            tools,
            memoryStore,
            config.Llm,
            config.Memory.MaxHistoryTurns,
            skills,
            skillsConfig: config.Skills,
            skillWorkspacePath: workspacePath,
            logger: logger,
            toolTimeoutSeconds: config.Tooling.ToolTimeoutSeconds,
            metrics: runtimeMetrics,
            parallelToolExecution: config.Tooling.ParallelToolExecution,
            enableCompaction: config.Memory.EnableCompaction,
            compactionThreshold: config.Memory.CompactionThreshold,
            compactionKeepRecent: config.Memory.CompactionKeepRecent,
            requireToolApproval: requireToolApproval,
            approvalRequiredTools: [.. approvalRequiredTools],
            hooks: hooks,
            sessionTokenBudget: config.SessionTokenBudget,
            recall: config.Memory.Recall);
    }

    private static MiddlewarePipeline CreateMiddlewarePipeline(GatewayConfig config, ILoggerFactory loggerFactory)
    {
        var middlewareList = new List<IMessageMiddleware>();
        if (config.SessionRateLimitPerMinute > 0)
            middlewareList.Add(new RateLimitMiddleware(config.SessionRateLimitPerMinute, loggerFactory.CreateLogger("RateLimit")));
        if (config.SessionTokenBudget > 0)
            middlewareList.Add(new TokenBudgetMiddleware(config.SessionTokenBudget, loggerFactory.CreateLogger("TokenBudget")));

        return new MiddlewarePipeline(middlewareList);
    }

    private static CronScheduler? StartCronIfEnabled(
        GatewayConfig config,
        ILoggerFactory loggerFactory,
        MessagePipeline pipeline,
        CancellationToken stoppingToken)
    {
        if (!config.Cron.Enabled)
            return null;

        var logger = loggerFactory.CreateLogger<CronScheduler>();
        var cronTask = new CronScheduler(config, logger, pipeline.InboundWriter);
        _ = cronTask.StartAsync(stoppingToken).ContinueWith(
            t => logger.LogError(t.Exception!.InnerException, "CronScheduler failed to start"),
            TaskContinuationOptions.OnlyOnFaulted);
        return cronTask;
    }

    private static void StartNativeEventBridges(
        GatewayConfig config,
        ILoggerFactory loggerFactory,
        MessagePipeline pipeline,
        CancellationToken stoppingToken)
    {
        if (config.Plugins.Native.HomeAssistant.Enabled && config.Plugins.Native.HomeAssistant.Events.Enabled)
        {
            var haLogger = loggerFactory.CreateLogger<HomeAssistantEventBridge>();
            var haBridge = new HomeAssistantEventBridge(
                config.Plugins.Native.HomeAssistant,
                haLogger,
                pipeline.InboundWriter);
            _ = haBridge.StartAsync(stoppingToken).ContinueWith(
                t => haLogger.LogError(t.Exception!.InnerException, "HomeAssistant event bridge failed to start"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        if (config.Plugins.Native.Mqtt.Enabled && config.Plugins.Native.Mqtt.Events.Enabled)
        {
            var mqttLogger = loggerFactory.CreateLogger<MqttEventBridge>();
            var mqttBridge = new MqttEventBridge(
                config.Plugins.Native.Mqtt,
                mqttLogger,
                pipeline.InboundWriter);
            _ = mqttBridge.StartAsync(stoppingToken).ContinueWith(
                t => mqttLogger.LogError(t.Exception!.InnerException, "MQTT event bridge failed to start"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private static (TwilioSmsChannel? Channel, TwilioSmsWebhookHandler? Handler) CreateTwilioResources(
        GatewayConfig config,
        AllowlistManager allowlists,
        RecentSendersStore recentSenders,
        AllowlistSemantics allowlistSemantics)
    {
        if (!config.Channels.Sms.Twilio.Enabled)
            return (null, null);

        if (config.Channels.Sms.Twilio.ValidateSignature &&
            string.IsNullOrWhiteSpace(config.Channels.Sms.Twilio.WebhookPublicBaseUrl))
        {
            throw new InvalidOperationException("OpenClaw:Channels:Sms:Twilio:WebhookPublicBaseUrl must be set when ValidateSignature is true.");
        }

        var twilioAuthToken = OpenClaw.Core.Security.SecretResolver.Resolve(config.Channels.Sms.Twilio.AuthTokenRef)
            ?? throw new InvalidOperationException("Twilio AuthTokenRef is not configured or could not be resolved.");

        var smsContacts = new FileContactStore(config.Memory.StoragePath);
        var httpClient = OpenClaw.Core.Http.HttpClientFactory.Create();
        var smsChannel = new TwilioSmsChannel(config.Channels.Sms.Twilio, twilioAuthToken, smsContacts, httpClient);
        var handler = new TwilioSmsWebhookHandler(
            config.Channels.Sms.Twilio,
            twilioAuthToken,
            smsContacts,
            allowlists,
            recentSenders,
            allowlistSemantics);

        return (smsChannel, handler);
    }

    private static IReadOnlyList<PluginLoadReport> GetCombinedPluginReports(
        PluginHost? pluginHost,
        NativeDynamicPluginHost? nativeDynamicPluginHost)
    {
        var reports = new List<PluginLoadReport>();
        if (pluginHost is not null)
            reports.AddRange(pluginHost.Reports);
        if (nativeDynamicPluginHost is not null)
            reports.AddRange(nativeDynamicPluginHost.Reports);
        return reports;
    }
}
