// -----------------------------------------------------------------------
// <copyright file="RegistrationConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Registration;

/// <summary>
/// Self-registration to an external control panel. Bound from the
/// <c>Registration</c> section of netclaw.json. A null or empty
/// <see cref="BaseUrl"/> turns the feature off; the daemon then makes no
/// outbound registration calls.
/// </summary>
public sealed class RegistrationConfig
{
    /// <summary>
    /// Control panel base URL. The daemon derives both endpoints from it:
    /// <c>{base}/api/v1/agent/status</c> for registration and status
    /// changes, <c>{base}/api/v1/agent/heartbeat</c> for liveness.
    /// Must be an absolute HTTP or HTTPS URI when set. A path prefix (for a
    /// reverse proxy) is allowed.
    /// </summary>
    [ConfigValue(Key = "Registration.BaseUrl", PersistTo = ConfigPersistStore.NetclawJson)]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Base URL of this daemon's HTTP API, advertised to the control panel
    /// in the registration envelope so the panel can issue commands.
    /// Explicit consent only: the daemon never derives this from its bind
    /// address or exposure mode — advertising a command surface is an
    /// authority decision. Must be an absolute HTTP or HTTPS URI when set.
    /// Unset keeps the agent telemetry-only.
    /// </summary>
    [ConfigValue(Key = "Registration.ApiBaseUrl", PersistTo = ConfigPersistStore.NetclawJson)]
    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// Lets the startup registration envelope carry a freshly minted device
    /// pairing code so the control panel can exchange it for a command
    /// token without the manual <c>netclaw daemon pair</c> step. Explicit
    /// consent only, and it requires <see cref="ApiBaseUrl"/>: the panel
    /// exchanges the code at the advertised API URL, so a code without a
    /// URL has no consumer. The code rides the startup envelope only,
    /// never heartbeats or change sends. Default: <c>false</c>.
    /// </summary>
    [ConfigValue(Key = "Registration.AutoPair", PersistTo = ConfigPersistStore.NetclawJson)]
    public bool AutoPair { get; set; }

    /// <summary>
    /// Temporary switch so the operator can pause registration without
    /// deleting the link. Default: <c>true</c>.
    /// </summary>
    [ConfigValue(Key = "Registration.Enabled", PersistTo = ConfigPersistStore.NetclawJson)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Seconds between registration heartbeat sends. Default: 300.
    /// Minimum: 30. Maximum: 86400.
    /// </summary>
    [ConfigValue(Key = "Registration.HeartbeatIntervalSeconds", PersistTo = ConfigPersistStore.NetclawJson)]
    public int HeartbeatIntervalSeconds { get; set; } = 300;
}

/// <summary>
/// Shared validation for <see cref="RegistrationConfig"/>. Daemon startup,
/// the config TUI, and doctor all reject the same values with the same
/// messages, so one surface cannot persist what another rejects.
/// </summary>
public static class RegistrationConfigValidator
{
    public const int MinimumHeartbeatIntervalSeconds = 30;
    public const int MaximumHeartbeatIntervalSeconds = 86400;

    /// <summary>
    /// True when a control panel base URL is set. A blank URL is the valid off
    /// state, not an error.
    /// </summary>
    public static bool IsConfigured(RegistrationConfig config)
        => !string.IsNullOrWhiteSpace(config.BaseUrl);

    /// <summary>
    /// Validates the config. A blank URL is valid (feature off). A set URL
    /// must be an absolute HTTP or HTTPS URI. The heartbeat interval must
    /// stay inside its bounds even while the feature is off, so a later
    /// enable can never activate an out-of-range interval silently.
    /// </summary>
    public static bool TryValidate(RegistrationConfig config, out string error)
    {
        ArgumentNullException.ThrowIfNull(config);
        error = string.Empty;

        if (IsConfigured(config))
        {
            var trimmed = config.BaseUrl!.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))
            {
                error = "Registration.BaseUrl must be an absolute HTTP or HTTPS URI.";
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(config.ApiBaseUrl)
            && ( !Uri.TryCreate(config.ApiBaseUrl.Trim(), UriKind.Absolute, out var apiUri)
                || apiUri.Scheme is not ("http" or "https")))
        {
            error = "Registration.ApiBaseUrl must be an absolute HTTP or HTTPS URI when set.";
            return false;
        }

        if (config.AutoPair && string.IsNullOrWhiteSpace(config.ApiBaseUrl))
        {
            // Validated even while the feature is off, mirroring the
            // heartbeat-interval rule: a later enable must never activate
            // an unusable auto-pairing silently.
            error = "Registration.AutoPair requires Registration.ApiBaseUrl.";
            return false;
        }

        if (config.HeartbeatIntervalSeconds is < MinimumHeartbeatIntervalSeconds
            or > MaximumHeartbeatIntervalSeconds)
        {
            error = $"Registration.HeartbeatIntervalSeconds must be between "
                + $"{MinimumHeartbeatIntervalSeconds} and {MaximumHeartbeatIntervalSeconds}.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Fail-loud variant for daemon startup: throws
    /// <see cref="InvalidOperationException"/> naming the invalid value.
    /// </summary>
    public static void ValidateOrThrow(RegistrationConfig config)
    {
        if (!TryValidate(config, out var error))
            throw new InvalidOperationException($"Invalid Registration configuration: {error}");
    }
}
