// -----------------------------------------------------------------------
// <copyright file="AcsGovernanceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Governance.Tests;

public class AcsGovernanceTests : IDisposable
{
    private readonly string _tempDir;

    public AcsGovernanceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nc-acs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteManifest(string yaml)
    {
        var path = Path.Combine(_tempDir, "manifest.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private void WriteBundle()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "policy"));
        File.WriteAllText(Path.Combine(_tempDir, "policy", "test_agent.rego"), """
            package test_agent

            import rego.v1

            default verdict := {"decision": "allow"}
            """);
    }

    private static string ValidManifestYaml(string policyType = "rego") => $"""
        agent_control_specification_version: "0.3.1-beta"
        metadata:
          name: test-agent
        policies:
          test_policy:
            type: {policyType}
            bundle: ./policy
            query: data.test_agent.verdict
        intervention_points:
          pre_tool_call:
            policy_target: "$.tool_call.args"
            policy_target_kind: tool_args
            tool_name_from: "$.tool_call.name"
            policy:
              id: test_policy
        tools:
          file_read:
            type: Tool
            id: file_read
            clearance: internal
        """;

    [Fact]
    public void Disabled_config_yields_disabled_holder()
    {
        var governance = AcsGovernance.Build(
            new GovernanceConfig { Enabled = false },
            new NetclawPaths(basePath: _tempDir),
            NullLogger.Instance);

        Assert.False(governance.Enabled);
        Assert.Null(governance.Control);
        Assert.Same(AcsGovernance.Disabled, governance);
    }

    [Fact]
    public void Enabled_with_missing_manifest_throws()
    {
        var config = new GovernanceConfig { Enabled = true, ManifestPath = "does-not-exist.yaml" };

        Assert.Throws<FileNotFoundException>(() =>
            AcsGovernance.Build(config, new NetclawPaths(basePath: _tempDir), NullLogger.Instance));
    }

    [Theory]
    [InlineData("cedar")]
    [InlineData("custom")]
    [InlineData("test")]
    public void Non_rego_policy_type_refuses_start(string policyType)
    {
        var manifestPath = WriteManifest(ValidManifestYaml(policyType));
        var config = new GovernanceConfig { Enabled = true, ManifestPath = manifestPath };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcsGovernance.Build(config, new NetclawPaths(basePath: _tempDir), NullLogger.Instance));

        Assert.Contains(policyType, ex.Message, StringComparison.Ordinal);
        Assert.Contains("rego", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_policy_type_refuses_start()
    {
        var manifestPath = WriteManifest("""
            agent_control_specification_version: "0.3.1-beta"
            metadata:
              name: test-agent
            policies:
              test_policy:
                bundle: ./policy
            intervention_points:
              pre_tool_call:
                policy:
                  id: test_policy
            """);
        var config = new GovernanceConfig { Enabled = true, ManifestPath = manifestPath };

        Assert.Throws<InvalidOperationException>(() =>
            AcsGovernance.Build(config, new NetclawPaths(basePath: _tempDir), NullLogger.Instance));
    }

    [Fact]
    public void Empty_policies_refuse_start()
    {
        var manifestPath = WriteManifest("""
            agent_control_specification_version: "0.3.1-beta"
            metadata:
              name: test-agent
            policies: {}
            intervention_points:
              pre_tool_call:
                policy:
                  id: nothing
            """);
        var config = new GovernanceConfig { Enabled = true, ManifestPath = manifestPath };

        Assert.Throws<InvalidOperationException>(() =>
            AcsGovernance.Build(config, new NetclawPaths(basePath: _tempDir), NullLogger.Instance));
    }

    [Fact]
    public void Missing_policies_block_refuses_start()
    {
        var manifestPath = WriteManifest("""
            agent_control_specification_version: "0.3.1-beta"
            metadata:
              name: test-agent
            """);
        var config = new GovernanceConfig { Enabled = true, ManifestPath = manifestPath };

        Assert.Throws<InvalidOperationException>(() =>
            AcsGovernance.Build(config, new NetclawPaths(basePath: _tempDir), NullLogger.Instance));
    }

    [Fact]
    public void Unbound_pre_tool_call_point_refuses_start()
    {
        var manifestPath = WriteManifest("""
            agent_control_specification_version: "0.3.1-beta"
            metadata:
              name: test-agent
            policies:
              test_policy:
                type: rego
                bundle: ./policy
                query: data.test_agent.verdict
            intervention_points:
              input:
                policy:
                  id: test_policy
            """);
        var config = new GovernanceConfig { Enabled = true, ManifestPath = manifestPath };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcsGovernance.Build(config, new NetclawPaths(basePath: _tempDir), NullLogger.Instance));

        Assert.Contains("pre_tool_call", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_yaml_refuses_start()
    {
        var manifestPath = WriteManifest("this: [is: not: valid: yaml");
        var config = new GovernanceConfig { Enabled = true, ManifestPath = manifestPath };

        Assert.Throws<InvalidOperationException>(() =>
            AcsGovernance.Build(config, new NetclawPaths(basePath: _tempDir), NullLogger.Instance));
    }

    [Fact]
    public void Artifact_validation_runs_through_native_core_without_opa()
    {
        // Exercises the P/Invoke core on every machine (no OPA needed for
        // schema/semantic diagnostics) and proves the canonical manifest
        // shape used throughout these tests is artifact-valid.
        var rego = """
            package test_agent

            import rego.v1

            default verdict := {"decision": "allow"}
            """;
        var valid = AgentControlSpecification.ArtifactValidator.Validate(
            ValidManifestYaml(),
            new Dictionary<string, string> { ["policy.rego"] = rego });
        Assert.True(
            valid.Valid,
            string.Join("; ", valid.Diagnostics.Select(static d => d.ToString())));

        var invalid = AgentControlSpecification.ArtifactValidator.Validate("not: [a: valid: manifest");
        Assert.False(invalid.Valid);
        Assert.NotEmpty(invalid.Diagnostics);
    }

    [Fact]
    public void Empty_tools_catalog_refuses_start()
    {
        var manifestPath = WriteManifest("""
            agent_control_specification_version: "0.3.1-beta"
            metadata:
              name: test-agent
            policies:
              test_policy:
                type: rego
                bundle: ./policy
                query: data.test_agent.verdict
            intervention_points:
              pre_tool_call:
                policy:
                  id: test_policy
            tools: {}
            """);
        var config = new GovernanceConfig { Enabled = true, ManifestPath = manifestPath };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcsGovernance.Build(config, new NetclawPaths(basePath: _tempDir), NullLogger.Instance));

        Assert.Contains("tools catalog", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rego_manifest_without_opa_refuses_start_or_builds_with_opa()
    {
        WriteBundle();
        var manifestPath = WriteManifest(ValidManifestYaml());
        var config = new GovernanceConfig { Enabled = true, ManifestPath = manifestPath };
        var paths = new NetclawPaths(basePath: _tempDir);

        var opa = OpaResolver.Resolve(config.OpaPath);
        if (opa is null)
        {
            // No OPA on this machine: the daemon must refuse to start rather
            // than run a policy that would fail closed on every evaluation.
            var ex = Assert.Throws<InvalidOperationException>(() =>
                AcsGovernance.Build(config, paths, NullLogger.Instance));
            Assert.Contains("OPA", ex.Message, StringComparison.Ordinal);
        }
        else
        {
            using var governance = AcsGovernance.Build(config, paths, NullLogger.Instance);
            Assert.True(governance.Enabled);
            Assert.NotNull(governance.Control);
        }
    }

    [Fact]
    public void Relative_manifest_resolves_against_config_directory()
    {
        var paths = new NetclawPaths(basePath: _tempDir);
        Directory.CreateDirectory(Path.Combine(paths.ConfigDirectory, "policy"));
        File.WriteAllText(
            Path.Combine(paths.ConfigDirectory, "policy", "test_agent.rego"),
            """
            package test_agent

            import rego.v1

            default verdict := {"decision": "allow"}
            """);
        File.WriteAllText(
            Path.Combine(paths.ConfigDirectory, "manifest.yaml"),
            ValidManifestYaml());

        var config = new GovernanceConfig { Enabled = true, ManifestPath = "manifest.yaml" };

        // The manifest exists, so construction proceeds past resolution. With
        // no OPA locally it refuses at the OPA gate; with OPA it builds. Both
        // outcomes prove the relative path resolved (a miss throws
        // FileNotFoundException instead).
        var opa = OpaResolver.Resolve(config.OpaPath);
        if (opa is null)
        {
            Assert.Throws<InvalidOperationException>(() =>
                AcsGovernance.Build(config, paths, NullLogger.Instance));
        }
        else
        {
            using var governance = AcsGovernance.Build(config, paths, NullLogger.Instance);
            Assert.True(governance.Enabled);
        }
    }
}
