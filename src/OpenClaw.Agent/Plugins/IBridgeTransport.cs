using OpenClaw.Core.Plugins;
using System.Diagnostics;
using System.Text.Json;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Abstraction for the transport layer between the gateway and a plugin bridge process.
/// </summary>
public interface IBridgeTransport : IAsyncDisposable
{
    Task PrepareAsync(CancellationToken ct);
    Task StartAsync(Process process, CancellationToken ct);
    Task SendRequestAsync(string method, JsonElement? parameters, CancellationToken ct);
    Task<BridgeResponse> SendAndWaitAsync(string method, JsonElement? parameters, CancellationToken ct);
    void SetNotificationHandler(Action<BridgeNotification> handler);
}
