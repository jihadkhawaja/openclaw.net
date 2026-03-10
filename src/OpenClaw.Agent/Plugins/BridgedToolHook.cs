using System.Text.Json;
using OpenClaw.Core.Abstractions;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Forwards tool hook events to a plugin bridge process.
/// Applies a 5-second timeout on BeforeExecuteAsync to prevent misbehaving plugins from blocking the agent loop.
/// </summary>
public sealed class BridgedToolHook : IToolHook
{
    private readonly PluginBridgeProcess _bridge;
    private readonly string _pluginId;
    private readonly string[] _eventSubscriptions;
    private readonly ILogger _logger;
    private static readonly TimeSpan BeforeTimeout = TimeSpan.FromSeconds(5);

    public string Name { get; }

    public BridgedToolHook(PluginBridgeProcess bridge, string pluginId, string[] eventSubscriptions, ILogger logger)
    {
        _bridge = bridge;
        _pluginId = pluginId;
        _eventSubscriptions = eventSubscriptions;
        _logger = logger;
        Name = $"plugin:{pluginId}";
    }

    public async ValueTask<bool> BeforeExecuteAsync(string toolName, string arguments, CancellationToken ct)
    {
        var eventName = $"tool:before";
        if (!_eventSubscriptions.Contains(eventName) && !_eventSubscriptions.Contains("tool:*"))
            return true;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(BeforeTimeout);

            var response = await _bridge.SendAndWaitAsync(
                "hook_before",
                new BridgeHookBeforeRequest
                {
                    EventName = eventName,
                    ToolName = toolName,
                    Arguments = arguments,
                },
                CoreJsonContext.Default.BridgeHookBeforeRequest,
                cts.Token);

            if (response.Result is { } result && result.TryGetProperty("allow", out var allowEl))
            {
                return allowEl.GetBoolean();
            }

            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Plugin '{PluginId}' hook timed out on tool:before for '{ToolName}', defaulting to allow", _pluginId, toolName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plugin '{PluginId}' hook failed on tool:before for '{ToolName}', defaulting to allow", _pluginId, toolName);
            return true;
        }
    }

    public async ValueTask AfterExecuteAsync(string toolName, string arguments, string result, TimeSpan duration, bool failed, CancellationToken ct)
    {
        var eventName = $"tool:after";
        if (!_eventSubscriptions.Contains(eventName) && !_eventSubscriptions.Contains("tool:*"))
            return;

        try
        {
            await _bridge.SendRequestAsync(
                "hook_after",
                new BridgeHookAfterRequest
                {
                    EventName = eventName,
                    ToolName = toolName,
                    Arguments = arguments,
                    Result = result,
                    DurationMs = duration.TotalMilliseconds,
                    Failed = failed,
                },
                CoreJsonContext.Default.BridgeHookAfterRequest,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plugin '{PluginId}' hook failed on tool:after for '{ToolName}'", _pluginId, toolName);
        }
    }
}
