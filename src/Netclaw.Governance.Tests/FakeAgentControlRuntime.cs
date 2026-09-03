// -----------------------------------------------------------------------
// <copyright file="FakeAgentControlRuntime.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using AgentControlSpecification;

namespace Netclaw.Governance.Tests;

/// <summary>
/// Scriptable <see cref="IAgentControlRuntime"/> fake: captures each request
/// (intervention point + snapshot) and returns the configured verdict. Never
/// touches the native core or OPA.
/// </summary>
internal sealed class FakeAgentControlRuntime : IAgentControlRuntime
{
    public sealed record CapturedRequest(InterventionPoint Point, JsonElement Snapshot, EnforcementMode Mode);

    public List<CapturedRequest> Requests { get; } = [];

    public Func<InterventionPointRequest, InterventionPointResult>? Respond { get; set; }

    public ValueTask<InterventionPointResult> EvaluateInterventionPointAsync(
        InterventionPointRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(new CapturedRequest(request.InterventionPoint, request.Snapshot, request.Mode));

        if (Respond is { } respond)
            return ValueTask.FromResult(respond(request));

        return ValueTask.FromResult(new InterventionPointResult(
            new Verdict(Decision.Allow, Reason: null, Message: null)));
    }

    public static InterventionPointResult Result(Decision decision, string? reason = null, string? message = null)
        => new(new Verdict(decision, reason, message));
}
