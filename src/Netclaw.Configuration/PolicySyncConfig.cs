// -----------------------------------------------------------------------
// <copyright file="PolicySyncConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Operator-facing configuration for pulling panel-composed ACS policies.
/// Bound from the <c>PolicySync</c> section in <c>netclaw.json</c>.
///
/// Disabled by default. When enabled, the daemon polls the control panel
/// (the same one configured under <c>Registration.BaseUrl</c> — there is no
/// separate URL) for the Rego policy set assigned to this agent, validates it
/// against the manifest and OPA before persisting anything, applies it
/// atomically under the manifest's <c>policy</c> directory (which the panel
/// then owns), and hot-reloads the governance runtime. A rejected set keeps
/// the previously applied policy; the error is reported back to the panel on
/// the next poll. Enabling PolicySync without a Registration.BaseUrl fails
/// daemon startup loudly.
/// </summary>
public sealed class PolicySyncConfig
{
    /// <summary>
    /// Master switch. When false (the default), no outbound calls are made
    /// and on-disk policies are untouched.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How often (in minutes) to poll the panel. Default: 5. Set to 0 to
    /// sync only at daemon startup. Clamped to 0–1440.
    /// </summary>
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>
    /// HTTP timeout in seconds for panel requests. Default: 30. Clamped to 1–300.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}
