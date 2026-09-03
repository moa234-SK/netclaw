// -----------------------------------------------------------------------
// <copyright file="AcsPolicyDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using Netclaw.Governance;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Validates the ACS policy governance configuration: manifest presence,
/// manifest contract (rego-only, pre_tool_call bound), artifact validation,
/// and OPA resolution. A disabled section passes with a note rather than
/// failing, because disabled is the default and fully supported state.
/// </summary>
public sealed class AcsPolicyDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, error) = DoctorJsonConfigReader.TryReadConfig(paths);
        if (error is not null)
            return Task.FromResult(error);

        GovernanceConfig governanceConfig;
        if (root?["Governance"] is JsonObject governanceNode)
        {
            try
            {
                governanceConfig = governanceNode.Deserialize<GovernanceConfig>(JsonDefaults.ConfigRead) ?? new GovernanceConfig();
            }
            catch (Exception ex)
            {
                return Task.FromResult(DoctorCheckResult.Error(
                    "ACS Policy Governance",
                    $"Failed to parse Governance configuration: {ex.Message}",
                    "Fix the Governance section in netclaw.json."));
            }
        }
        else
        {
            governanceConfig = new GovernanceConfig();
        }

        if (!governanceConfig.Enabled)
        {
            return Task.FromResult(DoctorCheckResult.Pass(
                "ACS Policy Governance",
                "Disabled (Governance:Enabled is false); the native tool gate runs unchanged."));
        }

        try
        {
            using var governance = AcsGovernance.Build(
                governanceConfig,
                paths,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            return Task.FromResult(DoctorCheckResult.Pass(
                "ACS Policy Governance",
                $"Enabled: manifest {governance.ManifestPath}, OPA {governance.OpaPath}"
                + $" ({governance.OpaVersion ?? "version unreported"})."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "ACS Policy Governance",
                ex.Message,
                "The daemon refuses to start in this state. Fix the manifest/OPA setup or set Governance:Enabled to false."));
        }
    }
}
