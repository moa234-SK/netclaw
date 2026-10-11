// -----------------------------------------------------------------------
// <copyright file="SelfUpdateInstaller.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.IO.Compression;
using System.Security.Cryptography;
using Netclaw.Configuration.Feeds;

namespace Netclaw.Configuration.Update;

/// <summary>
/// Thrown when a self-update download, verification, extraction, or binary
/// swap fails. The message is user-presentable; hosts decide how to render it.
/// </summary>
public sealed class SelfUpdateException(
    string message,
    bool targetRestored = false,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>
    /// True when a failed swap was rolled back and the previously installed
    /// binary is still in place at the target path.
    /// </summary>
    public bool TargetRestored { get; } = targetRestored;
}

/// <summary>
/// Shared binary self-update pipeline used by both <c>netclaw update</c> (CLI)
/// and the daemon's panel-triggered upgrade. Handles download, SHA-256
/// verification, archive extraction, and the atomic swap-with-backup of the
/// installed binaries. Hosts own process lifecycle around the swap: the CLI
/// stops and restarts the daemon externally; the daemon swaps in place and
/// restarts itself onto the new image.
/// </summary>
public static class SelfUpdateInstaller
{
    /// <summary>
    /// Incremental download progress for one component's archive.
    /// <see cref="BytesCopied"/> is per-component, not cumulative.
    /// </summary>
    public sealed record DownloadProgress(string Component, long BytesCopied, long? TotalBytes);

