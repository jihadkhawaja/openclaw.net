using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Validation;

namespace OpenClaw.Gateway;

internal sealed class AdminSettingsService
{
    private const string SettingsFileName = "admin-settings.json";
    private readonly object _gate = new();
    private readonly GatewayConfig _config;
    private readonly AdminSettingsSnapshot _baseSnapshot;
    private readonly string _settingsPath;
    private readonly ILogger<AdminSettingsService> _logger;

    public AdminSettingsService(
        GatewayConfig config,
        AdminSettingsSnapshot baseSnapshot,
        string settingsPath,
        ILogger<AdminSettingsService> logger)
    {
        _config = config;
        _baseSnapshot = baseSnapshot;
        _settingsPath = settingsPath;
        _logger = logger;
    }

    public static string GetSettingsPath(GatewayConfig config)
    {
        var storagePath = config.Memory.StoragePath;
        if (!Path.IsPathRooted(storagePath))
            storagePath = Path.GetFullPath(storagePath);

        return Path.Combine(storagePath, SettingsFileName);
    }

    public static AdminSettingsSnapshot CreateSnapshot(GatewayConfig config)
        => new()
        {
            UsageFooter = config.UsageFooter,
            MaxConcurrentSessions = config.MaxConcurrentSessions,
            SessionTimeoutMinutes = config.SessionTimeoutMinutes,
            SessionTokenBudget = config.SessionTokenBudget,
            SessionRateLimitPerMinute = config.SessionRateLimitPerMinute,
            AllowQueryStringToken = config.Security.AllowQueryStringToken,
            BrowserSessionIdleMinutes = config.Security.BrowserSessionIdleMinutes,
            BrowserRememberDays = config.Security.BrowserRememberDays,
            AutonomyMode = config.Tooling.AutonomyMode,
            RequireToolApproval = config.Tooling.RequireToolApproval,
            ToolApprovalTimeoutSeconds = config.Tooling.ToolApprovalTimeoutSeconds,
            ParallelToolExecution = config.Tooling.ParallelToolExecution,
            AllowShell = config.Tooling.AllowShell,
            ReadOnlyMode = config.Tooling.ReadOnlyMode,
            EnableBrowserTool = config.Tooling.EnableBrowserTool,
            AllowBrowserEvaluate = config.Tooling.AllowBrowserEvaluate,
            MaxHistoryTurns = config.Memory.MaxHistoryTurns,
            EnableCompaction = config.Memory.EnableCompaction,
            CompactionThreshold = config.Memory.CompactionThreshold,
            CompactionKeepRecent = config.Memory.CompactionKeepRecent,
            RetentionEnabled = config.Memory.Retention.Enabled,
            RetentionRunOnStartup = config.Memory.Retention.RunOnStartup,
            RetentionSweepIntervalMinutes = config.Memory.Retention.SweepIntervalMinutes,
            RetentionSessionTtlDays = config.Memory.Retention.SessionTtlDays,
            RetentionBranchTtlDays = config.Memory.Retention.BranchTtlDays,
            RetentionArchiveEnabled = config.Memory.Retention.ArchiveEnabled,
            RetentionArchiveRetentionDays = config.Memory.Retention.ArchiveRetentionDays,
            RetentionMaxItemsPerSweep = config.Memory.Retention.MaxItemsPerSweep,
            AllowlistSemantics = config.Channels.AllowlistSemantics,
            SmsEnabled = config.Channels.Sms.Twilio.Enabled,
            SmsValidateSignature = config.Channels.Sms.Twilio.ValidateSignature,
            SmsDmPolicy = config.Channels.Sms.DmPolicy,
            TelegramEnabled = config.Channels.Telegram.Enabled,
            TelegramValidateSignature = config.Channels.Telegram.ValidateSignature,
            TelegramDmPolicy = config.Channels.Telegram.DmPolicy,
            WhatsAppEnabled = config.Channels.WhatsApp.Enabled,
            WhatsAppValidateSignature = config.Channels.WhatsApp.ValidateSignature,
            WhatsAppDmPolicy = config.Channels.WhatsApp.DmPolicy
        };

