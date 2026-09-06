// -----------------------------------------------------------------------
// <copyright file="DaemonStatusSnapshotProvider.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Registration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Daemon-side adapter that feeds <see cref="SelfRegistrationService"/> from
/// <see cref="DaemonRuntimeStatusService"/>. Keeps the registration library
/// free of a reference to the daemon executable.
/// </summary>
internal sealed class DaemonStatusSnapshotProvider(DaemonRuntimeStatusService statusService)
    : IStatusSnapshotProvider
{
    public Task<DaemonRuntimeStatus.Response> GetStatusAsync(CancellationToken cancellationToken = default)
        => statusService.GetStatusAsync(cancellationToken);
}
