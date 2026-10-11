// -----------------------------------------------------------------------
// <copyright file="SessionInterventionStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Governance.SessionIntervention;

/// <summary>The intervention commands recordable in the ledger.</summary>
public enum SessionInterventionCommand
{
    Stop = 0,
    Suspend = 1,
    Quarantine = 2,
    Resume = 3
}

/// <summary>One append-only ledger event, in recorded order.</summary>
public sealed record SessionInterventionEvent(
    long Sequence,
    string SessionId,
    SessionInterventionCommand Command,
    SessionInterventionState StateAfter,
    string Reason,
    string InitiatedBy,
    long RecordedAtUnixMs);

/// <summary>
/// Durable authority for session intervention state in the shared SQLite
/// database (<c>netclaw.db</c>), following the <c>SessionCatalogService</c>
/// conventions: open-per-operation connections and idempotent
/// <c>CREATE TABLE IF NOT EXISTS</c> DDL — no migration file.
///
/// Unlike the catalog, failures here propagate instead of logging and
/// continuing: an intervention that cannot be recorded durably must not be
/// actuated (fail-closed), and an unreadable gate must not read as Active.
/// </summary>
public sealed class SessionInterventionStore : ISessionInterventionStateReader
{
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionInterventionStore> _logger;

    public SessionInterventionStore(
        NetclawPaths paths,
        TimeProvider timeProvider,
        ILogger<SessionInterventionStore> logger)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public SessionInterventionState GetInterventionState(SessionId sessionId)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT state FROM session_interventions WHERE session_id = $id";
        cmd.Parameters.AddWithValue("$id", sessionId.Value);

        var result = cmd.ExecuteScalar();
        if (result is null)
            return SessionInterventionState.Active;

        return result.ToString() switch
        {
            "active" => SessionInterventionState.Active,
            "suspended" => SessionInterventionState.Suspended,
            "stopped" => SessionInterventionState.Stopped,
            "quarantined" => SessionInterventionState.Quarantined,
            var unknown => throw new InvalidOperationException(
                $"Session '{sessionId.Value}' has an unknown durable intervention state '{unknown}'. "
                + "Refusing to guess: an unreadable gate must not read as Active.")
        };
    }

    /// <summary>
    /// Writes the new state and appends one ledger event in a single
    /// transaction. The caller must have validated the transition — this
    /// method is the write, not the policy.
    /// </summary>
    public void Apply(
        SessionId sessionId,
        SessionInterventionCommand command,
        SessionInterventionState newState,
        string reason,
        string initiatedBy)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        EnsureSchema(conn);

        using var tx = conn.BeginTransaction();

        using (var upsert = conn.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText =
                """
                INSERT INTO session_interventions (session_id, state, reason, initiated_by, updated_at_unix_ms)
                VALUES ($id, $state, $reason, $by, $now)
                ON CONFLICT(session_id) DO UPDATE SET
                    state = $state,
                    reason = $reason,
                    initiated_by = $by,
                    updated_at_unix_ms = $now
                """;
            upsert.Parameters.AddWithValue("$id", sessionId.Value);
            upsert.Parameters.AddWithValue("$state", ToStateText(newState));
            upsert.Parameters.AddWithValue("$reason", reason);
            upsert.Parameters.AddWithValue("$by", initiatedBy);
            upsert.Parameters.AddWithValue("$now", nowMs);
            upsert.ExecuteNonQuery();
        }

        using (var evt = conn.CreateCommand())
        {
            evt.Transaction = tx;
            evt.CommandText =
                """
                INSERT INTO session_intervention_events
                    (session_id, command, state_after, reason, initiated_by, recorded_at_unix_ms)
                VALUES ($id, $command, $state, $reason, $by, $now)
                """;
            evt.Parameters.AddWithValue("$id", sessionId.Value);
            evt.Parameters.AddWithValue("$command", ToCommandText(command));
            evt.Parameters.AddWithValue("$state", ToStateText(newState));
            evt.Parameters.AddWithValue("$reason", reason);
            evt.Parameters.AddWithValue("$by", initiatedBy);
            evt.Parameters.AddWithValue("$now", nowMs);
            evt.ExecuteNonQuery();
        }

        tx.Commit();
        _logger.LogInformation(
            "Session intervention recorded: session={SessionId} command={Command} state={State} initiatedBy={InitiatedBy}",
            sessionId.Value, command, newState, initiatedBy);
    }

    /// <summary>Returns the ledger events for one session, oldest first.</summary>
    public IReadOnlyList<SessionInterventionEvent> ListEvents(SessionId sessionId)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT seq, session_id, command, state_after, reason, initiated_by, recorded_at_unix_ms
            FROM session_intervention_events
            WHERE session_id = $id
            ORDER BY seq
            """;
        cmd.Parameters.AddWithValue("$id", sessionId.Value);

        var events = new List<SessionInterventionEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            events.Add(new SessionInterventionEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                FromCommandText(reader.GetString(2)),
                FromStateText(reader.GetString(3), sessionId.Value),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6)));
        }

        return events;
    }

    private static string ToStateText(SessionInterventionState state) => state switch
    {
        SessionInterventionState.Active => "active",
        SessionInterventionState.Suspended => "suspended",
        SessionInterventionState.Stopped => "stopped",
        SessionInterventionState.Quarantined => "quarantined",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    private static SessionInterventionState FromStateText(string text, string sessionId) => text switch
    {
        "active" => SessionInterventionState.Active,
        "suspended" => SessionInterventionState.Suspended,
        "stopped" => SessionInterventionState.Stopped,
        "quarantined" => SessionInterventionState.Quarantined,
        _ => throw new InvalidOperationException(
            $"Session '{sessionId}' has an unknown intervention state '{text}' in the ledger.")
    };

    private static string ToCommandText(SessionInterventionCommand command) => command switch
    {
        SessionInterventionCommand.Stop => "stop",
        SessionInterventionCommand.Suspend => "suspend",
        SessionInterventionCommand.Quarantine => "quarantine",
        SessionInterventionCommand.Resume => "resume",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
    };

    private static SessionInterventionCommand FromCommandText(string text) => text switch
    {
        "stop" => SessionInterventionCommand.Stop,
        "suspend" => SessionInterventionCommand.Suspend,
        "quarantine" => SessionInterventionCommand.Quarantine,
        "resume" => SessionInterventionCommand.Resume,
        _ => throw new InvalidOperationException(
            $"Unknown intervention command '{text}' in the ledger.")
    };

    public static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS session_interventions (
                session_id          TEXT NOT NULL PRIMARY KEY,
                state               TEXT NOT NULL,
                reason              TEXT NOT NULL,
                initiated_by        TEXT NOT NULL,
                updated_at_unix_ms  INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS session_intervention_events (
                seq                   INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id            TEXT NOT NULL,
                command               TEXT NOT NULL,
                state_after           TEXT NOT NULL,
                reason                TEXT NOT NULL,
                initiated_by          TEXT NOT NULL,
                recorded_at_unix_ms   INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_session_intervention_events_session
                ON session_intervention_events (session_id, seq);
            """;
        cmd.ExecuteNonQuery();
    }
}
