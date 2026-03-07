using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ChannelReadinessEvaluatorTests
{
    [Fact]
    public void Evaluate_SmsEnabledWithoutSecrets_ReturnsMisconfigured()
    {
        var config = new GatewayConfig
        {
            Channels = new ChannelsConfig
            {
                Sms = new SmsChannelConfig
                {
                    Twilio = new TwilioSmsConfig
                    {
                        Enabled = true,
                        ValidateSignature = true
                    }
                }
            }
        };

        var sms = ChannelReadinessEvaluator.Evaluate(config, isNonLoopbackBind: true)
            .Single(item => item.ChannelId == "sms");

        Assert.Equal("misconfigured", sms.Status);
        Assert.Contains(sms.MissingRequirements, value => value.Contains("AuthTokenRef", StringComparison.Ordinal));
        Assert.Contains(sms.MissingRequirements, value => value.Contains("WebhookPublicBaseUrl", StringComparison.Ordinal));
        Assert.Contains(sms.FixGuidance, value => value.Href == "#setup-ref-sms-auth-token");
    }

    [Fact]
    public void Evaluate_TelegramEnabledWithoutSignatureValidation_ReturnsDegraded()
    {
        var previous = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("TELEGRAM_BOT_TOKEN", "test-token");
            var config = new GatewayConfig
            {
                Channels = new ChannelsConfig
                {
                    Telegram = new TelegramChannelConfig
                    {
                        Enabled = true,
                        ValidateSignature = false
                    }
                }
            };

            var telegram = ChannelReadinessEvaluator.Evaluate(config, isNonLoopbackBind: true)
                .Single(item => item.ChannelId == "telegram");

            Assert.Equal("degraded", telegram.Status);
            Assert.Contains(telegram.Warnings, value => value.Contains("public bind", StringComparison.Ordinal));
            Assert.Contains(telegram.FixGuidance, value => value.Href == "#telegram-validate-signature-input");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TELEGRAM_BOT_TOKEN", previous);
        }
    }

    [Fact]
    public void Evaluate_WhatsAppBridgePublicWithoutToken_ReturnsMisconfigured()
    {
        var config = new GatewayConfig
        {
            Channels = new ChannelsConfig
            {
                WhatsApp = new WhatsAppChannelConfig
                {
                    Enabled = true,
                    Type = "bridge",
                    BridgeUrl = "https://bridge.example.com/inbound",
                    BridgeTokenRef = ""
                }
            }
        };

        var whatsapp = ChannelReadinessEvaluator.Evaluate(config, isNonLoopbackBind: true)
            .Single(item => item.ChannelId == "whatsapp");

        Assert.Equal("misconfigured", whatsapp.Status);
        Assert.Contains(whatsapp.MissingRequirements, value => value.Contains("BridgeToken", StringComparison.Ordinal));
        Assert.Contains(whatsapp.FixGuidance, value => value.Href == "#setup-ref-whatsapp-bridge-token");
    }
}
