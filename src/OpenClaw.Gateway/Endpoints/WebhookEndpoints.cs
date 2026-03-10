using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class WebhookEndpoints
{
    public static void MapOpenClawWebhookEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        if (runtime.TwilioSmsWebhookHandler is not null)
        {
            app.MapPost(startup.Config.Channels.Sms.Twilio.WebhookPath, async (HttpContext ctx) =>
            {
                var maxRequestSize = Math.Max(4 * 1024, startup.Config.Channels.Sms.Twilio.MaxRequestBytes);

                if (!ctx.Request.HasFormContentType)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsync("Expected form content.", ctx.RequestAborted);
                    return;
                }

                var (bodyOk, bodyText) = await EndpointHelpers.TryReadBodyTextAsync(ctx, maxRequestSize, ctx.RequestAborted);
                if (!bodyOk)
                {
                    ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await ctx.Response.WriteAsync("Request too large.", ctx.RequestAborted);
                    return;
                }

                var parsed = QueryHelpers.ParseQuery(bodyText);
                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kvp in parsed)
                    dict[kvp.Key] = kvp.Value.ToString();

                var sig = ctx.Request.Headers["X-Twilio-Signature"].ToString();

                var response = await runtime.TwilioSmsWebhookHandler.HandleAsync(
                    dict,
                    sig,
                    (msg, ct) => runtime.Pipeline.InboundWriter.WriteAsync(msg, ct),
                    ctx.RequestAborted);

                ctx.Response.StatusCode = response.StatusCode;
                if (response.Body is not null)
                {
                    ctx.Response.ContentType = response.ContentType;
                    await ctx.Response.WriteAsync(response.Body, ctx.RequestAborted);
                }
            });
        }

        if (startup.Config.Channels.Telegram.Enabled)
        {
            byte[]? telegramSecretBytes = null;
            if (startup.Config.Channels.Telegram.ValidateSignature)
            {
                var telegramSecret = startup.Config.Channels.Telegram.WebhookSecretToken
                    ?? SecretResolver.Resolve(startup.Config.Channels.Telegram.WebhookSecretTokenRef);
                if (string.IsNullOrWhiteSpace(telegramSecret))
                {
                    throw new InvalidOperationException(
                        "Telegram ValidateSignature is true but WebhookSecretToken/WebhookSecretTokenRef could not be resolved. " +
                        "Set TELEGRAM_WEBHOOK_SECRET or disable ValidateSignature.");
                }

                telegramSecretBytes = Encoding.UTF8.GetBytes(telegramSecret);
            }

            app.MapPost(startup.Config.Channels.Telegram.WebhookPath, async (HttpContext ctx) =>
            {
                if (telegramSecretBytes is not null)
                {
                    var provided = ctx.Request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
                    var providedBytes = Encoding.UTF8.GetBytes(provided ?? "");
                    if (providedBytes.Length != telegramSecretBytes.Length ||
                        !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(providedBytes, telegramSecretBytes))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                }

                var maxRequestSize = Math.Max(4 * 1024, startup.Config.Channels.Telegram.MaxRequestBytes);
                var (bodyOk, bodyText) = await EndpointHelpers.TryReadBodyTextAsync(ctx, maxRequestSize, ctx.RequestAborted);
                if (!bodyOk)
                {
                    ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await ctx.Response.WriteAsync("Request too large.", ctx.RequestAborted);
                    return;
                }

                using var document = JsonDocument.Parse(bodyText, new JsonDocumentOptions { MaxDepth = 64 });
                var root = document.RootElement;

                if (root.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("chat", out var chat) &&
                    chat.TryGetProperty("id", out var chatId))
                {
                    var senderIdStr = chatId.GetRawText();

                    await runtime.RecentSenders.RecordAsync("telegram", senderIdStr, senderName: null, ctx.RequestAborted);

                    var effective = runtime.Allowlists.GetEffective("telegram", new ChannelAllowlistFile
                    {
                        AllowedFrom = startup.Config.Channels.Telegram.AllowedFromUserIds
                    });

                    if (!AllowlistPolicy.IsAllowed(effective.AllowedFrom, senderIdStr, runtime.AllowlistSemantics))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }

                    string? text = null;
                    if (message.TryGetProperty("text", out var textNode))
                        text = textNode.GetString();

                    string? marker = null;
                    if (message.TryGetProperty("photo", out var photoNode) && photoNode.ValueKind == JsonValueKind.Array)
                    {
                        string? fileId = null;
                        foreach (var photo in photoNode.EnumerateArray())
                        {
                            if (photo.TryGetProperty("file_id", out var idNode))
                                fileId = idNode.GetString();
                        }

                        if (!string.IsNullOrWhiteSpace(fileId))
                            marker = $"[IMAGE:telegram:file_id={fileId}]";
                    }

                    if (!string.IsNullOrWhiteSpace(marker))
                    {
                        var caption = message.TryGetProperty("caption", out var capNode) ? capNode.GetString() : null;
                        text = string.IsNullOrWhiteSpace(caption) ? marker : marker + "\n" + caption;
                    }

                    if (!string.IsNullOrWhiteSpace(text) && text.Length > startup.Config.Channels.Telegram.MaxInboundChars)
                        text = text[..startup.Config.Channels.Telegram.MaxInboundChars];

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status200OK;
                        await ctx.Response.WriteAsync("OK");
                        return;
                    }

                    var inbound = new InboundMessage
                    {
                        ChannelId = "telegram",
                        SenderId = senderIdStr,
                        Text = text
                    };

                    await runtime.Pipeline.InboundWriter.WriteAsync(inbound, ctx.RequestAborted);
                }

                ctx.Response.StatusCode = StatusCodes.Status200OK;
                await ctx.Response.WriteAsync("OK");
            });
        }

        if (startup.Config.Channels.WhatsApp.Enabled)
        {
            var whatsappWebhookHandler = app.Services.GetRequiredService<WhatsAppWebhookHandler>();
            app.MapMethods(startup.Config.Channels.WhatsApp.WebhookPath, ["GET", "POST"], async (HttpContext ctx) =>
            {
                var response = await whatsappWebhookHandler.HandleAsync(
                    ctx,
                    (msg, ct) => runtime.Pipeline.InboundWriter.WriteAsync(msg, ct),
                    ctx.RequestAborted);

                ctx.Response.StatusCode = response.StatusCode;
                if (response.Body is not null)
                {
                    ctx.Response.ContentType = response.ContentType;
                    await ctx.Response.WriteAsync(response.Body, ctx.RequestAborted);
                }
            });
        }

        if (startup.Config.Webhooks.Enabled)
        {
            app.MapPost("/webhooks/{name}", async (HttpContext ctx, string name) =>
            {
                if (!startup.Config.Webhooks.Endpoints.TryGetValue(name, out var hookCfg))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                var maxRequestSize = Math.Max(4 * 1024, hookCfg.MaxRequestBytes);
                var (bodyOk, body) = await EndpointHelpers.TryReadBodyTextAsync(ctx, maxRequestSize, ctx.RequestAborted);
                if (!bodyOk)
                {
                    ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await ctx.Response.WriteAsync("Request too large.", ctx.RequestAborted);
                    return;
                }

                if (body.Length > hookCfg.MaxBodyLength)
                    body = body[..hookCfg.MaxBodyLength];

                if (hookCfg.ValidateHmac)
                {
                    var secret = SecretResolver.Resolve(hookCfg.Secret);
                    if (string.IsNullOrWhiteSpace(secret))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }

                    var signatureHeader = ctx.Request.Headers[hookCfg.HmacHeader].ToString();
                    if (!GatewaySecurity.IsHmacSha256SignatureValid(secret, body, signatureHeader))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                }

                var prompt = hookCfg.PromptTemplate.Replace("{body}", body);
                var msg = new InboundMessage
                {
                    ChannelId = "webhook",
                    SessionId = hookCfg.SessionId ?? $"webhook:{name}",
                    SenderId = "system",
                    Text = prompt
                };

                await runtime.Pipeline.InboundWriter.WriteAsync(msg, ctx.RequestAborted);
                ctx.Response.StatusCode = StatusCodes.Status202Accepted;
                await ctx.Response.WriteAsync("Webhook queued.");
            });
        }
    }
}
