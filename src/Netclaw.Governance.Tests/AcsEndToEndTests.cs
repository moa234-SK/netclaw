// -----------------------------------------------------------------------
// <copyright file="AcsEndToEndTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Governance.Tests;

/// <summary>
/// End-to-end ACS evaluation: real manifest, real native core, real OPA
/// subprocess, real Rego policy. Runs only where OPA resolves. CI sets
/// NETCLAW_ACS_REQUIRE_OPA=1 so these tests can never skip silently there.
/// </summary>
public class AcsEndToEndTests : IDisposable
{
    private readonly string _tempDir;

    public AcsEndToEndTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nc-acs-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private void WritePolicySet()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "policy"));
        File.WriteAllText(Path.Combine(_tempDir, "manifest.yaml"), """
            agent_control_specification_version: "0.3.1-beta"
            metadata:
              name: e2e-agent
            policies:
              tool_policy:
                type: rego
                bundle: ./policy
                query: data.e2e_agent.verdict
            intervention_points:
              pre_tool_call:
                policy_target: "$.tool_call.args"
                policy_target_kind: tool_args
                tool_name_from: "$.tool_call.name"
                policy:
                  id: tool_policy
            tools:
              file_write:
                type: Tool
                id: file_write
                clearance: internal
              file_read:
                type: Tool
                id: file_read
                clearance: internal
            """);
        File.WriteAllText(Path.Combine(_tempDir, "policy", "e2e_agent.rego"), """
            package e2e_agent

            import rego.v1

            default verdict := {"decision": "allow"}

            verdict := {
              "decision": "deny",
              "reason": "blocked_tool",
              "message": "The policy blocks this tool.",
            } if {
              input.tool.name == "file_write"
            }
            """);
    }

    private static ToolExecutionContext CreateContext()
    {
        var scope = new ToolRunScope
        {
            Session = new ToolSessionScope.Bound(
                "slack/C123/T456",
                SessionStoragePaths.CreateLegacy(
                    Path.Combine(AppContext.BaseDirectory, "netclaw-governance-e2e-workspace"),
                    Path.Combine(AppContext.BaseDirectory, "netclaw-governance-e2e-logs"),
                    "test-session",
                    ManagedTemporaryLayout.InsideSessionStorage.Instance)),
            Audience = TrustAudience.Personal,
            InlineOutputBudget = InlineOutputBudget.Default,
            InteractiveApproval = new InteractiveApprovalCapability.Unavailable(),
        };
        return new ToolExecutionContext(scope, ToolExecutionTimeout.Default);
    }

    [Fact]
    public async Task Rego_deny_blocks_and_rego_allow_proceeds_end_to_end()
    {
        var opa = OpaResolver.Resolve(null);
        if (opa is null)
        {
            if (Environment.GetEnvironmentVariable("NETCLAW_ACS_REQUIRE_OPA") == "1")
                throw new InvalidOperationException(
                    "NETCLAW_ACS_REQUIRE_OPA=1 but no OPA binary resolves; CI must install OPA.");

            return; // no OPA on this machine; CI covers the end-to-end path
        }

        WritePolicySet();
        using var governance = AcsGovernance.Build(
            new GovernanceConfig { Enabled = true, ManifestPath = Path.Combine(_tempDir, "manifest.yaml") },
            new NetclawPaths(basePath: _tempDir),
            NullLogger.Instance);
        var governor = new AcsToolInvocationGovernor(
            governance, NullLogger<AcsToolInvocationGovernor>.Instance);

        var denied = await governor.CheckAsync(
            new StubNetclawTool("file_write"), CreateContext(),
            new Dictionary<string, object?> { ["Path"] = "/tmp/a.txt" },
            TestContext.Current.CancellationToken);
        Assert.NotNull(denied.DenyReason);
        Assert.StartsWith("acs_policy_denied", denied.DenyReason, StringComparison.Ordinal);
        Assert.Contains("blocked_tool", denied.DenyReason, StringComparison.Ordinal);

        var allowed = await governor.CheckAsync(
            new StubNetclawTool("file_read"), CreateContext(),
            new Dictionary<string, object?> { ["Path"] = "/tmp/a.txt" },
            TestContext.Current.CancellationToken);
        Assert.Null(allowed.DenyReason);

        // A tool absent from the manifest tools catalog is denied fail-closed
        // by the ACS runtime itself (runtime_error:tool_unknown) — the
        // documented behavior for unlisted tools.
        var unlisted = await governor.CheckAsync(
            new StubNetclawTool("shell_execute"), CreateContext(),
            new Dictionary<string, object?> { ["Command"] = "echo hi" },
            TestContext.Current.CancellationToken);
        Assert.NotNull(unlisted.DenyReason);
        Assert.Contains("tool_unknown", unlisted.DenyReason, StringComparison.Ordinal);
    }
}
