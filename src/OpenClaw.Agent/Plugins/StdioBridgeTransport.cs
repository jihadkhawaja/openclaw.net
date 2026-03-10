using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Stdio-based bridge transport using the child process stdin/stdout streams.
/// </summary>
public sealed class StdioBridgeTransport : BridgeTransportBase
{
    public StdioBridgeTransport(ILogger logger)
        : base(logger)
    {
    }

    public override Task StartAsync(Process process, CancellationToken ct)
    {
        if (process.StandardOutput is null || process.StandardInput is null)
            throw new InvalidOperationException("Process stdio is not available for bridge transport.");

        AttachReaderWriter(process.StandardOutput, process.StandardInput);
        return Task.CompletedTask;
    }
}
