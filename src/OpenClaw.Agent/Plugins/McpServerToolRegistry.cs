using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Discovers tools from configured MCP servers and registers them as native OpenClaw tools.
/// </summary>
public sealed class McpServerToolRegistry : IDisposable
{
    private readonly McpPluginsConfig _config;
    private readonly ILogger _logger;
    private readonly List<IDisposable> _ownedResources = [];
    private readonly List<DiscoveredMcpTool> _tools = [];
    private bool _loaded;

    /// <summary>
    /// Creates a registry for configured MCP servers.
    /// </summary>
    public McpServerToolRegistry(McpPluginsConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Connects to configured MCP servers and registers discovered tools into the native registry.
    /// </summary>
    public async Task RegisterToolsAsync(NativePluginRegistry nativeRegistry, CancellationToken ct)
    {
        var tools = await LoadAsync(ct);
        nativeRegistry.RegisterOwnedResource(this);
        foreach (var tool in tools)
            nativeRegistry.RegisterExternalTool(tool.Tool, tool.PluginId, tool.Detail);
    }

    internal async Task<IReadOnlyList<DiscoveredMcpTool>> LoadAsync(CancellationToken ct)
    {
        if (_loaded)
            return _tools;

        _loaded = true;
        if (!_config.Enabled)
            return _tools;

        foreach (var (serverId, serverConfig) in _config.Servers)
        {
            if (!serverConfig.Enabled)
                continue;

            var session = CreateSession(serverId, serverConfig);
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(serverConfig.StartupTimeoutSeconds));
                var tools = await session.LoadToolsAsync(timeoutCts.Token);
                _ownedResources.Add(session);

                foreach (var tool in tools)
                {
                    _tools.Add(new DiscoveredMcpTool(
                        session.PluginId,
                        new McpNativeTool(session, tool.LocalName, tool.RemoteName, tool.Description, tool.InputSchemaText),
                        session.DisplayName));
                }
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        return _tools;
    }

    public void Dispose()
    {
        foreach (var resource in _ownedResources)
            resource.Dispose();
    }

    private McpServerSession CreateSession(string serverId, McpServerConfig config)
    {
        var displayName = string.IsNullOrWhiteSpace(config.Name) ? serverId : config.Name!;
        var pluginId = $"mcp:{serverId}";
        var transport = NormalizeTransport(config);
        return transport switch
        {
            "stdio" => new StdioMcpServerSession(serverId, pluginId, displayName, config, _logger),
            "http" => new HttpMcpServerSession(serverId, pluginId, displayName, config, _logger),
            _ => throw new InvalidOperationException($"Unsupported MCP transport '{config.Transport}' for server '{serverId}'.")
        };
    }

    private static string NormalizeTransport(McpServerConfig config)
    {
        var transport = config.Transport?.Trim();
        if (string.IsNullOrWhiteSpace(transport))
            return string.IsNullOrWhiteSpace(config.Url) ? "stdio" : "http";

        if (transport.Equals("streamable-http", StringComparison.OrdinalIgnoreCase) ||
            transport.Equals("streamable_http", StringComparison.OrdinalIgnoreCase))
        {
            return "http";
        }

        return transport.ToLowerInvariant();
    }

    internal sealed record DiscoveredMcpTool(string PluginId, ITool Tool, string Detail);

    private sealed class McpNativeTool(
        McpServerSession session,
        string localName,
        string remoteName,
        string description,
        string parameterSchema) : ITool
    {
        public string Name => localName;
        public string Description => description;
        public string ParameterSchema => parameterSchema;

        public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            try
            {
                using var argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                return await session.CallToolAsTextAsync(remoteName, argsDoc.RootElement.Clone(), ct);
            }
            catch (JsonException ex)
            {
                return $"Error: Invalid JSON arguments for MCP tool '{localName}': {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"Error: MCP tool '{localName}' failed: {ex.Message}";
            }
        }
    }

    private abstract class McpServerSession : IDisposable
    {
        protected readonly string ServerId;
        private readonly string? _toolNamePrefix;

