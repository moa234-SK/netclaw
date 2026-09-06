// -----------------------------------------------------------------------
// <copyright file="RegistrationState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Registration;

/// <summary>
/// Persisted outcome of the last registration send. Written by the daemon
/// after every send; read by <c>netclaw doctor</c>, which runs in a separate
/// process and cannot see in-memory state. <see cref="ErrorDetail"/> is
/// sanitized: it never contains the configured registration URL.
/// </summary>
public sealed record RegistrationState(
    DateTimeOffset? LastRegisteredUtc,
    string State,
    string? ErrorDetail,
    string? InstanceId = null)
{
    public const string StateRegistered = "registered";
    public const string StateFailed = "failed";
}

/// <summary>
/// Atomic reader/writer for <c>registration-state.json</c> under the daemon
/// runtime directory. One shape, two consumers: the daemon service writes
/// it and the CLI doctor check reads it.
/// </summary>
public sealed class RegistrationStateStore
{
    public const string FileName = "registration-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _stateFilePath;

    public RegistrationStateStore(NetclawPaths paths)
        : this(Path.Combine(paths.RuntimeDirectory, FileName))
    {
    }

    internal RegistrationStateStore(string stateFilePath) => _stateFilePath = stateFilePath;

    public string StateFilePath => _stateFilePath;

    /// <summary>
    /// Reads the state record. A missing, empty, or corrupt file yields
    /// <c>null</c> — doctor reports "not registered yet" and the daemon
    /// overwrites the file on its next send. Never throws for a bad file.
    /// </summary>
    public RegistrationState? TryRead()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
                return null;

            return JsonSerializer.Deserialize<RegistrationState>(File.ReadAllText(_stateFilePath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Atomically replaces the state file (write temp, then move) so a
    /// concurrent doctor read never observes a half-written record.
    /// </summary>
    public void Write(RegistrationState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var json = JsonSerializer.Serialize(state, JsonOptions);
        var tempPath = _stateFilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _stateFilePath, overwrite: true);
    }
}
