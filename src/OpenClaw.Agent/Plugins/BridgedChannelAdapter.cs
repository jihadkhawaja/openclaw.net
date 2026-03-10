using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Adapts a plugin-registered channel to <see cref="IChannelAdapter"/>.
/// Inbound messages arrive via bridge notifications; outbound messages are sent via bridge requests.
/// </summary>
public sealed class BridgedChannelAdapter : IChannelAdapter
{
    private readonly PluginBridgeProcess _bridge;
    private readonly ILogger _logger;

    public string ChannelId { get; }

    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;

    public BridgedChannelAdapter(PluginBridgeProcess bridge, string channelId, ILogger logger)
    {
        _bridge = bridge;
        _logger = logger;
        ChannelId = channelId;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _bridge.SendRequestAsync(
            "channel_start",
            new BridgeChannelControlRequest { ChannelId = ChannelId },
            CoreJsonContext.Default.BridgeChannelControlRequest,
            ct);
    }

    public async ValueTask SendAsync(OutboundMessage message, CancellationToken ct)
    {
        await _bridge.SendRequestAsync(
            "channel_send",
            new BridgeChannelSendRequest
            {
                ChannelId = ChannelId,
                RecipientId = message.RecipientId,
                Text = message.Text,
            },
            CoreJsonContext.Default.BridgeChannelSendRequest,
            ct);
    }

    /// <summary>
    /// Called by the notification dispatcher when a <c>channel_message</c> notification arrives.
    /// </summary>
    internal async ValueTask HandleInboundAsync(JsonElement parameters, CancellationToken ct)
    {
        var senderId = parameters.TryGetProperty("senderId", out var sid) ? sid.GetString() ?? "unknown" : "unknown";
        var text = parameters.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "";
        var sessionId = parameters.TryGetProperty("sessionId", out var sess) ? sess.GetString() : null;

        var msg = new InboundMessage
        {
            ChannelId = ChannelId,
            SenderId = senderId,
            Text = text,
            SessionId = sessionId
        };

        if (OnMessageReceived is not null)
        {
            try
            {
                await OnMessageReceived.Invoke(msg, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BridgedChannelAdapter '{ChannelId}' OnMessageReceived handler threw", ChannelId);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        // channel_stop is sent during plugin shutdown via PluginBridgeProcess.DisposeAsync
        return ValueTask.CompletedTask;
    }
}
