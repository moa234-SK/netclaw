// -----------------------------------------------------------------------
// <copyright file="UpgradeRecoveryService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Reconciles the persisted self-update state at startup. The upgrading
/// predecessor wrote its phase before exiting, so the successor decides the
/// outcome by comparing its own version against the recorded target and
/// publishes the terminal snapshot through <see cref="AgentUpgradeState"/> —
/// the panel polls <c>GET /api/agent/upgrade</c> across the restart and reads
/// exactly this result.
/// </summary>
public sealed class UpgradeRecoveryService(
    UpgradeStateStore stateStore,
    AgentUpgradeState upgradeState,
    ILogger<UpgradeRecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        PersistedUpgradeState? state;
        try
        {
            state = await stateStore.ReadAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the persisted upgrade state; ignoring it.");
            return;
        }

        if (state is null)
            return;

        try
        {
            var currentVersion = BuildInfo.FullVersion;
            if (string.Equals(currentVersion, state.TargetVersion, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Self-update to {TargetVersion} completed (from {FromVersion}).",
                    state.TargetVersion, state.FromVersion);
                upgradeState.Set(new AgentUpgradeSnapshot(
                    AgentUpgradeStates.Succeeded, currentVersion, state.TargetVersion,
                    state.BytesTotal, state.BytesTotal, null, state.StartedAtUtc));
            }
            else
            {
                var error = state.Error is not null
                    ? $"The upgrade to {state.TargetVersion} failed: {state.Error}"
                    : $"The upgrade to {state.TargetVersion} did not complete; the daemon is still running {currentVersion}.";
                logger.LogWarning("{UpgradeError}", error);
                upgradeState.Set(new AgentUpgradeSnapshot(
                    AgentUpgradeStates.Failed, currentVersion, state.TargetVersion,
                    0, state.BytesTotal, error, state.StartedAtUtc));
            }
        }
        finally
        {
            try
            {
                await stateStore.DeleteAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not delete the consumed upgrade state file.");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
