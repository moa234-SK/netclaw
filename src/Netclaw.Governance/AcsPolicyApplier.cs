// -----------------------------------------------------------------------
// <copyright file="AcsPolicyApplier.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using AgentControlSpecification;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Governance;

/// <summary>A file in a panel-composed policy set. Flat <c>.rego</c> file name plus content.</summary>
public sealed record PolicySyncFile(string Name, string Content);

/// <summary>Outcome of applying a policy set: applied, or rejected with the reason (old policy kept).</summary>
public sealed record AcsPolicyApplyResult(bool Applied, string? Error);

/// <summary>
/// Validates and applies a panel-composed Rego policy set under the
/// manifest's <c>policy</c> directory, then hot-reloads the governance
/// runtime. Fail-loud and fail-safe: the set is validated (file names, OPA +
/// artifact validation against the manifest) in a staging directory before
/// anything on disk changes, the live directory is swapped atomically with a
/// backup that is restored if the rebuilt runtime fails to construct, and any
/// rejection keeps the previously applied policy untouched.
/// </summary>
public sealed class AcsPolicyApplier
{
    private const string PolicyDirectoryName = "policy";
    private const int MaxFiles = 200;

    private readonly GovernanceConfig _config;
    private readonly NetclawPaths _paths;
    private readonly AcsGovernance _governance;
    private readonly ILogger<AcsPolicyApplier> _logger;

    public AcsPolicyApplier(
        GovernanceConfig config,
        NetclawPaths paths,
        AcsGovernance governance,
        ILogger<AcsPolicyApplier> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _governance = governance ?? throw new ArgumentNullException(nameof(governance));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public AcsPolicyApplyResult Apply(IReadOnlyList<PolicySyncFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var manifestPath = Path.IsPathRooted(_config.ManifestPath)
            ? _config.ManifestPath
            : Path.Combine(_paths.ConfigDirectory, _config.ManifestPath);
        if (_config.Enabled && !File.Exists(manifestPath))
            return Reject(
                "Governance is enabled but the ACS manifest at '" + manifestPath
                + "' does not exist; cannot apply or validate a policy set.");

        var manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidOperationException("Manifest path has no directory.");
        var policyDirectory = Path.Combine(manifestDirectory, PolicyDirectoryName);
        Directory.CreateDirectory(manifestDirectory);

        var rejected = ValidateFileNames(files);
        if (rejected is not null)
            return Reject(rejected);

        // An empty policy directory would make the daemon refuse its next
        // start while governance is enabled (default-deny posture); accept it
        // only when governance is off and the files are inert anyway.
        if (files.Count == 0 && _config.Enabled)
        {
            return Reject(
                "The panel serves an empty policy set. Refusing to remove every policy while "
                + "governance is enabled: the daemon would fail to start after a restart. Assign "
                + "at least one policy in the panel or disable governance.");
        }

        var stagingDirectory = Path.Combine(manifestDirectory, $".policy-staging-{Guid.NewGuid():N}");
        var backupDirectory = Path.Combine(manifestDirectory, $".policy-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        var stagingConsumed = false;
        try
        {
            foreach (var file in files)
            {
                File.WriteAllText(
                    Path.Combine(stagingDirectory, file.Name),
                    file.Content ?? string.Empty);
            }

            // Validate exactly what the daemon would load after the swap:
            // the staged files (as policy/) plus any operator rego modules
            // living elsewhere under the manifest directory.
            if (_config.Enabled)
            {
                var opaPath = OpaResolver.Resolve(_config.OpaPath);
                if (opaPath is null)
                {
                    return Reject(
                        "No OPA binary resolves (Governance.OpaPath, ACS_OPA_PATH, then PATH); "
                        + "cannot validate the policy set.");
                }

                var manifestYaml = File.ReadAllText(manifestPath);
                var modules = CollectModulesForValidation(manifestDirectory, policyDirectory, stagingDirectory);
                var validation = ArtifactValidator.Validate(manifestYaml, modules, opaPath);
                if (!validation.Valid)
                {
                    var diagnostics = string.Join("; ", validation.Diagnostics.Select(static d =>
                        $"{d.Component}/{d.Code}: {d.Message}"));
                    return Reject("Policy set failed validation: " + diagnostics);
                }
            }

            // Atomic swap of the policy directory (staging → live, old → backup).
            if (Directory.Exists(policyDirectory))
                Directory.Move(policyDirectory, backupDirectory);
            try
            {
                Directory.Move(stagingDirectory, policyDirectory);
                stagingConsumed = true;
            }
            catch (IOException)
            {
                if (!Directory.Exists(policyDirectory) && Directory.Exists(backupDirectory))
                    Directory.Move(backupDirectory, policyDirectory);
                throw;
            }

            // Rebuild the runtime from the swapped directory and hot-reload it.
            // A failure here restores the old files and keeps the old runtime.
            if (_config.Enabled)
            {
                try
                {
                    var rebuilt = AcsGovernance.Build(_config, _paths, _logger);
                    if (!_governance.TryAdopt(rebuilt))
                    {
                        rebuilt.Dispose();
                        return Reject("Governance runtime is disabled; cannot adopt the rebuilt policy runtime.");
                    }

                    rebuilt.Dispose(); // its control and sink were adopted; this is a no-op shell.
                }
                catch (Exception ex)
                {
                    RestoreBackup(policyDirectory, backupDirectory);
                    return Reject("The rebuilt policy runtime failed to construct: " + ex.Message);
                }
            }

            _logger.LogInformation(
                "Applied panel policy set: {FileCount} file(s) under {PolicyDirectory}.",
                files.Count, policyDirectory);

            return new AcsPolicyApplyResult(true, null);
        }
        finally
        {
            TryDeleteDirectory(stagingConsumed ? null : stagingDirectory);
            TryDeleteDirectory(backupDirectory);
        }
    }

    private static string? ValidateFileNames(IReadOnlyList<PolicySyncFile> files)
    {
        if (files.Count > MaxFiles)
            return $"Policy set has {files.Count} files; at most {MaxFiles} are accepted.";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var name = file.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name)
                || !name.EndsWith(".rego", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(name) != name
                || Path.IsPathRooted(name))
            {
                return $"Policy file name '{file.Name}' is invalid: expected a flat .rego file name.";
            }

            if (!seen.Add(name))
                return $"Duplicate policy file name '{name}'.";
        }

        return null;
    }

