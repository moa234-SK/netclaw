// -----------------------------------------------------------------------
// <copyright file="UpdateSuccessorHandoff.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Globalization;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Successor handoff for the detached (non-systemd) self-update restart. The
/// upgrading daemon spawns its replacement with <see cref="ParentPidVariable"/>
/// set; the successor waits (bounded) for its predecessor to exit before
/// acquiring the singleton lock file, which the predecessor only releases on
/// process exit.
/// </summary>
internal static class UpdateSuccessorHandoff
{
    public const string ParentPidVariable = "NETCLAW_UPDATE_PARENT_PID";

    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);
    private const int PollMilliseconds = 200;

    /// <summary>
    /// Blocks (bounded) while the predecessor daemon is still running, then
    /// returns so startup can proceed to lock acquisition. No-op unless this
    /// process was spawned as an update successor.
    /// </summary>
    public static async Task WaitForPredecessorExitAsync(CancellationToken cancellationToken = default)
    {
        var raw = Environment.GetEnvironmentVariable(ParentPidVariable);
        // Never propagate to grandchildren — the wait applies to exactly one hop.
        Environment.SetEnvironmentVariable(ParentPidVariable, null);
        if (!int.TryParse(raw, CultureInfo.InvariantCulture, out var parentPid) || parentPid <= 0)
            return;

        var deadline = Environment.TickCount64 + (long)MaxWait.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (HasExited(parentPid))
                return;

            try
            {
                await Task.Delay(PollMilliseconds, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static bool HasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true; // process no longer exists
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The predecessor PID was recycled by a process we cannot query
            // (elevated / another user). Access denied does not mean exited —
            // keep waiting; the bounded deadline caps the total wait and the
            // lock-file guard backstops a too-early start.
            return false;
        }
    }
}