    public static bool TryLoadPersistedSnapshot(string settingsPath, out AdminSettingsSnapshot? snapshot, out string? error)
    {
        snapshot = null;
        error = null;

        if (!File.Exists(settingsPath))
            return false;

        try
        {
            var json = File.ReadAllText(settingsPath);
            snapshot = JsonSerializer.Deserialize(json, CoreJsonContext.Default.AdminSettingsSnapshot);
            if (snapshot is null)
            {
                error = $"Admin settings file '{settingsPath}' is empty or invalid.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to load admin settings from '{settingsPath}': {ex.Message}";
            return false;
        }
    }

    public static void ApplySnapshot(GatewayConfig config, AdminSettingsSnapshot snapshot)
    {
        config.UsageFooter = snapshot.UsageFooter;
        config.MaxConcurrentSessions = snapshot.MaxConcurrentSessions;
        config.SessionTimeoutMinutes = snapshot.SessionTimeoutMinutes;
        config.SessionTokenBudget = snapshot.SessionTokenBudget;
        config.SessionRateLimitPerMinute = snapshot.SessionRateLimitPerMinute;
        config.Security.AllowQueryStringToken = snapshot.AllowQueryStringToken;
        config.Security.BrowserSessionIdleMinutes = snapshot.BrowserSessionIdleMinutes;
        config.Security.BrowserRememberDays = snapshot.BrowserRememberDays;
        config.Tooling.AutonomyMode = snapshot.AutonomyMode;
        config.Tooling.RequireToolApproval = snapshot.RequireToolApproval;
        config.Tooling.ToolApprovalTimeoutSeconds = snapshot.ToolApprovalTimeoutSeconds;
        config.Tooling.ParallelToolExecution = snapshot.ParallelToolExecution;
        config.Tooling.AllowShell = snapshot.AllowShell;
        config.Tooling.ReadOnlyMode = snapshot.ReadOnlyMode;
        config.Tooling.EnableBrowserTool = snapshot.EnableBrowserTool;
        config.Tooling.AllowBrowserEvaluate = snapshot.AllowBrowserEvaluate;
        config.Memory.MaxHistoryTurns = snapshot.MaxHistoryTurns;
        config.Memory.EnableCompaction = snapshot.EnableCompaction;
        config.Memory.CompactionThreshold = snapshot.CompactionThreshold;
        config.Memory.CompactionKeepRecent = snapshot.CompactionKeepRecent;
        config.Memory.Retention.Enabled = snapshot.RetentionEnabled;
        config.Memory.Retention.RunOnStartup = snapshot.RetentionRunOnStartup;
        config.Memory.Retention.SweepIntervalMinutes = snapshot.RetentionSweepIntervalMinutes;
        config.Memory.Retention.SessionTtlDays = snapshot.RetentionSessionTtlDays;
        config.Memory.Retention.BranchTtlDays = snapshot.RetentionBranchTtlDays;
        config.Memory.Retention.ArchiveEnabled = snapshot.RetentionArchiveEnabled;
        config.Memory.Retention.ArchiveRetentionDays = snapshot.RetentionArchiveRetentionDays;
        config.Memory.Retention.MaxItemsPerSweep = snapshot.RetentionMaxItemsPerSweep;
        config.Channels.AllowlistSemantics = snapshot.AllowlistSemantics;
        config.Channels.Sms.Twilio.Enabled = snapshot.SmsEnabled;
        config.Channels.Sms.Twilio.ValidateSignature = snapshot.SmsValidateSignature;
        config.Channels.Sms.DmPolicy = snapshot.SmsDmPolicy;
        config.Channels.Telegram.Enabled = snapshot.TelegramEnabled;
        config.Channels.Telegram.ValidateSignature = snapshot.TelegramValidateSignature;
        config.Channels.Telegram.DmPolicy = snapshot.TelegramDmPolicy;
        config.Channels.WhatsApp.Enabled = snapshot.WhatsAppEnabled;
        config.Channels.WhatsApp.ValidateSignature = snapshot.WhatsAppValidateSignature;
        config.Channels.WhatsApp.DmPolicy = snapshot.WhatsAppDmPolicy;
    }

    public AdminSettingsSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return CreateSnapshot(_config);
        }
    }

    public AdminSettingsPersistenceInfo GetPersistence()
    {
        lock (_gate)
        {
            return BuildPersistenceInfo();
        }
    }

    public AdminSettingsResult Update(AdminSettingsSnapshot snapshot)
    {
        lock (_gate)
        {
            var previous = CreateSnapshot(_config);
            var clone = CloneConfig(_config);
            ApplySnapshot(clone, snapshot);

            var errors = ConfigValidator.Validate(clone);
            if (errors.Count > 0)
            {
                return new AdminSettingsResult(
                    false,
                    previous,
                    BuildPersistenceInfo(),
                    false,
                    [],
                    errors);
            }

            ApplySnapshot(_config, snapshot);
            PersistSnapshot(snapshot);
            var changedRestartFields = GetRestartRequiredChanges(previous, snapshot);
            return new AdminSettingsResult(
                true,
                CreateSnapshot(_config),
                BuildPersistenceInfo(),
                changedRestartFields.Count > 0,
                changedRestartFields,
                []);
        }
    }

    public AdminSettingsResult Reset()
    {
        lock (_gate)
        {
            var previous = CreateSnapshot(_config);
            ApplySnapshot(_config, _baseSnapshot);

            try
            {
                if (File.Exists(_settingsPath))
                    File.Delete(_settingsPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete admin settings override file at {Path}", _settingsPath);
            }

            var changedRestartFields = GetRestartRequiredChanges(previous, _baseSnapshot);
            return new AdminSettingsResult(
                true,
                CreateSnapshot(_config),
                BuildPersistenceInfo(),
                changedRestartFields.Count > 0,
                changedRestartFields,
                []);
        }
    }

    public static IReadOnlyList<string> ImmediateFieldKeys { get; } =
    [
        "usageFooter",
        "security.allowQueryStringToken",
        "security.browserSessionIdleMinutes",
        "security.browserRememberDays",
        "channels.sms.dmPolicy",
        "channels.telegram.dmPolicy",
        "channels.whatsapp.dmPolicy"
    ];

    public static IReadOnlyList<string> RestartFieldKeys { get; } =
    [
        "general.maxConcurrentSessions",
        "general.sessionTimeoutMinutes",
        "general.sessionTokenBudget",
        "general.sessionRateLimitPerMinute",
        "tooling.autonomyMode",
        "tooling.requireToolApproval",
        "tooling.toolApprovalTimeoutSeconds",
        "tooling.parallelToolExecution",
        "tooling.allowShell",
        "tooling.readOnlyMode",
        "tooling.enableBrowserTool",
        "tooling.allowBrowserEvaluate",
        "memory.maxHistoryTurns",
        "memory.enableCompaction",
        "memory.compactionThreshold",
        "memory.compactionKeepRecent",
        "retention.enabled",
        "retention.runOnStartup",
        "retention.sweepIntervalMinutes",
        "retention.sessionTtlDays",
        "retention.branchTtlDays",
        "retention.archiveEnabled",
        "retention.archiveRetentionDays",
        "retention.maxItemsPerSweep",
        "channels.allowlistSemantics",
        "channels.sms.enabled",
        "channels.sms.validateSignature",
        "channels.telegram.enabled",
        "channels.telegram.validateSignature",
        "channels.whatsapp.enabled",
        "channels.whatsapp.validateSignature"
    ];

    private void PersistSnapshot(AdminSettingsSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var tempPath = _settingsPath + ".tmp";
        var json = JsonSerializer.Serialize(snapshot, CoreJsonContext.Default.AdminSettingsSnapshot);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _settingsPath, overwrite: true);
    }

    private AdminSettingsPersistenceInfo BuildPersistenceInfo()
    {
        var fileInfo = new FileInfo(_settingsPath);
        return new AdminSettingsPersistenceInfo
        {
            Path = _settingsPath,
            Exists = fileInfo.Exists,
            LastModifiedAtUtc = fileInfo.Exists ? fileInfo.LastWriteTimeUtc : null
        };
    }

    private static GatewayConfig CloneConfig(GatewayConfig config)
    {
        var json = JsonSerializer.Serialize(config, CoreJsonContext.Default.GatewayConfig);
        var clone = JsonSerializer.Deserialize(json, CoreJsonContext.Default.GatewayConfig);
        if (clone is null)
            throw new InvalidOperationException("Failed to clone gateway configuration.");

        return clone;
    }

    private static List<string> GetRestartRequiredChanges(AdminSettingsSnapshot before, AdminSettingsSnapshot after)
    {
        var changed = new List<string>();
        AddIfChanged(changed, "general.maxConcurrentSessions", before.MaxConcurrentSessions, after.MaxConcurrentSessions);
        AddIfChanged(changed, "general.sessionTimeoutMinutes", before.SessionTimeoutMinutes, after.SessionTimeoutMinutes);
        AddIfChanged(changed, "general.sessionTokenBudget", before.SessionTokenBudget, after.SessionTokenBudget);
        AddIfChanged(changed, "general.sessionRateLimitPerMinute", before.SessionRateLimitPerMinute, after.SessionRateLimitPerMinute);
        AddIfChanged(changed, "tooling.autonomyMode", before.AutonomyMode, after.AutonomyMode);
        AddIfChanged(changed, "tooling.requireToolApproval", before.RequireToolApproval, after.RequireToolApproval);
        AddIfChanged(changed, "tooling.toolApprovalTimeoutSeconds", before.ToolApprovalTimeoutSeconds, after.ToolApprovalTimeoutSeconds);
        AddIfChanged(changed, "tooling.parallelToolExecution", before.ParallelToolExecution, after.ParallelToolExecution);
        AddIfChanged(changed, "tooling.allowShell", before.AllowShell, after.AllowShell);
        AddIfChanged(changed, "tooling.readOnlyMode", before.ReadOnlyMode, after.ReadOnlyMode);
        AddIfChanged(changed, "tooling.enableBrowserTool", before.EnableBrowserTool, after.EnableBrowserTool);
        AddIfChanged(changed, "tooling.allowBrowserEvaluate", before.AllowBrowserEvaluate, after.AllowBrowserEvaluate);
        AddIfChanged(changed, "memory.maxHistoryTurns", before.MaxHistoryTurns, after.MaxHistoryTurns);
        AddIfChanged(changed, "memory.enableCompaction", before.EnableCompaction, after.EnableCompaction);
        AddIfChanged(changed, "memory.compactionThreshold", before.CompactionThreshold, after.CompactionThreshold);
        AddIfChanged(changed, "memory.compactionKeepRecent", before.CompactionKeepRecent, after.CompactionKeepRecent);
        AddIfChanged(changed, "retention.enabled", before.RetentionEnabled, after.RetentionEnabled);
        AddIfChanged(changed, "retention.runOnStartup", before.RetentionRunOnStartup, after.RetentionRunOnStartup);
        AddIfChanged(changed, "retention.sweepIntervalMinutes", before.RetentionSweepIntervalMinutes, after.RetentionSweepIntervalMinutes);
        AddIfChanged(changed, "retention.sessionTtlDays", before.RetentionSessionTtlDays, after.RetentionSessionTtlDays);
        AddIfChanged(changed, "retention.branchTtlDays", before.RetentionBranchTtlDays, after.RetentionBranchTtlDays);
        AddIfChanged(changed, "retention.archiveEnabled", before.RetentionArchiveEnabled, after.RetentionArchiveEnabled);
        AddIfChanged(changed, "retention.archiveRetentionDays", before.RetentionArchiveRetentionDays, after.RetentionArchiveRetentionDays);
        AddIfChanged(changed, "retention.maxItemsPerSweep", before.RetentionMaxItemsPerSweep, after.RetentionMaxItemsPerSweep);
        AddIfChanged(changed, "channels.allowlistSemantics", before.AllowlistSemantics, after.AllowlistSemantics);
        AddIfChanged(changed, "channels.sms.enabled", before.SmsEnabled, after.SmsEnabled);
        AddIfChanged(changed, "channels.sms.validateSignature", before.SmsValidateSignature, after.SmsValidateSignature);
        AddIfChanged(changed, "channels.telegram.enabled", before.TelegramEnabled, after.TelegramEnabled);
        AddIfChanged(changed, "channels.telegram.validateSignature", before.TelegramValidateSignature, after.TelegramValidateSignature);
        AddIfChanged(changed, "channels.whatsapp.enabled", before.WhatsAppEnabled, after.WhatsAppEnabled);
        AddIfChanged(changed, "channels.whatsapp.validateSignature", before.WhatsAppValidateSignature, after.WhatsAppValidateSignature);
        return changed;
    }

    private static void AddIfChanged<T>(ICollection<string> changes, string fieldKey, T before, T after)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
            changes.Add(fieldKey);
    }
}

internal sealed record AdminSettingsResult(
    bool Success,
    AdminSettingsSnapshot Snapshot,
    AdminSettingsPersistenceInfo Persistence,
    bool RestartRequired,
    IReadOnlyList<string> RestartRequiredFields,
    IReadOnlyList<string> Errors);
