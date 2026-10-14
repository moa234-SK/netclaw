// -----------------------------------------------------------------------
// <copyright file="RegistrationConfigViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

// Round-trip + the fake-failure gates the coverage audit requires for the
// registration leaf editor. Validation here must mirror what daemon startup
// and doctor enforce (RegistrationConfigValidator), and every rejected save
// must leave netclaw.json untouched.
using System.Text.Json;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Netclaw.Registration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class RegistrationConfigViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public RegistrationConfigViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, """{ "configVersion": 1, "Daemon": { "Port": 5299 } }""");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Save_persists_registration_and_preserves_unrelated_sections()
    {
        using var vm = new RegistrationConfigViewModel(_paths);
        vm.BaseUrlDraft.Value = "https://panel.example.com";
        vm.ApiBaseUrlDraft.Value = "http://127.0.0.1:5199";
        vm.AutoPair.Value = true;
        vm.HeartbeatIntervalDraft.Value = "60";

        Assert.True(vm.Save());
        Assert.True(vm.IsSaved.Value);

        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var registration = doc.RootElement.GetProperty("Registration");
        Assert.Equal("https://panel.example.com", registration.GetProperty("BaseUrl").GetString());
        Assert.Equal("http://127.0.0.1:5199", registration.GetProperty("ApiBaseUrl").GetString());
        Assert.True(registration.GetProperty("AutoPair").GetBoolean());
        Assert.True(registration.GetProperty("Enabled").GetBoolean());
        Assert.Equal(60, registration.GetProperty("HeartbeatIntervalSeconds").GetInt32());
        // A section write must never clobber the rest of the document.
        Assert.Equal(5299, doc.RootElement.GetProperty("Daemon").GetProperty("Port").GetInt32());

        // Round trip: a fresh view model reads back exactly what was saved.
        using var reloaded = new RegistrationConfigViewModel(_paths);
        Assert.Equal("https://panel.example.com", reloaded.BaseUrlDraft.Value);
        Assert.Equal("http://127.0.0.1:5199", reloaded.ApiBaseUrlDraft.Value);
        Assert.True(reloaded.AutoPair.Value);
        Assert.Equal("60", reloaded.HeartbeatIntervalDraft.Value);
    }

    [Fact]
    public void Save_with_blank_urls_is_the_off_state_and_round_trips()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            { "configVersion": 1, "Registration": { "Enabled": true, "BaseUrl": "https://old.example.com" } }
            """);
        using var vm = new RegistrationConfigViewModel(_paths);

        // Blank drafts clear the URLs: the valid off state, not an error.
        vm.BaseUrlDraft.Value = string.Empty;
        vm.ApiBaseUrlDraft.Value = string.Empty;

        Assert.True(vm.Save());

        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var registration = doc.RootElement.GetProperty("Registration");
        Assert.Equal(JsonValueKind.Null, registration.GetProperty("BaseUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, registration.GetProperty("ApiBaseUrl").ValueKind);

        using var reloaded = new RegistrationConfigViewModel(_paths);
        Assert.Equal(string.Empty, reloaded.BaseUrlDraft.Value);
    }

    [Fact]
    public void Save_rejects_invalid_base_url_before_persistence()
    {
        using var vm = new RegistrationConfigViewModel(_paths);
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        vm.BaseUrlDraft.Value = "panel.example.com";

        Assert.False(vm.Save());
        Assert.Contains("Registration.BaseUrl must be an absolute HTTP or HTTPS URI.",
            vm.Status.Value.Text, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void ToggleAutoPair_without_api_base_url_is_reverted_before_persistence()
    {
        using var vm = new RegistrationConfigViewModel(_paths);
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        vm.BaseUrlDraft.Value = "https://panel.example.com";

        // Auto-pair needs the advertised API URL; the toggle must revert and save nothing.
        Assert.False(vm.ToggleAutoPair());
        Assert.False(vm.AutoPair.Value);
        Assert.Contains("Registration.AutoPair requires Registration.ApiBaseUrl.",
            vm.Status.Value.Text, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Save_rejects_out_of_range_heartbeat_interval_before_persistence()
    {
        using var vm = new RegistrationConfigViewModel(_paths);
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        vm.BaseUrlDraft.Value = "https://panel.example.com";
        vm.HeartbeatIntervalDraft.Value = "10";

        Assert.False(vm.Save());
        Assert.Contains("Registration.HeartbeatIntervalSeconds must be between",
            vm.Status.Value.Text, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Save_enforces_the_same_rules_as_daemon_startup()
    {
        // The cross-surface contract: what this editor refuses, startup refuses too.
        var invalid = new RegistrationConfig { BaseUrl = "no-scheme", HeartbeatIntervalSeconds = 300 };
        Assert.False(RegistrationConfigValidator.TryValidate(invalid, out var error));
        Assert.Equal("Registration.BaseUrl must be an absolute HTTP or HTTPS URI.", error);
    }
}
