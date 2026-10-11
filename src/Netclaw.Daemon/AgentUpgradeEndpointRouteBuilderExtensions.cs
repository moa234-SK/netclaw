// -----------------------------------------------------------------------
// <copyright file="AgentUpgradeEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Netclaw.Daemon.Services;

namespace Netclaw.Daemon;

public static class AgentUpgradeEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapAgentUpgradeEndpoints(this IEndpointRouteBuilder app)
    {
        var agent = app.MapGroup("/api/agent")
            .WithTags("Agent")
            .RequireAuthorization();

        agent.MapPost("/upgrade",
                async Task<Results<Ok<AgentUpgradeResponse>, Conflict<AgentUpgradeErrorResponse>>> (
                    AgentUpgradeRequest? request,
                    AgentUpgradeService upgradeService,
                    CancellationToken cancellationToken) =>
            {
                var outcome = await upgradeService.StartAsync(cancellationToken);
                if (outcome.BlockedReason is { } reason)
                    return TypedResults.Conflict(new AgentUpgradeErrorResponse(reason));

                return TypedResults.Ok(outcome.UpToDate
                    ? new AgentUpgradeResponse("up-to-date", null, null)
                    : new AgentUpgradeResponse("upgrade-started", null, outcome.TargetVersion));
            })
            .WithName("UpgradeAgent")
            .WithSummary("Download and install the latest netclaw release, then restart the daemon onto it (Operator).");

        agent.MapGet("/upgrade",
                Ok<AgentUpgradeStatusResponse> (AgentUpgradeState upgradeState) =>
            {
                var snapshot = upgradeState.Current;
                return TypedResults.Ok(new AgentUpgradeStatusResponse(
                    snapshot.State,
                    snapshot.CurrentVersion,
                    snapshot.TargetVersion,
                    snapshot.BytesDownloaded,
                    snapshot.BytesTotal,
                    snapshot.Error,
                    snapshot.StartedAtUtc));
            })
            .WithName("GetAgentUpgradeStatus")
            .WithSummary("Current self-update progress — idle, downloading, applying, restarting, succeeded, or failed.");

        return app;
    }

    public sealed record AgentUpgradeRequest;

    public sealed record AgentUpgradeResponse(string Status, string? Message, string? TargetVersion);

    public sealed record AgentUpgradeErrorResponse(string Error);

    public sealed record AgentUpgradeStatusResponse(
        string State,
        string? CurrentVersion,
        string? TargetVersion,
        long BytesDownloaded,
        long BytesTotal,
        string? Error,
        DateTimeOffset? StartedAtUtc);
}
