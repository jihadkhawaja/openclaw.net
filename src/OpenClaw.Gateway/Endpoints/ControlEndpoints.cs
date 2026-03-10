using System.Text;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class ControlEndpoints
{
    public static void MapOpenClawControlEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        app.MapPost("/pairing/approve", (HttpContext ctx, string channelId, string senderId, string code) =>
        {
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                return Results.Unauthorized();

            if (runtime.PairingManager.TryApprove(channelId, senderId, code, out var error))
                return Results.Ok(new { success = true, message = "Approved successfully." });

            if (error.Contains("Too many invalid attempts", StringComparison.Ordinal))
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = error },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            return Results.BadRequest(new { success = false, error });
        });

        app.MapPost("/pairing/revoke", (HttpContext ctx, string channelId, string senderId) =>
        {
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                return Results.Unauthorized();

            runtime.PairingManager.Revoke(channelId, senderId);
            return Results.Ok(new { success = true });
        });

        app.MapGet("/pairing/list", (HttpContext ctx) =>
        {
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                return Results.Unauthorized();

            return Results.Ok(runtime.PairingManager.GetApprovedList());
        });

        app.MapGet("/allowlists/{channelId}", (HttpContext ctx, string channelId) =>
        {
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                return Results.Unauthorized();

            var cfg = EndpointHelpers.GetConfigAllowlist(startup.Config, channelId);
            var dyn = runtime.Allowlists.TryGetDynamic(channelId);
            var effective = runtime.Allowlists.GetEffective(channelId, cfg);
            return Results.Ok(new
            {
                channelId,
                semantics = runtime.AllowlistSemantics.ToString().ToLowerInvariant(),
                config = cfg,
                dynamic = dyn,
                effective
            });
        });

        app.MapPost("/allowlists/{channelId}/add_latest", (HttpContext ctx, string channelId) =>
        {
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                return Results.Unauthorized();

            var latest = runtime.RecentSenders.TryGetLatest(channelId);
            if (latest is null)
                return Results.NotFound(new { success = false, error = "No recent sender found for that channel." });

            runtime.Allowlists.AddAllowedFrom(channelId, latest.SenderId);
            return Results.Ok(new { success = true, senderId = latest.SenderId });
        });

        app.MapPost("/allowlists/{channelId}/tighten", (HttpContext ctx, string channelId) =>
        {
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                return Results.Unauthorized();

            var paired = runtime.PairingManager.GetApprovedList()
                .Select(s =>
                {
                    var idx = s.IndexOf(':', StringComparison.Ordinal);
                    if (idx <= 0 || idx + 1 >= s.Length) return (Channel: "", Sender: "");
                    return (Channel: s[..idx], Sender: s[(idx + 1)..]);
                })
                .Where(t => string.Equals(t.Channel, channelId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(t.Sender))
                .Select(t => t.Sender)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (paired.Length == 0)
                return Results.BadRequest(new { success = false, error = "No paired senders found for that channel." });

            runtime.Allowlists.SetAllowedFrom(channelId, paired);
            return Results.Ok(new { success = true, count = paired.Length });
        });

        app.MapPost("/admin/reload-skills", async (HttpContext ctx) =>
        {
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                return Results.Unauthorized();

            var loadedSkillNames = await runtime.AgentRuntime.ReloadSkillsAsync(ctx.RequestAborted);
            return Results.Ok(new
            {
                reloaded = loadedSkillNames.Count,
                skills = loadedSkillNames
            });
        });

        app.MapPost("/tools/approve", (HttpContext ctx, string approvalId, bool approved, string? requesterChannelId, string? requesterSenderId) =>
        {
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(approvalId))
                return Results.BadRequest(new { success = false, error = "approvalId is required." });

            if (!startup.Config.Security.RequireRequesterMatchForHttpToolApproval)
            {
                var ok = runtime.ToolApprovalService.TrySetDecision(approvalId, approved);
                return ok
                    ? Results.Ok(new { success = true, mode = "admin_override" })
                    : Results.NotFound(new { success = false, error = "No pending approval found for that id." });
            }

            if (string.IsNullOrWhiteSpace(requesterChannelId) || string.IsNullOrWhiteSpace(requesterSenderId))
            {
                return Results.BadRequest(new
                {
                    success = false,
                    error = "requesterChannelId and requesterSenderId are required when RequireRequesterMatchForHttpToolApproval=true."
                });
            }

            var decisionResult = runtime.ToolApprovalService.TrySetDecision(
                approvalId,
                approved,
                requesterChannelId,
                requesterSenderId,
                requireRequesterMatch: true);

            return decisionResult switch
            {
                ToolApprovalDecisionResult.Recorded => Results.Ok(new { success = true, mode = "requester_match" }),
                ToolApprovalDecisionResult.Unauthorized => Results.Json(
                    new OperationStatusResponse
                    {
                        Success = false,
                        Error = "Requester does not match pending approval owner."
                    },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status403Forbidden),
                _ => Results.NotFound(new { success = false, error = "No pending approval found for that id." })
            };
        });
    }
}
