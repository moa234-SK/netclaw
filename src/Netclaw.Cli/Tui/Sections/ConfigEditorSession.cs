// -----------------------------------------------------------------------
// <copyright file="ConfigEditorSession.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Sections;

/// <summary>
/// Shared merge pipeline for config leaf editors. It applies explicit editor
/// contributions to runtime config, secrets, and passive editor state.
/// </summary>
internal sealed class ConfigEditorSession
{
    private readonly NetclawPaths _paths;
    private readonly ConfigEditorStateStore _stateStore;
    private readonly List<SectionContribution> _secretContributions = [];

    public ConfigEditorSession(NetclawPaths paths)
    {
        _paths = paths;
        _stateStore = new ConfigEditorStateStore(paths);
        Config = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
        Secrets = ConfigFileHelper.LoadJsonDict(paths.SecretsPath);
    }

    internal Dictionary<string, object> Config { get; }

    internal Dictionary<string, object> Secrets { get; }

    public void Apply(SectionContribution contribution)
    {
        ApplyFieldActions(Config, contribution);
        ApplySecretActions(Secrets, contribution);
        if (HasMutatingSecretActions(contribution))
            _secretContributions.Add(contribution);
        _stateStore.Apply(contribution.StateActionsOrEmpty);
    }

    public void Save()
    {
        _paths.EnsureDirectoriesExist();
        Config["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;
        EnsureLoads();
        ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, Config);

        if (_secretContributions.Count > 0)
        {
            ConfigFileHelper.UpdateSecretsFile(_paths, (secrets, fileExisted) =>
            {
                var changed = false;
                foreach (var contribution in _secretContributions)
                    changed |= ApplySecretActions(secrets, contribution);

                return changed && (fileExisted || HasUserSecretData(secrets));
            });
        }
    }

    // Validate after the version assignment, which can introduce a duplicate key.
    private void EnsureLoads()
    {
        try
        {
            using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(Config, JsonDefaults.ConfigFile));
            new ConfigurationBuilder().AddJsonStream(stream).Build();
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException)
        {
            throw new InvalidOperationException($"The new netclaw.json would not load: {ex.Message}");
        }
    }

    internal static bool ApplyFieldActions(Dictionary<string, object> config, SectionContribution contribution)
    {
        var changed = false;
        foreach (var action in contribution.FieldActionsOrEmpty)
        {
            // The daemon reads keys without case: write into the spelling the file already has,
            // or the file gets a second "Identity" beside "identity" and the daemon will not start.
            var path = ConfigFileHelper.ResolveExistingKeyPath(config, action.Path);
            switch (action.Action)
            {
                case SectionFieldActionKind.Set:
                    ConfigFileHelper.SetPathValue(config, path, action.Value);
                    changed = true;
                    break;
                case SectionFieldActionKind.Delete:
                    changed |= ConfigFileHelper.RemovePath(config, path);
                    break;
            }
        }

        return changed;
    }

    internal static bool ApplySecretActions(Dictionary<string, object> secrets, SectionContribution contribution)
    {
        var changed = false;
        foreach (var action in contribution.SecretActionsOrEmpty)
        {
            switch (action.Action)
            {
                case SectionSecretActionKind.Preserve:
                    break;
                case SectionSecretActionKind.Set:
                    SetSecretPathValue(secrets, action.Path, action.Value!);
                    changed = true;
                    break;
                case SectionSecretActionKind.Delete:
                    changed |= RemoveSecretPath(secrets, action.Path);
                    break;
            }
        }

        return changed;
    }

    internal static void ApplyEditorStateActions(
        NetclawPaths paths,
        IEnumerable<SectionContribution> contributions)
    {
        var stateStore = new ConfigEditorStateStore(paths);
        foreach (var contribution in contributions)
            stateStore.Apply(contribution.StateActionsOrEmpty);
    }

    private static bool HasUserSecretData(Dictionary<string, object> secrets)
        => secrets.Keys.Any(static key => !string.Equals(key, "configVersion", StringComparison.Ordinal));

    private static bool HasMutatingSecretActions(SectionContribution contribution)
        => contribution.SecretActionsOrEmpty.Any(static action => action.Action is not SectionSecretActionKind.Preserve);

    // Secret path-merge lives in the shared engine (NetclawConfigFiles) so the CLI and the daemon
    // config API cannot drift. It mirrors SecretsJsonUpdater's colon-collision cleanup but rejects
    // a scalar at an intermediate segment — see ConfigEditorSessionTests for the pinned behavior.
    private static void SetSecretPathValue(Dictionary<string, object> secrets, string path, object value)
        => NetclawConfigFiles.SetSecretPathValue(secrets, path, value);

    private static bool RemoveSecretPath(Dictionary<string, object> secrets, string path)
        => NetclawConfigFiles.RemoveSecretPath(secrets, path);
}
