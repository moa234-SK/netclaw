// -----------------------------------------------------------------------
// <copyright file="PolicySyncState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;

namespace Netclaw.Configuration;

/// <summary>
/// Persisted outcome of the last panel policy sync. <see cref="Version"/> is
/// the panel-published hash of the applied file set (null before the first
/// successful apply). <see cref="Error"/> carries the last rejection reason —
/// it is replayed to the panel on the next poll so the panel can surface it,
/// and clears only on a successful apply.
/// </summary>
public sealed record PolicySyncState(
    string? Version,
    DateTimeOffset? AppliedAtUtc,
    string? Error);

/// <summary>
/// Atomic reader/writer for <c>policy-sync-state.json</c> under the daemon
/// runtime directory. Written by the sync service after every apply attempt;
/// read on every poll to report what this daemon currently runs.
/// </summary>
public sealed class PolicySyncStateStore
{
    public const string FileName = "policy-sync-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _stateFilePath;

    public PolicySyncStateStore(NetclawPaths paths)
        : this(Path.Combine(paths.RuntimeDirectory, FileName))
    {
    }

    internal PolicySyncStateStore(string stateFilePath) => _stateFilePath = stateFilePath;

    public string StateFilePath => _stateFilePath;

    /// <summary>
    /// Reads the state record. A missing, empty, or corrupt file yields
    /// <c>null</c> — the daemon then reports "nothing applied yet" and the
    /// panel serves the full set on the next check. Never throws for a bad file.
    /// </summary>
    public PolicySyncState? TryRead()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
                return null;

            return JsonSerializer.Deserialize<PolicySyncState>(File.ReadAllText(_stateFilePath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Atomically replaces the state file (write temp, then move) so a
    /// concurrent reader never observes a half-written record.
    /// </summary>
    public void Write(PolicySyncState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var json = JsonSerializer.Serialize(state, JsonOptions);
        var tempPath = _stateFilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _stateFilePath, overwrite: true);
    }
}
