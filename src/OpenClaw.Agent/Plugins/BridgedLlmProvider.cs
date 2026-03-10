using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Wraps a plugin-registered LLM provider as an <see cref="IChatClient"/>.
/// </summary>
public sealed class BridgedLlmProvider : IChatClient
{
    private readonly PluginBridgeProcess _bridge;
    private readonly string _providerId;
    private readonly ILogger _logger;

    public BridgedLlmProvider(PluginBridgeProcess bridge, string providerId, ILogger logger)
    {
        _bridge = bridge;
        _providerId = providerId;
        _logger = logger;
    }

    public ChatClientMetadata Metadata => new(nameof(BridgedLlmProvider), null, _providerId);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = new BridgeProviderRequest
        {
            ProviderId = _providerId,
            Messages = SerializeMessages(chatMessages),
            Options = SerializeOptions(options)
        };

        var response = await _bridge.SendAndWaitAsync(
            "provider_complete",
            request,
            CoreJsonContext.Default.BridgeProviderRequest,
            cancellationToken);

        if (response.Error is not null)
            throw new InvalidOperationException($"Provider '{_providerId}' error: {response.Error.Message}");

        var text = "";
        if (response.Result is { } result)
        {
            if (result.TryGetProperty("text", out var textEl))
                text = textEl.GetString() ?? "";
            else
                text = result.GetRawText();
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(chatMessages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(IChatClient) ? this : null;

    public void Dispose() { }

    private BridgeProviderOptions? SerializeOptions(ChatOptions? options)
    {
        if (options is null)
            return null;

        var additional = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (options.AdditionalProperties is not null)
        {
            foreach (var (key, value) in options.AdditionalProperties)
            {
                if (TrySerializeArbitraryValue(value, out var element))
                    additional[key] = element;
                else
                    _logger.LogWarning("Dropping non-serializable provider option '{OptionKey}' for plugin provider '{ProviderId}'", key, _providerId);
            }
        }

#pragma warning disable MEAI001
        return new BridgeProviderOptions
        {
            ConversationId = options.ConversationId,
            Instructions = options.Instructions,
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxOutputTokens,
            TopP = options.TopP,
            TopK = options.TopK,
            FrequencyPenalty = options.FrequencyPenalty,
            PresencePenalty = options.PresencePenalty,
            Seed = options.Seed,
            Reasoning = SerializeReasoning(options.Reasoning),
            ResponseFormat = SerializeResponseFormat(options.ResponseFormat),
            ModelId = options.ModelId,
            StopSequences = options.StopSequences?.ToArray() ?? [],
            AllowMultipleToolCalls = options.AllowMultipleToolCalls,
            ToolMode = SerializeToolMode(options.ToolMode),
            Tools = SerializeTools(options.Tools),
            AllowBackgroundResponses = options.AllowBackgroundResponses,
            ContinuationToken = options.ContinuationToken?.ToString(),
            AdditionalProperties = additional
        };
#pragma warning restore MEAI001
    }

    private static BridgeReasoningOptions? SerializeReasoning(ReasoningOptions? reasoning)
    {
        if (reasoning is null)
            return null;

        return new BridgeReasoningOptions
        {
            Effort = reasoning.Effort?.ToString(),
            Output = reasoning.Output?.ToString()
        };
    }

    private static BridgeResponseFormat? SerializeResponseFormat(ChatResponseFormat? format)
    {
        if (format is null)
            return null;

        if (format is ChatResponseFormatJson json)
        {
            return new BridgeResponseFormat
            {
                Kind = "json",
                Schema = json.Schema,
                SchemaName = json.SchemaName,
                SchemaDescription = json.SchemaDescription
            };
        }

        if (format is ChatResponseFormatText)
        {
            return new BridgeResponseFormat
            {
                Kind = "text"
            };
        }

        return new BridgeResponseFormat
        {
            Kind = format.GetType().Name
        };
    }

    private static BridgeToolMode? SerializeToolMode(ChatToolMode? toolMode)
    {
        if (toolMode is null || toolMode.Equals(ChatToolMode.Auto))
            return new BridgeToolMode { Kind = "auto" };

        if (toolMode.Equals(ChatToolMode.None))
            return new BridgeToolMode { Kind = "none" };

        if (toolMode.Equals(ChatToolMode.RequireAny))
            return new BridgeToolMode { Kind = "requireAny" };

        if (toolMode is RequiredChatToolMode required)
        {
            return new BridgeToolMode
            {
                Kind = "requireSpecific",
                FunctionName = required.RequiredFunctionName
            };
        }

        return new BridgeToolMode { Kind = toolMode.GetType().Name };
    }

    private static BridgeToolDescriptor[] SerializeTools(IEnumerable<AITool>? tools)
    {
        if (tools is null)
            return [];

        var descriptors = new List<BridgeToolDescriptor>();
        foreach (var tool in tools)
        {
            var declaration = tool as AIFunctionDeclaration ?? tool.GetService<AIFunctionDeclaration>();
            descriptors.Add(new BridgeToolDescriptor
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = declaration?.JsonSchema,
                ReturnSchema = declaration?.ReturnJsonSchema
            });
        }

        return [.. descriptors];
    }

    private static JsonElement SerializeMessages(IEnumerable<ChatMessage> messages)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (var msg in messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", msg.Role.Value);
                writer.WriteString("content", msg.Text ?? "");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        ms.Position = 0;
        using var doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }

    private static bool TrySerializeArbitraryValue(object? value, out JsonElement element)
    {
        switch (value)
        {
            case null:
                element = ParseLiteral("null");
                return true;
            case JsonElement jsonElement:
                element = jsonElement.Clone();
                return true;
            case string str:
                element = SerializeString(str);
                return true;
            case bool boolean:
                element = ParseLiteral(boolean ? "true" : "false");
                return true;
            case int int32:
                element = ParseLiteral(int32.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case long int64:
                element = ParseLiteral(int64.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case float single:
                element = ParseLiteral(single.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case double dbl:
                element = ParseLiteral(dbl.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case decimal dec:
                element = ParseLiteral(dec.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case IEnumerable<string> strings:
                element = SerializeStringArray(strings);
                return true;
            default:
                element = default;
                return false;
        }
    }

    private static JsonElement ParseLiteral(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement SerializeString(string value)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStringValue(value);
        }

        ms.Position = 0;
        using var doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }

    private static JsonElement SerializeStringArray(IEnumerable<string> values)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (var value in values)
                writer.WriteStringValue(value);
            writer.WriteEndArray();
        }

        ms.Position = 0;
        using var doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }
}
