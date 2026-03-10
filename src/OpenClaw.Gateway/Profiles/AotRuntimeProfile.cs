using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Profiles;

internal sealed class AotRuntimeProfile : IRuntimeProfile
{
    public GatewayRuntimeMode Mode => GatewayRuntimeMode.Aot;

    public GatewayRuntimeCapabilities Capabilities { get; } = new(
        SupportsExpandedBridgeSurfaces: false,
        SupportsNativeDynamicPlugins: false);

    public void ConfigureServices(IServiceCollection services, GatewayStartupContext startup)
    {
    }

    public ValueTask OnRuntimeInitializedAsync(WebApplication app, GatewayStartupContext startup, GatewayAppRuntime runtime)
        => ValueTask.CompletedTask;
}
