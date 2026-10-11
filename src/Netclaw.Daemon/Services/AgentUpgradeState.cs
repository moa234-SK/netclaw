// -----------------------------------------------------------------------
// <copyright file="AgentUpgradeState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Daemon.Services;

/// <summary>
/// Upgrade progress states surfaced via <c>GET /api/agent/upgrade</c> and the
/// status API's update section.
/// </summary>
public static class AgentUpgradeStates
{
    public const string Idle = "idle";
    public const string Downloading = "downloading";
    public const string Applying = "applying";
    public const string Restarting = "restarting";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>
/// Immutable snapshot of self-update progress, safe to publish across threads.
/// </summary>
public sealed record AgentUpgradeSnapshot(
    string State,
    string? CurrentVersion,
    string? TargetVersion,
    long BytesDownloaded,
    long BytesTotal,
    string? Error,
    DateTimeOffset? StartedAtUtc)
{
    public static AgentUpgradeSnapshot None { get; } =
        new(AgentUpgradeStates.Idle, null, null, 0, 0, null, null);

    public bool IsActive =>
        State is AgentUpgradeStates.Downloading or AgentUpgradeStates.Applying or AgentUpgradeStates.Restarting;
}

/// <summary>
/// Process-wide upgrade progress hub. Shared by the (per-request resolved)
/// <see cref="AgentUpgradeService"/>, the startup-time <see cref="UpgradeRecoveryService"/>,
/// and the status/upgrade endpoints. Also owns the single-flight gate so two
/// concurrent upgrade requests can never interleave downloads or swaps.
/// </summary>
public sealed class AgentUpgradeState
{
    private AgentUpgradeSnapshot _snapshot = AgentUpgradeSnapshot.None;
    private int _inFlight;

    public AgentUpgradeSnapshot Current => Volatile.Read(ref _snapshot);

    /// <summary>Attempts to claim the single upgrade slot. False when one is already running.</summary>
    public bool TryBegin() => Interlocked.CompareExchange(ref _inFlight, 1, 0) == 0;

    /// <summary>Releases the single upgrade slot.</summary>
    public void Complete() => Volatile.Write(ref _inFlight, 0);

    public void Set(AgentUpgradeSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
}
