using Microsoft.Extensions.Logging;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Plugins;

internal static class BridgeTransportFactory
{
    public static (IBridgeTransport Transport, BridgeTransportRuntimeConfig RuntimeConfig) Create(
        BridgeTransportConfig config,
        string pluginId,
        ILogger logger)
    {
        var mode = NormalizeMode(config.Mode);
        var socketPath = mode == "stdio" ? null : ResolveSocketPath(config.SocketPath, pluginId);

        return mode switch
        {
            "stdio" => (new StdioBridgeTransport(logger), new BridgeTransportRuntimeConfig { Mode = mode }),
            "socket" => (new SocketBridgeTransport(socketPath!, logger), new BridgeTransportRuntimeConfig { Mode = mode, SocketPath = socketPath }),
            "hybrid" => (new HybridBridgeTransport(socketPath!, logger), new BridgeTransportRuntimeConfig { Mode = mode, SocketPath = socketPath }),
            _ => throw new InvalidOperationException($"Unsupported plugin bridge transport mode '{config.Mode}'. Supported modes: stdio, socket, hybrid.")
        };
    }

    private static string NormalizeMode(string? mode)
        => string.IsNullOrWhiteSpace(mode) ? "stdio" : mode.Trim().ToLowerInvariant();

    private static string ResolveSocketPath(string? configuredPath, string pluginId)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                const string prefix = @"\\.\pipe\";
                return configuredPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? configuredPath
                    : prefix + configuredPath.Trim('\\', '/');
            }

            return $@"\\.\pipe\openclaw-{Sanitize(pluginId)}-{Guid.NewGuid():N}";
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(configuredPath);

        var tempRoot = Directory.Exists("/tmp")
            ? "/tmp"
            : Path.GetTempPath();
        Directory.CreateDirectory(tempRoot);
        var stem = Sanitize(pluginId);
        if (stem.Length > 24)
            stem = stem[..24];
        return Path.Combine(tempRoot, $"ocb-{stem}-{Guid.NewGuid():N}.sock");
    }

    private static string Sanitize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        return builder.ToString().Trim('-');
    }
}