        protected McpServerSession(string serverId, string pluginId, string displayName, McpServerConfig config, ILogger logger)
        {
            ServerId = serverId;
            PluginId = pluginId;
            DisplayName = displayName;
            Config = config;
            Logger = logger;
            _toolNamePrefix = config.ToolNamePrefix;
        }

        protected McpServerConfig Config { get; }
        protected ILogger Logger { get; }
        public string PluginId { get; }
        public string DisplayName { get; }

        public async Task<IReadOnlyList<McpToolDescriptor>> LoadToolsAsync(CancellationToken ct)
        {
            await InitializeAsync(ct);
            var response = await SendRequestAsync("tools/list", null, ct);
            return ParseTools(response);
        }

        public async Task<string> CallToolAsTextAsync(string remoteName, JsonElement arguments, CancellationToken ct)
        {
            var response = await SendRequestAsync(
                "tools/call",
                writer =>
                {
                    writer.WriteString("name", remoteName);
                    writer.WritePropertyName("arguments");
                    arguments.WriteTo(writer);
                },
                ct);

            var isError = response.TryGetProperty("isError", out var isErrorEl) && isErrorEl.ValueKind == JsonValueKind.True;
            if (response.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var item in contentEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    if (item.TryGetProperty("type", out var typeEl) &&
                        typeEl.ValueKind == JsonValueKind.String &&
                        string.Equals(typeEl.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                        item.TryGetProperty("text", out var textEl) &&
                        textEl.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(textEl.GetString() ?? "");
                    }
                }

                if (parts.Count > 0)
                {
                    var text = string.Join("\n\n", parts);
                    return isError ? $"Error: {text}" : text;
                }
            }

            var raw = response.GetRawText();
            return isError ? $"Error: {raw}" : raw;
        }

        public abstract void Dispose();

        protected abstract Task<JsonElement> SendRequestAsync(
            string method,
            Action<Utf8JsonWriter>? writeParams,
            CancellationToken ct);

        protected virtual async Task InitializeAsync(CancellationToken ct)
        {
            await SendRequestAsync(
                "initialize",
                writer =>
                {
                    writer.WriteString("protocolVersion", "2025-03-26");
                    writer.WriteStartObject("capabilities");
                    writer.WriteEndObject();
                    writer.WriteStartObject("clientInfo");
                    writer.WriteString("name", "OpenClaw Gateway");
                    writer.WriteString("version", "1.0.0");
                    writer.WriteEndObject();
                },
                ct);
        }

        protected string ResolveToolName(string remoteName)
        {
            var prefix = _toolNamePrefix;
            if (prefix is null)
                prefix = $"{SanitizePrefixPart(ServerId)}.";

            return string.IsNullOrEmpty(prefix) ? remoteName : prefix + remoteName;
        }

        private IReadOnlyList<McpToolDescriptor> ParseTools(JsonElement result)
        {
            if (!result.TryGetProperty("tools", out var toolsEl) || toolsEl.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"MCP server '{DisplayName}' returned an invalid tools/list response.");

            var tools = new List<McpToolDescriptor>();
            foreach (var item in toolsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("name", out var nameEl) ||
                    nameEl.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException($"MCP server '{DisplayName}' returned a tool entry without a valid name.");
                }

                var remoteName = nameEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(remoteName))
                    throw new InvalidOperationException($"MCP server '{DisplayName}' returned a tool entry with an empty name.");
                var localName = ResolveToolName(remoteName);
                var description = item.TryGetProperty("description", out var descriptionEl) && descriptionEl.ValueKind == JsonValueKind.String
                    ? $"{descriptionEl.GetString()} (from MCP server '{DisplayName}')"
                    : $"MCP tool '{remoteName}' from server '{DisplayName}'.";
                var inputSchema = item.TryGetProperty("inputSchema", out var schemaEl) ? schemaEl.GetRawText() : "{}";
                tools.Add(new McpToolDescriptor(localName, remoteName, description, inputSchema));
            }

