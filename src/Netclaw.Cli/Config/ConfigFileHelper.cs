// -----------------------------------------------------------------------
// <copyright file="ConfigFileHelper.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Cli.Config;

/// <summary>
/// CLI forwarding wrapper over <see cref="NetclawConfigFiles"/>, the shared engine for
/// netclaw.json and secrets.json. Kept internal to the CLI so existing call sites do not churn;
/// new daemon-side callers use the engine directly. The bodies must stay one-line forwards —
/// logic here would drift from the daemon's writes.
/// </summary>
internal static class ConfigFileHelper
{
    /// <summary>
    /// Appended to TUI status messages after a config write. True whether or not a daemon is
    /// running: the daemon's config watcher reloads on change, and with none running the next
    /// start reads the file.
    /// </summary>
    internal const string DaemonAppliesChange = "A running daemon applies the change automatically.";

    internal static (Dictionary<string, object> config, Dictionary<string, object> secrets)
        LoadConfigFiles(Configuration.NetclawPaths paths)
        => NetclawConfigFiles.LoadConfigFiles(paths);

    /// <summary>
    /// Binds the <c>Tools</c> section of netclaw.json the same way as the daemon: on top of the
    /// defaults for the configured posture. A missing file gives the defaults.
    /// </summary>
    /// <remarks>
    /// Do not deserialize <see cref="ToolConfig"/> from the raw JSON. A deserializer replaces a
    /// partial audience profile with an empty one, so an unset <c>McpServersMode</c> reads as an
    /// empty allowlist while the daemon reads the posture default. An editor that saves from that
    /// view narrows the profile (issue #2362).
    /// </remarks>
    internal static ToolConfig LoadToolConfig(Configuration.NetclawPaths paths)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddJsonFile(paths.NetclawConfigPath, optional: true, reloadOnChange: false)
            .Build();
        return PolicyConfiguration.Bind(configuration).Tools;
    }

    internal static string ResolveExistingKeyPath(Dictionary<string, object> root, string path)
        => NetclawConfigFiles.ResolveExistingKeyPath(root, path);

    /// <summary>
    /// Resolves a name typed by the operator to the spelling the file already has. Configuration
    /// keys are read without case, so "Git" is the server "git". An exact match wins; a name that
    /// matches nothing comes back unchanged.
    /// </summary>
    internal static string ResolveName(IEnumerable<string> storedNames, string name)
        => NetclawConfigFiles.ResolveName(storedNames, name);

    /// <summary>
    /// Load a JSON file as a mutable dictionary. Returns a default skeleton if the file doesn't exist.
    /// Comments and trailing commas are an error unless <paramref name="readOnly"/> is set: a caller
    /// that writes the dictionary back would drop them. Read-only callers accept what the daemon accepts.
    /// </summary>
    internal static Dictionary<string, object> LoadJsonDict(string path, bool readOnly = false)
        => NetclawConfigFiles.LoadJsonDict(path, readOnly);

    internal static Dictionary<string, object>? LoadJsonDictOrNull(string path)
        => NetclawConfigFiles.LoadJsonDictOrNull(path);

    internal static Dictionary<string, object>? TryLoadJsonDictOrNull(string path, out string? error)
        => NetclawConfigFiles.TryLoadJsonDictOrNull(path, out error);

    internal static Dictionary<string, object> GetOrCreateSection(Dictionary<string, object> dict, string key)
        => NetclawConfigFiles.GetOrCreateSection(dict, key);

    internal static Dictionary<string, object>? GetSectionOrNull(Dictionary<string, object> dict, string key)
        => NetclawConfigFiles.GetSectionOrNull(dict, key);

    internal static T? DeserializeSection<T>(object raw)
        => NetclawConfigFiles.DeserializeSection<T>(raw);

    internal static T LoadSection<T>(Dictionary<string, object> root, string sectionName) where T : new()
        => NetclawConfigFiles.LoadSection<T>(root, sectionName);

    internal static void WriteConfigFile(string path, Dictionary<string, object> data)
        => NetclawConfigFiles.WriteConfigFile(path, data);

    // The writer is a parameter so a test can make the write fail like a full disk.
    internal static void WriteConfigFile(
        string path,
        Dictionary<string, object> data,
        Action<string, string, Action<string>?> atomicWrite)
        => NetclawConfigFiles.WriteConfigFile(path, data, atomicWrite);

    internal static bool CanReplaceLink(Exception ex)
        => NetclawConfigFiles.CanReplaceLink(ex);

    internal static void WriteSecretsFile(Configuration.NetclawPaths paths, Dictionary<string, object> data)
        => NetclawConfigFiles.WriteSecretsFile(paths, data);

    internal static void UpdateSecretsFile(
        Configuration.NetclawPaths paths,
        Func<Dictionary<string, object>, bool, bool> update,
        ISecretsProtector? protector = null,
        CancellationToken cancellationToken = default)
        => NetclawConfigFiles.UpdateSecretsFile<object?>(
            paths,
            (secrets, fileExisted) => (update(secrets, fileExisted), null),
            protector,
            cancellationToken);

    internal static TResult UpdateSecretsFile<TResult>(
        Configuration.NetclawPaths paths,
        Func<Dictionary<string, object>, bool, (bool Write, TResult Result)> update,
        ISecretsProtector? protector = null,
        CancellationToken cancellationToken = default)
        => NetclawConfigFiles.UpdateSecretsFile(paths, update, protector, cancellationToken);

    internal static bool PathPresent(Dictionary<string, object> root, string path)
        => NetclawConfigFiles.PathPresent(root, path);

    internal static bool TryGetPathValue(Dictionary<string, object> root, string path, out object? value)
        => NetclawConfigFiles.TryGetPathValue(root, path, out value);

    internal static void SetPathValue(Dictionary<string, object> root, string path, object? value)
        => NetclawConfigFiles.SetPathValue(root, path, value);

    internal static bool RemovePath(Dictionary<string, object> root, string path)
        => NetclawConfigFiles.RemovePath(root, path);

    internal static bool SecretPresent(Configuration.NetclawPaths paths, string path)
        => NetclawConfigFiles.SecretPresent(paths, path);

    internal static string DecryptIfEncrypted(Configuration.NetclawPaths paths, string? value)
        => NetclawConfigFiles.DecryptIfEncrypted(paths, value);

    internal static string? ReadDecryptedSecret(Configuration.NetclawPaths paths, string path)
        => NetclawConfigFiles.ReadDecryptedSecret(paths, path);
}
