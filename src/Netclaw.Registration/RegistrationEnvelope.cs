// -----------------------------------------------------------------------
// <copyright file="RegistrationEnvelope.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Registration;

/// <summary>
/// Supplies the runtime status snapshot the registration envelope carries.
/// Implemented by the daemon over its status service; the seam keeps this
/// library free of a reference to the daemon executable.
/// </summary>
public interface IStatusSnapshotProvider
{
    Task<DaemonRuntimeStatus.Response> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The JSON body POSTed to the control panel's status endpoint. The
/// <see cref="Status"/> field equals the body of
/// <c>GET /api/health/status</c>. <see cref="InstanceId"/> is stable across
/// daemon restarts so the panel can dedupe daemons (the pid inside the
/// status body changes on every restart and cannot serve that purpose).
/// </summary>
public sealed class RegistrationEnvelope : IWireType
{
    public required string InstanceId { get; init; }

    public required DateTimeOffset RegisteredAtUtc { get; init; }

    public required DaemonRuntimeStatus.Response Status { get; init; }
}

/// <summary>
/// The JSON body POSTed to the control panel's heartbeat endpoint between
/// status changes. Liveness only — the panel already holds the full status
/// from the last envelope.
/// </summary>
public sealed class HeartbeatPayload : IWireType
{
    public required string InstanceId { get; init; }

    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Derives the two registration endpoints from the configured base URL.
/// This path pair is the daemon↔panel contract; both sides must implement
/// and expect exactly these paths.
/// </summary>
public static class RegistrationEndpoints
{
    public const string StatusPath = "api/v1/agent/status";
    public const string HeartbeatPath = "api/v1/agent/heartbeat";

    public static Uri StatusUri(string baseUrl) => Resolve(baseUrl, StatusPath);

    public static Uri HeartbeatUri(string baseUrl) => Resolve(baseUrl, HeartbeatPath);

    /// <summary>
    /// Normalizes the base (trailing slash trimmed, then re-added) so bases
    /// with and without a trailing slash — and bases carrying a reverse-proxy
    /// path prefix — all resolve to the same endpoint URLs.
    /// </summary>
    private static Uri Resolve(string baseUrl, string relativePath)
    {
        var normalized = new Uri(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute);
        return new Uri(normalized, relativePath);
    }
}
