// -----------------------------------------------------------------------
// <copyright file="SessionInterventionEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Claims;
using Akka.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Governance.SessionIntervention;

namespace Netclaw.Daemon;

public static class SessionInterventionEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapSessionInterventionEndpoints(this IEndpointRouteBuilder app)
    {
        // Session ids contain '/' ({channelId}/{threadTs}), and ASP.NET Core
        // forbids a catch-all followed by more segments — so each verb mounts
        // ONE catch-all whose trailing segment names the command
        // (/api/sessions/<id>/stop). Encoded separators (%2F) are decoded by
        // the server before routing, so both id forms arrive here intact.
        var sessions = app.MapGroup("/api/sessions")
            .WithTags("Sessions")
            .RequireAuthorization();

        sessions.MapPost("/{**target}",
                async Task<Results<Ok<InterventionResponse>, ValidationProblem, Conflict<ProblemDetails>, NotFound>> (
                    string target,
                    InterventionRequest request,
                    SessionInterventionCoordinator coordinator,
                    IRequiredActor<SessionManagerActorKey> actor,
                    ClaimsPrincipal principal,
                    CancellationToken ct) =>
            {
                if (!TrySplitTarget(target, out var sessionId, out var command))
                {
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["sessionId"] = ["The path must be /api/sessions/<sessionId>/<command>."]
                    });
                }

                return command switch
                {
                    "stop" => await InterveneAsync(sessionId, SessionInterventionCommand.Stop,
                        request, coordinator, actor, principal, ct),
                    "suspend" => await InterveneAsync(sessionId, SessionInterventionCommand.Suspend,
                        request, coordinator, actor, principal, ct),
                    "quarantine" => await InterveneAsync(sessionId, SessionInterventionCommand.Quarantine,
                        request, coordinator, actor, principal, ct),
                    "resume" => await InterveneAsync(sessionId, SessionInterventionCommand.Resume,
                        request, coordinator, actor, principal, ct),
                    _ => TypedResults.NotFound()
                };
            })
            .WithName("SessionIntervention")
            .WithSummary("Intervene on one session: /api/sessions/<sessionId>/stop|suspend|quarantine|resume (Operator).");

        sessions.MapGet("/{**target}",
                Results<Ok<IReadOnlyList<InterventionEventDto>>, ValidationProblem, NotFound> (string target) =>
            {
                if (!TrySplitTarget(target, out var sessionId, out var command) || command != "interventions")
                    return TypedResults.NotFound();

                var coordinator = app.ServiceProvider.GetRequiredService<SessionInterventionCoordinator>();
                var events = coordinator.ListEvents(new SessionId(sessionId));
                return TypedResults.Ok<IReadOnlyList<InterventionEventDto>>(
                    events.Select(ToInterventionEventDto).ToList());
            })
            .WithName("ListSessionInterventions")
            .WithSummary("List the intervention history for one session: /api/sessions/<sessionId>/interventions (Operator).");

        return app;
    }

    private static bool TrySplitTarget(string target, out string sessionId, out string command)
    {
        sessionId = string.Empty;
        command = string.Empty;

        // The path was server-decoded, so separators may arrive raw or as
        // %2F; unescape once more to normalize proxies that re-encode.
        var normalized = Uri.UnescapeDataString(target);
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash <= 0 || lastSlash == normalized.Length - 1)
            return false;

        sessionId = normalized[..lastSlash];
        command = normalized[(lastSlash + 1)..];
        return sessionId.Length > 0;
    }

    private static async Task<Results<Ok<InterventionResponse>, ValidationProblem, Conflict<ProblemDetails>, NotFound>> InterveneAsync(
        string sessionId,
        SessionInterventionCommand command,
        InterventionRequest request,
        SessionInterventionCoordinator coordinator,
        IRequiredActor<SessionManagerActorKey> actor,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["sessionId"] = ["The session id is empty."]
            });
        }

        var initiatedBy = ResolveInitiator(principal);
        var sessionManager = await actor.GetAsync(ct);

        try
        {
            var outcome = await coordinator.InterveneAsync(
                new SessionId(sessionId),
                command,
                request?.Reason ?? string.Empty,
                initiatedBy,
                sessionManager,
                ct);

            return TypedResults.Ok(new InterventionResponse(
                outcome.SessionId,
                outcome.State.ToString().ToLowerInvariant(),
                outcome.AlreadyInState ? "already-in-state" : "applied"));
        }
        catch (SessionInterventionConflictException ex)
        {
            return TypedResults.Conflict(new ProblemDetails
            {
                Title = "Intervention rejected",
                Detail = ex.Message
            });
        }
    }

    private static string ResolveInitiator(ClaimsPrincipal principal)
    {
        // The paired device id (or "local" for loopback) identifies the
        // operator surface; the ledger records it on every intervention.
        var deviceId = principal.FindFirst(NetclawClaimTypes.DeviceId)?.Value;
        if (!string.IsNullOrWhiteSpace(deviceId))
            return deviceId;

        return principal.Identity?.Name is { Length: > 0 } name ? name : "operator";
    }

    private static InterventionEventDto ToInterventionEventDto(SessionInterventionEvent evt)
        => new(
            evt.Sequence,
            evt.SessionId,
            evt.Command.ToString().ToLowerInvariant(),
            evt.StateAfter.ToString().ToLowerInvariant(),
            evt.Reason,
            evt.InitiatedBy,
            evt.RecordedAtUnixMs);

    public sealed record InterventionRequest(string? Reason);

    public sealed record InterventionResponse(
        string SessionId,
        string State,
        string Status);

    public sealed record InterventionEventDto(
        long Sequence,
        string SessionId,
        string Command,
        string StateAfter,
        string Reason,
        string InitiatedBy,
        long RecordedAtUnixMs);
}
