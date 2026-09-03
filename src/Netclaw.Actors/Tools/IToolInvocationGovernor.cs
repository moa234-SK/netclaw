// -----------------------------------------------------------------------
// <copyright file="IToolInvocationGovernor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// The outcome of an external governance check on one resolved tool invocation.
/// </summary>
public sealed record ToolGovernorDecision
{
    /// <summary>No objection. The native gate decides as before.</summary>
    public static ToolGovernorDecision Proceed { get; } = new((string?)null);

    /// <summary>
    /// A categorical deny. It is not approvable, flows through the standard
    /// deny/audit surface, and reaches the model as
    /// <c>Tool access denied: {reason}</c>.
    /// </summary>
    public static ToolGovernorDecision Deny(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ToolGovernorDecision(reason);
    }

    private ToolGovernorDecision(string? denyReason) => DenyReason = denyReason;

    public string? DenyReason { get; }
}

/// <summary>
/// Additive deny-overrides gate consulted by <see cref="DispatchingToolExecutor"/>
/// after tool resolution but before the native <see cref="ToolAccessPolicy"/>
/// authorization, so an external governance engine (Agent Control Specification)
/// can only tighten what the native gate already permits. Implementations MUST
/// fail closed: an internal evaluation error returns a deny decision rather
/// than falling through to allow.
/// </summary>
public interface IToolInvocationGovernor
{
    /// <summary>
    /// Evaluate a resolved tool invocation against external governance policy.
    /// </summary>
    /// <param name="tool">The resolved registered tool (canonical name).</param>
    /// <param name="context">The per-call execution context (session/audience scope).</param>
    /// <param name="args">The raw model-supplied arguments, possibly null.</param>
    /// <param name="ct">Cancellation for the evaluation.</param>
    /// <returns>The decision; never null.</returns>
    Task<ToolGovernorDecision> CheckAsync(
        INetclawTool tool,
        ToolExecutionContext context,
        IDictionary<string, object?>? args,
        CancellationToken ct = default);
}

/// <summary>
/// The no-op governor used when external governance is disabled. Returning
/// <see cref="ToolGovernorDecision.Proceed"/> unconditionally keeps the executor
/// behavior identical to its pre-governor state; this is the explicit disabled
/// state of the feature toggle, not a silent fallback.
/// </summary>
public sealed class NoOpToolInvocationGovernor : IToolInvocationGovernor
{
    public static NoOpToolInvocationGovernor Instance { get; } = new();

    private NoOpToolInvocationGovernor()
    {
    }

    public Task<ToolGovernorDecision> CheckAsync(
        INetclawTool tool,
        ToolExecutionContext context,
        IDictionary<string, object?>? args,
        CancellationToken ct = default)
        => Task.FromResult(ToolGovernorDecision.Proceed);
}
