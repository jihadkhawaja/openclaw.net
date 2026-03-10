namespace OpenClaw.Gateway.Bootstrap;

internal sealed class BootstrapResult
{
    public required bool ShouldExit { get; init; }
    public required int ExitCode { get; init; }
    public GatewayStartupContext? Startup { get; init; }
}
