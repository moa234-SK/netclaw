// -----------------------------------------------------------------------
// <copyright file="AgentUpgradeService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Configuration.Update;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Outcome of an upgrade request: started (running in the background),
/// up-to-date (no action taken), or blocked (with a user-presentable reason).
/// </summary>
public sealed record AgentUpgradeStartOutcome(
    bool Started, bool UpToDate, string? TargetVersion, string? BlockedReason)
{
    public static AgentUpgradeStartOutcome StartedUpgrade(string targetVersion) =>
        new(true, false, targetVersion, null);

    public static AgentUpgradeStartOutcome AlreadyUpToDate() =>
        new(false, true, null, null);

    public static AgentUpgradeStartOutcome Blocked(string reason) =>
        new(false, false, null, reason);
}

/// <summary>
/// Panel-triggered in-process self-update: downloads and swaps the binaries via
/// the shared <see cref="SelfUpdateInstaller"/>, then restarts the daemon onto
/// the new image — under systemd by exiting (the unit's Restart=always
/// relaunches the swapped ExecStart), otherwise by spawning a successor process
/// that waits for this one to release the lock file. Progress is published to
/// <see cref="AgentUpgradeState"/> (polled via GET /api/agent/upgrade) and
/// persisted to <see cref="UpgradeStateStore"/> so the post-restart daemon can
/// reconcile the outcome via <see cref="UpgradeRecoveryService"/>.
/// </summary>
public sealed class AgentUpgradeService(
    HttpClient httpClient,
    DaemonConfig daemonConfig,
    NetclawPaths paths,
    AgentUpgradeState upgradeState,
    UpgradeStateStore stateStore,
    IDaemonRestartCoordinator restartCoordinator,
    TimeProvider timeProvider,
    ILogger<AgentUpgradeService> logger)
{
    private const string ContainerSupervisorVariable = "NETCLAW_CONTAINER_SUPERVISOR";

    /// <summary>
    /// Test seam for the systemd ownership probe: (command, arguments) → exit code.
    /// </summary>
    internal Func<string, string, Task<int>>? TestCommandRunner { get; set; }

    /// <summary>
    /// Validates the request, evaluates the manifest inline (so the caller gets
    /// an immediate started / up-to-date / blocked answer), and launches the
    /// install in the background.
    /// </summary>
    public async Task<AgentUpgradeStartOutcome> StartAsync(CancellationToken cancellationToken = default)
    {
        if (daemonConfig.DisableSelfUpdate)
            return AgentUpgradeStartOutcome.Blocked(
                "Self-update is disabled (Daemon.DisableSelfUpdate=true). Pull a newer container image to upgrade.");

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ContainerSupervisorVariable)))
            return AgentUpgradeStartOutcome.Blocked(
                "The daemon runs under an external container supervisor. Pull a newer container image to upgrade.");

        if (!upgradeState.TryBegin())
            return AgentUpgradeStartOutcome.Blocked("An upgrade is already in progress.");

        var outcome = await EvaluateAndLaunchAsync(cancellationToken);

        // Once started, the background task owns the slot and releases it when
        // the upgrade reaches a terminal state (the restart case included).
        if (!outcome.Started)
            upgradeState.Complete();

        return outcome;
    }

    private async Task<AgentUpgradeStartOutcome> EvaluateAndLaunchAsync(CancellationToken cancellationToken)
    {
        var result = await UpdateCheckService.CheckForUpdateAsync(
            httpClient, BuildInfo.FullVersion, cancellationToken, daemonConfig.UpdateChannel);

        if (!result.CheckSucceeded)
            return AgentUpgradeStartOutcome.Blocked($"The update check failed: {result.ErrorDetail}");

        if (!result.IsUpdateAvailable)
            return AgentUpgradeStartOutcome.AlreadyUpToDate();

        var startedAt = timeProvider.GetUtcNow();
        var totalBytes = result.MatchingAssets.Sum(static a => a.SizeBytes);
        upgradeState.Set(new AgentUpgradeSnapshot(
            AgentUpgradeStates.Downloading, result.CurrentVersion, result.LatestVersion,
            0, totalBytes, null, startedAt));
        await PersistAsync(AgentUpgradeStates.Downloading, result.CurrentVersion, result.LatestVersion,
            startedAt, totalBytes, null);

        _ = RunAsync(result, startedAt, totalBytes);
        return AgentUpgradeStartOutcome.StartedUpgrade(result.LatestVersion);
    }

    private async Task RunAsync(UpdateCheckResult result, DateTimeOffset startedAt, long totalBytes)
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-update-{Guid.NewGuid():N}");
            IReadOnlyDictionary<string, string> extractedPaths;
            try
            {
                // Per-component progress → cumulative bytes across assets.
                long completedBytes = 0;
                string? completedComponent = null;
                long lastCopied = 0;

                extractedPaths = await SelfUpdateInstaller.DownloadAndExtractAsync(
                    result, tempDir, httpClient,
                    progress: p =>
                    {
                        if (completedComponent != p.Component)
                        {
                            completedBytes += lastCopied;
                            completedComponent = p.Component;
                            lastCopied = 0;
                        }

                        lastCopied = p.BytesCopied;
                        PublishProgress(completedBytes + lastCopied);
                    },
                    log: message => logger.LogInformation("{UpdateMessage}", message),
                    cancellationToken: CancellationToken.None);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }

            Transition(AgentUpgradeStates.Applying, totalBytes);
            await PersistAsync(AgentUpgradeStates.Applying, result.CurrentVersion, result.LatestVersion,
                startedAt, totalBytes, null);

            var installDir = SelfUpdateInstaller.GetInstallDirectory();
            SelfUpdateInstaller.SwapBinaries(
                extractedPaths, installDir,
                warn: warning => logger.LogWarning("{UpdateWarning}", warning));
            SelfUpdateInstaller.CleanupBackupFiles(
                extractedPaths, installDir, Environment.ProcessPath,
                warn: warning => logger.LogWarning("{UpdateWarning}", warning));

            logger.LogInformation(
                "Self-update to {Version} installed; restarting the daemon.", result.LatestVersion);

            Transition(AgentUpgradeStates.Restarting, totalBytes);
            await PersistAsync(AgentUpgradeStates.Restarting, result.CurrentVersion, result.LatestVersion,
                startedAt, totalBytes, null);

            var systemdManaged = await IsSystemdManagedAsync();
            var accepted = await restartCoordinator.RequestUpdateExitAsync(
                CancellationToken.None,
                systemdManaged ? null : SpawnSuccessorAsync);

            if (!accepted)
            {
                await FailAsync("The binaries were updated but another restart was in progress, so the daemon did not "
                    + "restart. Restart the daemon manually to finish the upgrade.",
                    result, startedAt, totalBytes);
            }

            // When accepted, the process exits with the snapshot left at
            // 'restarting'; the successor's UpgradeRecoveryService reconciles
            // it to 'succeeded' (or 'failed' when the version did not change).
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Self-update failed.");
            await FailAsync(ex.Message, result, startedAt, totalBytes);
        }
        finally
        {
            upgradeState.Complete();
        }
    }

    /// <summary>
    /// Spawns the swapped-in binary as a detached successor that waits for this
    /// process to exit (and release the lock file) before booting. Mirrors the
    /// daemon spawn flags of the CLI's DaemonManager: no stdio redirection, so
    /// pipe handles never wedge either process.
    /// </summary>
    private Task SpawnSuccessorAsync()
    {
        var processPath = Environment.ProcessPath
            ?? throw new SelfUpdateException("The daemon's own process path is unknown; cannot spawn a successor.");

        var startInfo = new ProcessStartInfo
        {
            // The swap renamed the new binary into place at this exact path.
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment[UpdateSuccessorHandoff.ParentPidVariable] =
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

        using var successor = Process.Start(startInfo)
            ?? throw new SelfUpdateException("Failed to spawn the successor daemon process.");

        logger.LogInformation(
            "Spawned successor daemon {Pid} onto the new binary; it waits for this process to exit.",
            successor.Id);
        return Task.CompletedTask;
    }

    /// <summary>
    /// True when the daemon's lifecycle is owned by the systemd user service
    /// (unit file present and active) — in that mode exiting is enough, since
    /// the unit's Restart=always relaunches the swapped ExecStart.
    /// </summary>
    private async Task<bool> IsSystemdManagedAsync()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        // Mirrors DaemonManager.SystemdUserUnitFilePath from the CLI.
        var unitPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "systemd", "user", "netclaw.service");
        if (!File.Exists(unitPath))
            return false;

        var runner = TestCommandRunner ?? RunCommandAsync;
        var exitCode = await runner("systemctl", "--user is-active --quiet netclaw.service");
        return exitCode == 0;
    }

    private async Task<int> RunCommandAsync(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return -1;

            await process.WaitForExitAsync(CancellationToken.None);
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Command {Command} {Arguments} failed; assuming systemd does not own the daemon.",
                fileName, arguments);
            return -1;
        }
    }

    private void PublishProgress(long bytesDownloaded)
    {
        var current = upgradeState.Current;
        if (current.State == AgentUpgradeStates.Downloading)
            upgradeState.Set(current with { BytesDownloaded = bytesDownloaded });
    }

    private void Transition(string state, long bytesDownloaded)
    {
        var current = upgradeState.Current;
        upgradeState.Set(current with { State = state, BytesDownloaded = bytesDownloaded });
    }

    private async Task FailAsync(string error, UpdateCheckResult result, DateTimeOffset startedAt, long totalBytes)
    {
        var current = upgradeState.Current;
        upgradeState.Set(current with { State = AgentUpgradeStates.Failed, Error = error });

        // Best-effort persist so a later restart does not misread a lingering
        // 'restarting' phase as a pending outcome.
        try
        {
            await stateStore.WriteAsync(new PersistedUpgradeState
            {
                Phase = AgentUpgradeStates.Failed,
                FromVersion = result.CurrentVersion,
                TargetVersion = result.LatestVersion,
                StartedAtUtc = startedAt,
                BytesTotal = totalBytes,
                Error = error,
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not persist the failed upgrade state.");
        }
    }

    private async Task PersistAsync(
        string phase, string fromVersion, string targetVersion,
        DateTimeOffset startedAt, long bytesTotal, string? error)
    {
        try
        {
            await stateStore.WriteAsync(new PersistedUpgradeState
            {
                Phase = phase,
                FromVersion = fromVersion,
                TargetVersion = targetVersion,
                StartedAtUtc = startedAt,
                BytesTotal = bytesTotal,
                Error = error,
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not persist upgrade state to {Path}.", paths.UpgradeStatePath);
        }
    }

    private static void TryDeleteDirectory(string tempDir)
    {
        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch (Exception ex)
        {
            // Temp cleanup must never fail the update itself.
            System.Diagnostics.Debug.WriteLine($"warn: temp cleanup failed: {ex.Message}");
        }
    }
}
