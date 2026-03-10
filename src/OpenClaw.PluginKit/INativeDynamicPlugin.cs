using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.PluginKit;

public interface INativeDynamicPlugin
{
    void Register(INativeDynamicPluginContext context);
}

public interface INativeDynamicPluginContext
{
    string PluginId { get; }
    JsonElement? Config { get; }
    ILogger Logger { get; }

    void RegisterTool(ITool tool);
    void RegisterChannel(IChannelAdapter adapter);
    void RegisterCommand(string name, string description, Func<string, CancellationToken, Task<string>> handler);
    void RegisterProvider(string providerId, string[] models, IChatClient client);
    void RegisterHook(IToolHook hook);
    void RegisterService(INativeDynamicPluginService service);
}

public interface INativeDynamicPluginService
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
