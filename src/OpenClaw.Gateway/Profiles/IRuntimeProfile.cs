using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Profiles;

internal sealed record GatewayRuntimeCapabilities(
    bool SupportsExpandedBridgeSurfaces,
    bool SupportsNativeDynamicPlugins);

internal interface IRuntimeProfile
{
    GatewayRuntimeMode Mode { get; }
    GatewayRuntimeCapabilities Capabilities { get; }
    void ConfigureServices(IServiceCollection services, GatewayStartupContext startup);
    ValueTask OnRuntimeInitializedAsync(WebApplication app, GatewayStartupContext startup, GatewayAppRuntime runtime);
}
