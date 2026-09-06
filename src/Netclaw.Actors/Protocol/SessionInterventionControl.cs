// -----------------------------------------------------------------------
// <copyright file="SessionInterventionControl.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Durable intervention state for a session. The SQLite store owned by
/// <c>Netclaw.Governance</c> is the authority; the session actor caches the
/// value and re-reads it at cold recovery.
/// </summary>
public enum SessionInterventionState
{
    /// <summary>No intervention recorded. The session accepts work.</summary>
    Active = 0,

    /// <summary>Operator suspend: actor stays resident, input rejected.</summary>
    Suspended = 1,

    /// <summary>Operator stop: actor terminated, stays dead until resume.</summary>
    Stopped = 2,

    /// <summary>Operator quarantine: actor resident, input and tools denied.</summary>
    Quarantined = 3,
}

/// <summary>
/// Operator command: abort the in-flight turn, passivate, and stop the
/// session actor. The session stays dead until an operator sends
/// <see cref="ResumeSession"/>.
/// </summary>
public sealed record StopSession(SessionId SessionId, string Reason, string InitiatedBy)
    : IWithSessionId, INoSerializationVerificationNeeded;

/// <summary>
/// Operator command: abort the in-flight turn and gate new input. The
/// session actor stays resident.
/// </summary>
public sealed record SuspendSession(SessionId SessionId, string Reason, string InitiatedBy)
    : IWithSessionId, INoSerializationVerificationNeeded;

/// <summary>
/// Operator command: abort the in-flight turn, gate new input, and deny
/// every tool call while gated. The session actor stays resident.
/// </summary>
public sealed record QuarantineSession(SessionId SessionId, string Reason, string InitiatedBy)
    : IWithSessionId, INoSerializationVerificationNeeded;

/// <summary>
/// Operator command: clear the intervention gate. A resident actor resumes
/// accepting work; a stopped session materializes normally on the next
/// message.
/// </summary>
public sealed record ResumeSession(SessionId SessionId, string Reason, string InitiatedBy)
    : IWithSessionId, INoSerializationVerificationNeeded;

/// <summary>
/// Read access to the durable intervention state. Implemented by the
/// SQLite-backed store in <c>Netclaw.Governance</c>; the interface lives here
/// so the session actor can consume it without a reverse project reference
/// (same pattern as <see cref="Channels.ISessionLifecycleObserver"/>).
/// </summary>
public interface ISessionInterventionStateReader
{
    /// <summary>
    /// Returns the durable intervention state. A session with no record is
    /// <see cref="SessionInterventionState.Active"/>. Throws when the store
    /// is unreadable or holds an unknown state value — never silently
    /// degrades to Active, which would bypass an operator gate.
    /// </summary>
    SessionInterventionState GetInterventionState(SessionId sessionId);
}

/// <summary>
/// Reader for harnesses that wire session actors without the governance
/// store. Semantically identical to an empty store: every session reads
/// Active. The daemon must register the SQLite-backed reader instead; the
/// DI wiring does.
/// </summary>
public sealed class ActiveSessionInterventionStateReader : ISessionInterventionStateReader
{
    public SessionInterventionState GetInterventionState(SessionId sessionId)
        => SessionInterventionState.Active;
}

/// <summary>User-visible rejection messages for gated sessions.</summary>
public static class SessionInterventionMessages
{
    public static string Blocked(SessionInterventionState state, string reason)
    {
        var label = state switch
        {
            SessionInterventionState.Suspended => "suspended",
            SessionInterventionState.Stopped => "stopped",
            SessionInterventionState.Quarantined => "quarantined",
            _ => "gated"
        };

        return $"This session was {label} by the operator"
            + (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason}")
            + " Ask the operator to resume it.";
    }
}
