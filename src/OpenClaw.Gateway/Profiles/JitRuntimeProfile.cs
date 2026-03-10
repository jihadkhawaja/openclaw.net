using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Profiles;

internal sealed class JitRuntimeProfile : IRuntimeProfile
{
    public GatewayRuntimeMode Mode => GatewayRuntimeMode.Jit;

    public GatewayRuntimeCapabilities Capabilities { get; } = new(
        SupportsExpandedBridgeSurfaces: true,
        SupportsNativeDynamicPlugins: true);

    public void ConfigureServices(IServiceCollection services, GatewayStartupContext startup)
    {
    }

    public ValueTask OnRuntimeInitializedAsync(WebApplication app, GatewayStartupContext startup, GatewayAppRuntime runtime)
        => ValueTask.CompletedTask;
}
