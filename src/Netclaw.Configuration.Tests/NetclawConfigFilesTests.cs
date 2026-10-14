// -----------------------------------------------------------------------
// <copyright file="NetclawConfigFilesTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Tests the shared config write engine: path navigation with key-case
/// resolution, prune-on-delete, secret path merge, and the legacy Models
/// backup guard. The CLI wrappers and the daemon config API both run on this
/// code, so these tests pin the behaviors both surfaces rely on.
/// </summary>
public sealed class NetclawConfigFilesTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string ConfigPath => Path.Combine(_dir.Path, "netclaw.json");

    [Fact]
    public void ResolveExistingKeyPath_matches_existing_key_case()
    {
        var root = new Dictionary<string, object>
        {
            ["identity"] = new Dictionary<string, object> { ["agentName"] = "claw" },
        };

        var resolved = NetclawConfigFiles.ResolveExistingKeyPath(root, "Identity.AgentName");

        Assert.Equal("identity.agentName", resolved);
    }

    [Fact]
    public void ResolveExistingKeyPath_keeps_absent_path_unchanged()
    {
        var root = new Dictionary<string, object>();

        var resolved = NetclawConfigFiles.ResolveExistingKeyPath(root, "Memory.Enabled");

        Assert.Equal("Memory.Enabled", resolved);
    }

    [Fact]
    public void RemovePath_prunes_empty_parent_sections()
    {
        var root = new Dictionary<string, object>
        {
            ["Identity"] = new Dictionary<string, object>
            {
                ["UserName"] = "moaaz",
                ["Extra"] = new Dictionary<string, object> { ["Deep"] = 1 },
            },
        };

        var removed = NetclawConfigFiles.RemovePath(root, "Identity.Extra.Deep");

        Assert.True(removed);
        var identity = Assert.IsType<Dictionary<string, object>>(root["Identity"]);
        Assert.False(identity.ContainsKey("Extra"));
    }

    [Fact]
    public void RemovePath_returns_false_for_absent_path()
    {
        var root = new Dictionary<string, object>();

        Assert.False(NetclawConfigFiles.RemovePath(root, "Nope.Nothing"));
    }

    [Fact]
    public void SetPathValue_creates_nested_sections()
    {
        var root = new Dictionary<string, object>();

        NetclawConfigFiles.SetPathValue(root, "Search.Enabled", true);

        Assert.True(NetclawConfigFiles.TryGetPathValue(root, "Search.Enabled", out var value));
        Assert.Equal(true, value);
    }

    [Fact]
    public void LoadJsonDict_missing_file_returns_skeleton()
    {
        var loaded = NetclawConfigFiles.LoadJsonDict(ConfigPath);

        Assert.Equal(EmbeddedSchemaLoader.CurrentSchemaVersion, loaded["configVersion"]);
    }

    [Fact]
    public void WriteConfigFile_round_trips_nested_document()
    {
        var root = new Dictionary<string, object>
        {
            ["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion,
            ["Memory"] = new Dictionary<string, object> { ["Enabled"] = true },
        };

        NetclawConfigFiles.WriteConfigFile(ConfigPath, root);
        var loaded = NetclawConfigFiles.LoadJsonDict(ConfigPath);

        Assert.True(NetclawConfigFiles.TryGetPathValue(loaded, "Memory.Enabled", out var enabled));
        Assert.Equal(true, enabled);
    }

    [Fact]
    public void WriteConfigFile_replacing_legacy_models_creates_backup()
    {
        File.WriteAllText(ConfigPath, """
            { "Models": { "Main": { "Provider": "openai", "Model": "gpt" } } }
            """);
        var root = new Dictionary<string, object>
        {
            ["Models"] = new Dictionary<string, object>
            {
                ["Definitions"] = new Dictionary<string, object>(),
            },
        };

        NetclawConfigFiles.WriteConfigFile(ConfigPath, root);

        Assert.True(File.Exists($"{ConfigPath}.legacy-models.bak"));
        Assert.Contains("Main", File.ReadAllText($"{ConfigPath}.legacy-models.bak"));
    }

    [Fact]
    public void WriteConfigFile_without_legacy_models_writes_no_backup()
    {
        NetclawConfigFiles.WriteConfigFile(
            ConfigPath,
            new Dictionary<string, object> { ["Memory"] = new Dictionary<string, object> { ["Enabled"] = true } });

        Assert.False(File.Exists($"{ConfigPath}.legacy-models.bak"));
    }

    [Fact]
    public void NextBackupPath_advances_past_existing_files()
    {
        var first = Path.Combine(_dir.Path, "netclaw.json");
        File.WriteAllText($"{first}.legacy-models.bak", "old");

        var next = NetclawConfigFiles.NextBackupPath(first, "legacy-models");

        Assert.Equal($"{first}.legacy-models.2.bak", next);
    }

    [Fact]
    public void SetSecretPathValue_writes_nested_and_cleans_literal_collision()
    {
        var secrets = new Dictionary<string, object>
        {
            ["Providers:OpenAI:ApiKey"] = "stale-literal",
        };

        NetclawConfigFiles.SetSecretPathValue(secrets, "Providers.OpenAI.ApiKey", "sk-new");

        Assert.False(secrets.ContainsKey("Providers:OpenAI:ApiKey"));
        var providers = Assert.IsType<Dictionary<string, object>>(secrets["Providers"]);
        var openAi = Assert.IsType<Dictionary<string, object>>(providers["OpenAI"]);
        Assert.Equal("sk-new", openAi["ApiKey"]);
    }

    [Fact]
    public void RemoveSecretPath_removes_and_prunes_empty_sections()
    {
        var secrets = new Dictionary<string, object>
        {
            ["Providers"] = new Dictionary<string, object>
            {
                ["OpenAI"] = new Dictionary<string, object> { ["ApiKey"] = "sk" },
            },
        };

        var changed = NetclawConfigFiles.RemoveSecretPath(secrets, "Providers.OpenAI.ApiKey");

        Assert.True(changed);
        Assert.Empty(secrets);
    }

    [Fact]
    public void RemoveSecretPath_returns_false_when_absent()
        => Assert.False(NetclawConfigFiles.RemoveSecretPath([], "Providers.OpenAI.ApiKey"));

    [Fact]
    public void ParseSecretKeyPath_accepts_dotted_and_colon_forms()
    {
        var dotted = NetclawConfigFiles.ParseSecretKeyPath("Providers.OpenAI.ApiKey");
        var colon = NetclawConfigFiles.ParseSecretKeyPath("Providers:OpenAI:ApiKey");

        Assert.Equal(dotted, colon);
    }

    [Fact]
    public void ParseSecretKeyPath_rejects_empty_segments()
        => Assert.Throws<InvalidOperationException>(
            () => NetclawConfigFiles.ParseSecretKeyPath("Providers..ApiKey"));
}
