// -----------------------------------------------------------------------
// <copyright file="GovernedExecutorGateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Governance.Tests;

/// <summary>
/// Proves the governor gate inside <see cref="DispatchingToolExecutor"/>: a
/// native-gate-allowed call is denied when the governor objects (additive
/// deny-overrides), executes when it does not, and the deny rides the standard
/// <see cref="ToolAccessDeniedException"/> surface on every entry point.
/// </summary>
public class GovernedExecutorGateTests
{
    private readonly ToolRegistry _registry;
    private readonly DispatchingToolExecutor _executor;
    private readonly RecordingGovernor _governor = new();

    public GovernedExecutorGateTests()
    {
        _registry = new ToolRegistry();
        _registry.Register(new StubNetclawTool("file_read", result: "governor-allows"));
        _executor = new DispatchingToolExecutor(
            _registry,
            new ToolAccessPolicy(
                new NetclawPaths(),
                new ToolConfig(),
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])),
            governor: _governor);
    }

    private static ToolExecutionContext CreateContext() => new(
        new ToolRunScope
        {
            Session = new ToolSessionScope.Bound(
                "slack/C777/T1",
                SessionStoragePaths.CreateLegacy(
                    Path.Combine(AppContext.BaseDirectory, "netclaw-governance-test-workspace"),
                    Path.Combine(AppContext.BaseDirectory, "netclaw-governance-test-logs"),
                    "test-session")),
            Audience = TrustAudience.Personal,
            InlineOutputBudget = InlineOutputBudget.Default,
            InteractiveApproval = new InteractiveApprovalCapability.Unavailable(),
        },
        ToolExecutionTimeout.Default);

    private static FunctionCallContent ToolCall(string callId, string name, string? argValue = "x") =>
        new(callId, name, new Dictionary<string, object?>
        {
            ["Path"] = argValue,
            ["_rationale"] = "governor gate test",
        });

    [Fact]
    public async Task Governor_deny_blocks_native_allowed_call()
    {
        _governor.DenyWhen = toolName => toolName == "file_read";

        var ex = await Assert.ThrowsAsync<ToolAccessDeniedException>(() =>
            _executor.ExecuteAsync(ToolCall("call-1", "file_read"), CreateContext(), CancellationToken.None));

        Assert.StartsWith("acs_test_denied", ex.DenyReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Governor_allow_executes_the_tool()
    {
        _governor.DenyWhen = static _ => false;

        var result = await _executor.ExecuteAsync(
            ToolCall("call-2", "file_read"), CreateContext(), CancellationToken.None);

        Assert.Equal("governor-allows", result);
    }

    [Fact]
    public async Task Governor_deny_blocks_streaming_path()
    {
        _governor.DenyWhen = toolName => toolName == "file_read";
        var toolCall = ToolCall("call-3", "file_read");

        var ex = await Assert.ThrowsAsync<ToolAccessDeniedException>(async () =>
        {
            await foreach (var _ in _executor.ExecuteStreamAsync(toolCall, CreateContext(), CancellationToken.None))
            {
            }
        });

        Assert.StartsWith("acs_test_denied", ex.DenyReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthorizeAsync_surfaces_governor_deny()
    {
        _governor.DenyWhen = toolName => toolName == "file_read";

        await Assert.ThrowsAsync<ToolAccessDeniedException>(() =>
            _executor.AuthorizeAsync(ToolCall("call-4", "file_read"), CreateContext(), CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_tool_denies_before_governor_is_consulted()
    {
        _governor.DenyWhen = static _ => false;

        var ex = await Assert.ThrowsAsync<ToolAccessDeniedException>(() =>
            _executor.AuthorizeAsync(ToolCall("call-5", "no_such_tool"), CreateContext(), CancellationToken.None));

        Assert.Equal("tool_not_found", ex.DenyReason);
        Assert.Empty(_governor.SeenToolNames);
    }

    [Fact]
    public async Task No_governor_constructor_default_keeps_prior_behavior()
    {
        var executor = new DispatchingToolExecutor(
            _registry,
            new ToolAccessPolicy(
                new NetclawPaths(),
                new ToolConfig(),
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])));

        var result = await executor.ExecuteAsync(
            ToolCall("call-6", "file_read"), CreateContext(), CancellationToken.None);

        Assert.Equal("governor-allows", result);
    }

    private sealed class RecordingGovernor : IToolInvocationGovernor
    {
        public Func<string, bool> DenyWhen { get; set; } = static _ => false;
        public List<string> SeenToolNames { get; } = [];

        public Task<ToolGovernorDecision> CheckAsync(
            INetclawTool tool,
            ToolExecutionContext context,
            IDictionary<string, object?>? args,
            CancellationToken ct = default)
        {
            SeenToolNames.Add(tool.Name);
            return Task.FromResult(
                DenyWhen(tool.Name)
                    ? ToolGovernorDecision.Deny("acs_test_denied (test rule)")
                    : ToolGovernorDecision.Proceed);
        }
    }
}
