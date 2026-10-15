// -----------------------------------------------------------------------
// <copyright file="UpdateCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Configuration.Update;

namespace Netclaw.Cli.Update;

/// <summary>
/// Handles <c>netclaw update</c> CLI subcommands: check, download + install.
/// </summary>
internal static class UpdateCommand
{
    internal static Func<HttpMessageHandler>? TestHttpMessageHandlerFactory { get; set; }
    internal static Func<NetclawPaths, TimeProvider, IDaemonProcessLifecycle>? TestDaemonProcessManagerFactory { get; set; }
    internal static Func<NetclawPaths, SystemdUserService>? TestSystemdUserServiceFactory { get; set; }

    internal static bool ShouldRunStartupUpdateCheck(string mode, string[] args)
    {
        switch (mode)
        {
            case "init":
            case "update":
            case "secrets":
            case "daemon":
            case "chat":
            case "sessions":
            case "headless":
                return false;
            case "stats":
                return !args.Contains("--tui", StringComparer.Ordinal);
            case "mcp":
                var mcpSubcommand = args.Length > 1 ? args[1] : "help";
                return !((mcpSubcommand is "tools" or "permissions") && args.Length <= 2);
            case "provider":
            case "model":
                return args.Length > 1;
            case "approvals":
                return !(args.Length == 1 || (args.Length > 1 && args[1] is "tui"));
            case "reminder":
                return !(args.Length > 1 && args[1] is ("ui" or "tui"));
            default:
                return true;
        }
    }

