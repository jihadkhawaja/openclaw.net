using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Agent.Tools;

public sealed class McpNativeTool(
    McpClient client,
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
            if (argsDoc.RootElement.ValueKind != JsonValueKind.Object)
                return $"Error: Invalid JSON arguments for MCP tool '{localName}': JSON root must be an object.";
            var argsDict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in argsDoc.RootElement.EnumerateObject())
            {
                object? value = null;
                var v = prop.Value;
                switch (v.ValueKind)
                {
                    case JsonValueKind.String:
                        value = v.GetString();
                        break;
                    case JsonValueKind.Number:
                        value = v.Clone();
                        break;
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        value = v.GetBoolean();
                        break;
                    case JsonValueKind.Null:
                        value = null;
                        break;
                    default:
                        value = v.Clone();
                        break;
                }
                argsDict[prop.Name] = value;
            }
            var response = await client.CallToolAsync(remoteName, argsDict, progress: null, cancellationToken: ct);
            var text = FormatResponseContent(response);
            var isError = response.IsError ?? false;
            return isError ? $"Error: {text}" : text;
        }
        catch (JsonException ex)
        {
            return $"Error: Invalid JSON arguments for MCP tool '{localName}': {ex.Message}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: MCP tool '{localName}' failed: {ex.Message}";
        }
    }

    private static string FormatResponseContent(CallToolResult response)
    {
        var parts = new List<string>();

        foreach (var item in response.Content ?? [])
        {
            switch (item)
            {
                case TextContentBlock textBlock when !string.IsNullOrEmpty(textBlock.Text):
                    parts.Add(textBlock.Text);
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource } when !string.IsNullOrEmpty(resource.Text):
                    parts.Add(resource.Text);
                    break;
                default:
                    parts.Add(JsonSerializer.Serialize(item, McpToolSerializerContext.Default.ContentBlock));
                    break;
            }
        }

        if (response.StructuredContent is { } structuredContent &&
            structuredContent.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            parts.Add(structuredContent.GetRawText());
        }

        return string.Join("\n\n", parts);
    }
}

[JsonSerializable(typeof(ContentBlock))]
[JsonSerializable(typeof(TextContentBlock))]
[JsonSerializable(typeof(ImageContentBlock))]
[JsonSerializable(typeof(AudioContentBlock))]
[JsonSerializable(typeof(EmbeddedResourceBlock))]
[JsonSerializable(typeof(ResourceLinkBlock))]
[JsonSerializable(typeof(ToolUseContentBlock))]
[JsonSerializable(typeof(ToolResultContentBlock))]
[JsonSerializable(typeof(ResourceContents))]
[JsonSerializable(typeof(TextResourceContents))]
[JsonSerializable(typeof(BlobResourceContents))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
internal sealed partial class McpToolSerializerContext : JsonSerializerContext;
