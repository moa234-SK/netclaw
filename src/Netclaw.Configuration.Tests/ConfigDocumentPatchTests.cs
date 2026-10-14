// -----------------------------------------------------------------------
// <copyright file="ConfigDocumentPatchTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Pins the config document pipeline: redaction replaces exactly the
/// secrets-file leaves, the sentinel round-trip keeps stored secrets, null
/// deletes and prunes, read-only detection catches every touch form, and the
/// merge keeps unrelated keys.
/// </summary>
public sealed class ConfigDocumentPatchTests
{
    private static Dictionary<string, object> Config(params (string Key, object Value)[] entries)
        => new(entries.Select(e => new KeyValuePair<string, object>(e.Key, e.Value)));

    private static Dictionary<string, object?> Patch(params (string Key, object? Value)[] entries)
        => new(entries.Select(e => new KeyValuePair<string, object?>(e.Key, e.Value)));

    [Fact]
    public void BuildRedactedView_replaces_exactly_the_secrets_leaves()
    {
        var config = Config(
            ("configVersion", 1),
            ("Providers", new Dictionary<string, object>
            {
                ["OpenAI"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai",
                    ["OAuthTokenExpiry"] = "2026-01-01T00:00:00Z",
                },
            }));
        var secrets = Config(
            ("Providers", new Dictionary<string, object>
            {
                ["OpenAI"] = new Dictionary<string, object> { ["ApiKey"] = "sk-real" },
            }));

        var view = ConfigDocumentPatch.BuildRedactedView(config, secrets, out var redacted);

        Assert.True(TryGet(view, "Providers.OpenAI.ApiKey", out var apiKey));
        Assert.Equal(ConfigDocumentPatch.SecretPlaceholder, apiKey);
        // Non-secret neighbors stay visible — including the expiry that must
        // live in netclaw.json, never in secrets.
        Assert.True(TryGet(view, "Providers.OpenAI.OAuthTokenExpiry", out var expiry));
        Assert.Equal("2026-01-01T00:00:00Z", expiry);
        Assert.Equal(["Providers.OpenAI.ApiKey"], redacted);
    }

    [Fact]
    public void MergePatch_updates_one_field_and_keeps_the_rest()
    {
        var target = Config(
            ("Memory", new Dictionary<string, object> { ["Enabled"] = true, ["Provider"] = "sqlite" }),
            ("Identity", new Dictionary<string, object> { ["AgentName"] = "claw" }));

        ConfigDocumentPatch.ApplyMergePatch(target, Patch(("Memory", new Dictionary<string, object?>
        {
            ["Enabled"] = false,
        })));

        Assert.True(TryGet(target, "Memory.Enabled", out var enabled));
        Assert.Equal(false, enabled);
        Assert.True(TryGet(target, "Memory.Provider", out var provider));
        Assert.Equal("sqlite", provider);
        Assert.True(target.ContainsKey("Identity"));
    }

    [Fact]
    public void MergePatch_null_deletes_and_whole_patch_replaces_scalar_section()
    {
        var target = Config(
            ("Identity", new Dictionary<string, object> { ["AgentName"] = "claw", ["UserName"] = "moaaz" }),
            ("Search", "misplaced-scalar"));

        ConfigDocumentPatch.ApplyMergePatch(target, Patch(
            ("Identity", new Dictionary<string, object?> { ["UserName"] = null }),
            ("Search", new Dictionary<string, object?> { ["Enabled"] = true })));

        var identity = Assert.IsType<Dictionary<string, object>>(target["Identity"]);
        Assert.False(identity.ContainsKey("UserName"));
        Assert.True(identity.ContainsKey("AgentName"));
        var search = Assert.IsType<Dictionary<string, object>>(target["Search"]);
        Assert.Equal(true, search["Enabled"]);
    }

    [Fact]
    public void MergePatch_follows_existing_key_case()
    {
        var target = Config(
            ("identity", new Dictionary<string, object> { ["agentName"] = "claw" }));

        ConfigDocumentPatch.ApplyMergePatch(target, Patch(
            ("Identity", new Dictionary<string, object?> { ["AgentName"] = "netclaw" })));

        Assert.Single(target);
        Assert.True(TryGet(target, "identity.agentName", out var name));
        Assert.Equal("netclaw", name);
    }

