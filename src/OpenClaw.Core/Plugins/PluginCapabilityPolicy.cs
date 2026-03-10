using OpenClaw.Core.Models;

namespace OpenClaw.Core.Plugins;

public static class PluginCapabilityPolicy
{
    public const string Tools = "tools";
    public const string Services = "services";
    public const string Skills = "skills";
    public const string Channels = "channels";
    public const string Commands = "commands";
    public const string Providers = "providers";
    public const string Hooks = "hooks";
    public const string NativeDynamic = "native_dynamic";

    private static readonly HashSet<string> AotSafeCapabilities = new(StringComparer.Ordinal)
    {
        Tools,
        Services,
        Skills
    };

    public static string[] Normalize(IEnumerable<string> capabilities)
        => capabilities
            .Where(cap => !string.IsNullOrWhiteSpace(cap))
            .Select(cap => cap.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(cap => cap, StringComparer.Ordinal)
            .ToArray();

    public static string[] GetBlockedCapabilities(GatewayRuntimeMode runtimeMode, IEnumerable<string> capabilities)
    {
        var normalized = Normalize(capabilities);
        if (runtimeMode != GatewayRuntimeMode.Aot)
            return [];

        return normalized
            .Where(cap => !AotSafeCapabilities.Contains(cap))
            .ToArray();
    }

    public static bool RequiresJit(GatewayRuntimeMode runtimeMode, IEnumerable<string> capabilities)
        => GetBlockedCapabilities(runtimeMode, capabilities).Length > 0;
}
