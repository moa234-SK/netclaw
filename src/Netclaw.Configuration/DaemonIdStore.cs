// -----------------------------------------------------------------------
// <copyright file="DaemonIdStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;

namespace Netclaw.Configuration;

/// <summary>
/// The daemon identity. The daemon mints <see cref="DaemonId"/> once and
/// persists it, so the id survives restarts. Registration envelopes, AGT
/// governance classification, and operational webhook payloads consume the
/// same id. All three then agree on one daemon identity, even when several
/// daemons share a host.
/// </summary>
public sealed record DaemonIdentity(string DaemonId, DateTimeOffset CreatedUtc);

/// <summary>
/// Atomic reader and writer for <c>daemon-id.json</c> under the daemon
/// runtime directory. The daemon resolves its identity once at startup
/// through <see cref="GetOrCreate"/>. A missing file yields a minted id.
/// A corrupt or blank record is not a valid identity; the store mints a
/// replacement and overwrites the file. The write failure path stays loud:
/// an unreadable runtime directory throws, because a daemon without a
/// stable identity would silently break panel dedupe.
/// </summary>
public sealed class DaemonIdStore
{
    public const string FileName = "daemon-id.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _idFilePath;
    private readonly TimeProvider _timeProvider;

    public DaemonIdStore(NetclawPaths paths, TimeProvider timeProvider)
        : this(Path.Combine(paths.RuntimeDirectory, FileName), timeProvider)
    {
    }

    internal DaemonIdStore(string idFilePath, TimeProvider timeProvider)
    {
        _idFilePath = idFilePath;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns the persisted identity. The store mints and persists a new
    /// identity when the file holds none. The minted format matches the
    /// historical fallback id format, so old and new ids stay
    /// indistinguishable on the wire.
    /// </summary>
    public DaemonIdentity GetOrCreate()
    {
        var existing = TryRead();
        if (existing is not null)
            return existing;

        var minted = new DaemonIdentity(
            $"netclaw-{Guid.NewGuid().ToString("N")[..12]}",
            _timeProvider.GetUtcNow());
        Write(minted);
        return minted;
    }

    /// <summary>
    /// Reads the identity file. A missing, corrupt, or blank record yields
    /// <c>null</c>; the caller then mints a replacement.
    /// </summary>
    public DaemonIdentity? TryRead()
    {
        try
        {
            if (!File.Exists(_idFilePath))
                return null;

            var identity = JsonSerializer.Deserialize<DaemonIdentity>(
                File.ReadAllText(_idFilePath), JsonOptions);
            return identity is { DaemonId.Length: > 0 } ? identity : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Atomically replaces the identity file (write temp, then move) so a
    /// concurrent reader never observes a half-written record.
    /// </summary>
    public void Write(DaemonIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var json = JsonSerializer.Serialize(identity, JsonOptions);
        var tempPath = _idFilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _idFilePath, overwrite: true);
    }
}