    /// <summary>
    /// The module map the ACS runtime will see after the swap: operator rego
    /// files under the manifest directory (outside the policy directory) plus
    /// the staged files keyed as <c>policy/&lt;name&gt;</c>.
    /// </summary>
    private static Dictionary<string, string> CollectModulesForValidation(
        string manifestDirectory,
        string policyDirectory,
        string stagingDirectory)
    {
        var modules = new Dictionary<string, string>(StringComparer.Ordinal);

        var policyRoot = Path.GetFullPath(policyDirectory) + Path.DirectorySeparatorChar;
        var stagingRoot = Path.GetFullPath(stagingDirectory) + Path.DirectorySeparatorChar;

        foreach (var file in Directory.EnumerateFiles(
                     manifestDirectory, "*.rego", SearchOption.AllDirectories).Take(MaxFiles))
        {
            var fullPath = Path.GetFullPath(file);
            if (fullPath.StartsWith(policyRoot, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            var key = Path.GetRelativePath(manifestDirectory, file).Replace('\\', '/');
            modules[key] = File.ReadAllText(file);
        }

        foreach (var file in Directory.EnumerateFiles(stagingDirectory, "*.rego", SearchOption.TopDirectoryOnly))
        {
            var key = PolicyDirectoryName + "/" + Path.GetFileName(file);
            modules[key] = File.ReadAllText(file);
        }

        return modules;
    }

    private static void RestoreBackup(string policyDirectory, string backupDirectory)
    {
        try
        {
            if (Directory.Exists(policyDirectory))
                Directory.Delete(policyDirectory, recursive: true);
            if (Directory.Exists(backupDirectory))
                Directory.Move(backupDirectory, policyDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next successful apply overwrites the directory
            // again. Loud in logs, never a crash loop.
        }
    }

    private static void TryDeleteDirectory(string? directory)
    {
        if (directory is null)
            return;

        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Staging/backup leftovers are harmless; next apply uses new guids.
        }
    }

    private AcsPolicyApplyResult Reject(string error)
    {
        _logger.LogWarning("Panel policy set rejected: {Error}", error);
        return new AcsPolicyApplyResult(false, error);
    }
}
