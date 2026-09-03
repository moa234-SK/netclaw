// -----------------------------------------------------------------------
// <copyright file="AcsGovernance.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using AgentControlSpecification;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using YamlDotNet.Serialization;

namespace Netclaw.Governance;

/// <summary>
/// Holds the constructed ACS <see cref="AgentControl"/> runtime and its
/// enabled state. Always non-null in the DI container: when governance is
/// disabled the instance carries <see cref="Enabled"/>=false and a null
/// control, so consumers can branch explicitly without nullable plumbing.
/// </summary>
public sealed class AcsGovernance : IDisposable
{
    /// <summary>
    /// A disabled instance with no runtime. Used when <c>Governance:Enabled</c>
    /// is false (the default).
    /// </summary>
    public static AcsGovernance Disabled { get; } = new(enabled: false, control: null, sink: null, manifestPath: null, opaPath: null, opaVersion: null);

    private AcsGovernance(
        bool enabled,
        AgentControl? control,
        OtelMetricsTelemetrySink? sink,
        string? manifestPath,
        string? opaPath,
        string? opaVersion)
    {
        Enabled = enabled;
        Control = control;
        _sink = sink;
        ManifestPath = manifestPath;
        OpaPath = opaPath;
        OpaVersion = opaVersion;
    }

    /// <summary>
    /// Whether ACS policy evaluation is active. When false, <see cref="Control"/>
    /// is null and every consumer must proceed without policy evaluation. This is
    /// an explicit, operator-chosen feature toggle — not a silent skip.
    /// </summary>
    public bool Enabled { get; }

    /// <summary>The ACS runtime, or null when <see cref="Enabled"/> is false.</summary>
    public AgentControl? Control { get; }

    private readonly OtelMetricsTelemetrySink? _sink;

    /// <summary>The resolved manifest path, or null when disabled.</summary>
    public string? ManifestPath { get; }

    /// <summary>The resolved OPA binary path, or null when disabled.</summary>
    public string? OpaPath { get; }

    /// <summary>The first line of <c>opa version</c>, or null when disabled or unreported.</summary>
    public string? OpaVersion { get; }

    private bool _disposed;

