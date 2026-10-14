// -----------------------------------------------------------------------
// <copyright file="ConfigDocumentPatch.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;

namespace Netclaw.Configuration;

/// <summary>
/// Pure helpers for the config HTTP API document view: secret redaction, RFC
/// 7386 merge patch, touched-path collection, read-only path policy, and the
/// merged-document split back into netclaw.json and secrets.json shapes.
/// No I/O — the callers own the files.
/// </summary>
public static class ConfigDocumentPatch
{
    /// <summary>
    /// Placeholder returned by GET for every leaf stored in secrets.json. A
    /// PATCH that sends this value back at a secret path keeps the stored
    /// value; a plain string replaces it; null deletes it. Reserved value.
    /// </summary>
    public const string SecretPlaceholder = "__netclaw_secret__";

    /// <summary>
    /// Daemon network fields that HTTP cannot change. They can lock the caller
    /// out (host, exposure mode) or bypass authentication (a broad trusted
    /// proxy entry lets any caller spoof loopback through forwarded headers).
    /// Local CLI, TUI, and file edits stay the mutation paths.
    /// </summary>
    public static readonly IReadOnlyList<string> ReadOnlyPaths =
    [
        "Daemon.Host",
        "Daemon.ExposureMode",
        "Daemon.TrustedProxies",
    ];

