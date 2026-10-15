// -----------------------------------------------------------------------
// <copyright file="NetclawConfigFiles.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Configuration;

/// <summary>
/// The single engine for reading and writing netclaw.json and secrets.json.
/// The CLI (TUI, wizard, commands) and the daemon config HTTP API both route
/// through it so the two writers cannot drift. Owns path navigation with
/// existing-key case resolution (a duplicate key of different case stops the
/// daemon at startup), atomic mode-preserving writes, the legacy Models backup,
/// and the secrets path merge with colon-collision cleanup.
/// </summary>
public static class NetclawConfigFiles
{
    private static readonly JsonStringEnumConverter EnumConverter = new();

    /// <summary>Config and secrets file serialization: indented, enum values as strings.</summary>
    public static readonly JsonSerializerOptions ConfigFileOptions = new()
    {
        WriteIndented = true,
        Converters = { EnumConverter },
    };

    /// <summary>Config file deserialization: web defaults plus enum values as strings.</summary>
    public static readonly JsonSerializerOptions ConfigReadOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { EnumConverter },
    };

    /// <summary>
    /// Plain-dictionary reads of netclaw.json and secrets.json that never write the file back:
    /// comments and trailing commas load, as they do for the daemon.
    /// </summary>
    public static readonly JsonSerializerOptions ConfigDictReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Plain indented output, matching the CLI's secrets-file formatting.</summary>
    public static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Load both netclaw.json and secrets.json as mutable dictionaries.
    /// Missing files get a default <c>{ "configVersion": 1 }</c> skeleton.
    /// </summary>
    public static (Dictionary<string, object> Config, Dictionary<string, object> Secrets)
        LoadConfigFiles(NetclawPaths paths)
    {
        var config = LoadJsonDict(paths.NetclawConfigPath);
        var secrets = LoadJsonDict(paths.SecretsPath);
        return (config, secrets);
    }

    /// <summary>
    /// Load a JSON file as a mutable dictionary. Returns a default skeleton if the file doesn't exist.
    /// Comments and trailing commas are an error unless <paramref name="readOnly"/> is set: a caller
    /// that writes the dictionary back would drop them. Read-only callers accept what the daemon accepts.
    /// </summary>
    public static Dictionary<string, object> LoadJsonDict(string path, bool readOnly = false)
    {
        if (!File.Exists(path))
            return new Dictionary<string, object> { ["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion };

        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, object>>(text, readOnly ? ConfigDictReadOptions : null)
               ?? new Dictionary<string, object> { ["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion };
    }

    /// <summary>
    /// Load a JSON file as a mutable dictionary, or <c>null</c> when the file is missing or empty.
    /// </summary>
    public static Dictionary<string, object>? LoadJsonDictOrNull(string path)
    {
        if (!File.Exists(path))
            return null;

        var config = LoadJsonDict(path);
        return config.Count == 0 ? null : config;
    }

    /// <summary>
    /// Like <see cref="LoadJsonDictOrNull"/> but never throws on an unreadable or malformed file:
    /// returns the parsed dict (or <c>null</c>) and a human-readable reason via
    /// <paramref name="error"/> when the file existed but could not be read.
    /// </summary>
    public static Dictionary<string, object>? TryLoadJsonDictOrNull(string path, out string? error)
    {
        error = null;
        try
        {
            return LoadJsonDictOrNull(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = $"Could not read {Path.GetFileName(path)}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Returns <paramref name="path"/> with each segment replaced by the key that the file already
    /// has, when one differs only in letter case. The daemon reads keys without case, so a write
    /// to <c>AllowedTools</c> beside an existing <c>allowedTools</c> gives a duplicate key, and
    /// the daemon then stops at startup.
    /// </summary>
    public static string ResolveExistingKeyPath(Dictionary<string, object> root, string path)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        object? current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            // A loaded file holds nested objects as JsonElement until a writer converts them.
            IEnumerable<string>? keys = current switch
            {
                Dictionary<string, object> dictionary => dictionary.Keys,
                JsonElement { ValueKind: JsonValueKind.Object } element => element.EnumerateObject().Select(static property => property.Name),
                _ => null
            };
            var segment = segments[i];
            var existing = keys?.FirstOrDefault(key => string.Equals(key, segment, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                break;

            segments[i] = existing;
            current = current is Dictionary<string, object> parent ? parent[existing] : ((JsonElement)current!).GetProperty(existing);
        }

        return string.Join('.', segments);
    }

    /// <summary>
    /// Resolves a name typed by the operator to the spelling the file already has. Configuration
    /// keys are read without case, so "Git" is the server "git". An exact match wins; a name that
    /// matches nothing comes back unchanged.
    /// </summary>
    public static string ResolveName(IEnumerable<string> storedNames, string name)
    {
        var stored = storedNames.ToList();
        return stored.Contains(name, StringComparer.Ordinal)
            ? name
            : stored.FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) ?? name;
    }

    /// <summary>
    /// Get or create a nested dictionary section. Handles JsonElement deserialization
    /// when the section was loaded from a file.
    /// </summary>
    public static Dictionary<string, object> GetOrCreateSection(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var existing) && existing is not null)
        {
            if (existing is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Null)
                {
                    var fresh = new Dictionary<string, object>();
                    dict[key] = fresh;
                    return fresh;
                }

                var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(je.GetRawText())
                             ?? [];
                dict[key] = parsed;
                return parsed;
            }

            return (Dictionary<string, object>)existing;
        }

        var section = new Dictionary<string, object>();
        dict[key] = section;
        return section;
    }

    /// <summary>
    /// Get an existing nested dictionary section, or null if it doesn't exist.
    /// </summary>
    public static Dictionary<string, object>? GetSectionOrNull(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var existing))
            return null;

        if (existing is JsonElement je)
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(je.GetRawText())
                         ?? [];
            dict[key] = parsed;
            return parsed;
        }

        return existing as Dictionary<string, object>;
    }

    /// <summary>
    /// Deserialize a loaded config section value into <typeparamref name="T"/>. The value may be a
    /// <see cref="JsonElement"/> (freshly loaded from disk) or an already-materialized CLR object
    /// (just written in-memory) — both shapes are handled.
    /// </summary>
    public static T? DeserializeSection<T>(object raw)
    {
        var json = raw is JsonElement element
            ? element.GetRawText()
            : JsonSerializer.Serialize(raw, ConfigFileOptions);
        return JsonSerializer.Deserialize<T>(json, ConfigReadOptions);
    }

    /// <summary>
    /// Read a typed config section out of a loaded config dictionary, returning <c>new T()</c>
    /// when the section is absent, null, or deserializes to null.
    /// </summary>
    public static T LoadSection<T>(Dictionary<string, object> root, string sectionName) where T : new()
    {
        if (!root.TryGetValue(sectionName, out var raw) || raw is null)
            return new T();

        return DeserializeSection<T>(raw) ?? new T();
    }

    /// <summary>
    /// Serialize a config dictionary and write it to disk, creating parent directories if needed.
    /// The file keeps its mode (an owner-only netclaw.json stays owner-only), and a symbolic link is
    /// followed so the file it points at is rewritten and the link stays a link. When the link's
    /// target is not writable (permission denied, read-only, or busy), the link itself is replaced.
    /// </summary>
    public static void WriteConfigFile(string path, Dictionary<string, object> data)
        => WriteConfigFile(path, data, AtomicFile.WriteAllText);

    /// <summary>
    /// The writer is a parameter so a test can make the write fail like a full disk.
    /// </summary>
    public static void WriteConfigFile(
        string path,
        Dictionary<string, object> data,
        Action<string, string, Action<string>?> atomicWrite)
    {
        var info = new FileInfo(path);
        var target = info.LinkTarget is null ? path : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        var json = JsonSerializer.Serialize(data, ConfigFileOptions);

        try
        {
            WriteKeepingMode(target, data, json, atomicWrite);
        }
        catch (Exception ex) when (target != path && CanReplaceLink(ex))
        {
            // The link's target cannot take a new file: another owner, a read-only file system, or
            // a single file mounted into a container (busy). Replace the link itself, as saves did
            // before links were followed. Any other write failure, such as a full disk, is a save
            // error: replacing the link would detach it.
            WriteKeepingMode(path, data, json, atomicWrite);
        }
    }

    // .NET on Unix puts the errno in IOException.HResult. These values are the same on Linux and macOS.
    private const int BusyErrno = 16;
    private const int ReadOnlyFileSystemErrno = 30;

    /// <summary>
    /// True for the write errors that mean the link's target cannot take the file: permission
    /// denied, a read-only file system, or a busy file (a single-file bind mount).
    /// </summary>
    public static bool CanReplaceLink(Exception ex)
        => ex is UnauthorizedAccessException
           || (ex is IOException { HResult: BusyErrno or ReadOnlyFileSystemErrno } && !OperatingSystem.IsWindows());

    private static void WriteKeepingMode(string path, Dictionary<string, object> data, string json, Action<string, string, Action<string>?> atomicWrite)
    {
        PreserveLegacyModelsBackup(path, data);
        atomicWrite(path, json, temp => AtomicFile.CopyUnixMode(path, temp));
    }

    private static void PreserveLegacyModelsBackup(string path, Dictionary<string, object> data)
    {
        if (!File.Exists(path)
            || GetSectionOrNull(data, "Models") is not { } newModels
            || !newModels.ContainsKey("Definitions"))
            return;

        using var existing = JsonDocument.Parse(File.ReadAllText(path));
        if (!existing.RootElement.TryGetProperty("Models", out var oldModels)
            || oldModels.ValueKind != JsonValueKind.Object
            || !oldModels.TryGetProperty("Main", out _))
            return;

        ThrowIfLegacyModelsEnvironmentOverride();

        File.Copy(path, NextBackupPath(path, "legacy-models"), overwrite: false);
    }

    private static readonly string[] LegacyModelsEnvironmentPrefixes =
    [
        "NETCLAW_Models__Main__",
        "NETCLAW_Models__Fallback__",
        "NETCLAW_Models__Compaction__",
    ];

    /// <summary>The legacy <c>NETCLAW_Models__Main__*</c> style override that is set, if any.</summary>
    public static string? FindLegacyModelsEnvironmentOverride()
        => Environment.GetEnvironmentVariables().Keys
            .OfType<string>()
            .FirstOrDefault(key => LegacyModelsEnvironmentPrefixes.Any(prefix =>
                key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// A legacy <c>NETCLAW_Models__Main__*</c> environment override wins over the file at every
    /// read, so replacing the legacy Models shape on disk would be silently ignored. Refuse the
    /// write instead, loudly.
    /// </summary>
    public static void ThrowIfLegacyModelsEnvironmentOverride()
    {
        var legacyEnvironmentOverride = FindLegacyModelsEnvironmentOverride();

        if (legacyEnvironmentOverride is null)
            return;

        throw new ModelConfigurationException(
            $"Cannot migrate Models while legacy environment override '{legacyEnvironmentOverride}' is set. " +
            "Move model overrides to NETCLAW_Models__Definitions__<name>__* and " +
            "NETCLAW_Models__Roles__* first.");
    }

    /// <summary>
    /// Returns <c>{path}.{name}.bak</c>, then <c>.{name}.2.bak</c>, and so on: the first name that
    /// is not a file. A directory at a candidate name is not skipped, so the copy fails loudly
    /// instead of the write proceeding without a backup.
    /// </summary>
    public static string NextBackupPath(string path, string name)
    {
        var candidate = $"{path}.{name}.bak";
        for (var number = 2; File.Exists(candidate); number++)
            candidate = $"{path}.{name}.{number}.bak";

        return candidate;
    }

    /// <summary>
    /// Serialize and write secrets.json using hardened permissions and encryption-at-rest.
    /// </summary>
    public static void WriteSecretsFile(NetclawPaths paths, Dictionary<string, object> data)
    {
        var protector = SecretsProtection.CreateProtector(paths);
        SecretsFileWriter.Write(paths.SecretsPath, data, options: IndentedOptions, protector: protector);
    }

    public static TResult UpdateSecretsFile<TResult>(
        NetclawPaths paths,
        Func<Dictionary<string, object>, bool, (bool Write, TResult Result)> update,
        ISecretsProtector? protector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var effectiveProtector = protector ?? SecretsProtection.CreateProtector(paths);
        return SecretsFileWriter.Update(
            paths.SecretsPath,
            (root, fileExisted) =>
            {
                var secrets = JsonSerializer.Deserialize<Dictionary<string, object>>(
                                  root.ToJsonString(ConfigFileOptions),
                                  ConfigReadOptions)
                              ?? [];
                var outcome = update(secrets, fileExisted);
                if (!outcome.Write)
                    return (null, outcome.Result);

                var updatedRoot = JsonSerializer.SerializeToNode(secrets, ConfigFileOptions)?.AsObject()
                                  ?? [];
                return (updatedRoot, outcome.Result);
            },
            effectiveProtector,
            IndentedOptions,
            cancellationToken);
    }

    public static bool PathPresent(Dictionary<string, object> root, string path)
        => TryGetPathValue(root, path, out _);

    public static bool TryGetPathValue(Dictionary<string, object> root, string path, out object? value)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        object? current = root;

        foreach (var segment in segments)
        {
            if (!TryGetChildValue(current, segment, out current))
            {
                value = null;
                return false;
            }
        }

        value = NormalizeNodeValue(current);
        return true;
    }

    public static void SetPathValue(Dictionary<string, object> root, string path, object? value)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        Dictionary<string, object> current = root;

        for (var i = 0; i < segments.Length - 1; i++)
            current = GetOrCreateSection(current, segments[i]);

        current[segments[^1]] = value!;
    }

    public static bool RemovePath(Dictionary<string, object> root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        Dictionary<string, object>? current = root;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = current is null ? null : GetSectionOrNull(current, segments[i]);
            if (current is null)
                return false;
        }

        if (current is null)
            return false;

        var removed = current.Remove(segments[^1]);
        if (!removed)
            return false;

        PruneEmptySections(root, segments);
        return true;
    }

    public static bool SecretPresent(NetclawPaths paths, string path)
    {
        // Read-only: secrets.json may hold comments or trailing commas the daemon accepts.
        var secrets = LoadJsonDict(paths.SecretsPath, readOnly: true);
        return PathPresent(secrets, path);
    }

    public static string DecryptIfEncrypted(NetclawPaths paths, string? value)
    {
        if (string.IsNullOrEmpty(value) || !ISecretsProtector.IsEncrypted(value))
            return value ?? string.Empty;

        var protector = SecretsProtection.CreateProtector(paths);
        return protector.Unprotect(value);
    }

    /// <summary>
    /// Read a secret value from secrets.json at <paramref name="path"/>, decrypting it if it was
    /// stored encrypted-at-rest. Returns <c>null</c> when the file or path is absent. Deliberately
    /// does NOT apply whitespace normalization — each caller applies its own policy.
    /// </summary>
    public static string? ReadDecryptedSecret(NetclawPaths paths, string path)
    {
        // Read-only: secrets.json may hold comments or trailing commas the daemon accepts.
        var secrets = LoadJsonDict(paths.SecretsPath, readOnly: true);
        return TryGetPathValue(secrets, path, out var value)
            ? DecryptIfEncrypted(paths, value?.ToString())
            : null;
    }

    /// <summary>
    /// Split a secret key path on dots and colons. A colon inside one segment is a literal-key
    /// spelling, so both forms are accepted and normalized into segments.
    /// </summary>
    public static string[] ParseSecretKeyPath(string keyPath)
    {
        var delimiters = new[] { '.', ':' };
        var segments = keyPath.Split(delimiters, StringSplitOptions.None)
            .Select(segment => segment.Trim())
            .ToArray();

        if (segments.Length == 0 || segments.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Secret key must be a non-empty dotted or colon-delimited path.");

        return segments;
    }

    /// <summary>
    /// Upsert a secret value into a loaded secrets dictionary, cleaning colon-collision literal
    /// keys first. Mirrors the JsonObject-based updater's path-merge, but over the
    /// <see cref="Dictionary{String, Object}"/> shape the engine loads. One deliberate difference:
    /// this rejects a scalar at an intermediate segment (GetOrCreateSection throws) instead of
    /// overwriting it.
    /// </summary>
    public static void SetSecretPathValue(Dictionary<string, object> secrets, string path, object value)
    {
        var segments = ParseSecretKeyPath(path);
        RemoveLiteralCollisionKeys(secrets, segments);

        var current = secrets;
        for (var i = 0; i < segments.Length - 1; i++)
            current = GetOrCreateSection(current, segments[i]);

        current[segments[^1]] = value;
    }

    /// <summary>
    /// Remove a secret at a dotted or colon-delimited path, cleaning colon-collision literal keys,
    /// and prune sections the removal left empty.
    /// </summary>
    public static bool RemoveSecretPath(Dictionary<string, object> secrets, string path)
    {
        var segments = ParseSecretKeyPath(path);
        var changed = RemovePathBySegments(secrets, segments);
        changed |= RemoveLiteralCollisionKeys(secrets, segments);
        return changed;
    }

    private static bool RemovePathBySegments(Dictionary<string, object> root, IReadOnlyList<string> segments)
    {
        var current = root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var next = GetSectionOrNull(current, segments[i]);
            if (next is null)
                return false;

            current = next;
        }

        var removed = current.Remove(segments[^1]);
        if (removed)
            PruneEmptySections(root, segments.ToArray());

        return removed;
    }

    // A literal key "A:B" collides with a nested write to A→B. Before a nested write, the literal
    // spellings of every prefix are removed so both cannot coexist. Keep in sync with
    // SecretsJsonUpdater.RemoveLiteralCollisionKeys in the CLI (JsonObject-based variant).
    private static bool RemoveLiteralCollisionKeys(Dictionary<string, object> root, IReadOnlyList<string> segments)
        => RemoveLiteralCollisionKeys(root, segments, offset: 0);

    private static bool RemoveLiteralCollisionKeys(Dictionary<string, object> current, IReadOnlyList<string> segments, int offset)
    {
        var changed = false;
        for (var end = offset + 2; end <= segments.Count; end++)
            changed |= current.Remove(string.Join(':', segments.Skip(offset).Take(end - offset)));

        if (offset < segments.Count - 1 && GetSectionOrNull(current, segments[offset]) is { } child)
            changed |= RemoveLiteralCollisionKeys(child, segments, offset + 1);

        return changed;
    }

    private static bool TryGetChildValue(object? current, string segment, out object? child)
    {
        switch (current)
        {
            case Dictionary<string, object> dict when dict.TryGetValue(segment, out child):
                return true;
            case JsonObject jsonObject when jsonObject.TryGetPropertyValue(segment, out var node):
                child = node;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Object && element.TryGetProperty(segment, out var property):
                child = property;
                return true;
            default:
                child = null;
                return false;
        }
    }

    private static object? NormalizeNodeValue(object? value)
        => value switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.Object
                => JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText()),
            JsonElement element when element.ValueKind == JsonValueKind.Array
                => JsonSerializer.Deserialize<object[]>(element.GetRawText()),
            JsonElement element when element.ValueKind == JsonValueKind.String
                => element.GetString(),
            JsonElement element when element.ValueKind == JsonValueKind.True
                => true,
            JsonElement element when element.ValueKind == JsonValueKind.False
                => false,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var longValue)
                => longValue,
            JsonElement element when element.ValueKind == JsonValueKind.Number
                => element.GetDouble(),
            JsonNode node => node.Deserialize<object>(),
            _ => value
        };

    private static void PruneEmptySections(Dictionary<string, object> root, string[] segments)
    {
        for (var depth = segments.Length - 1; depth > 0; depth--)
        {
            var parentPath = string.Join('.', segments.Take(depth));
            if (!TryGetPathValue(root, parentPath, out var parentValue)
                || parentValue is not Dictionary<string, object> parentSection)
            {
                continue;
            }

            if (parentSection.Count != 0)
                break;

            RemovePath(root, parentPath);
        }
    }
}
