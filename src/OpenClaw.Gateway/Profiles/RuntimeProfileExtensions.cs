using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;

namespace OpenClaw.Gateway.Profiles;

internal static class RuntimeProfileExtensions
{
    public static IServiceCollection ApplyOpenClawRuntimeProfile(this IServiceCollection services, GatewayStartupContext startup)
    {
        IRuntimeProfile profile = startup.RuntimeState.EffectiveMode switch
        {
            GatewayRuntimeMode.Aot => new AotRuntimeProfile(),
            GatewayRuntimeMode.Jit => new JitRuntimeProfile(),
            _ => throw new InvalidOperationException($"Unsupported runtime mode: {startup.RuntimeState.EffectiveMode}")
        };

        services.AddSingleton(profile);
        services.AddSingleton(profile.Capabilities);
        profile.ConfigureServices(services, startup);
        return services;
    }
}