    // Leaf names that mark a credential under Providers.{name}. Kept in sync with
    // ProviderCredentialWriter and OAuthTokenPersistence, which own these shapes.
    private static readonly IReadOnlySet<string> ProviderSecretLeafNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "ApiKey",
        "OAuthAccessToken",
        "OAuthRefreshToken",
        "OAuthAccountId",
    };

    // Channel credential paths, matching ChannelsConfigPersistenceMapper's spec table.
    private static readonly IReadOnlySet<string> ChannelSecretPaths = new HashSet<string>(StringComparer.Ordinal)
    {
        "Slack.BotToken",
        "Slack.AppToken",
        "Discord.BotToken",
        "Mattermost.BotToken",
    };

    /// <summary>
    /// Build the effective view for GET: netclaw.json overlaid with secrets.json,
    /// each secrets leaf replaced by <see cref="SecretPlaceholder"/>. The secrets
    /// file defines the secret set — no hardcoded path list.
    /// </summary>
    public static Dictionary<string, object> BuildRedactedView(
        Dictionary<string, object> config,
        Dictionary<string, object> secrets,
        out IReadOnlyList<string> redactedPaths)
    {
        var view = Materialize(config);
        var redacted = new List<string>();
        OverlaySecretsAsPlaceholders(view, Materialize(secrets), [], redacted);
        redactedPaths = redacted;
        return view;
    }

    private static void OverlaySecretsAsPlaceholders(
        Dictionary<string, object> target,
        Dictionary<string, object> secrets,
        List<string> prefix,
        List<string> redacted)
    {
        foreach (var (key, value) in secrets)
        {
            if (value is Dictionary<string, object> section)
            {
                var child = NetclawConfigFiles.GetOrCreateSection(target, key);
                OverlaySecretsAsPlaceholders(child, section, [.. prefix, key], redacted);
                continue;
            }

            // A scalar (or array) stored in the secrets file is a secret leaf. The
            // placeholder replaces whatever the config file had at the same path.
            // The walk already descended into target, so write the leaf directly.
            target[key] = SecretPlaceholder;
            redacted.Add(string.Join('.', prefix.Append(key)));
        }
    }

    /// <summary>
    /// Collect every path a patch touches — set, replace, or delete — including
    /// paths whose value merely repeats the current one. A section delete
    /// counts as a touch of the section path itself.
    /// </summary>
    public static IReadOnlyList<string> CollectTouchedPaths(Dictionary<string, object?> patch)
    {
        var touched = new List<string>();
        CollectTouched(patch, [], touched);
        return touched;
    }

    private static void CollectTouched(Dictionary<string, object?> node, List<string> prefix, List<string> touched)
    {
        foreach (var (key, value) in node)
        {
            if (value is Dictionary<string, object?> section)
            {
                CollectTouched(section, [.. prefix, key], touched);
                continue;
            }

            touched.Add(string.Join('.', prefix.Append(key)));
        }
    }

    /// <summary>
    /// The read-only paths a patch touches, directly or through an ancestor
    /// (a whole-section set or null-delete that would remove or replace them).
    /// Any touch rejects the whole patch, so every intersection is returned.
    /// </summary>
    public static IReadOnlyList<string> FindReadOnlyTouches(IReadOnlyList<string> touchedPaths)
        => touchedPaths
            .Where(touched => ReadOnlyPaths.Any(readOnly =>
                IsPathOrAncestor(touched, readOnly)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="readOnly"/>, a
    /// member inside it, or an ancestor of it (a section write that would
    /// swallow the read-only member).
    /// </summary>
    private static bool IsPathOrAncestor(string candidate, string readOnly)
        => string.Equals(candidate, readOnly, StringComparison.Ordinal)
           || candidate.StartsWith(readOnly + ".", StringComparison.Ordinal)
           || readOnly.StartsWith(candidate + ".", StringComparison.Ordinal);

    /// <summary>
    /// Convert a raw JSON merge-patch body into the tree shape
    /// <see cref="ApplyMergePatch"/> expects: nested objects as dictionaries,
    /// null as null, scalars as CLR values.
    /// </summary>
    public static Dictionary<string, object?> ParseMergePatch(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The merge patch body must be a JSON object.");

        var result = new Dictionary<string, object?>();
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.Object => ParseMergePatch(property.Value),
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => property.Value.TryGetInt64(out var integer) ? integer : property.Value.GetDouble(),
                JsonValueKind.Array => property.Value.Deserialize<object[]>(),
                _ => property.Value.Deserialize<object>(),
            };
        }

        return result;
    }

    /// <summary>
    /// Apply an RFC 7386 JSON Merge Patch to a target document, in place:
    /// null deletes the member, an object recurses (replacing a non-object
    /// target member), any other value replaces. Absent keys are untouched.
    /// Key case follows the file's existing spelling to avoid duplicate keys.
    /// </summary>
    public static void ApplyMergePatch(Dictionary<string, object> target, Dictionary<string, object?> patch)
    {
        foreach (var (key, value) in patch)
        {
            // The file may hold a differently-cased key; writing beside it would
            // duplicate the section and stop the daemon at startup.
            var existingKey = target.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                              ?? key;
            if (value is null)
            {
                target.Remove(existingKey);
                continue;
            }

            if (value is Dictionary<string, object?> section)
            {
                if (NetclawConfigFiles.GetSectionOrNull(target, existingKey) is { } child)
                {
                    ApplyMergePatch(child, section);
                    continue;
                }

                var fresh = new Dictionary<string, object>();
                target[existingKey] = fresh;
                ApplyMergePatch(fresh, section);
                continue;
            }

            target[existingKey] = value;
        }
    }

    /// <summary>
    /// The top-level sections whose content changed between two document
    /// versions, compared by normalized serialization. Secret values are
    /// already placeholders at this layer, so the diff never sees material.
    /// </summary>
    public static IReadOnlyList<string> ChangedTopLevelSections(
        Dictionary<string, object> before,
        Dictionary<string, object> after)
    {
        var changed = new List<string>();
        var keys = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var hadBefore = before.TryGetValue(key, out var oldValue);
            var hadAfter = after.TryGetValue(key, out var newValue);

            if (!hadBefore && !hadAfter)
                continue;

            if (hadBefore != hadAfter
                || JsonSerializer.Serialize(oldValue, NetclawConfigFiles.ConfigFileOptions)
                   != JsonSerializer.Serialize(newValue, NetclawConfigFiles.ConfigFileOptions))
            {
                changed.Add(key);
            }
        }

        return changed;
    }

    /// <summary>
    /// Split the merged effective view back into the netclaw.json and
    /// secrets.json shapes. Existing secret paths: a placeholder keeps the
    /// stored value, a plain value replaces it, absence deletes it. New paths
    /// that the credential classification covers route their string value into
    /// secrets; a placeholder at a path with no stored secret is a no-op.
    /// Everything else lands in the config document, with empty sections
    /// pruned. All-or-nothing: the caller persists both or neither.
    /// </summary>
    public static ConfigDocumentSplit Split(
        Dictionary<string, object> config,
        Dictionary<string, object> secrets,
        Dictionary<string, object> merged)
    {
        // Materialize first: a document loaded from disk holds nested sections as
        // JsonElement, and every walk below matches on Dictionary<string, object>.
        var secretsOut = Materialize(secrets);
        var mergedView = Materialize(merged);
        var secretChanges = new List<string>();

        // 1. Reconcile every existing secret leaf against the merged view.
        var secretPaths = new List<string>();
        CollectLeafPaths(secretsOut, [], secretPaths);
        foreach (var path in secretPaths)
        {
            if (!NetclawConfigFiles.TryGetPathValue(mergedView, path, out var mergedValue))
            {
                if (NetclawConfigFiles.RemoveSecretPath(secretsOut, path))
                    secretChanges.Add(path);
            }
            else if (mergedValue is string text && text == SecretPlaceholder)
            {
                // Placeholder round-trip: keep the stored value.
            }
            else
            {
                NetclawConfigFiles.SetSecretPathValue(secretsOut, path, mergedValue!);
                secretChanges.Add(path);
            }
        }

        // 2. Route new credential-classified leaves into secrets so a fresh
        //    credential never lands in plaintext netclaw.json.
        var mergedLeaves = new List<(string Path, object? Value)>();
        CollectLeafPairs(mergedView, [], mergedLeaves);
        foreach (var (path, value) in mergedLeaves)
        {
            if (value is not string text || text == SecretPlaceholder)
                continue;

            if (NetclawConfigFiles.PathPresent(secretsOut, path) || !IsClassifiedSecretPath(path))
                continue;

            NetclawConfigFiles.SetSecretPathValue(secretsOut, path, text);
            secretChanges.Add(path);
        }

        // 3. Everything else in the merged view goes to the config document,
        //    with placeholder leaves and routed secrets removed.
        var configOut = mergedView;
        RemoveSecretLeaves(configOut, secretsOut, []);
        PruneEmptySections(configOut);

        return new ConfigDocumentSplit(configOut, secretsOut, secretChanges);
    }

    private static void RemoveSecretLeaves(
        Dictionary<string, object> node,
        Dictionary<string, object> secretsOut,
        List<string> prefix)
    {
        foreach (var key in node.Keys.ToArray())
        {
            var value = node[key];
            var path = string.Join('.', prefix.Append(key));
            if (value is Dictionary<string, object> section)
            {
                RemoveSecretLeaves(section, secretsOut, [.. prefix, key]);
                continue;
            }

            // A section in secretsOut is not a leaf: "Slack" exists there because
            // it holds BotToken, but that must not remove the whole config section.
            if (value is string text && text == SecretPlaceholder
                || IsScalarSecret(secretsOut, path))
            {
                node.Remove(key);
            }
        }
    }

    private static bool IsScalarSecret(Dictionary<string, object> secrets, string path)
        => NetclawConfigFiles.TryGetPathValue(secrets, path, out var value)
           && value is not Dictionary<string, object>;

    /// <summary>
    /// Deep-convert a loaded document into plain CLR dictionaries and scalars.
    /// A file-loaded tree holds nested objects as <see cref="JsonElement"/>,
    /// which the dictionary-shape walks here do not descend into.
    /// </summary>
    public static Dictionary<string, object> Materialize(Dictionary<string, object> document)
    {
        var result = new Dictionary<string, object>(document.Count);
        foreach (var (key, value) in document)
            result[key] = MaterializeValue(value)!;
        return result;
    }

    private static object? MaterializeValue(object? value)
        => value switch
        {
            Dictionary<string, object> dict => Materialize(dict),
            JsonElement { ValueKind: JsonValueKind.Object } element
                => Materialize(JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText()) ?? []),
            JsonElement { ValueKind: JsonValueKind.Array } element
                => JsonSerializer.Deserialize<object[]>(element.GetRawText()) ?? [],
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            JsonElement element => element.Deserialize<object>() ?? string.Empty,
            null => null,
            _ => value,
        };

    private static void PruneEmptySections(Dictionary<string, object> node)
    {
        foreach (var key in node.Keys.ToArray())
        {
            if (node[key] is not Dictionary<string, object> section)
                continue;

            PruneEmptySections(section);
            if (section.Count == 0)
                node.Remove(key);
        }
    }

    /// <summary>
    /// True when a new path is a credential by convention: a provider
    /// credential leaf, or a channel bot/app token. A plaintext string at such
    /// a path must never land in netclaw.json.
    /// </summary>
    public static bool IsClassifiedSecretPath(string path)
    {
        if (ChannelSecretPaths.Contains(path))
            return true;

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2
               && string.Equals(segments[0], "Providers", StringComparison.Ordinal)
               && ProviderSecretLeafNames.Contains(segments[^1]);
    }

    private static void CollectLeafPaths(Dictionary<string, object> node, List<string> prefix, List<string> paths)
    {
        foreach (var (key, value) in node)
        {
            if (value is Dictionary<string, object> section)
                CollectLeafPaths(section, [.. prefix, key], paths);
            else
                paths.Add(string.Join('.', prefix.Append(key)));
        }
    }

    private static void CollectLeafPairs(Dictionary<string, object> node, List<string> prefix, List<(string, object?)> leaves)
    {
        foreach (var (key, value) in node)
        {
            if (value is Dictionary<string, object> section)
                CollectLeafPairs(section, [.. prefix, key], leaves);
            else
                leaves.Add((string.Join('.', prefix.Append(key)), value));
        }
    }
}

/// <summary>The split result: the new config document, the new secrets document, and changed secret paths.</summary>
public sealed record ConfigDocumentSplit(
    Dictionary<string, object> Config,
    Dictionary<string, object> Secrets,
    IReadOnlyList<string> ChangedSecretPaths);
