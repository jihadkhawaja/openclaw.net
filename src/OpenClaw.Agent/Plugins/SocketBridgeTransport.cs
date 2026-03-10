using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Local IPC transport using Unix domain sockets on Unix and named pipes on Windows.
/// </summary>
public sealed class SocketBridgeTransport : BridgeTransportBase
{
    private readonly ILogger _logger;
    private readonly string _socketPath;
    private readonly string? _pipeName;
    private Socket? _listener;
    private Socket? _acceptedSocket;
    private NamedPipeServerStream? _pipeServer;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public SocketBridgeTransport(string socketPath, ILogger logger)
        : base(logger)
    {
        _logger = logger;
        _socketPath = socketPath;
        _pipeName = OperatingSystem.IsWindows() ? NormalizePipeName(socketPath) : null;
    }

    public override Task PrepareAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (OperatingSystem.IsWindows())
        {
            _pipeServer = new NamedPipeServerStream(
                _pipeName!,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            return Task.CompletedTask;
        }

        var dir = Path.GetDirectoryName(_socketPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(_socketPath))
        {
            try { File.Delete(_socketPath); } catch { }
        }

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listener.Listen(1);
        return Task.CompletedTask;
    }

    public override async Task StartAsync(Process process, CancellationToken ct)
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(10));

        if (OperatingSystem.IsWindows())
        {
            if (_pipeServer is null)
                throw new InvalidOperationException("Named pipe server is not prepared.");

            await _pipeServer.WaitForConnectionAsync(connectCts.Token);
            _reader = new StreamReader(_pipeServer, Encoding.UTF8, leaveOpen: true);
            _writer = new StreamWriter(_pipeServer, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            AttachReaderWriter(_reader, _writer);
            return;
        }

        if (_listener is null)
            throw new InvalidOperationException("Socket listener is not prepared.");

        _acceptedSocket = await _listener.AcceptAsync(connectCts.Token);
        var stream = new NetworkStream(_acceptedSocket, ownsSocket: false);
        _reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        AttachReaderWriter(_reader, _writer);
    }

    protected override ValueTask DisposeCoreAsync()
    {
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _acceptedSocket?.Dispose(); } catch { }
        try { _listener?.Dispose(); } catch { }
        try
        {
            if (_pipeServer is not null)
            {
                if (_pipeServer.IsConnected)
                    _pipeServer.Disconnect();
                _pipeServer.Dispose();
            }
        }
        catch { }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                if (File.Exists(_socketPath))
                    File.Delete(_socketPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to remove bridge socket path {SocketPath}", _socketPath);
            }
        }

        return ValueTask.CompletedTask;
    }

    private static string NormalizePipeName(string socketPath)
    {
        const string prefix = @"\\.\pipe\";
        if (socketPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return socketPath[prefix.Length..];

        var sanitized = socketPath
            .Replace('\\', '-')
            .Replace('/', '-')
            .Replace(':', '-');

        return sanitized.Trim('-');
    }
}
