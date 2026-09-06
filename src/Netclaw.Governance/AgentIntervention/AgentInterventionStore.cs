// -----------------------------------------------------------------------
// <copyright file="AgentInterventionStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Governance.AgentIntervention;

/// <summary>The agent-level intervention commands recordable in the ledger.</summary>
public enum AgentInterventionCommand
{
    Suspend = 0,
    Resume = 1
}

/// <summary>The durable governance state of the agent as a whole.</summary>
public enum AgentInterventionState
{
    Active = 0,
    Suspended = 1
}

/// <summary>Read-only view over the durable agent state for status surfaces.</summary>
public interface IAgentInterventionStateReader
{
    AgentInterventionState GetState();
}

/// <summary>One append-only agent-level ledger event, in recorded order.</summary>
public sealed record AgentInterventionEvent(
    long Sequence,
    AgentInterventionCommand Command,
    AgentInterventionState StateAfter,
    string Reason,
    string InitiatedBy,
    long RecordedAtUnixMs);

/// <summary>
/// Durable authority for agent-level intervention state in the shared
/// SQLite database (<c>netclaw.db</c>), following the
/// <c>SessionInterventionStore</c> conventions: open-per-operation
/// connections, idempotent DDL, and fail-closed reads — an unreadable
/// state must never read as Active.
/// </summary>
public sealed class AgentInterventionStore : IAgentInterventionStateReader
{
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentInterventionStore> _logger;

    public AgentInterventionStore(
        NetclawPaths paths,
        TimeProvider timeProvider,
        ILogger<AgentInterventionStore> logger)
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
    public AgentInterventionState GetState()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT state FROM agent_intervention WHERE id = 1";

        var result = cmd.ExecuteScalar();
        if (result is null)
            return AgentInterventionState.Active;

        return FromStateText(result.ToString()!);
    }

    /// <summary>
    /// Writes the new agent state and appends one ledger event in a single
    /// transaction. The caller validates the transition — this method is
    /// the write, not the policy.
    /// </summary>
    public void Apply(
        AgentInterventionCommand command,
        AgentInterventionState newState,
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
                INSERT INTO agent_intervention (id, state, reason, initiated_by, updated_at_unix_ms)
                VALUES (1, $state, $reason, $by, $now)
                ON CONFLICT(id) DO UPDATE SET
                    state = $state,
                    reason = $reason,
                    initiated_by = $by,
                    updated_at_unix_ms = $now
                """;
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
                INSERT INTO agent_intervention_events
                    (command, state_after, reason, initiated_by, recorded_at_unix_ms)
                VALUES ($command, $state, $reason, $by, $now)
                """;
            evt.Parameters.AddWithValue("$command", ToCommandText(command));
            evt.Parameters.AddWithValue("$state", ToStateText(newState));
            evt.Parameters.AddWithValue("$reason", reason);
            evt.Parameters.AddWithValue("$by", initiatedBy);
            evt.Parameters.AddWithValue("$now", nowMs);
            evt.ExecuteNonQuery();
        }

        tx.Commit();
        _logger.LogInformation(
            "Agent intervention recorded: command={Command} state={State} initiatedBy={InitiatedBy}",
            command, newState, initiatedBy);
    }

    /// <summary>Returns the agent-level ledger events, oldest first.</summary>
    public IReadOnlyList<AgentInterventionEvent> ListEvents()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT seq, command, state_after, reason, initiated_by, recorded_at_unix_ms
            FROM agent_intervention_events
            ORDER BY seq
            """;

        var events = new List<AgentInterventionEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            events.Add(new AgentInterventionEvent(
                reader.GetInt64(0),
                FromCommandText(reader.GetString(1)),
                FromStateText(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5)));
        }

        return events;
    }

    private static string ToStateText(AgentInterventionState state) => state switch
    {
        AgentInterventionState.Active => "active",
        AgentInterventionState.Suspended => "suspended",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    private static AgentInterventionState FromStateText(string text) => text switch
    {
        "active" => AgentInterventionState.Active,
        "suspended" => AgentInterventionState.Suspended,
        var unknown => throw new InvalidOperationException(
            $"The agent has an unknown durable intervention state '{unknown}'. "
            + "Refusing to guess: an unreadable gate must not read as Active.")
    };

    private static string ToCommandText(AgentInterventionCommand command) => command switch
    {
        AgentInterventionCommand.Suspend => "suspend",
        AgentInterventionCommand.Resume => "resume",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
    };

    private static AgentInterventionCommand FromCommandText(string text) => text switch
    {
        "suspend" => AgentInterventionCommand.Suspend,
        "resume" => AgentInterventionCommand.Resume,
        _ => throw new InvalidOperationException(
            $"Unknown agent intervention command '{text}' in the ledger.")
    };

    private void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS agent_intervention (
                id                  INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                state               TEXT NOT NULL,
                reason              TEXT NOT NULL,
                initiated_by        TEXT NOT NULL,
                updated_at_unix_ms  INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS agent_intervention_events (
                seq                   INTEGER PRIMARY KEY AUTOINCREMENT,
                command               TEXT NOT NULL,
                state_after           TEXT NOT NULL,
                reason                TEXT NOT NULL,
                initiated_by          TEXT NOT NULL,
                recorded_at_unix_ms   INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }
}