    /// <summary>
    /// Resolves the directory the binaries are installed in — the directory
    /// of the running executable (CLI or daemon; both install side by side),
    /// falling back to <c>~/.netclaw/bin</c>.
    /// </summary>
    public static string GetInstallDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is not null)
            return Path.GetDirectoryName(processPath)!;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".netclaw", "bin");
    }

    /// <summary>
    /// Downloads, verifies, and extracts every matching asset from
    /// <paramref name="result"/> into <paramref name="tempDir"/>. Returns a
    /// map of component name → extraction directory. Throws
    /// <see cref="SelfUpdateException"/> on any failure; the temp directory
    /// lifecycle is owned by the caller.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> DownloadAndExtractAsync(
        UpdateCheckResult result,
        string tempDir,
        HttpClient httpClient,
        Action<DownloadProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(tempDir);

        var extractedPaths = new Dictionary<string, string>();
        foreach (var asset in result.MatchingAssets)
        {
            log?.Invoke($"Downloading {asset.Component}...");
            var archivePath = Path.Combine(tempDir, Path.GetFileName(new Uri(asset.Url).AbsolutePath));

            await DownloadAndVerifyAsync(httpClient, asset, archivePath, progress, cancellationToken);

            var extractDir = Path.Combine(tempDir, asset.Component);
            Directory.CreateDirectory(extractDir);

            if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                await ExtractTarGzAsync(archivePath, extractDir, cancellationToken);
            else if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ZipFile.ExtractToDirectory(archivePath, extractDir);

            extractedPaths[asset.Component] = extractDir;
            log?.Invoke($"{asset.Component} verified.");
        }

        return extractedPaths;
    }

    /// <summary>
    /// Swaps every downloaded component into <paramref name="installDir"/>,
    /// backing up the previous binaries and setting the executable bit on
    /// Unix. Throws <see cref="SelfUpdateException"/> on failure; check
    /// <see cref="SelfUpdateException.TargetRestored"/> for whether the
    /// previously installed binary survived a failed swap.
    /// </summary>
    public static void SwapBinaries(
        IReadOnlyDictionary<string, string> extractedPaths,
        string installDir,
        Action<string>? warn = null)
    {
        Directory.CreateDirectory(installDir);

        foreach (var (component, extractDir) in extractedPaths)
        {
            var binaryName = OperatingSystem.IsWindows()
                ? $"{component}.exe"
                : component;

            var sourcePath = FindBinaryInExtracted(extractDir, binaryName);
            if (sourcePath is null)
                throw new SelfUpdateException($"Could not find {binaryName} in the downloaded archive.");

            var targetPath = Path.Combine(installDir, binaryName);
            var backupPath = targetPath + ".backup";

            try
            {
                // Swap with automatic rollback: a failed swap restores the
                // previous binary so the install directory is never left
                // without an executable (which would brick the CLI).
                SwapBinaryIntoPlace(sourcePath, targetPath, backupPath, warn);
            }
            catch (Exception ex)
            {
                var targetRestored = File.Exists(targetPath);
                var detail = targetRestored
                    ? $"Failed to replace {binaryName}: {ex.Message} The previous binary was restored."
                    : $"Failed to replace {binaryName}: {ex.Message} The install directory is missing {binaryName}; restore it from {binaryName}.backup.";
                throw new SelfUpdateException(detail, targetRestored, ex);
            }

            // Set executable permission on Unix
            if (!OperatingSystem.IsWindows())
                SetExecutable(targetPath, warn);
        }
    }

    /// <summary>
    /// Deletes leftover <c>.backup</c> files after a successful swap.
    /// The backup of the currently running binary is the image this process
    /// still executes from; on Windows DeleteFile on a running image fails
    /// with <see cref="UnauthorizedAccessException"/>. Leave it — the install
    /// step deletes stale backups before moving the new binary on the next
    /// update. A leftover backup must never turn a successful update into a
    /// fatal error, so any other delete failure only warns.
    /// </summary>
    /// <param name="extractedPaths">The swapped components (from <see cref="DownloadAndExtractAsync"/>).</param>
    /// <param name="installDir">The install directory the binaries were swapped into.</param>
    /// <param name="runningProcessPath">
    /// The running process's own binary path (<c>Environment.ProcessPath</c>), or <c>null</c> if unknown.
    /// </param>
    public static void CleanupBackupFiles(
        IReadOnlyDictionary<string, string> extractedPaths,
        string installDir,
        string? runningProcessPath,
        Action<string>? warn = null)
    {
        var runningBackupPath = runningProcessPath is not null
            ? runningProcessPath + ".backup"
            : null;

        foreach (var component in extractedPaths.Keys)
        {
            var binaryName = OperatingSystem.IsWindows()
                ? $"{component}.exe"
                : component;
            var backupPath = Path.Combine(installDir, binaryName + ".backup");
            CleanupBackupFile(backupPath, runningBackupPath, OperatingSystem.IsWindows(), warn);
        }
    }

    /// <summary>
    /// Downloads one asset's archive and verifies its SHA-256 digest.
    /// Throws <see cref="SelfUpdateException"/> on download or checksum failure.
    /// </summary>
    public static async Task DownloadAndVerifyAsync(
        HttpClient httpClient,
        BinaryAsset asset,
        string archivePath,
        Action<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        long? totalBytes;
        try
        {
            using var response = await httpClient.GetAsync(
                asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            totalBytes = response.Content.Headers.ContentLength is { } contentLength and > 0
                ? contentLength
                : (asset.SizeBytes > 0 ? asset.SizeBytes : null);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = File.Create(archivePath);

            if (progress is null)
            {
                await stream.CopyToAsync(fileStream, cancellationToken);
            }
            else
            {
                var buffer = new byte[64 * 1024];
                long copied = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                    progress(new DownloadProgress(asset.Component, copied, totalBytes));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SelfUpdateException(
                $"download failed for {asset.Component}: {ex.Message}", innerException: ex);
        }

        var hash = await ComputeFileSha256Async(archivePath);
        if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new SelfUpdateException(
                $"checksum mismatch for {asset.Component} (expected {asset.Sha256}, got {hash})");
    }

    /// <summary>
    /// Replaces <paramref name="targetPath"/> with
    /// <paramref name="sourcePath"/>, preserving the previous binary at
    /// <paramref name="backupPath"/>. On failure the previous binary is
    /// restored to <paramref name="targetPath"/> so a failed swap never
    /// leaves the install directory without an executable.
    /// </summary>
    public static void SwapBinaryIntoPlace(
        string sourcePath, string targetPath, string backupPath, Action<string>? warn = null)
    {
        var movedOldToBackup = false;
        try
        {
            if (File.Exists(targetPath))
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                File.Move(targetPath, backupPath);
                movedOldToBackup = true;
            }

            File.Move(sourcePath, targetPath);
        }
        catch
        {
            // Roll the previous binary back so a failed swap leaves a
            // working binary in place instead of a missing executable.
            if (movedOldToBackup && !File.Exists(targetPath) && File.Exists(backupPath))
            {
                try { File.Move(backupPath, targetPath); }
                catch (Exception rollbackEx)
                {
                    // Best-effort rollback; the original swap failure is
                    // rethrown below and reported by the host.
                    warn?.Invoke(
                        $"warn: failed to restore {targetPath} from {backupPath}: {rollbackEx.Message}");
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Deletes a leftover <c>.backup</c> file. On Windows the backup of the
    /// currently running image cannot be deleted — the process still executes
    /// from that file. Such backups are removed by the install step on the
    /// next update. Any other delete failure only warns.
    /// </summary>
    /// <param name="backupPath">The backup file to delete.</param>
    /// <param name="runningBackupPath">
    /// The backup path of the currently running process
    /// (<c>Environment.ProcessPath + ".backup"</c>), or <c>null</c> if unknown.
    /// </param>
    /// <param name="isWindows">True when running on Windows.</param>
    public static void CleanupBackupFile(
        string backupPath, string? runningBackupPath, bool isWindows, Action<string>? warn = null)
    {
        if (isWindows
            && runningBackupPath is not null
            && string.Equals(backupPath, runningBackupPath, StringComparison.OrdinalIgnoreCase))
        {
            // Running image — cannot be deleted on Windows.
            return;
        }

        try
        {
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
        catch (Exception ex)
        {
            warn?.Invoke($"warn: could not remove backup {backupPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Locates the binary with <paramref name="binaryName"/> inside an
    /// extracted archive directory — directly in the directory or one level deep.
    /// </summary>
    public static string? FindBinaryInExtracted(string extractDir, string binaryName)
    {
        var direct = Path.Combine(extractDir, binaryName);
        if (File.Exists(direct))
            return direct;

        foreach (var dir in Directory.GetDirectories(extractDir))
        {
            var candidate = Path.Combine(dir, binaryName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Sets the executable bit on Unix via <c>chmod +x</c>. Warn-only on failure.
    /// </summary>
    public static void SetExecutable(string path, Action<string>? warn = null)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit();
        }
        catch (Exception ex)
        {
            warn?.Invoke($"warn: chmod +x failed for {path}: {ex.Message}");
        }
    }

    private static async Task<string> ComputeFileSha256Async(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task ExtractTarGzAsync(
        string archivePath, string extractDir, CancellationToken cancellationToken)
    {
        // Use tar command — available on all supported platforms (Linux, Windows 10+)
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "tar",
            Arguments = $"xzf \"{archivePath}\" -C \"{extractDir}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.WaitForExitAsync(cancellationToken);

        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync(cancellationToken);
            throw new SelfUpdateException($"tar extraction failed: {stderr}");
        }
    }
}
