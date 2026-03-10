using System.Text;
using Microsoft.AspNetCore.Http.Features;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway.Endpoints;

internal static class EndpointHelpers
{
    public static bool IsAuthorizedRequest(HttpContext ctx, GatewayConfig config, bool isNonLoopbackBind)
    {
        if (!isNonLoopbackBind)
            return true;

        var token = GatewaySecurity.GetToken(ctx, config.Security.AllowQueryStringToken);
        return GatewaySecurity.IsTokenValid(token, config.AuthToken!);
    }

    public static bool TrySetMaxRequestBodySize(HttpContext ctx, long maxBytes)
    {
        var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = maxBytes;
            return true;
        }

        return false;
    }

    public static string GetHttpRateLimitKey(HttpContext ctx, GatewayConfig config)
    {
        var token = GatewaySecurity.GetToken(ctx, config.Security.AllowQueryStringToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            var bytes = Encoding.UTF8.GetBytes(token);
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            return "token:" + Convert.ToHexString(hash.AsSpan(0, 8));
        }

        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        return "ip:" + (string.IsNullOrWhiteSpace(ip) ? "unknown" : ip);
    }

    public static async Task<(bool Success, string Text)> TryReadBodyTextAsync(HttpContext ctx, long maxBytes, CancellationToken ct)
    {
        var contentLength = ctx.Request.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBytes)
            return (false, "");

        TrySetMaxRequestBodySize(ctx, maxBytes);

        var buffer = new byte[8 * 1024];
        await using var ms = new MemoryStream();
        while (true)
        {
            var read = await ctx.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0)
                break;

            if (ms.Length + read > maxBytes)
                return (false, "");

            ms.Write(buffer, 0, read);
        }

        return (true, Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
    }

    public static ChannelAllowlistFile GetConfigAllowlist(GatewayConfig config, string channelId)
    {
        return channelId switch
        {
            "telegram" => new ChannelAllowlistFile { AllowedFrom = config.Channels.Telegram.AllowedFromUserIds },
            "whatsapp" => new ChannelAllowlistFile { AllowedFrom = config.Channels.WhatsApp.AllowedFromIds },
            "sms" => new ChannelAllowlistFile
            {
                AllowedFrom = config.Channels.Sms.Twilio.AllowedFromNumbers,
                AllowedTo = config.Channels.Sms.Twilio.AllowedToNumbers
            },
            _ => new ChannelAllowlistFile()
        };
    }

    public static string ResolveWorkspaceRoot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        if (value.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(value[4..]) ?? "";

        return value;
    }

    public static string ToBoolWord(bool value) => value ? "yes" : "no";
}
