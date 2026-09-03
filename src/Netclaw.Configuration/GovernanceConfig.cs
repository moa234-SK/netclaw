// -----------------------------------------------------------------------
// <copyright file="GovernanceConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Operator-facing configuration for the Agent Control Specification (ACS)
/// policy integration. Bound from the <c>Governance</c> section in
/// <c>netclaw.json</c>.
///
/// Disabled by default. When <see cref="Enabled"/> is true, ACS Rego policy
/// evaluation runs as an additive deny-overrides layer over the existing
/// <see cref="Netclaw.Actors.Tools.ToolAccessPolicy"/> gate: a call blocked by
/// either gate is blocked, and the policy can only tighten, never loosen. The
/// ACS runtime (<c>AgentControlSpecification</c>) is referenced by the
/// <c>Netclaw.Governance</c> assembly; this POCO carries no ACS dependency so
/// it can live in the contract layer and be schema-bound.
/// </summary>
public sealed class GovernanceConfig
{
    /// <summary>
    /// Master switch for ACS policy evaluation. When false (the default), no
    /// ACS types are constructed and the tool gate behaves exactly as before.
    /// When true, <see cref="ManifestPath"/> must resolve to a valid ACS
    /// manifest that binds only <c>rego</c> policies, and an OPA binary must
    /// resolve, or the daemon refuses to start. No silent fallback to an empty
    /// policy that would implicitly allow everything.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Path to the ACS manifest. Relative paths resolve against the Netclaw
    /// config directory. The manifest's Rego bundle paths resolve relative to
    /// the manifest, matching the ACS convention of co-locating policy files.
    /// </summary>
    public string ManifestPath { get; set; } = "acs/manifest.yaml";

    /// <summary>
    /// Optional explicit path to the OPA binary (or its containing directory)
    /// used by the ACS Rego dispatcher. When unset, OPA resolves from the
    /// <c>ACS_OPA_PATH</c> environment variable, then from <c>PATH</c>.
    /// Required to resolve when <see cref="Enabled"/> is true.
    /// </summary>
    public string? OpaPath { get; set; }
}
