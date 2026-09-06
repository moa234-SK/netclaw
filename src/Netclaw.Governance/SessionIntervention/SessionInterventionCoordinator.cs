// -----------------------------------------------------------------------
// <copyright file="SessionInterventionCoordinator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using AgentGovernance.Lifecycle;
using Akka.Actor;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Protocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Governance.SessionIntervention;

/// <summary>
/// Thrown when a requested transition is structurally invalid (for example
/// suspending a stopped session). The caller surfaces a conflict; no
/// durable write has happened.
/// </summary>
public sealed class SessionInterventionConflictException : InvalidOperationException
{
    public SessionInterventionConflictException(string message) : base(message)
    {
    }
}

/// <summary>Outcome of an accepted (or idempotent) intervention command.</summary>
public sealed record SessionInterventionOutcome(
    string SessionId,
    SessionInterventionState State,
    bool AlreadyInState);

/// <summary>
/// Single entry point for session intervention. Ordering contract
/// (design D3): validate the transition, write the durable state, then
/// command the actor, then record the AGT classification. A crash between
/// the durable write and the actuation leaves a gated session that cold
/// recovery respects — the safe direction.
/// </summary>
public sealed class SessionInterventionCoordinator
{
    private static readonly TimeSpan ActuationTimeout = TimeSpan.FromSeconds(30);

    private readonly SessionInterventionStore _store;
    private readonly AgentGovernanceRuntime _runtime;
    private readonly ILogger<SessionInterventionCoordinator> _logger;

    public SessionInterventionCoordinator(
        SessionInterventionStore store,
        AgentGovernanceRuntime runtime,
        ILogger<SessionInterventionCoordinator> logger)
    {
        _store = store;
        _runtime = runtime;
        _logger = logger;
    }

    public async Task<SessionInterventionOutcome> InterveneAsync(
        SessionId sessionId,
        SessionInterventionCommand command,
        string reason,
        string initiatedBy,
        IActorRef sessionManager,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentException.ThrowIfNullOrWhiteSpace(initiatedBy);

        var current = _store.GetInterventionState(sessionId);
        var target = TargetState(command);

        if (current == target)
        {
            // Idempotent: the session is already in the requested state. No
            // new ledger event, no actor command (spec: same-state commands
            // return the current state and write nothing).
            return new SessionInterventionOutcome(sessionId.Value, current, AlreadyInState: true);
        }

        if (!IsValidTransition(current, target))
            throw new SessionInterventionConflictException(
                $"Cannot {ToCommandText(command)} session '{sessionId.Value}': "
                + $"it is {ToStateText(current)}. Resume it first.");

        // Durable write FIRST: the actor gate and cold recovery both read
        // this state, so a crash after this line still leaves a gated
        // session rather than an ungated killed one.
        _store.Apply(sessionId, command, target, reason, initiatedBy);

        RecordGovernanceEvents(sessionId, command, target, reason, initiatedBy);

        // Actuation. The session-manager materializes the actor when
        // needed; the recovery gate makes that harmless (a stopped session
        // refuses resurrection, a gated one refuses work).
        var ack = await sessionManager.Ask<ISessionResponse>(
            BuildCommand(sessionId, command, reason, initiatedBy),
            ActuationTimeout,
            cancellationToken);

        switch (ack)
        {
            case CommandAck:
                return new SessionInterventionOutcome(sessionId.Value, target, AlreadyInState: false);
            case CommandNack nack:
                // The durable state was written before the ask, so the gate
                // stands even though the actor refused the command. Report
                // the refusal loudly — the operator must see it.
                _logger.LogError(
                    "Session actor refused intervention {Command} for {SessionId}: {NackReason}. The durable gate remains {State}.",
                    command, sessionId.Value, nack.Reason, target);
                throw new SessionInterventionConflictException(
                    $"Session actor refused {ToCommandText(command)}: {nack.Reason}. "
                    + $"The durable gate remains {ToStateText(target)}.");
            default:
                throw new InvalidOperationException(
                    $"Unexpected reply '{ack.GetType().Name}' from session manager for intervention {command}.");
        }
    }

    public IReadOnlyList<SessionInterventionEvent> ListEvents(SessionId sessionId)
        => _store.ListEvents(sessionId);

    internal static bool IsValidTransition(SessionInterventionState from, SessionInterventionState to) => from switch
    {
        SessionInterventionState.Active => to is SessionInterventionState.Suspended
            or SessionInterventionState.Stopped
            or SessionInterventionState.Quarantined,
        SessionInterventionState.Suspended => to == SessionInterventionState.Active,
        SessionInterventionState.Stopped => to == SessionInterventionState.Active,
        SessionInterventionState.Quarantined => to == SessionInterventionState.Active,
        _ => false
    };

    internal static SessionInterventionState TargetState(SessionInterventionCommand command) => command switch
    {
        SessionInterventionCommand.Stop => SessionInterventionState.Stopped,
        SessionInterventionCommand.Suspend => SessionInterventionState.Suspended,
        SessionInterventionCommand.Quarantine => SessionInterventionState.Quarantined,
        SessionInterventionCommand.Resume => SessionInterventionState.Active,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
    };

    private static IWithSessionId BuildCommand(
        SessionId sessionId,
        SessionInterventionCommand command,
        string reason,
        string initiatedBy) => command switch
    {
        SessionInterventionCommand.Stop => new StopSession(sessionId, reason, initiatedBy),
        SessionInterventionCommand.Suspend => new SuspendSession(sessionId, reason, initiatedBy),
        SessionInterventionCommand.Quarantine => new QuarantineSession(sessionId, reason, initiatedBy),
        SessionInterventionCommand.Resume => new ResumeSession(sessionId, reason, initiatedBy),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
    };

    private void RecordGovernanceEvents(
        SessionId sessionId,
        SessionInterventionCommand command,
        SessionInterventionState target,
        string reason,
        string initiatedBy)
    {
        if (command == SessionInterventionCommand.Stop)
        {
            // KillEvent has no initiator field — the initiator rides in
            // Detail; the ledger column is the authoritative copy.
            _runtime.RecordKill(sessionId.Value, reason, initiatedBy);
            _runtime.RecordTransition(sessionId.Value, LifecycleState.Decommissioned, reason, initiatedBy);
        }
        else
        {
            var lifecycleTarget = target switch
            {
                SessionInterventionState.Suspended => LifecycleState.Suspended,
                SessionInterventionState.Quarantined => LifecycleState.Quarantined,
                SessionInterventionState.Active => LifecycleState.Active,
                _ => throw new InvalidOperationException(
                    $"No lifecycle mapping for intervention state {target}.")
            };
            _runtime.RecordTransition(sessionId.Value, lifecycleTarget, reason, initiatedBy);
        }

        _runtime.Audit.Log(
            sessionId.Value,
            $"intervention:{ToCommandText(command)}",
            $"accepted:{ToStateText(target)}");
    }

    private static string ToCommandText(SessionInterventionCommand command) => command switch
    {
        SessionInterventionCommand.Stop => "stop",
        SessionInterventionCommand.Suspend => "suspend",
        SessionInterventionCommand.Quarantine => "quarantine",
        SessionInterventionCommand.Resume => "resume",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
    };

    private static string ToStateText(SessionInterventionState state) => state switch
    {
        SessionInterventionState.Active => "active",
        SessionInterventionState.Suspended => "suspended",
        SessionInterventionState.Stopped => "stopped",
        SessionInterventionState.Quarantined => "quarantined",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };
}
