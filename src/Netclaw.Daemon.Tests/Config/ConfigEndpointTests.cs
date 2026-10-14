// -----------------------------------------------------------------------
// <copyright file="ConfigEndpointTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Config;
using Netclaw.Daemon.Security;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Config;

/// <summary>
/// Endpoint-level behavior for GET/PATCH /api/config: authorization, secret
/// redaction, validation-before-persistence (both files must stay
/// byte-identical on any 400), read-only daemon fields, and secret rotation.
/// </summary>
public sealed class ConfigEndpointTests : IAsyncDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly DaemonRestartSignal _restartSignal = new();
    private readonly RecordingLoggerProvider _loggerProvider = new();

    private string ConfigText => File.ReadAllText(_paths.NetclawConfigPath);
    private string SecretsText => File.ReadAllText(_paths.SecretsPath);

    public ConfigEndpointTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, """
            {
              "configVersion": 1,
              "Memory": { "Enabled": true, "RecallTimeoutMs": 300 },
              "Security": { "ShellExecutionMode": "Off" },
              "Daemon": { "Host": "127.0.0.1", "Port": 5199 },
              "Providers": { "OpenAI": { "Type": "openai", "Endpoint": "https://api.openai.com/v1" } }
            }
            """);
        File.WriteAllText(_paths.SecretsPath, """
            { "Providers": { "OpenAI": { "ApiKey": "sk-original" } } }
            """);
    }

    public ValueTask DisposeAsync()
    {
        _dir.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Operator_GET_returns_redacted_document()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var response = await app.GetTestClient().GetAsync("/api/config", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var config = json.GetProperty("config");

        // The secret leaf is the placeholder; its non-secret neighbors stay visible.
        Assert.Equal(ConfigDocumentPatch.SecretPlaceholder,
            config.GetProperty("Providers").GetProperty("OpenAI").GetProperty("ApiKey").GetString());
        Assert.Equal("https://api.openai.com/v1",
            config.GetProperty("Providers").GetProperty("OpenAI").GetProperty("Endpoint").GetString());

        var redacted = json.GetProperty("redactedPaths");
        Assert.Contains("Providers.OpenAI.ApiKey", redacted.EnumerateArray().Select(p => p.GetString()));

        var readOnly = json.GetProperty("readOnlyPaths");
        Assert.Contains("Daemon.Host", readOnly.EnumerateArray().Select(p => p.GetString()));
        Assert.Contains("Daemon.ExposureMode", readOnly.EnumerateArray().Select(p => p.GetString()));
        Assert.Contains("Daemon.TrustedProxies", readOnly.EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task Unauthenticated_GET_and_PATCH_return_401()
    {
        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();

        var get = await client.GetAsync("/api/config", TestContext.Current.CancellationToken);
        var patch = await client.PatchAsync("/api/config",
            JsonContent("""{ "Memory": { "Enabled": false } }"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, patch.StatusCode);
        Assert.DoesNotContain("sk-original", await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Operator_PATCH_happy_path_persists_and_reports_pending_restart()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);

        var response = await app.GetTestClient().PatchAsync("/api/config",
            JsonContent("""{ "Memory": { "Enabled": false } }"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(json.GetProperty("restartPending").GetBoolean());
        Assert.Contains("Memory", json.GetProperty("changedTopLevel").EnumerateArray().Select(p => p.GetString()));

        var onDisk = NetclawConfigFiles.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(NetclawConfigFiles.TryGetPathValue(onDisk, "Memory.Enabled", out var enabled));
        Assert.Equal(false, enabled);
        // Unrelated keys survive the merge.
        Assert.True(NetclawConfigFiles.TryGetPathValue(onDisk, "Memory.RecallTimeoutMs", out _));
        Assert.True(NetclawConfigFiles.TryGetPathValue(onDisk, "Providers.OpenAI.Endpoint", out _));
    }

    [Fact]
    public async Task PATCH_noop_reports_no_restart_and_writes_nothing()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var before = (ConfigText, SecretsText);

        var response = await app.GetTestClient().PatchAsync("/api/config",
            JsonContent("""{ "Memory": { "Enabled": true } }"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(json.GetProperty("restartPending").GetBoolean());
        Assert.Equal(before.ConfigText, ConfigText);
        Assert.Equal(before.SecretsText, SecretsText);
    }

    [Fact]
    public async Task PATCH_read_only_touch_returns_400_and_writes_nothing()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var before = (ConfigText, SecretsText);
        var client = app.GetTestClient();

        var change = await client.PatchAsync("/api/config",
            JsonContent("""{ "Daemon": { "ExposureMode": "reverse-proxy" } }"""),
            TestContext.Current.CancellationToken);
        var sameValue = await client.PatchAsync("/api/config",
            JsonContent("""{ "Daemon": { "Host": "127.0.0.1" } }"""),
            TestContext.Current.CancellationToken);
        var sectionDelete = await client.PatchAsync("/api/config",
            JsonContent("""{ "Daemon": null }"""),
            TestContext.Current.CancellationToken);

        foreach (var response in new[] { change, sameValue, sectionDelete })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.NotEmpty(body.GetProperty("errors").EnumerateArray());
            Assert.NotEmpty(body.GetProperty("rejectedPaths").EnumerateArray());
        }

        Assert.Equal(before.ConfigText, ConfigText);
        Assert.Equal(before.SecretsText, SecretsText);
    }

    [Fact]
    public async Task PATCH_editable_daemon_field_is_accepted()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);

        var response = await app.GetTestClient().PatchAsync("/api/config",
            JsonContent("""{ "Daemon": { "Port": 5200 } }"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var onDisk = NetclawConfigFiles.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(NetclawConfigFiles.TryGetPathValue(onDisk, "Daemon.Port", out var port));
        Assert.Equal(5200L, port);
    }

    [Fact]
    public async Task PATCH_schema_invalid_value_returns_400_and_writes_nothing()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var before = (ConfigText, SecretsText);

        var response = await app.GetTestClient().PatchAsync("/api/config",
            JsonContent("""{ "Security": { "ShellExecutionMode": "Always" } }"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var errors = string.Join(" ", body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("Security", errors);

        // The validation gate fires before persistence: both files byte-identical.
        Assert.Equal(before.ConfigText, ConfigText);
        Assert.Equal(before.SecretsText, SecretsText);
    }

    [Fact]
    public async Task PATCH_unknown_provider_type_on_bound_model_returns_400_and_writes_nothing()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var before = (ConfigText, SecretsText);

        var response = await app.GetTestClient().PatchAsync("/api/config",
            JsonContent("""
                {
                  "Models": { "Main": { "Provider": "Ghost", "ModelId": "m1" } },
                  "Providers": { "Ghost": { "Type": "nonsense", "ApiKey": "sk-ghost" } }
                }
                """),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var errors = string.Join(" ", body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("nonsense", errors);

        Assert.Equal(before.ConfigText, ConfigText);
        Assert.Equal(before.SecretsText, SecretsText);
    }

    [Fact]
    public async Task PATCH_rotates_api_key_encrypted_into_secrets_and_not_config()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);

        var response = await app.GetTestClient().PatchAsync("/api/config",
            JsonContent("""{ "Providers": { "OpenAI": { "ApiKey": "sk-rotated" } } }"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The rotated key decrypts back from secrets.json and never reaches netclaw.json.
        Assert.Equal("sk-rotated", NetclawConfigFiles.ReadDecryptedSecret(_paths, "Providers.OpenAI.ApiKey"));
        Assert.DoesNotContain("sk-rotated", ConfigText);
        Assert.DoesNotContain("sk-rotated", SecretsText);
        Assert.False(NetclawConfigFiles.PathPresent(
            NetclawConfigFiles.LoadJsonDict(_paths.NetclawConfigPath), "Providers.OpenAI.ApiKey"));
        // The response body carries no key material.
        Assert.DoesNotContain("sk-rotated", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PATCH_placeholder_round_trip_keeps_stored_secret()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var before = SecretsText;

        var response = await app.GetTestClient().PatchAsync("/api/config",
            JsonContent($$"""{ "Providers": { "OpenAI": { "ApiKey": "{{ConfigDocumentPatch.SecretPlaceholder}}" } } }"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("sk-original", NetclawConfigFiles.ReadDecryptedSecret(_paths, "Providers.OpenAI.ApiKey"));
        Assert.Equal(before, SecretsText);
    }

    [Fact]
    public async Task PATCH_null_deletes_key()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);

        var response = await app.GetTestClient().PatchAsync("/api/config",
            JsonContent("""{ "Memory": { "RecallTimeoutMs": null } }"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var onDisk = NetclawConfigFiles.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.False(NetclawConfigFiles.TryGetPathValue(onDisk, "Memory.RecallTimeoutMs", out _));
        Assert.True(NetclawConfigFiles.TryGetPathValue(onDisk, "Memory.Enabled", out _));
    }

    [Fact]
    public async Task PATCH_security_change_is_audited_at_warning_with_old_and_new_values()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);

        var response = await app.GetTestClient().PatchAsync("/api/config",
            JsonContent("""{ "Security": { "ShellExecutionMode": "HostAllowed" } }"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var warnings = _loggerProvider.Entries
            .Where(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Security"))
            .ToList();
        Assert.NotEmpty(warnings);
        Assert.Contains("Off", warnings[0].Message);
        Assert.Contains("HostAllowed", warnings[0].Message);
    }

    [Fact]
    public async Task PATCH_secret_rotation_audit_entry_contains_no_key_material()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);

        var response = await app.GetTestClient().PatchAsync("/api/config",
            JsonContent("""{ "Providers": { "OpenAI": { "ApiKey": "sk-audit-check" } } }"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("sk-audit-check", string.Join("\n", _loggerProvider.Entries.Select(e => e.Message)));
    }

    [Fact]
    public async Task PATCH_non_object_body_returns_400()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);

        var response = await app.GetTestClient().PatchAsync("/api/config",
            JsonContent("""[ { "Memory": { "Enabled": false } } ]"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private async Task<WebApplication> CreateAppAsync(bool spoofLoopback)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(_paths);
        builder.Services.AddSingleton(_restartSignal);
        builder.Services.AddSingleton(new ConfigDocumentService(
            _paths,
            plugins: [],
            _restartSignal,
            LoggerFactory.Create(factory => factory.AddProvider(_loggerProvider))
                .CreateLogger<ConfigDocumentService>()));
        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options =>
        {
            ConfigEndpointRouteBuilderExtensions.AddConfigWriteRateLimitPolicy(options);
            options.RejectionStatusCode = 429;
        });

        var app = builder.Build();

        if (spoofLoopback)
        {
            app.Use(async (ctx, next) =>
            {
                ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
                await next(ctx);
            });
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapConfigEndpoints();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => owner._entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
