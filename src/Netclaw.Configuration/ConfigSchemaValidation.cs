// -----------------------------------------------------------------------
// <copyright file="ConfigSchemaValidation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Netclaw.Configuration;

/// <summary>
/// Validates a candidate netclaw.json document against the embedded v1 schema
/// before anything is written to disk. The same rules the doctor enforces on
/// the file, applied to the in-memory merge result. The daemon config API is
/// the primary caller.
/// </summary>
public static class ConfigSchemaValidation
{
    /// <summary>
    /// Validate a config document against its schema. A missing
    /// <c>configVersion</c> validates against the current schema. Returns
    /// field-located errors, capped so one bad patch cannot flood the caller.
    /// </summary>
    public static bool TryValidate(Dictionary<string, object> document, out IReadOnlyList<string> errors)
    {
        var node = JsonSerializer.SerializeToNode(document, NetclawConfigFiles.ConfigFileOptions) as JsonObject
                   ?? [];
        if (node["configVersion"] is null or JsonValue)
        {
            if (!node.ContainsKey("configVersion"))
                node["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;
        }

        var version = node["configVersion"] is JsonValue versionValue && versionValue.TryGetValue<int>(out var parsed)
            ? parsed
            : EmbeddedSchemaLoader.CurrentSchemaVersion;

        var schemaText = EmbeddedSchemaLoader.LoadConfigSchema(version);
        if (schemaText is null)
        {
            errors = [$"No embedded schema found for configVersion {version}."];
            return false;
        }

        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(schemaText);
        }
        catch (Exception ex)
        {
            errors = [$"Failed parsing embedded schema for v{version}: {ex.Message}"];
            return false;
        }

        var evaluation = schema.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (evaluation.IsValid)
        {
            errors = [];
            return true;
        }

        var found = new List<string>();
        if (evaluation.Details is not null)
        {
            foreach (var detail in evaluation.Details)
            {
                if (detail.IsValid || detail.Errors is null || detail.Errors.Count == 0)
                    continue;

                var messages = string.Join("; ", detail.Errors.Select(error => $"{error.Key}: {error.Value}"));
                var rawLocation = detail.InstanceLocation.ToString();
                var location = string.IsNullOrEmpty(rawLocation) || rawLocation == "/" ? "(root)" : rawLocation;
                found.Add($"{location}: {messages}");
                if (found.Count >= 10)
                    break;
            }
        }

        if (found.Count == 0)
            found.Add("Config does not match the netclaw.json schema.");

        errors = found;
        return false;
    }
}
