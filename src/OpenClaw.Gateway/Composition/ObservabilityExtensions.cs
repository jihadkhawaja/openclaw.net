using OpenClaw.Gateway.Extensions;

namespace OpenClaw.Gateway.Composition;

internal static class ObservabilityExtensions
{
    public static WebApplicationBuilder AddOpenClawObservability(this WebApplicationBuilder builder)
    {
        builder.AddGatewayTelemetry();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        return builder;
    }
}
