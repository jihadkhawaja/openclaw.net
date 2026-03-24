using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

public sealed class McpServerToolRegistryTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = [];

    [Fact]
    public async Task LoadAsync_HttpServer_DiscoversAndExecutesTools()
    {
        var (serverUrl, calls) = await StartMcpServerAsync();
        var registry = new McpServerToolRegistry(
            new McpPluginsConfig
            {
                Enabled = true,
                Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    ["demo"] = new()
                    {
                        Transport = "http",
                        Url = serverUrl
                    }
                }
            },
            NullLogger<McpServerToolRegistry>.Instance);
        using var nativeRegistry = new NativePluginRegistry(new NativePluginsConfig(), NullLogger.Instance, new ToolingConfig());

        await registry.RegisterToolsAsync(nativeRegistry, CancellationToken.None);

        var tool = Assert.Single(nativeRegistry.Tools);
        Assert.Equal("demo.echo", tool.Name);
        Assert.Contains("Demo echo tool", tool.Description, StringComparison.Ordinal);
        Assert.Equal("demo:hello", await tool.ExecuteAsync("""{"text":"hello"}""", CancellationToken.None));
        Assert.Equal(1, calls.InitializeCalls);
        Assert.Equal(1, calls.ListCalls);
        Assert.Equal(1, calls.CallCalls);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            await app.DisposeAsync();
    }

    private async Task<(string ServerUrl, McpCallTracker Tracker)> StartMcpServerAsync()
    {
        var tracker = new McpCallTracker();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(0));
        var app = builder.Build();
        app.MapPost("/", async context =>
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var method = document.RootElement.GetProperty("method").GetString();
            var id = document.RootElement.GetProperty("id").GetString()!;

            var response = method switch
            {
                "initialize" => BuildResponse(id, writer =>
                {
                    tracker.InitializeCalls++;
                    writer.WriteStartObject("capabilities");
                    writer.WriteStartObject("tools");
                    writer.WriteBoolean("listChanged", false);
                    writer.WriteEndObject();
                    writer.WriteStartObject("resources");
                    writer.WriteBoolean("listChanged", false);
                    writer.WriteBoolean("supportsTemplates", false);
                    writer.WriteEndObject();
                    writer.WriteStartObject("prompts");
                    writer.WriteBoolean("listChanged", false);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteStartObject("serverInfo");
                    writer.WriteString("name", "demo");
                    writer.WriteString("version", "1.0.0");
                    writer.WriteEndObject();
                }),
                "tools/list" => BuildResponse(id, writer =>
                {
                    tracker.ListCalls++;
                    writer.WriteStartArray("tools");
                    writer.WriteStartObject();
                    writer.WriteString("name", "echo");
                    writer.WriteString("description", "Demo echo tool");
                    writer.WriteStartObject("inputSchema");
                    writer.WriteString("type", "object");
                    writer.WriteStartObject("properties");
                    writer.WriteStartObject("text");
                    writer.WriteString("type", "string");
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteStartArray("required");
                    writer.WriteStringValue("text");
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                }),
                "tools/call" => BuildResponse(id, writer =>
                {
                    tracker.CallCalls++;
                    var text = document.RootElement.GetProperty("params").GetProperty("arguments").GetProperty("text").GetString();
                    writer.WriteStartArray("content");
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", $"demo:{text}");
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                    writer.WriteBoolean("isError", false);
                }),
                _ => throw new InvalidOperationException($"Unexpected MCP method '{method}'.")
            };

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(response, context.RequestAborted);
        });

        await app.StartAsync();
        _apps.Add(app);
        var address = app.Urls.Single();
        return ($"{address.TrimEnd('/')}/", tracker);
    }

    private static string BuildResponse(string id, Action<Utf8JsonWriter> writeResult)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("id", id);
            writer.WritePropertyName("result");
            writer.WriteStartObject();
            writeResult(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private sealed class McpCallTracker
    {
        public int InitializeCalls { get; set; }
        public int ListCalls { get; set; }
        public int CallCalls { get; set; }
    }
}
