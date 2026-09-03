// -----------------------------------------------------------------------
// <copyright file="AcsPolicyDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class AcsPolicyDoctorCheckTests : IDisposable
{
    private readonly string _basePath;
    private readonly NetclawPaths _paths;

    public AcsPolicyDoctorCheckTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"));
        _paths = new NetclawPaths(_basePath);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        if (Directory.Exists(_basePath))
            Directory.Delete(_basePath, recursive: true);
    }

    private async Task WriteConfigAsync(string json)
        => await File.WriteAllTextAsync(_paths.NetclawConfigPath, json, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Passes_with_disabled_note_when_section_absent()
    {
        await WriteConfigAsync("""{ "configVersion": 1 }""");

        var result = await new AcsPolicyDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("Disabled", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Passes_with_disabled_note_when_enabled_is_false()
    {
        await WriteConfigAsync("""
            {
              "configVersion": 1,
              "Governance": { "Enabled": false }
            }
            """);

        var result = await new AcsPolicyDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("Disabled", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Errors_when_enabled_but_manifest_is_missing()
    {
        await WriteConfigAsync("""
            {
              "configVersion": 1,
              "Governance": { "Enabled": true }
            }
            """);

        var result = await new AcsPolicyDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("manifest", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Errors_when_manifest_binds_non_rego_policy()
    {
        var manifestDir = Path.Combine(_paths.ConfigDirectory, "acs");
        Directory.CreateDirectory(manifestDir);
        await File.WriteAllTextAsync(
            Path.Combine(manifestDir, "manifest.yaml"),
            """
            agent_control_specification_version: "0.3.1-beta"
            metadata:
              name: doctor-agent
            policies:
              p:
                type: cedar
            intervention_points:
              pre_tool_call:
                policy:
                  id: p
            """,
            TestContext.Current.CancellationToken);
        await WriteConfigAsync("""
            {
              "configVersion": 1,
              "Governance": { "Enabled": true }
            }
            """);

        var result = await new AcsPolicyDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("cedar", result.Message, StringComparison.Ordinal);
    }
}
