// -----------------------------------------------------------------------
// <copyright file="AcsToolInvocationGovernorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using AgentControlSpecification;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Governance.Tests;

public class AcsToolInvocationGovernorTests
{
    private static ToolExecutionContext CreateContext(
        string? sessionId = "slack/C123/T456",
        TrustAudience audience = TrustAudience.Personal,
        string? channelType = "slack")
    {
        var scope = new ToolRunScope
        {
            Session = sessionId is null
                ? new ToolSessionScope.Sessionless()
                : new ToolSessionScope.Bound(
                    sessionId,
                    SessionStoragePaths.CreateLegacy(
                        Path.Combine(AppContext.BaseDirectory, "netclaw-governor-test-workspace"),
                        Path.Combine(AppContext.BaseDirectory, "netclaw-governor-test-logs"),
                        "test-session")),
            Audience = audience,
            InlineOutputBudget = InlineOutputBudget.Default,
            ChannelType = channelType,
            InteractiveApproval = new InteractiveApprovalCapability.Unavailable(),
        };
        return new ToolExecutionContext(scope, ToolExecutionTimeout.Default);
    }

    private static (AcsToolInvocationGovernor Governor, FakeAgentControlRuntime Runtime) CreateGovernor()
    {
        var runtime = new FakeAgentControlRuntime();
        var governance = AcsGovernance.CreateEnabledForTesting(new AgentControl(runtime));
        return (new AcsToolInvocationGovernor(governance, NullLogger<AcsToolInvocationGovernor>.Instance), runtime);
    }

    private static Task<ToolGovernorDecision> Check(
        AcsToolInvocationGovernor governor,
        string toolName = "file_read",
        IDictionary<string, object?>? args = null,
        ToolExecutionContext? context = null)
        => governor.CheckAsync(new StubNetclawTool(toolName), context ?? CreateContext(), args);

    [Fact]
    public async Task Allow_proceeds()
    {
        var (governor, _) = CreateGovernor();
        var result = await governor.CheckAsync(
            new StubNetclawTool("file_read"), CreateContext(), null, TestContext.Current.CancellationToken);

        Assert.Null(result.DenyReason);
    }

    [Fact]
    public async Task Warn_proceeds()
    {
        var (governor, runtime) = CreateGovernor();
        runtime.Respond = _ => FakeAgentControlRuntime.Result(Decision.Warn, reason: "suspicious_path");

        var result = await Check(governor);

        Assert.Null(result.DenyReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("path_outside_workspace")]
    public async Task Deny_maps_to_policy_reason(string? reason)
    {
        var (governor, runtime) = CreateGovernor();
        runtime.Respond = _ => FakeAgentControlRuntime.Result(Decision.Deny, reason: reason);

        var result = await Check(governor);

        Assert.NotNull(result.DenyReason);
        Assert.StartsWith("acs_policy_denied", result.DenyReason, StringComparison.Ordinal);
        if (reason is not null)
            Assert.Contains(reason, result.DenyReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Escalate_fails_closed()
    {
        var (governor, runtime) = CreateGovernor();
        runtime.Respond = _ => FakeAgentControlRuntime.Result(Decision.Escalate, reason: "needs_human");

        var result = await Check(governor);

        Assert.StartsWith("acs_policy_escalate", result.DenyReason, StringComparison.Ordinal);
        Assert.Contains("needs_human", result.DenyReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transform_fails_closed_without_applying()
    {
        var (governor, runtime) = CreateGovernor();
        runtime.Respond = _ => FakeAgentControlRuntime.Result(Decision.Transform, reason: "redact_args");

        var result = await Check(governor);

        Assert.Equal("acs_transform_unsupported: redact_args", result.DenyReason);
    }

    [Fact]
    public async Task Evaluation_error_fails_closed()
    {
        var (governor, runtime) = CreateGovernor();
        runtime.Respond = _ => throw new InvalidOperationException("engine blew up");

        var result = await Check(governor);

        Assert.Equal("acs_evaluation_failed", result.DenyReason);
    }

    [Fact]
    public async Task Disabled_governance_proceeds_without_evaluation()
    {
        var governance = AcsGovernance.Disabled;
        var governor = new AcsToolInvocationGovernor(governance, NullLogger<AcsToolInvocationGovernor>.Instance);

        var result = await governor.CheckAsync(
            new StubNetclawTool("file_read"), CreateContext(), null, TestContext.Current.CancellationToken);

        Assert.Null(result.DenyReason);
    }

    [Fact]
    public async Task Pre_tool_call_point_is_evaluated_in_evaluate_only_mode()
    {
        var (governor, runtime) = CreateGovernor();

        _ = await Check(governor);

        var request = Assert.Single(runtime.Requests);
        Assert.Equal(InterventionPoint.PreToolCall, request.Point);
        Assert.Equal(EnforcementMode.EvaluateOnly, request.Mode);
    }

    [Fact]
    public async Task Snapshot_carries_tool_name_args_and_session()
    {
        var (governor, runtime) = CreateGovernor();

        _ = await Check(
            governor,
            args: new Dictionary<string, object?> { ["Path"] = "/tmp/a.txt" },
            context: CreateContext(sessionId: "slack/C1/T1", audience: TrustAudience.Team, channelType: "slack"));

        var snapshot = Assert.Single(runtime.Requests).Snapshot;
        Assert.Equal("file_read", snapshot.GetProperty("tool_call").GetProperty("name").GetString());
        Assert.Equal(
            "/tmp/a.txt",
            snapshot.GetProperty("tool_call").GetProperty("args").GetProperty("Path").GetString());
        Assert.Equal("slack/C1/T1", snapshot.GetProperty("session").GetProperty("id").GetString());
        Assert.Equal("team", snapshot.GetProperty("session").GetProperty("audience").GetString());
        Assert.Equal("slack", snapshot.GetProperty("session").GetProperty("channel_type").GetString());
    }

    [Fact]
    public async Task Null_argument_values_are_skipped()
    {
        var (governor, runtime) = CreateGovernor();

        _ = await Check(
            governor,
            args: new Dictionary<string, object?> { ["Path"] = "/tmp/a.txt", ["WorkingDirectory"] = null });

        var args = Assert.Single(runtime.Requests).Snapshot.GetProperty("tool_call").GetProperty("args");
        Assert.Single(args.EnumerateObject());
        Assert.True(args.TryGetProperty("Path", out _));
    }

    [Fact]
    public async Task Sessionless_context_serializes_null_session_id()
    {
        var (governor, runtime) = CreateGovernor();

        _ = await Check(governor, context: CreateContext(sessionId: null));

        var session = Assert.Single(runtime.Requests).Snapshot.GetProperty("session");
        Assert.Equal(JsonValueKind.Null, session.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_failing_closed()
    {
        var (governor, runtime) = CreateGovernor();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        runtime.Respond = _ => throw new OperationCanceledException(cts.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            governor.CheckAsync(
                new StubNetclawTool("file_read"), CreateContext(), null, cts.Token));
    }
}
