// -----------------------------------------------------------------------
// <copyright file="StubNetclawTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Tools;

namespace Netclaw.Governance.Tests;

/// <summary>
/// Minimal INetclawTool fake: no schema, fixed result, tracks execution.
/// Interface default members cover streaming and pipeline hints.
/// </summary>
internal sealed class StubNetclawTool : INetclawTool
{
    public StubNetclawTool(string name, string result = "ok")
    {
        Name = name;
        LlmFacingName = LlmFacingToolName.FromCanonical(name);
        Result = result;
    }

    public string Name { get; }
    public LlmFacingToolName LlmFacingName { get; }
    public string Description => "Stub tool";
    public string GrantCategory => "builtin";
    public JsonElement ParameterSchema => default;
    public string Result { get; }
    public bool WasCalled { get; private set; }

    public AITool ToAITool() => AIFunctionFactory.Create(() => Result, name: Name, description: Description);

    public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolInvocationContext context, CancellationToken ct = default)
    {
        WasCalled = true;
        return Task.FromResult(Result);
    }
}
