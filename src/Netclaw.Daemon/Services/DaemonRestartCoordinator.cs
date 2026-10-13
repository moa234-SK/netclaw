// -----------------------------------------------------------------------
// <copyright file="DaemonRestartCoordinator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Akka.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;

namespace Netclaw.Daemon.Services;

public interface IDaemonRestartCoordinator
{
    Task RequestConfigRestartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Drains sessions and stops the host so the process exits onto freshly
    /// swapped binaries (self-update restart). Unlike the config restart this
    /// does not iterate the in-process restart loop — the new image must be
    /// launched by the supervisor (systemd Restart=always) or by the successor
    /// spawned via <paramref name="onDrained"/>.
    /// </summary>
    /// <returns>
    /// False when another coordinated restart owns the ingress gate and the
    /// exit could not be scheduled; true once shutdown has been initiated.
    /// </returns>
    Task<bool> RequestUpdateExitAsync(CancellationToken cancellationToken, Func<Task>? onDrained = null);
}

/// <summary>
/// Coordinates graceful daemon restart by closing ingress, draining active sessions,
/// persisting restart recovery state, and only then requesting host shutdown.
/// </summary>
public sealed class DaemonRestartCoordinator : IDaemonRestartCoordinator
{
    internal static readonly TimeSpan RestartDrainTimeout = TimeSpan.FromSeconds(20);

    private readonly SessionIngressGate _ingressGate;
    private readonly RestartManifestStore _manifestStore;
    private readonly IRequiredActor<SessionManagerActorKey> _sessionManagerProvider;
    private readonly DaemonRestartSignal _restartSignal;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly DaemonLifecycleNotifier _lifecycleNotifier;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DaemonRestartCoordinator> _logger;
    private readonly TimeSpan _restartDrainTimeout;

    public DaemonRestartCoordinator(
        SessionIngressGate ingressGate,
        RestartManifestStore manifestStore,
        IRequiredActor<SessionManagerActorKey> sessionManagerProvider,
        DaemonRestartSignal restartSignal,
        IHostApplicationLifetime appLifetime,
        DaemonLifecycleNotifier lifecycleNotifier,
        TimeProvider timeProvider,
        ILogger<DaemonRestartCoordinator> logger,
        TimeSpan? restartDrainTimeout = null)
    {
        _ingressGate = ingressGate;
        _manifestStore = manifestStore;
        _sessionManagerProvider = sessionManagerProvider;
        _restartSignal = restartSignal;
        _appLifetime = appLifetime;
        _lifecycleNotifier = lifecycleNotifier;
        _timeProvider = timeProvider;
        _logger = logger;
        _restartDrainTimeout = restartDrainTimeout ?? RestartDrainTimeout;
    }

    public async Task RequestConfigRestartAsync(CancellationToken cancellationToken)
    {
        if (!_ingressGate.TryClose(SessionIngressGate.RestartInProgressMessage))
        {
            if (_ingressGate.ClosedReason == SessionIngressGate.AgentSuspendedMessage)
            {
                // The operator froze the agent. A restart now would reopen
                // nothing (the boot gate re-freezes) but would churn the
                // process for no benefit — defer until resume.
                _logger.LogInformation(
                    "Agent is suspended by operator intervention; deferring the config reload restart until the agent is resumed.");
            }
            else
            {
                _logger.LogInformation("Coordinated restart already in progress; ignoring duplicate config reload request.");
            }

            return;
        }

        try
        {
            var sessionManager = await _sessionManagerProvider.GetAsync(cancellationToken);

            using var deadlineCts = new CancellationTokenSource(_restartDrainTimeout, _timeProvider);
            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadlineCts.Token);

            var drainResult = await SessionDrainHelper.DrainAsync(
                sessionManager,
                "config-reload",
                _logger,
                drainCts.Token,
                cancellationToken);

            var manifest = new RestartManifest
            {
                RestartReminders = [.. drainResult.RestartReminders]
            };

            if (manifest.RestartReminders.Count == 0)
                await _manifestStore.DeleteAsync();
            else
                await _manifestStore.WriteAsync(manifest, cancellationToken);

            _lifecycleNotifier.NotifyShutdown("config-reload", drainResult.ToNotificationContext());
            _restartSignal.RequestRestart();
            _appLifetime.StopApplication();
        }
        catch
        {
            _ingressGate.Reopen();
            throw;
        }
    }

    public async Task<bool> RequestUpdateExitAsync(
        CancellationToken cancellationToken,
        Func<Task>? onDrained = null)
    {
        var weClosedGate = _ingressGate.TryClose(SessionIngressGate.RestartInProgressMessage);

        if (!weClosedGate)
        {
            if (_ingressGate.ClosedReason == SessionIngressGate.AgentSuspendedMessage)
            {
                // A suspended agent can still be upgraded: the gate is already
                // closed and the boot gate re-freezes the successor on startup,
                // so exiting now does not reopen any ingress.
                _logger.LogInformation(
                    "Agent is suspended by operator intervention; proceeding with the self-update restart (the daemon comes back frozen).");
            }
            else
            {
                _logger.LogWarning(
                    "A coordinated restart is already in progress; the self-update restart could not be scheduled.");
                return false;
            }
        }

        try
        {
            var sessionManager = await _sessionManagerProvider.GetAsync(cancellationToken);

            using var deadlineCts = new CancellationTokenSource(_restartDrainTimeout, _timeProvider);
            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadlineCts.Token);

            var drainResult = await SessionDrainHelper.DrainAsync(
                sessionManager,
                "self-update",
                _logger,
                drainCts.Token,
                cancellationToken);

            var manifest = new RestartManifest
            {
                RestartReminders = [.. drainResult.RestartReminders]
            };

            if (manifest.RestartReminders.Count == 0)
                await _manifestStore.DeleteAsync();
            else
                await _manifestStore.WriteAsync(manifest, cancellationToken);

            _lifecycleNotifier.NotifyShutdown("self-update", drainResult.ToNotificationContext());

            // Last chance for the caller to act while this process is still the
            // sole holder of the lock file — the detached restart mode spawns
            // its successor here.
            if (onDrained is not null)
                await onDrained();

            // Deliberately no _restartSignal.RequestRestart(): the do/while loop
            // must not iterate (it would keep serving from the old image). The
            // process exits and systemd (Restart=always) or the spawned
            // successor relaunches the new binary.
            _appLifetime.StopApplication();
            return true;
        }
        catch
        {
            // Only reopen a gate this method closed — reopening an
            // operator-suspended gate would unfreeze the agent.
            if (weClosedGate)
                _ingressGate.Reopen();
            throw;
        }
    }
}