    [Fact]
    public void CollectTouchedPaths_and_readOnly_detection_catches_all_forms()
    {
        var direct = ConfigDocumentPatch.CollectTouchedPaths(Patch(
            ("Daemon", new Dictionary<string, object?> { ["ExposureMode"] = "Loopback" })));
        Assert.Equal(["Daemon.ExposureMode"], ConfigDocumentPatch.FindReadOnlyTouches(direct));

        var sameValue = ConfigDocumentPatch.CollectTouchedPaths(Patch(
            ("Daemon", new Dictionary<string, object?> { ["Host"] = "127.0.0.1" })));
        Assert.Equal(["Daemon.Host"], ConfigDocumentPatch.FindReadOnlyTouches(sameValue));

        var memberDelete = ConfigDocumentPatch.CollectTouchedPaths(Patch(
            ("Daemon", new Dictionary<string, object?>
            {
                ["TrustedProxies"] = null,
            })));
        Assert.Equal(["Daemon.TrustedProxies"], ConfigDocumentPatch.FindReadOnlyTouches(memberDelete));

        // A whole-section delete would remove the read-only members with it.
        var sectionDelete = ConfigDocumentPatch.CollectTouchedPaths(Patch(("Daemon", null)));
        Assert.Equal(["Daemon"], ConfigDocumentPatch.FindReadOnlyTouches(sectionDelete));

        var arrayMember = ConfigDocumentPatch.CollectTouchedPaths(Patch(
            ("Daemon", new Dictionary<string, object?> { ["TrustedProxies"] = new[] { "10.0.0.0/8" } })));
        Assert.Equal(["Daemon.TrustedProxies"], ConfigDocumentPatch.FindReadOnlyTouches(arrayMember));
    }

    [Fact]
    public void FindReadOnlyTouches_ignores_editable_daemon_fields()
    {
        var touched = ConfigDocumentPatch.CollectTouchedPaths(Patch(
            ("Daemon", new Dictionary<string, object?> { ["Port"] = 5200 })));

        Assert.Empty(ConfigDocumentPatch.FindReadOnlyTouches(touched));
    }

    [Fact]
    public void Split_sentinel_round_trip_keeps_stored_secret()
    {
        var config = Config(
            ("configVersion", 1),
            ("Providers", new Dictionary<string, object>
            {
                ["OpenAI"] = new Dictionary<string, object> { ["Type"] = "openai" },
            }));
        var secrets = Config(
            ("Providers", new Dictionary<string, object>
            {
                ["OpenAI"] = new Dictionary<string, object> { ["ApiKey"] = "sk-old" },
            }));
        var merged = Config(
            ("configVersion", 1),
            ("Providers", new Dictionary<string, object>
            {
                ["OpenAI"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai",
                    ["ApiKey"] = ConfigDocumentPatch.SecretPlaceholder,
                },
            }));

        var split = ConfigDocumentPatch.Split(config, secrets, merged);

        Assert.True(TryGet(split.Secrets, "Providers.OpenAI.ApiKey", out var kept));
        Assert.Equal("sk-old", kept);
        Assert.Empty(split.ChangedSecretPaths);
        Assert.False(ContainsKey(split.Config, "Providers.OpenAI.ApiKey"));
    }

    [Fact]
    public void Split_new_plain_value_rotates_secret_and_keeps_it_out_of_config()
    {
        var config = Config(
            ("configVersion", 1),
            ("Providers", new Dictionary<string, object>
            {
                ["OpenAI"] = new Dictionary<string, object> { ["Type"] = "openai" },
            }));
        var secrets = Config(
            ("Providers", new Dictionary<string, object>
            {
                ["OpenAI"] = new Dictionary<string, object> { ["ApiKey"] = "sk-old" },
            }));
        var merged = Config(
            ("configVersion", 1),
            ("Providers", new Dictionary<string, object>
            {
                ["OpenAI"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai",
                    ["ApiKey"] = "sk-new",
                },
            }));

        var split = ConfigDocumentPatch.Split(config, secrets, merged);

        Assert.True(TryGet(split.Secrets, "Providers.OpenAI.ApiKey", out var rotated));
        Assert.Equal("sk-new", rotated);
        Assert.False(ContainsKey(split.Config, "Providers.OpenAI.ApiKey"));
        Assert.Equal(["Providers.OpenAI.ApiKey"], split.ChangedSecretPaths);
    }

