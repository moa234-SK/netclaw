// -----------------------------------------------------------------------
// <copyright file="AgentInterventionEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Netclaw.Governance.AgentIntervention;

namespace Netclaw.Daemon;

public static class AgentInterventionEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapAgentInterventionEndpoints(this IEndpointRouteBuilder app)
    {
        var agent = app.MapGroup("/api/agent")
            .WithTags("Agent")
            .RequireAuthorization();

        agent.MapPost("/suspend",
                async Task<Ok<AgentInterventionResponse>> (
                    AgentInterventionRequest request,
                    AgentInterventionCoordinator coordinator,
                    ClaimsPrincipal principal) =>
            {
                var outcome = await coordinator.SuspendAsync(
                    request?.Reason ?? string.Empty,
                    SessionInterventionEndpointRouteBuilderExtensions.ResolveInitiator(principal));
                return TypedResults.Ok(ToResponse(outcome));
            })
            .WithName("SuspendAgent")
            .WithSummary("Freeze the whole agent: all new session ingress is rejected until resume. In-flight turns finish (Operator).");

        agent.MapPost("/resume",
                async Task<Ok<AgentInterventionResponse>> (
                    AgentInterventionRequest request,
                    AgentInterventionCoordinator coordinator,
                    ClaimsPrincipal principal) =>
            {
                var outcome = await coordinator.ResumeAsync(
                    request?.Reason ?? string.Empty,
                    SessionInterventionEndpointRouteBuilderExtensions.ResolveInitiator(principal));
                return TypedResults.Ok(ToResponse(outcome));
            })
            .WithName("ResumeAgent")
            .WithSummary("Unfreeze the agent: session ingress reopens (Operator).");

        agent.MapGet("/interventions",
                Ok<IReadOnlyList<AgentInterventionEventDto>> (AgentInterventionCoordinator coordinator) =>
            {
                var events = coordinator.ListEvents();
                return TypedResults.Ok<IReadOnlyList<AgentInterventionEventDto>>(
                    events.Select(ToEventDto).ToList());
            })
            .WithName("ListAgentInterventions")
            .WithSummary("List the agent-level intervention history (Operator).");

        return app;
    }

    private static AgentInterventionResponse ToResponse(AgentInterventionOutcome outcome)
        => new(
            outcome.State.ToString().ToLowerInvariant(),
            outcome.AlreadyInState ? "already-in-state" : "applied");

    private static AgentInterventionEventDto ToEventDto(AgentInterventionEvent evt)
        => new(
            evt.Sequence,
            evt.Command.ToString().ToLowerInvariant(),
            evt.StateAfter.ToString().ToLowerInvariant(),
            evt.Reason,
            evt.InitiatedBy,
            evt.RecordedAtUnixMs);

    public sealed record AgentInterventionRequest(string? Reason);

    public sealed record AgentInterventionResponse(string State, string Status);

    public sealed record AgentInterventionEventDto(
        long Sequence,
        string Command,
        string StateAfter,
        string Reason,
        string InitiatedBy,
        long RecordedAtUnixMs);
}
