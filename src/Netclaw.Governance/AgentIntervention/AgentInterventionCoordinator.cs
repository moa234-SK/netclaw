// -----------------------------------------------------------------------
// <copyright file="AgentInterventionCoordinator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using AgentGovernance.Lifecycle;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Governance.SessionIntervention;

namespace Netclaw.Governance.AgentIntervention;

/// <summary>Outcome of an accepted (or idempotent) agent-level command.</summary>
public sealed record AgentInterventionOutcome(
    AgentInterventionState State,
    bool AlreadyInState);

/// <summary>
/// Single entry point for agent-level intervention (the operator freeze).
/// Ordering contract, mirroring the session coordinator: durable write
/// FIRST, then in-process actuation, then the advisory AGT classification.
/// A crash after the durable write leaves a suspended agent whose boot
/// check re-closes the ingress gate — the safe direction.
///
/// The freeze is soft: it gates all new session ingress (channels, session
/// creation, reminder delivery through gateway actors) but lets in-flight
/// turns finish and deliver. Sessions stay Active at the session level;
/// the agent-level gate is the only thing that changed.
/// </summary>
public sealed class AgentInterventionCoordinator
{
    private readonly AgentInterventionStore _store;
    private readonly AgentGovernanceRuntime _runtime;
    private readonly SessionIngressGate _ingressGate;
    private readonly string _agentId;
    private readonly ILogger<AgentInterventionCoordinator> _logger;

    public AgentInterventionCoordinator(
        AgentInterventionStore store,
        AgentGovernanceRuntime runtime,
        SessionIngressGate ingressGate,
        string agentId,
        ILogger<AgentInterventionCoordinator> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        _store = store;
        _runtime = runtime;
        _ingressGate = ingressGate;
        _agentId = agentId;
        _logger = logger;
    }

    /// <summary>The identity used for AGT classification — the registration instance id.</summary>
    public string AgentId => _agentId;

    public Task<AgentInterventionOutcome> SuspendAsync(
        string reason,
        string initiatedBy,
        CancellationToken cancellationToken = default)
        => ApplyAsync(AgentInterventionCommand.Suspend, AgentInterventionState.Suspended, reason, initiatedBy);

    public Task<AgentInterventionOutcome> ResumeAsync(
        string reason,
        string initiatedBy,
        CancellationToken cancellationToken = default)
        => ApplyAsync(AgentInterventionCommand.Resume, AgentInterventionState.Active, reason, initiatedBy);

    public AgentInterventionState GetState() => _store.GetState();

    public IReadOnlyList<AgentInterventionEvent> ListEvents() => _store.ListEvents();

    private Task<AgentInterventionOutcome> ApplyAsync(
        AgentInterventionCommand command,
        AgentInterventionState target,
        string reason,
        string initiatedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initiatedBy);

        var current = _store.GetState();
        if (current == target)
        {
            // Idempotent: same-state commands write nothing and command
            // nothing (same policy as session interventions).
            return Task.FromResult(new AgentInterventionOutcome(current, AlreadyInState: true));
        }

        // Durable write FIRST: the boot gate reads this state, so a crash
        // after this line still leaves a frozen (or unfrozen) agent that
        // cold start re-establishes.
        _store.Apply(command, target, reason, initiatedBy);

        Actuate(command);
        RecordGovernanceEvents(command, target, reason, initiatedBy);

        return Task.FromResult(new AgentInterventionOutcome(target, AlreadyInState: false));
    }

    private void Actuate(AgentInterventionCommand command)
    {
        switch (command)
        {
            case AgentInterventionCommand.Suspend:
                if (!_ingressGate.TryClose(SessionIngressGate.AgentSuspendedMessage))
                {
                    // The gate is already closed by a restart drain or by
                    // shutdown. The durable state stands and the boot check
                    // re-closes the gate after any restart — report loudly.
                    _logger.LogWarning(
                        "Agent suspend: the ingress gate was already closed ('{ClosedReason}'). "
                        + "The durable suspended state stands; the boot check re-freezes after any restart.",
                        _ingressGate.ClosedReason);
                }
                break;

            case AgentInterventionCommand.Resume:
                // If a shutdown or restart drain holds the gate, this reopen
                // is transient — the process recycles and the boot check
                // re-establishes the (now active) durable truth.
                _ingressGate.Reopen();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }
    }

    private void RecordGovernanceEvents(
        AgentInterventionCommand command,
        AgentInterventionState target,
        string reason,
        string initiatedBy)
    {
        _runtime.RecordTransition(
            _agentId,
            target == AgentInterventionState.Suspended ? LifecycleState.Suspended : LifecycleState.Active,
            reason,
            initiatedBy);

        _runtime.Audit.Log(
            _agentId,
            command == AgentInterventionCommand.Suspend ? "intervention:agent-suspend" : "intervention:agent-resume",
            $"accepted:{(target == AgentInterventionState.Suspended ? "suspended" : "active")}");
    }
}