    [Fact]
    public void Split_null_deletes_secret_and_prunes_empty_provider()
    {
        var config = Config(
            ("configVersion", 1),
            ("Providers", new Dictionary<string, object>
            {
                ["OpenAI"] = new Dictionary<string, object> { ["Type"] = "openai" },
            }));
        var secrets = Config(
            ("Providers", new Dictionary<string, object>
            {
                ["Local"] = new Dictionary<string, object> { ["ApiKey"] = "sk-local" },
            }));
        var merged = Config(
            ("configVersion", 1),
            ("Providers", new Dictionary<string, object>
            {
                ["OpenAI"] = new Dictionary<string, object> { ["Type"] = "openai" },
            }));

        var split = ConfigDocumentPatch.Split(config, secrets, merged);

        Assert.False(ContainsKey(split.Secrets, "Providers.Local.ApiKey"));
        Assert.Equal(["Providers.Local.ApiKey"], split.ChangedSecretPaths);
        // The provider entry that lost its only secret is pruned from secrets.
        Assert.False(ContainsKey(split.Secrets, "Providers.Local"));
    }

    [Fact]
    public void Split_routes_new_classified_credential_into_secrets()
    {
        var config = Config(("configVersion", 1));
        var secrets = Config();
        var merged = Config(
            ("configVersion", 1),
            ("Slack", new Dictionary<string, object>
            {
                ["Enabled"] = true,
                ["BotToken"] = "xoxb-new",
            }));

        var split = ConfigDocumentPatch.Split(config, secrets, merged);

        Assert.True(TryGet(split.Secrets, "Slack.BotToken", out var token));
        Assert.Equal("xoxb-new", token);
        Assert.False(ContainsKey(split.Config, "Slack.BotToken"));
        Assert.True(TryGet(split.Config, "Slack.Enabled", out var enabled));
        Assert.Equal(true, enabled);
    }

    [Fact]
    public void Split_keeps_unclassified_plaintext_out_of_secrets()
    {
        var merged = Config(
            ("configVersion", 1),
            ("Identity", new Dictionary<string, object> { ["AgentName"] = "claw" }));

        var split = ConfigDocumentPatch.Split(Config(("configVersion", 1)), Config(), merged);

        Assert.True(TryGet(split.Config, "Identity.AgentName", out var name));
        Assert.Equal("claw", name);
        Assert.Empty(split.ChangedSecretPaths);
    }

    [Fact]
    public void Split_prunes_sections_emptied_by_null_deletes()
    {
        var config = Config(
            ("configVersion", 1),
            ("Identity", new Dictionary<string, object> { ["UserName"] = "moaaz" }));
        var merged = Config(("configVersion", 1));

        var split = ConfigDocumentPatch.Split(config, Config(), merged);

        Assert.False(split.Config.ContainsKey("Identity"));
    }

    [Fact]
    public void ChangedTopLevelSections_reports_only_differing_sections()
    {
        var before = Config(
            ("Memory", new Dictionary<string, object> { ["Enabled"] = true }),
            ("Search", new Dictionary<string, object> { ["Enabled"] = true }));
        var after = Config(
            ("Memory", new Dictionary<string, object> { ["Enabled"] = false }),
            ("Search", new Dictionary<string, object> { ["Enabled"] = true }),
            ("Scheduling", new Dictionary<string, object> { ["Enabled"] = true }));

        var changed = ConfigDocumentPatch.ChangedTopLevelSections(before, after);

        Assert.Equal(["Memory", "Scheduling"], changed.Order());
    }

    private static bool TryGet(Dictionary<string, object> root, string path, out object? value)
        => NetclawConfigFiles.TryGetPathValue(root, path, out value);

    private static bool ContainsKey(Dictionary<string, object> root, string path)
        => NetclawConfigFiles.PathPresent(root, path);
}
