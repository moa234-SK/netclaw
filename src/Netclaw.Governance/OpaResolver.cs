// -----------------------------------------------------------------------
// <copyright file="OpaResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;

namespace Netclaw.Governance;

/// <summary>
/// Resolves and verifies the OPA binary required by the ACS Rego dispatcher.
/// Resolution order: the configured <c>Governance.OpaPath</c>, then the
/// <c>ACS_OPA_PATH</c> environment variable, then <c>PATH</c>. A configured or
/// env-provided value may name the binary itself or its containing directory.
/// </summary>
public static class OpaResolver
{
    private static readonly string BinaryName = OperatingSystem.IsWindows() ? "opa.exe" : "opa";
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Resolve the OPA binary path, or null when nothing resolves. Does not
    /// execute the binary.
    /// </summary>
    public static string? Resolve(string? configuredOpaPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredOpaPath))
            return ResolveFromLocation(configuredOpaPath);

        var envPath = Environment.GetEnvironmentVariable("ACS_OPA_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            return ResolveFromLocation(envPath);

        return ResolveFromPath();
    }

    /// <summary>
    /// Run <c>opa version</c> against the resolved binary. Returns the first
    /// version line, or null when the binary exists but does not report one.
    /// Throws when the binary cannot be executed at all.
    /// </summary>
    public static string? Verify(string opaPath)
    {
        var info = new ProcessStartInfo
        {
            FileName = opaPath,
            Arguments = "version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Failed to launch OPA binary at '{opaPath}'.");
        using var cts = new CancellationTokenSource(VersionTimeout);

        try
        {
            process.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                $"'opa version' at '{opaPath}' did not complete within {VersionTimeout.TotalSeconds}s.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"'opa version' at '{opaPath}' exited with code {process.ExitCode}.");

        var firstLine = process.StandardOutput.ReadLine()?.Trim();
        return string.IsNullOrEmpty(firstLine) ? null : firstLine;
    }

    private static string? ResolveFromLocation(string location)
    {
        location = location.Trim('"').Trim();
        if (File.Exists(location))
            return Path.GetFullPath(location);

        if (Directory.Exists(location))
        {
            var inDir = Path.Combine(location, BinaryName);
            return File.Exists(inDir) ? Path.GetFullPath(inDir) : null;
        }

        return null;
    }

    private static string? ResolveFromPath()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
            return null;

        foreach (var dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // Path.Combine throws on malformed segments (invalid chars, embedded
            // colons); those entries cannot contain OPA, so they are skipped by
            // this pre-check rather than caught.
            if (!IsSafePathSegment(dir))
                continue;

            var candidate = Path.Combine(dir.Trim(), BinaryName);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static bool IsSafePathSegment(string segment)
    {
        var trimmed = segment.Trim();
        if (trimmed.Length == 0)
            return false;

        return trimmed.IndexOfAny(Path.GetInvalidPathChars()) < 0;
    }
}