    public static async Task<int> RunAsync(
        CliContext cli,
        string[] args,
        bool selfUpdateDisabled,
        UpdateChannel channel)
    {
        var paths = cli.Paths;
        var input = cli.Input;
        var output = cli.Output;
        var error = cli.Error;
        var checkOnly = false;
        var force = false;
        UpdateChannel? channelOverride = null;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--check":
                    checkOnly = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--channel":
                    if (i + 1 >= args.Length)
                    {
                        error.WriteLine("--channel requires a value: stable or beta.");
                        WriteHelp(output);
                        return 1;
                    }
                    if (!DaemonConfig.TryParseUpdateChannel(args[++i], out var parsedChannel))
                    {
                        error.WriteLine($"Unknown channel: '{args[i]}'. Valid values: stable, beta.");
                        WriteHelp(output);
                        return 1;
                    }
                    channelOverride = parsedChannel;
                    break;
                case "-h" or "--help" or "help":
                    WriteHelp(output);
                    return 0;
                default:
                    output.WriteLine($"Unknown option: {args[i]}");
                    WriteHelp(output);
                    return 1;
            }
        }

        // Specifying --channel selects the channel this run evaluates against.
        // Outside of --check it also switches the channel: the value is written
        // back to netclaw.json so the daemon's background check and future runs
        // follow it. --check is a read-only verb, so it previews the requested
        // channel without persisting anything.
        if (channelOverride is { } overrideChannel)
        {
            channel = overrideChannel;

            if (checkOnly)
            {
                output.WriteLine($"Checking '{channel.ToWireValue()}' channel (run without --check to switch).");
            }
            else
            {
                try
                {
                    PersistChannel(paths, channel);
                }
                catch (Exception ex)
                {
                    error.WriteLine($"Error: could not save update channel to {paths.NetclawConfigPath}: {ex.Message}");
                    return 1;
                }

                output.WriteLine($"Update channel set to '{channel.ToWireValue()}' ({paths.NetclawConfigPath}).");
            }
        }

        var currentVersion = BuildInfo.FullVersion;

        using var httpClient = TestHttpMessageHandlerFactory is { } createHandler
            ? new HttpClient(createHandler())
            : new HttpClient();

        // Fetch manifest with signature verification
        var fetchResult = await UpdateCheckService.FetchVerifiedManifestAsync(
            httpClient);

        if (!fetchResult.IsSuccess)
        {
            if (fetchResult.Status is ManifestFetchStatus.SignatureFailure or ManifestFetchStatus.PlatformUnavailable)
            {
                error.WriteLine($"Error: {fetchResult.ErrorMessage}");
                error.WriteLine(fetchResult.Status == ManifestFetchStatus.PlatformUnavailable
                    ? "The update manifest could not be verified because signature verification is unavailable on this platform."
                    : "The update manifest could not be verified. This may indicate tampering.");
                error.WriteLine("If this persists, report the issue at https://github.com/netclaw-dev/netclaw/issues");
                return 1;
            }

            // Network failure — could be transient
            error.WriteLine($"Could not check for updates: {fetchResult.ErrorMessage}");
            return 1;
        }

        var result = UpdateCheckService.EvaluateManifest(fetchResult.Manifest!, currentVersion, channel);

        if (!result.IsUpdateAvailable)
        {
            output.WriteLine($"Netclaw is up to date (v{result.CurrentVersion}).");
            return 0;
        }

        output.WriteLine($"Update available: v{result.CurrentVersion} → v{result.LatestVersion}");
        if (result.ReleaseNotesUrl is not null)
            output.WriteLine($"Release notes: {result.ReleaseNotesUrl}");

        if (checkOnly)
            return 0;

        if (selfUpdateDisabled)
        {
            output.WriteLine();
            output.WriteLine("Self-update is disabled (Daemon.DisableSelfUpdate=true).");
            output.WriteLine("Pull a newer container image to upgrade.");
            return 1;
        }

        // Show what will be downloaded
        output.WriteLine();
        foreach (var asset in result.MatchingAssets)
        {
            var sizeMb = asset.SizeBytes / (1024.0 * 1024.0);
            output.WriteLine($"  {asset.Component} ({asset.Rid}) — {sizeMb:F1} MB");
        }

        if (!force)
        {
            output.Write("\nProceed with update? [y/N]: ");
            var response = input.ReadLine();
            if (!string.Equals(response, "y", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine("Update cancelled.");
                return 0;
            }
        }

        return await PerformUpdateAsync(result, cli, httpClient);
    }

    private static async Task<int> PerformUpdateAsync(
        UpdateCheckResult result, CliContext cli, HttpClient httpClient)
    {
        var output = cli.Output;
        var error = cli.Error;
        void Warn(string message) => error.WriteLine(message);
        var installDir = SelfUpdateInstaller.GetInstallDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Download, verify, and extract each asset. The installer owns the
            // archive handling for both this command and the daemon's upgrade
            // path, so the two can never drift apart.
            IReadOnlyDictionary<string, string> extractedPaths;
            try
            {
                extractedPaths = await SelfUpdateInstaller.DownloadAndExtractAsync(
                    result, tempDir, httpClient,
                    log: message => output.WriteLine(message));
            }
            catch (SelfUpdateException ex)
            {
                output.WriteLine($"Error: {ex.Message}");
                return 1;
            }

            // Check if daemon is running (we'll need to restart it)
            var manager = CreateDaemonProcessManager(cli.Paths, cli.Time);
            var systemdService = CreateSystemdUserService(cli.Paths);
            var daemonStatus = manager.GetStatus();
            var stopResult = UpdateDaemonStopResult.Succeeded(
                UpdateDaemonOwner.None,
                daemonStatus.Message);

            if (daemonStatus.IsRunning)
            {
                output.Write("Stopping daemon...");
                stopResult = await StopDaemonForUpdateAsync(manager, systemdService, daemonStatus);
                if (!stopResult.Success)
                {
                    output.WriteLine($" failed: {stopResult.Message}");
                    output.WriteLine("Update aborted. Stop the daemon manually, fix service state, and retry.");
                    return 1;
                }
                output.WriteLine(" done.");
            }

            // Replace binaries. SwapBinaries rolls a failed swap back into
            // place and reports whether the previous binary survived; the
            // detail message already names the component and the restore
            // outcome, so only the daemon hint is added here.
            output.Write("Installing...");
            try
            {
                SelfUpdateInstaller.SwapBinaries(extractedPaths, installDir, Warn);
            }
            catch (SelfUpdateException ex)
            {
                output.WriteLine($"\n  {ex.Message}");
                output.WriteLine("  The daemon is stopped; start it with 'netclaw daemon start'.");
                return 1;
            }

            output.WriteLine(" done.");

            // Restart daemon if it was running
            if (stopResult.ShouldRestart)
            {
                output.Write("Restarting daemon...");
                var startResult = await StartDaemonAfterUpdateAsync(stopResult.Owner, manager, systemdService);
                if (!startResult.Success)
                {
                    output.WriteLine($" failed: {startResult.Message}");
                    output.WriteLine("Update installed, but daemon restart failed. Start the daemon manually and check `netclaw status`.");
                    return 1;
                }

                output.WriteLine(" done.");
            }

            // Clean up backup files. The backup of the currently running CLI
            // binary is the image this process still executes from; on Windows
            // DeleteFile on a running image fails with
            // UnauthorizedAccessException. Leave it - the install step deletes
            // stale backups before moving the new binary on the next update.
            // A leftover backup must never turn a successful update into a
            // fatal error, so any other delete failure only warns.
            SelfUpdateInstaller.CleanupBackupFiles(
                extractedPaths, installDir, Environment.ProcessPath, Warn);

            output.WriteLine($"\nUpdated to v{result.LatestVersion}.");
            return 0;
        }
        finally
        {
            // Clean up temp directory
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { error.WriteLine($"warn: temp cleanup failed: {ex.Message}"); }
        }
    }

    internal static async Task<UpdateDaemonStopResult> StopDaemonForUpdateAsync(
        IDaemonProcessLifecycle manager,
        SystemdUserService systemdService,
        DaemonStatus? daemonStatus = null)
    {
        daemonStatus ??= manager.GetStatus();
        if (!daemonStatus.IsRunning)
        {
            return UpdateDaemonStopResult.Succeeded(
                UpdateDaemonOwner.None,
                daemonStatus.Message);
        }

        return await StopDaemonAsync(manager, systemdService, "update");
    }

    /// <summary>
    /// Stops this home's daemon so that nothing brings it back: the installed systemd user unit
    /// first when it could (re)start a daemon for this home (its <c>Restart=always</c> would undo
    /// a stop that went around it), then whatever daemon process is still alive.
    /// </summary>
    internal static async Task<UpdateDaemonStopResult> StopDaemonAsync(
        IDaemonProcessLifecycle manager,
        SystemdUserService systemdService,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var systemdOwnership = await systemdService.GetStopOwnershipAsync(manager.GetStatus());
        if (systemdOwnership.Kind == SystemdUserServiceOwnershipKind.Unknown)
        {
            return UpdateDaemonStopResult.Failed(
                UpdateDaemonOwner.None,
                $"Could not determine whether systemd owns the daemon lifecycle: {systemdOwnership.Message}");
        }

        DaemonResult? unitStop = null;
        if (systemdOwnership.Kind == SystemdUserServiceOwnershipKind.Managed)
        {
            unitStop = await systemdService.StopAsync();
            if (!unitStop.Success)
            {
                return UpdateDaemonStopResult.Failed(
                    UpdateDaemonOwner.SystemdUserService,
                    $"systemd stop failed: {unitStop.Message}");
            }
        }

        // Without a unit stop this also reports "Daemon is not running." when nothing runs.
        if (unitStop is null || manager.GetStatus().IsRunning)
        {
            var processStop = await manager.StopAsync(reason, cancellationToken);
            if (unitStop is null)
            {
                return processStop.Success
                    ? UpdateDaemonStopResult.Succeeded(UpdateDaemonOwner.DetachedProcess, processStop.Message)
                    : UpdateDaemonStopResult.Failed(UpdateDaemonOwner.DetachedProcess, processStop.Message);
            }

            return processStop.Success
                ? UpdateDaemonStopResult.Succeeded(
                    UpdateDaemonOwner.SystemdUserService, $"{unitStop.Message} {processStop.Message}")
                : UpdateDaemonStopResult.Failed(
                    UpdateDaemonOwner.SystemdUserService,
                    "systemd service stopped, but a daemon process is still running and "
                    + $"could not be stopped: {processStop.Message}");
        }

        return UpdateDaemonStopResult.Succeeded(UpdateDaemonOwner.SystemdUserService, unitStop.Message);
    }

    /// <summary>
    /// Starts the daemon through whatever owns it. An installed systemd user unit starts the
    /// daemon itself, so the CLI never spawns a second, detached copy beside it.
    /// </summary>
    internal static async Task<DaemonResult> StartDaemonAsync(
        IDaemonProcessLifecycle manager,
        SystemdUserService systemdService)
    {
        // A running daemon holds the home whoever started it: report it, and never queue a unit
        // start whose daemon would only crash-loop on the singleton lock.
        if (manager.GetStatus().IsRunning)
            return manager.Start();

        var systemdOwnership = await systemdService.GetStartOwnershipAsync();
        return systemdOwnership.Kind switch
        {
            SystemdUserServiceOwnershipKind.Unknown => new DaemonResult(false,
                $"Could not determine whether systemd owns the daemon lifecycle: {systemdOwnership.Message}"),
            SystemdUserServiceOwnershipKind.Managed => await systemdService.StartAsync(),
            _ => manager.Start()
        };
    }

    internal static async Task<DaemonResult> StartDaemonAfterUpdateAsync(
        UpdateDaemonOwner owner,
        IDaemonProcessLifecycle manager,
        SystemdUserService systemdService)
    {
        return owner switch
        {
            UpdateDaemonOwner.None => new DaemonResult(true, "Daemon was not running."),
            UpdateDaemonOwner.SystemdUserService => await systemdService.StartAsync(),
            UpdateDaemonOwner.DetachedProcess => manager.Start(),
            _ => new DaemonResult(false, $"Unknown daemon lifecycle owner: {owner}.")
        };
    }

    private static IDaemonProcessLifecycle CreateDaemonProcessManager(NetclawPaths paths, TimeProvider time)
    {
        return TestDaemonProcessManagerFactory?.Invoke(paths, time)
            ?? new DaemonProcessLifecycle(new DaemonManager(paths, time));
    }

    private static SystemdUserService CreateSystemdUserService(NetclawPaths paths)
    {
        return TestSystemdUserServiceFactory?.Invoke(paths)
            ?? new SystemdUserService(paths.BasePath);
    }

    internal sealed class DaemonProcessLifecycle(DaemonManager manager) : IDaemonProcessLifecycle
    {
        public DaemonStatus GetStatus() => manager.GetStatus();

        public Task<DaemonResult> StopAsync(string reason, CancellationToken cancellationToken)
            => manager.StopAsync(reason, cancellationToken);

        public DaemonResult Start() => manager.Start();
    }

    /// <summary>
    /// Persists the chosen update channel to <c>Daemon.UpdateChannel</c> in
    /// netclaw.json, preserving every other field. Writes the canonical wire
    /// value (<c>stable</c>/<c>beta</c>) so the on-disk config stays schema-valid.
    /// </summary>
    private static void PersistChannel(NetclawPaths paths, UpdateChannel channel)
    {
        var config = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
        var daemon = ConfigFileHelper.GetOrCreateSection(config, "Daemon");
        daemon["UpdateChannel"] = channel.ToWireValue();
        ConfigFileHelper.WriteConfigFile(paths.NetclawConfigPath, config);
    }

    internal static void WriteHelp(TextWriter output)
    {
        output.WriteLine("Usage: netclaw update [options]");
        output.WriteLine();
        output.WriteLine("Check for and install Netclaw updates.");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --check              Check for updates without installing");
        output.WriteLine("  --force              Skip confirmation prompt");
        output.WriteLine("  --channel <name>     Switch the release channel (saved to netclaw.json): stable or beta");
    }

    /// <summary>
    /// Holds the result of the most recent background update check so the
    /// notice can be emitted at a safe time (after a TUI exits, after a CLI
    /// command finishes writing its own output), instead of from inside the
    /// background task itself — which would corrupt any running TUI.
    /// </summary>
    private static string? _pendingNotice;

    /// <summary>
    /// Quick background update check for CLI startup. Stores a one-line
    /// notification in a static buffer if an update is available; emitted by
    /// <see cref="EmitPendingNoticeIfReady"/> when the program is about to exit.
    /// </summary>
    internal static async Task BackgroundUpdateCheckAsync(bool selfUpdateDisabled = false, UpdateChannel channel = UpdateChannel.Stable)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var httpClient = new HttpClient();
            var result = await UpdateCheckService.CheckForUpdateAsync(
                httpClient, BuildInfo.FullVersion, cts.Token, channel);

            if (result.IsUpdateAvailable)
            {
                var hint = selfUpdateDisabled
                    ? "pull a newer container image to upgrade"
                    : "run 'netclaw update'";
                _pendingNotice =
                    $"Update available: v{result.CurrentVersion} → v{result.LatestVersion} — {hint}";
            }
        }
        catch (Exception ex)
        {
            // A failed background check must never write to stderr mid-TUI;
            // Debug.WriteLine is a no-op in Release and bypasses the alt-screen.
            System.Diagnostics.Debug.WriteLine(
                $"background update check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Emit the buffered update notice (if the background check completed
    /// and an update is available) to stderr. Safe to call from anywhere —
    /// no-op if the check is still in flight or no update was found.
    /// Intended to run after the mode handler returns, when any TUI has
    /// already torn down its alt screen.
    /// </summary>
    internal static void EmitPendingNoticeIfReady()
    {
        var notice = Interlocked.Exchange(ref _pendingNotice, null);
        if (notice is not null)
            Console.Error.WriteLine(notice);
    }
}

internal interface IDaemonProcessLifecycle
{
    DaemonStatus GetStatus();

    Task<DaemonResult> StopAsync(string reason, CancellationToken cancellationToken);

    DaemonResult Start();
}

internal enum UpdateDaemonOwner
{
    None,
    DetachedProcess,
    SystemdUserService
}

internal sealed record UpdateDaemonStopResult(
    bool Success,
    UpdateDaemonOwner Owner,
    string Message)
{
    public bool ShouldRestart => Success && Owner is not UpdateDaemonOwner.None;

    public static UpdateDaemonStopResult Succeeded(UpdateDaemonOwner owner, string message) =>
        new(true, owner, message);

    public static UpdateDaemonStopResult Failed(UpdateDaemonOwner owner, string message) =>
        new(false, owner, message);

    public DaemonResult ToDaemonResult() => new(Success, Message);
}
