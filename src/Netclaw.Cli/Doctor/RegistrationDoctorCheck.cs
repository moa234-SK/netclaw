// -----------------------------------------------------------------------
// <copyright file="RegistrationDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Registration;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Reports the self-registration state. Not configured and disabled are both
/// valid Pass states (the off state is a feature toggle, not a fault). An
/// invalid configured URL is an Error; a persisted failed send is a Warning.
/// Reads the same state file the daemon writes, never the live daemon.
/// </summary>
public sealed class RegistrationDoctorCheck : IDoctorCheck
{
    private const string CheckName = "Registration";

    private readonly RegistrationConfig _config;
    private readonly RegistrationStateStore _stateStore;

    public RegistrationDoctorCheck(RegistrationConfig config, NetclawPaths paths)
    {
        _config = config;
        _stateStore = new RegistrationStateStore(paths);
    }

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!RegistrationConfigValidator.IsConfigured(_config))
            return Task.FromResult(DoctorCheckResult.Pass(CheckName, "Not configured."));

        if (!_config.Enabled)
            return Task.FromResult(DoctorCheckResult.Pass(CheckName, "Configured but disabled."));

        if (!RegistrationConfigValidator.TryValidate(_config, out var error))
            return Task.FromResult(DoctorCheckResult.Error(
                CheckName,
                error,
                "Fix the Registration section in netclaw.json, then run `netclaw doctor` again."));

        var state = _stateStore.TryRead();
        switch (state?.State)
        {
            case RegistrationState.StateFailed:
                return Task.FromResult(DoctorCheckResult.Warning(
                    CheckName,
                    $"Last registration send failed: {state.ErrorDetail ?? "unknown error"}",
                    "Check that the control panel is reachable. The daemon retries on the next heartbeat."));
            case RegistrationState.StateRegistered:
                return Task.FromResult(DoctorCheckResult.Pass(
                    CheckName,
                    $"Registered (last send {state.LastRegisteredUtc:u})."));
            default:
                return Task.FromResult(DoctorCheckResult.Pass(
                    CheckName,
                    "Configured; no registration recorded yet."));
        }
    }
}