            Logger.LogInformation("MCP server enabled: {ServerId} ({DisplayName}) with {ToolCount} tool(s)",
                ServerId, DisplayName, tools.Count);
            return tools;
        }

        protected static string BuildRequestJson(string requestId, string method, Action<Utf8JsonWriter>? writeParams)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteString("id", requestId);
                writer.WriteString("method", method);
                writer.WritePropertyName("params");
                writer.WriteStartObject();
                writeParams?.Invoke(writer);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }

        protected static JsonElement ParseResponse(string payload, string requestId)
        {
            using var responseDoc = JsonDocument.Parse(payload);
            var root = responseDoc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("MCP server returned a non-object JSON-RPC response.");

            if (!root.TryGetProperty("id", out var idEl) || !IdsMatch(idEl, requestId))
                throw new InvalidOperationException("MCP server returned an unexpected JSON-RPC response id.");

            if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.Object)
            {
                var code = errorEl.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number ? codeEl.GetInt32() : 0;
                var message = errorEl.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String
                    ? messageEl.GetString()
                    : "Unknown MCP error.";
                throw new InvalidOperationException($"MCP request failed ({code}): {message}");
            }

            if (!root.TryGetProperty("result", out var resultEl))
                throw new InvalidOperationException("MCP server response did not contain a result.");

            return resultEl.Clone();
        }

        private static bool IdsMatch(JsonElement responseId, string requestId)
            => responseId.ValueKind switch
            {
                JsonValueKind.String => string.Equals(responseId.GetString(), requestId, StringComparison.Ordinal),
                JsonValueKind.Number => string.Equals(responseId.GetRawText(), requestId, StringComparison.Ordinal),
                _ => false
            };

        private static string SanitizePrefixPart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "mcp";

            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')
                    sb.Append(char.ToLowerInvariant(ch));
                else
                    sb.Append('_');
            }

            return sb.Length == 0 ? "mcp" : sb.ToString();
        }
    }

    private sealed record McpToolDescriptor(string LocalName, string RemoteName, string Description, string InputSchemaText);

    private sealed class HttpMcpServerSession : McpServerSession
    {
        private readonly HttpClient _httpClient;
        private long _requestId;

        public HttpMcpServerSession(string serverId, string pluginId, string displayName, McpServerConfig config, ILogger logger)
            : base(serverId, pluginId, displayName, config, logger)
        {
            if (string.IsNullOrWhiteSpace(config.Url))
                throw new InvalidOperationException($"MCP server '{serverId}' requires Url for HTTP transport.");

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(config.Url, UriKind.Absolute)
            };

            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            foreach (var (headerName, rawValue) in config.Headers)
            {
                var value = SecretResolver.Resolve(rawValue) ?? rawValue;
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(headerName, value);
            }
        }

        public override void Dispose() => _httpClient.Dispose();

        protected override async Task<JsonElement> SendRequestAsync(
            string method,
            Action<Utf8JsonWriter>? writeParams,
            CancellationToken ct)
        {
            var requestId = Interlocked.Increment(ref _requestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var payload = BuildRequestJson(requestId, method, writeParams);
            using var request = new HttpRequestMessage(HttpMethod.Post, "")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Config.RequestTimeoutSeconds));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            response.EnsureSuccessStatusCode();
            var responsePayload = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            return ParseResponse(responsePayload, requestId);
        }
    }

    private sealed class StdioMcpServerSession : McpServerSession
    {
        private readonly Process _process;
        private readonly Stream _stdin;
        private readonly Stream _stdout;
        private readonly SemaphoreSlim _mutex = new(1, 1);
        private long _requestId;

        public StdioMcpServerSession(string serverId, string pluginId, string displayName, McpServerConfig config, ILogger logger)
            : base(serverId, pluginId, displayName, config, logger)
        {
            if (string.IsNullOrWhiteSpace(config.Command))
                throw new InvalidOperationException($"MCP server '{serverId}' requires Command for stdio transport.");

            var startInfo = new ProcessStartInfo
            {
                FileName = config.Command,
                WorkingDirectory = string.IsNullOrWhiteSpace(config.WorkingDirectory) ? Environment.CurrentDirectory : config.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var argument in config.Arguments)
                startInfo.ArgumentList.Add(argument);
            foreach (var (name, rawValue) in config.Environment)
                startInfo.Environment[name] = SecretResolver.Resolve(rawValue) ?? rawValue;

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start MCP server '{serverId}' using command '{config.Command}'.");
            _stdin = _process.StandardInput.BaseStream;
            _stdout = _process.StandardOutput.BaseStream;

            _ = Task.Run(async () =>
            {
                while (!_process.HasExited)
                {
                    var line = await _process.StandardError.ReadLineAsync();
                    if (line is null)
                        break;
                    if (!string.IsNullOrWhiteSpace(line))
                        Logger.LogWarning("MCP server {ServerId} stderr: {Line}", serverId, line);
                }
            });
        }

        public override void Dispose()
        {
            _mutex.Dispose();
            _stdin.Dispose();
            _stdout.Dispose();
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }
            _process.Dispose();
        }

        protected override async Task<JsonElement> SendRequestAsync(
            string method,
            Action<Utf8JsonWriter>? writeParams,
            CancellationToken ct)
        {
            await _mutex.WaitAsync(ct);
            try
            {
                var requestId = Interlocked.Increment(ref _requestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var payload = BuildRequestJson(requestId, method, writeParams);
                await WriteMessageAsync(payload, ct);

                while (true)
                {
                    var message = await ReadMessageAsync(ct);
                    using var doc = JsonDocument.Parse(message);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                        !doc.RootElement.TryGetProperty("id", out var idEl) ||
                        !idEl.ValueEquals(requestId))
                    {
                        continue;
                    }

                    return ParseResponse(message, requestId);
                }
            }
            finally
            {
                _mutex.Release();
            }
        }

        private async Task WriteMessageAsync(string payload, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Config.RequestTimeoutSeconds));
            await _stdin.WriteAsync(header, timeoutCts.Token);
            await _stdin.WriteAsync(bytes, timeoutCts.Token);
            await _stdin.FlushAsync(timeoutCts.Token);
        }

        private async Task<string> ReadMessageAsync(CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Config.RequestTimeoutSeconds));
            var contentLength = await ReadHeadersAsync(timeoutCts.Token);
            var buffer = new byte[contentLength];
            var offset = 0;
            while (offset < contentLength)
            {
                var read = await _stdout.ReadAsync(buffer.AsMemory(offset, contentLength - offset), timeoutCts.Token);
                if (read == 0)
                    throw new InvalidOperationException($"MCP server '{ServerId}' closed its stdout unexpectedly.");
                offset += read;
            }

            return Encoding.UTF8.GetString(buffer);
        }

        private async Task<int> ReadHeadersAsync(CancellationToken ct)
        {
            var contentLength = -1;
            while (true)
            {
                var line = await ReadLineAsync(ct);
                if (line.Length == 0)
                    break;

                const string headerName = "Content-Length:";
                if (line.StartsWith(headerName, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line[headerName.Length..].Trim(), out var parsed))
                {
                    contentLength = parsed;
                }
            }

            if (contentLength < 0)
                throw new InvalidOperationException($"MCP server '{ServerId}' did not send a Content-Length header.");

            return contentLength;
        }

        private async Task<string> ReadLineAsync(CancellationToken ct)
        {
            var bytes = new List<byte>();
            while (true)
            {
                var next = new byte[1];
                var read = await _stdout.ReadAsync(next.AsMemory(0, 1), ct);
                if (read == 0)
                    throw new InvalidOperationException($"MCP server '{ServerId}' closed its stdout unexpectedly.");

                if (next[0] == (byte)'\n')
                    break;

                if (next[0] != (byte)'\r')
                    bytes.Add(next[0]);
            }

            return Encoding.ASCII.GetString(bytes.ToArray());
        }
    }
}
