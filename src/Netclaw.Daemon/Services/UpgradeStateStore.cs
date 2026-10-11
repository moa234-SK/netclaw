// -----------------------------------------------------------------------
// <copyright file="UpgradeStateStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Persisted self-update progress. Written by <see cref="AgentUpgradeService"/>
/// at each phase transition so the post-restart daemon (running the new binary)
/// can reconcile the outcome — the predecessor cannot know whether its restart
/// succeeded because it exits as part of it.
/// </summary>
public sealed record PersistedUpgradeState
{
    /// <summary>Phase at the time of the last write: downloading, applying, restarting, or failed.</summary>
    public required string Phase { get; init; }

    public required string FromVersion { get; init; }
    public required string TargetVersion { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public long BytesTotal { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Persists <see cref="PersistedUpgradeState"/> to
/// <see cref="NetclawPaths.UpgradeStatePath"/> across the update restart,
/// mirroring the <see cref="RestartManifestStore"/> write/read/delete lifecycle.
/// </summary>
public sealed class UpgradeStateStore(NetclawPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task WriteAsync(PersistedUpgradeState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        paths.EnsureDirectoriesExist();
        await using var stream = File.Create(paths.UpgradeStatePath);
        await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
    }

    public async Task<PersistedUpgradeState?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.UpgradeStatePath))
            return null;
        await using var stream = File.OpenRead(paths.UpgradeStatePath);
        return await JsonSerializer.DeserializeAsync<PersistedUpgradeState>(stream, JsonOptions, cancellationToken);
    }

    public Task DeleteAsync()
    {
        if (File.Exists(paths.UpgradeStatePath))
            File.Delete(paths.UpgradeStatePath);
        return Task.CompletedTask;
    }
}
