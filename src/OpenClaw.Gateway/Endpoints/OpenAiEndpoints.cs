using System.Text.Json;
using OpenClaw.Agent;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class OpenAiEndpoints
{
    public static void MapOpenClawOpenAiEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
        {
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            OpenAiChatCompletionRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync(
                    ctx.Request.Body,
                    CoreJsonContext.Default.OpenAiChatCompletionRequest,
                    ctx.RequestAborted);
            }
            catch
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("Invalid JSON request body.", ctx.RequestAborted);
                return;
            }

            if (req is null || req.Messages.Count == 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("Request must include at least one message.", ctx.RequestAborted);
                return;
            }

            var lastUserMsg = req.Messages.FindLast(m =>
                string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
            var userText = lastUserMsg?.Content ?? req.Messages[^1].Content ?? "";

            var httpMwCtx = new MessageContext
            {
                ChannelId = "openai-http",
                SenderId = EndpointHelpers.GetHttpRateLimitKey(ctx, startup.Config),
                Text = userText ?? "",
                SessionInputTokens = 0,
                SessionOutputTokens = 0
            };
            var allow = await runtime.MiddlewarePipeline.ExecuteAsync(httpMwCtx, ctx.RequestAborted);
            if (!allow)
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await ctx.Response.WriteAsync(httpMwCtx.ShortCircuitResponse ?? "Request blocked.", ctx.RequestAborted);
                return;
            }

            var requestId = $"oai-http:{Guid.NewGuid():N}";
            var session = await runtime.SessionManager.GetOrCreateAsync("openai-http", requestId, ctx.RequestAborted);
            if (req.Model is not null)
                session.ModelOverride = req.Model;

            try
            {
                var lastUserIndex = req.Messages.FindLastIndex(m =>
                    string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
                var excludeIndex = lastUserIndex >= 0 ? lastUserIndex : req.Messages.Count - 1;

                for (var i = 0; i < req.Messages.Count; i++)
                {
                    if (i == excludeIndex)
                        continue;

                    var message = req.Messages[i];
                    if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                    {
                        session.History.Add(new ChatTurn
                        {
                            Role = message.Role.ToLowerInvariant(),
                            Content = message.Content
                        });
                    }
                }

                var completionId = $"chatcmpl-{Guid.NewGuid():N}"[..29];
                var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var model = req.Model ?? startup.Config.Llm.Model;

                if (req.Stream)
                {
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.Headers.CacheControl = "no-cache";
                    ctx.Response.Headers.Connection = "keep-alive";

                    var roleChunk = new OpenAiStreamChunk
                    {
                        Id = completionId,
                        Created = created,
                        Model = model,
                        Choices = [new OpenAiStreamChoice { Index = 0, Delta = new OpenAiDelta { Role = "assistant" } }]
                    };
                    var roleJson = JsonSerializer.Serialize(roleChunk, CoreJsonContext.Default.OpenAiStreamChunk);
                    await ctx.Response.WriteAsync($"data: {roleJson}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

                    await foreach (var evt in runtime.AgentRuntime.RunStreamingAsync(session, userText ?? "", ctx.RequestAborted))
                    {
                        if (evt.Type == AgentStreamEventType.TextDelta && !string.IsNullOrEmpty(evt.Content))
                        {
                            var chunk = new OpenAiStreamChunk
                            {
                                Id = completionId,
                                Created = created,
                                Model = model,
                                Choices = [new OpenAiStreamChoice { Index = 0, Delta = new OpenAiDelta { Content = evt.Content } }]
                            };
                            var json = JsonSerializer.Serialize(chunk, CoreJsonContext.Default.OpenAiStreamChunk);
                            await ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted);
                            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        }
                        else if (evt.Type == AgentStreamEventType.Done)
                        {
                            var doneChunk = new OpenAiStreamChunk
                            {
                                Id = completionId,
                                Created = created,
                                Model = model,
                                Choices = [new OpenAiStreamChoice { Index = 0, Delta = new OpenAiDelta(), FinishReason = "stop" }]
                            };
                            var doneJson = JsonSerializer.Serialize(doneChunk, CoreJsonContext.Default.OpenAiStreamChunk);
                            await ctx.Response.WriteAsync($"data: {doneJson}\n\n", ctx.RequestAborted);
                            await ctx.Response.WriteAsync("data: [DONE]\n\n", ctx.RequestAborted);
                            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        }
                    }
                }
                else
                {
                    var result = await runtime.AgentRuntime.RunAsync(session, userText ?? "", ctx.RequestAborted);

                    var response = new OpenAiChatCompletionResponse
                    {
                        Id = completionId,
                        Created = created,
                        Model = model,
                        Choices =
                        [
                            new OpenAiChoice
                            {
                                Index = 0,
                                Message = new OpenAiResponseMessage { Role = "assistant", Content = result },
                                FinishReason = "stop"
                            }
                        ],
                        Usage = new OpenAiUsage
                        {
                            PromptTokens = (int)session.TotalInputTokens,
                            CompletionTokens = (int)session.TotalOutputTokens,
                            TotalTokens = (int)(session.TotalInputTokens + session.TotalOutputTokens)
                        }
                    };

                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(
                        JsonSerializer.Serialize(response, CoreJsonContext.Default.OpenAiChatCompletionResponse),
                        ctx.RequestAborted);
                }
            }
            finally
            {
                runtime.SessionManager.RemoveActive(session.Id);
            }
        });

        app.MapPost("/v1/responses", async (HttpContext ctx) =>
        {
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            OpenAiResponseRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync(
                    ctx.Request.Body,
                    CoreJsonContext.Default.OpenAiResponseRequest,
                    ctx.RequestAborted);
            }
            catch
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("Invalid JSON request body.", ctx.RequestAborted);
                return;
            }

            if (req is null || string.IsNullOrWhiteSpace(req.Input))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("Request must include an 'input' field.", ctx.RequestAborted);
                return;
            }

            var httpMwCtx = new MessageContext
            {
                ChannelId = "openai-http",
                SenderId = EndpointHelpers.GetHttpRateLimitKey(ctx, startup.Config),
                Text = req.Input,
                SessionInputTokens = 0,
                SessionOutputTokens = 0
            };
            var allow = await runtime.MiddlewarePipeline.ExecuteAsync(httpMwCtx, ctx.RequestAborted);
            if (!allow)
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await ctx.Response.WriteAsync(httpMwCtx.ShortCircuitResponse ?? "Request blocked.", ctx.RequestAborted);
                return;
            }

            var requestId = $"oai-resp:{Guid.NewGuid():N}";
            var session = await runtime.SessionManager.GetOrCreateAsync("openai-responses", requestId, ctx.RequestAborted);
            if (req.Model is not null)
                session.ModelOverride = req.Model;

            try
            {
                var result = await runtime.AgentRuntime.RunAsync(session, req.Input, ctx.RequestAborted);

                var responseId = $"resp-{Guid.NewGuid():N}"[..24];
                var msgId = $"msg-{Guid.NewGuid():N}"[..23];

                var response = new OpenAiResponseResponse
                {
                    Id = responseId,
                    Status = "completed",
                    Output =
                    [
                        new OpenAiResponseOutput
                        {
                            Id = msgId,
                            Role = "assistant",
                            Content = [new OpenAiResponseContent { Text = result }]
                        }
                    ],
                    Usage = new OpenAiUsage
                    {
                        PromptTokens = (int)session.TotalInputTokens,
                        CompletionTokens = (int)session.TotalOutputTokens,
                        TotalTokens = (int)(session.TotalInputTokens + session.TotalOutputTokens)
                    }
                };

                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(
                    JsonSerializer.Serialize(response, CoreJsonContext.Default.OpenAiResponseResponse),
                    ctx.RequestAborted);
            }
            finally
            {
                runtime.SessionManager.RemoveActive(session.Id);
            }
        });
    }
}
