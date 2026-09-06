// -----------------------------------------------------------------------
// <copyright file="AgentGovernanceRuntime.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using AgentGovernance.Audit;
using AgentGovernance.Hypervisor;
using AgentGovernance.Lifecycle;
using Microsoft.Extensions.Logging;

namespace Netclaw.Governance.SessionIntervention;

/// <summary>
/// Always-on Microsoft Agent Governance Toolkit runtime: one armed kill
/// switch, one hash-chained audit logger, and one lifecycle manager per
/// session (agentId = session id).
///
/// AGT types are in-memory classification only — nothing here survives a
/// restart and nothing here enforces anything. The SQLite intervention
/// ledger is the durable authority; this runtime records who decided what
/// for the in-process governance view.
/// </summary>
public sealed class AgentGovernanceRuntime
{
    private readonly ILogger<AgentGovernanceRuntime> _logger;
    private readonly ConcurrentDictionary<string, LifecycleManager> _lifecycle = new(StringComparer.Ordinal);

    public AgentGovernanceRuntime(ILogger<AgentGovernanceRuntime> logger)
    {
        _logger = logger;
        KillSwitch = new KillSwitch();
        KillSwitch.Arm();
        KillSwitch.OnKill += (_, evt) => _logger.LogWarning(
            "AGT kill event: agent={AgentId} reason={Reason} detail={Detail}",
            evt.AgentId, evt.Reason, evt.Detail);
        Audit = new AuditLogger();
    }

    /// <summary>The process-wide kill switch. Armed at construction; Netclaw never disarms it.</summary>
    public KillSwitch KillSwitch { get; }

    /// <summary>Hash-chained audit log for governance decisions (in-memory).</summary>
    public AuditLogger Audit { get; }

    /// <summary>
    /// Records an operator kill. The initiator travels in
    /// <see cref="KillEvent.Detail"/> because AGT's KillEvent has no
    /// initiator field; the durable ledger column is the authoritative copy.
    /// </summary>
    public KillEvent RecordKill(string sessionId, string reason, string initiatedBy)
        => KillSwitch.Kill(
            sessionId,
            KillReason.ManualOverride,
            $"initiatedBy={initiatedBy}; reason={reason}");

    /// <summary>
    /// Records a lifecycle transition on the session's manager. Advisory
    /// only: when AGT's state model cannot express the transition (a
    /// Netclaw resume re-materializes a stopped session, but AGT treats
    /// Decommissioned as terminal), the manager is re-provisioned fresh for
    /// the new incarnation and the skip is logged — the durable ledger
    /// already recorded the decision.
    /// </summary>
    public void RecordTransition(string sessionId, LifecycleState target, string reason, string initiatedBy)
    {
        var manager = LifecycleFor(sessionId);
        if (!manager.CanTransition(target))
        {
            _logger.LogInformation(
                "AGT lifecycle cannot express {Target} from {Current} for agent={AgentId}; re-provisioning manager (durable ledger remains authoritative)",
                target, manager.State, sessionId);
            var fresh = new LifecycleManager(sessionId);
            fresh.Activate("re-provisioned after operator resume");
            if (!fresh.CanTransition(target))
            {
                _logger.LogWarning(
                    "Skipping advisory AGT transition {Target} for agent={AgentId}: illegal even after re-provision",
                    target, sessionId);
                return;
            }

            _lifecycle[sessionId] = fresh;
            manager = fresh;
        }

        manager.Transition(target, reason, initiatedBy);
    }

    internal LifecycleManager LifecycleFor(string sessionId)
        => _lifecycle.GetOrAdd(sessionId, id =>
        {
            var manager = new LifecycleManager(id);
            // AGT managers start in Provisioning; a materialized Netclaw
            // session is Active by definition.
            if (manager.State == LifecycleState.Provisioning)
                manager.Activate("session materialized");
            return manager;
        });
}
