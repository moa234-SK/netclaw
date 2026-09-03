// -----------------------------------------------------------------------
// <copyright file="AcsToolInvocationGovernor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using AgentControlSpecification;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Tools;
using Netclaw.Tools;

namespace Netclaw.Governance;

/// <summary>
/// ACS-backed implementation of <see cref="IToolInvocationGovernor"/>.
/// Evaluates every resolved tool invocation at the <c>pre_tool_call</c>
/// intervention point as an additive deny-overrides layer: a deny here blocks
/// the call regardless of what the native <see cref="ToolAccessPolicy"/> would
/// allow, and an allow here never loosens the native gate. Fails closed: any
/// exception from the ACS evaluation denies the call rather than falling
/// through to allow.
/// </summary>
public sealed class AcsToolInvocationGovernor : IToolInvocationGovernor
{
    internal const string DenyReasonPrefix = "acs_policy_denied";
    internal const string EscalateDenyReason = "acs_policy_escalate";
    internal const string TransformUnsupportedDenyReason = "acs_transform_unsupported";
    internal const string FailClosedDenyReason = "acs_evaluation_failed";

    private readonly AcsGovernance _governance;
    private readonly ILogger<AcsToolInvocationGovernor> _logger;

    public AcsToolInvocationGovernor(
        AcsGovernance governance,
        ILogger<AcsToolInvocationGovernor> logger)
    {
        _governance = governance ?? throw new ArgumentNullException(nameof(governance));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ToolGovernorDecision> CheckAsync(
        INetclawTool tool,
        ToolExecutionContext context,
        IDictionary<string, object?>? args,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(context);

        // The disabled state is an explicit feature toggle decided by the
        // operator at startup; AcsGovernance.Build already logged it.
        if (!_governance.Enabled || _governance.Control is not { } control)
            return ToolGovernorDecision.Proceed;

        // Tool.Name is canonical at this call site (the executor resolved it
        // from the registry), so policy authors and the approval store see the
        // same operator-facing identifier.
        var snapshot = SnapshotBuilder.BuildPreToolCall(tool.Name, args, context);

        InterventionPointResult result;
        try
        {
            // EvaluateOnly: this governor is the enforcement point and maps
            // verdicts itself; it does not use the SDK's throwing enforce helpers.
            result = await control.EvaluateInterventionPointAsync(
                InterventionPoint.PreToolCall,
                snapshot,
                EnforcementMode.EvaluateOnly,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed: a governance engine error must not degrade into an
            // implicit allow. The native gate is never reached for this call.
            _logger.LogError(ex,
                "ACS policy evaluation threw for tool {ToolName} session {SessionId}; denying (fail closed).",
                tool.Name, context.SessionId);
            return ToolGovernorDecision.Deny(FailClosedDenyReason);
        }

        return MapVerdict(result.Verdict, tool.Name, context.SessionId);
    }

    private ToolGovernorDecision MapVerdict(Verdict verdict, string toolName, string? sessionId)
    {
        switch (verdict.Decision)
        {
            case Decision.Allow:
                return ToolGovernorDecision.Proceed;

            case Decision.Warn:
                _logger.LogWarning(
                    "ACS policy warned for tool {ToolName} session {SessionId}: {Reason} {Message}",
                    toolName, sessionId, verdict.Reason ?? "(no reason)", verdict.Message ?? string.Empty);
                return ToolGovernorDecision.Proceed;

            case Decision.Deny:
                _logger.LogWarning(
                    "ACS policy denied tool {ToolName} session {SessionId}: {Reason} {Message}",
                    toolName, sessionId, verdict.Reason ?? "(no reason)", verdict.Message ?? string.Empty);
                return ToolGovernorDecision.Deny(AppendReason(DenyReasonPrefix, verdict.Reason));

            case Decision.Escalate:
                // No approval backend is wired in this phase, so escalate fails
                // closed per the ACS host contract.
                _logger.LogWarning(
                    "ACS policy escalated tool {ToolName} session {SessionId} ({Reason}); no approval backend is wired, denying fail-closed.",
                    toolName, sessionId, verdict.Reason ?? "(no reason)");
                return ToolGovernorDecision.Deny(AppendReason(EscalateDenyReason, verdict.Reason));

            case Decision.Transform:
                // Phase 1 does not rewrite tool arguments. Applying a transform
                // silently is worse than denying: the operator's policy expected
                // a redacted target that this host never produced.
                _logger.LogWarning(
                    "ACS policy returned a transform verdict for tool {ToolName} session {SessionId} ({Reason}); transforms are not supported, denying fail-closed.",
                    toolName, sessionId, verdict.Reason ?? "(no reason)");
                return ToolGovernorDecision.Deny(AppendReason(TransformUnsupportedDenyReason, verdict.Reason));

            default:
                // An unrecognized decision is unenforceable; deny rather than guess.
                _logger.LogError(
                    "ACS policy returned an unrecognized decision '{Decision}' for tool {ToolName}; denying fail-closed.",
                    verdict.Decision.ToString(), toolName);
                return ToolGovernorDecision.Deny(FailClosedDenyReason);
        }
    }

    private static string AppendReason(string prefix, string? reason)
        => string.IsNullOrWhiteSpace(reason) ? prefix : $"{prefix}: {reason}";
}

/// <summary>
/// Builds the JSON snapshots handed to ACS at each intervention point. The
/// manifest binds <c>policy_target: $.tool_call.args</c> and
/// <c>tool_name_from: $.tool_call.name</c> against this shape.
/// </summary>
internal static class SnapshotBuilder
{
    public static JsonElement BuildPreToolCall(
        string toolName,
        IDictionary<string, object?>? args,
        ToolExecutionContext context)
    {
        // Null-valued arguments carry no policy signal; skipping them keeps the
        // snapshot clean for object.get-style Rego policies.
        var cleanArgs = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (args is not null)
        {
            foreach (var (key, value) in args)
            {
                if (value is not null)
                    cleanArgs[key] = value;
            }
        }

        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tool_call"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = toolName,
                ["args"] = cleanArgs,
            },
            ["session"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = context.SessionId,
                ["audience"] = context.Audience.ToString().ToLowerInvariant(),
                ["channel_type"] = context.ChannelType,
                ["boundary"] = context.Boundary?.Value,
            },
        };

        return JsonSerializer.SerializeToElement(snapshot);
    }
}