    /// <summary>
    /// Test-only construction of an enabled instance around a pre-built
    /// <see cref="AgentControl"/> (typically over a fake runtime), bypassing
    /// manifest validation and OPA resolution.
    /// </summary>
    internal static AcsGovernance CreateEnabledForTesting(AgentControl control, string? manifestPath = null)
        => new(enabled: true, control, sink: null, manifestPath, opaPath: null, opaVersion: null);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sink?.Dispose();
    }

    /// <summary>
    /// Construct an <see cref="AcsGovernance"/> from operator config. Returns
    /// <see cref="Disabled"/> when governance is off. Throws (fail loud, no
    /// silent fallback) when governance is enabled but the runtime cannot be
    /// fully constructed: missing manifest, invalid manifest or Rego, a
    /// non-<c>rego</c> policy type, no bound <c>pre_tool_call</c> point, or no
    /// resolvable OPA binary. An absent or partial policy would implicitly
    /// allow every call and violate the default-deny posture.
    /// </summary>
    public static AcsGovernance Build(
        GovernanceConfig config,
        NetclawPaths paths,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        if (!config.Enabled)
        {
            logger.LogDebug("ACS policy governance disabled (Governance:Enabled is false).");
            return Disabled;
        }

        var manifestPath = Path.IsPathRooted(config.ManifestPath)
            ? config.ManifestPath
            : Path.Combine(paths.ConfigDirectory, config.ManifestPath);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                "Governance is enabled but the ACS manifest was not found at '" + manifestPath
                + "'. Relative paths resolve against the Netclaw config directory ("
                + paths.ConfigDirectory + "). Refusing to start rather than silently running "
                + "without the configured policy.");
        }

        var manifestYaml = File.ReadAllText(manifestPath);
        AssertRegoOnlyManifest(manifestYaml, manifestPath);

        var opaPath = OpaResolver.Resolve(config.OpaPath)
            ?? throw new InvalidOperationException(
                "Governance is enabled with a Rego policy but no OPA binary resolves. "
                + "Set Governance.OpaPath, the ACS_OPA_PATH environment variable, or install OPA on PATH. "
                + "Refusing to start: without OPA every tool call would fail closed at evaluation time.");

        string? opaVersion;
        try
        {
            opaVersion = OpaResolver.Verify(opaPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Governance is enabled but the resolved OPA binary at '" + opaPath
                + "' failed verification: " + ex.Message, ex);
        }

        // The SDK reads ACS_OPA_PATH into the native environment at runtime
        // construction; an explicit config value must win over a stale env var.
        if (!string.IsNullOrWhiteSpace(config.OpaPath))
            Environment.SetEnvironmentVariable("ACS_OPA_PATH", opaPath);

        var regoModules = CollectRegoModules(Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidOperationException("Manifest path has no directory."));
        var validation = ArtifactValidator.Validate(manifestYaml, regoModules, opaPath);
        if (!validation.Valid)
        {
            var diagnostics = string.Join("; ", validation.Diagnostics.Select(static d =>
                $"{d.Component}/{d.Code}: {d.Message}"));
            throw new InvalidOperationException(
                "Governance is enabled but the ACS manifest at '" + manifestPath
                + "' failed validation: " + diagnostics);
        }

        var sink = new OtelMetricsTelemetrySink();
        AgentControl control;
        try
        {
            control = AgentControl.FromPath(manifestPath, telemetrySink: sink);
        }
        catch (Exception ex)
        {
            sink.Dispose();
            throw new InvalidOperationException(
                "Governance is enabled but the ACS runtime failed to construct from '"
                + manifestPath + "': " + ex.Message, ex);
        }

        logger.LogInformation(
            "ACS policy governance enabled: manifest {ManifestPath}, OPA {OpaPath} ({OpaVersion}), {RegoModuleCount} Rego module(s).",
            manifestPath, opaPath, opaVersion ?? "version unreported", regoModules.Count);

        return new AcsGovernance(enabled: true, control, sink, manifestPath, opaPath, opaVersion);
    }

    /// <summary>
    /// Enforce the phase-1 manifest contract: at least one policy, every policy
    /// of type <c>rego</c>, and the <c>pre_tool_call</c> intervention point
    /// bound. Anything else would either run an untested engine or never be
    /// consulted, which is an implicit allow.
    /// </summary>
    internal static void AssertRegoOnlyManifest(string manifestYaml, string manifestPath)
    {
        object? root;
        try
        {
            root = new DeserializerBuilder().Build().Deserialize(manifestYaml);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Governance manifest at '" + manifestPath + "' is not valid YAML: " + ex.Message, ex);
        }

        if (root is not Dictionary<object, object> rootMap)
            throw new InvalidOperationException(
                "Governance manifest at '" + manifestPath + "' must be a YAML mapping.");

        if (!rootMap.TryGetValue("policies", out var policiesObj)
            || policiesObj is not Dictionary<object, object> policies
            || policies.Count == 0)
        {
            throw new InvalidOperationException(
                "Governance manifest at '" + manifestPath + "' declares no policies. An empty "
                + "policy set would implicitly allow every tool call; refusing to start.");
        }

        foreach (var (nameObj, policyObj) in policies)
        {
            var type = policyObj is Dictionary<object, object> policyMap
                       && policyMap.TryGetValue("type", out var typeObj)
                ? typeObj?.ToString()
                : null;
            if (!string.Equals(type, "rego", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Governance manifest at '" + manifestPath + "' binds policy '" + nameObj
                    + "' with type '" + (type ?? "(missing)") + "'. Only 'rego' policies are "
                    + "supported by this Netclaw version. Cedar, custom, and test policy types "
                    + "are rejected so no untested engine is silently wired.");
            }
        }

        if (!rootMap.TryGetValue("intervention_points", out var pointsObj)
            || pointsObj is not Dictionary<object, object> points
            || !points.ContainsKey("pre_tool_call"))
        {
            throw new InvalidOperationException(
                "Governance manifest at '" + manifestPath + "' does not bind the "
                + "pre_tool_call intervention point. Netclaw evaluates that point only; an "
                + "unbound point would never consult the policy, which is an implicit allow.");
        }

        // The ACS runtime resolves the called tool against the manifest's
        // tools catalog; a tool that is absent from the catalog denies with
        // runtime_error:tool_unknown. An empty catalog would therefore deny
        // every tool call at evaluation time — almost certainly operator
        // error, and expressible as an explicit deny policy when intended.
        if (!rootMap.TryGetValue("tools", out var toolsObj)
            || toolsObj is not Dictionary<object, object> tools
            || tools.Count == 0)
        {
            throw new InvalidOperationException(
                "Governance manifest at '" + manifestPath + "' declares no tools catalog. "
                + "ACS resolves every called tool against the manifest 'tools' block; a tool "
                + "missing from it is denied (runtime_error:tool_unknown). An empty catalog "
                + "would deny every tool call at runtime. List the governed tools, or express "
                + "a full lockdown as an explicit deny policy.");
        }
    }

    private static Dictionary<string, string> CollectRegoModules(string manifestDirectory)
    {
        var modules = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(
                     manifestDirectory, "*.rego", SearchOption.AllDirectories).Take(200))
        {
            var key = Path.GetRelativePath(manifestDirectory, file).Replace('\\', '/');
            modules[key] = File.ReadAllText(file);
        }

        return modules;
    }
}
