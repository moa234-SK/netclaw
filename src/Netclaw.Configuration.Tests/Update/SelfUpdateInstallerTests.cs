// -----------------------------------------------------------------------
// <copyright file="SelfUpdateInstallerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration.Update;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests.Update;

public sealed class SelfUpdateInstallerTests : IDisposable
{
    /// <summary>
    /// xunit.v3 <c>SkipUnless</c> hook for tests that simulate Windows
    /// file-lock failures by revoking directory write permission. The
    /// simulation is POSIX-only (<c>File.SetUnixFileMode</c>) and is
    /// ineffective for root, which bypasses directory permission bits.
    /// </summary>
    public static bool CanSimulateFileLock =>
        !OperatingSystem.IsWindows() && Environment.UserName != "root";

    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void CleanupBackupFile_DoesNotDelete_RunningImageBackup_OnWindows()
    {
        var backupPath = Path.Combine(_dir.Path, "netclaw.exe.backup");
        File.WriteAllText(backupPath, "old image");

        // The running process's backup is the very image this process executes
        // from; on Windows DeleteFile fails with UnauthorizedAccessException.
        // NTFS path comparison is case-insensitive, so pin that here — a
        // regression to Ordinal would leave the backup deleted.
        var runningBackupPath = Path.Combine(_dir.Path, "NETCLAW.EXE.BACKUP");
        SelfUpdateInstaller.CleanupBackupFile(backupPath, runningBackupPath, isWindows: true);

        Assert.True(File.Exists(backupPath));
    }

    [Fact]
    public void SwapBinaryIntoPlace_ReplacesTarget_AndBacksUpOldBinary()
    {
        var sourcePath = Path.Combine(_dir.Path, "new.exe");
        var targetPath = Path.Combine(_dir.Path, "netclaw.exe");
        var backupPath = targetPath + ".backup";
        File.WriteAllText(sourcePath, "new image");
        File.WriteAllText(targetPath, "old image");

        SelfUpdateInstaller.SwapBinaryIntoPlace(sourcePath, targetPath, backupPath);

        Assert.Equal("new image", File.ReadAllText(targetPath));
        Assert.Equal("old image", File.ReadAllText(backupPath));
    }

    [Fact]
    public void SwapBinaryIntoPlace_RestoresOldBinary_WhenNewBinaryMoveFails()
    {
        var sourcePath = Path.Combine(_dir.Path, "new.exe");
        var targetPath = Path.Combine(_dir.Path, "netclaw.exe");
        var backupPath = targetPath + ".backup";
        File.WriteAllText(sourcePath, "new image");
        File.WriteAllText(targetPath, "old image");

        // Make the final move fail after the old binary was backed up: the
        // install directory must never be left without an executable.
        File.Delete(sourcePath);

        Assert.ThrowsAny<Exception>(
            () => SelfUpdateInstaller.SwapBinaryIntoPlace(sourcePath, targetPath, backupPath));
        // The old binary is rolled back into place; the backup is consumed by
        // the restore, so the install directory is left with a working binary.
        Assert.Equal("old image", File.ReadAllText(targetPath));
        Assert.False(File.Exists(backupPath));
    }

    [Fact(SkipUnless = nameof(CanSimulateFileLock), Skip = "POSIX-only permission simulation (ineffective on Windows or as root)")]
    [SlopwatchSuppress("SW001", "Simulates Windows file locks via POSIX directory permissions, which cannot run on Windows or as root.")]
    public void SwapBinaryIntoPlace_LeavesTargetIntact_WhenStaleBackupDeleteFails()
    {
        if (OperatingSystem.IsWindows())
            return; // SkipUnless gates the skip; this guard satisfies CA1416 for Unix-only APIs

        var sourcePath = Path.Combine(_dir.Path, "new.exe");
        var targetPath = Path.Combine(_dir.Path, "netclaw.exe");
        var backupPath = targetPath + ".backup";
        File.WriteAllText(sourcePath, "new image");
        File.WriteAllText(targetPath, "old image");
        File.WriteAllText(backupPath, "stale image");
        var dir = Path.GetDirectoryName(targetPath)!;
        var originalMode = File.GetUnixFileMode(dir);

        try
        {
            // Remove write permission on the directory so the stale-backup
            // delete fails with UnauthorizedAccessException — the same failure
            // class as an AV-locked file on Windows. The target must be left
            // untouched (no half-swap).
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            Assert.ThrowsAny<Exception>(
                () => SelfUpdateInstaller.SwapBinaryIntoPlace(sourcePath, targetPath, backupPath));
            Assert.Equal("old image", File.ReadAllText(targetPath));
            Assert.Equal("stale image", File.ReadAllText(backupPath));
        }
        finally
        {
            File.SetUnixFileMode(dir, originalMode);
        }
    }

    [Fact]
    public void CleanupBackupFile_Deletes_OtherComponentBackup_OnWindows()
    {
        var backupPath = Path.Combine(_dir.Path, "netclawd.exe.backup");
        File.WriteAllText(backupPath, "old image");
        var runningBackupPath = Path.Combine(_dir.Path, "netclaw.exe") + ".backup";

        SelfUpdateInstaller.CleanupBackupFile(backupPath, runningBackupPath, isWindows: true);

        Assert.False(File.Exists(backupPath));
    }

    [Fact]
    public void CleanupBackupFile_Deletes_Backup_OnNonWindows()
    {
        var backupPath = Path.Combine(_dir.Path, "netclaw.backup");
        File.WriteAllText(backupPath, "old image");

        // POSIX allows unlinking a running image, so even the running
        // process's own backup is removed.
        SelfUpdateInstaller.CleanupBackupFile(backupPath, runningBackupPath: backupPath, isWindows: false);

        Assert.False(File.Exists(backupPath));
    }

    [Fact(SkipUnless = nameof(CanSimulateFileLock), Skip = "POSIX-only permission simulation (ineffective on Windows or as root)")]
    [SlopwatchSuppress("SW001", "Simulates Windows file locks via POSIX directory permissions, which cannot run on Windows or as root.")]
    public void CleanupBackupFile_DoesNotThrow_WhenDeleteFails()
    {
        if (OperatingSystem.IsWindows())
            return; // SkipUnless gates the skip; this guard satisfies CA1416 for Unix-only APIs

        var backupPath = Path.Combine(_dir.Path, "netclaw.backup");
        File.WriteAllText(backupPath, "old image");
        var dir = Path.GetDirectoryName(backupPath)!;
        var originalMode = File.GetUnixFileMode(dir);

        try
        {
            // Remove write permission on the directory so unlink fails with
            // UnauthorizedAccessException — the same failure class as deleting
            // a running image on Windows.
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            SelfUpdateInstaller.CleanupBackupFile(backupPath, runningBackupPath: null, isWindows: false);

            // Warned, not crashed; the leftover self-heals on the next update.
            Assert.True(File.Exists(backupPath));
        }
        finally
        {
            File.SetUnixFileMode(dir, originalMode);
        }
    }
}

/// <summary>
/// Supplies source-level Slopwatch suppressions without a runtime package dependency.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class SlopwatchSuppressAttribute(string ruleId, string reason) : Attribute
{
    public string RuleId { get; } = ruleId;

    public string Reason { get; } = reason;
}
