// -----------------------------------------------------------------------
// <copyright file="ConfigPatchWatcherIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Config;
using Netclaw.Daemon.Services;
using Netclaw.Providers;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Config;

/// <summary>
/// Proves the single-reload-trigger contract end to end: a patch the config
/// API accepts and writes is a file the config watcher validates and restarts
/// on. The endpoint never calls the restart coordinator itself.
/// </summary>
public sealed class ConfigPatchWatcherIntegrationTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly CountingRestartCoordinator _restartCoordinator = new();
    private readonly RejectedConfigState _rejectedConfig = new();
    private readonly ConfigWatcherService _watcher;
    private readonly ConfigDocumentService _documentService;

    public ConfigPatchWatcherIntegrationTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, """
            {
              "configVersion": 1,
              "Providers": { "p": { "Type": "ollama" } },
              "Models": { "Definitions": { "d": { "Provider": "p", "ModelId": "m" } }, "Roles": { "Main": "d" } },
              "Memory": { "Enabled": true }
            }
            """);

        var services = new ServiceCollection();
        services.AddSingleton(_paths);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddLlmProviders();
        var plugins = services.BuildServiceProvider().GetServices<ILlmProviderPlugin>();

        _watcher = new ConfigWatcherService(
            _paths,
            new FakeTimeProvider(),
            _restartCoordinator,
            _rejectedConfig,
            plugins,
            NullLogger<ConfigWatcherService>.Instance);
        _documentService = new ConfigDocumentService(
            _paths,
            plugins,
            new DaemonRestartSignal(),
            NullLogger<ConfigDocumentService>.Instance);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Endpoint_accepted_patch_is_watcher_validated_and_restarts_the_daemon()
    {
        var actor = new System.Security.Claims.ClaimsPrincipal();
        var outcome = await _documentService.PatchAsync(
            System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                Memory = new { Enabled = false },
            }),
            actor,
            CancellationToken.None);

        Assert.Null(outcome.Rejection);
        Assert.True(outcome.Acceptance!.RestartPending);
        Assert.Equal(0, _restartCoordinator.ConfigRestartCount);

        // The file the endpoint wrote must pass the watcher's own validation
        // and drive the coordinated restart — nothing else does.
        await _watcher.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.ConfigRestartCount);
        Assert.Null(_rejectedConfig.Reason);
        var onDisk = NetclawConfigFiles.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(NetclawConfigFiles.TryGetPathValue(onDisk, "Memory.Enabled", out var enabled));
        Assert.Equal(false, enabled);
    }

    private sealed class CountingRestartCoordinator : IDaemonRestartCoordinator
    {
        public int ConfigRestartCount { get; private set; }

        public Task RequestConfigRestartAsync(CancellationToken cancellationToken)
        {
            ConfigRestartCount++;
            return Task.CompletedTask;
        }

        public Task<bool> RequestUpdateExitAsync(CancellationToken cancellationToken, Func<Task>? onDrained = null)
            => Task.FromResult(true);
    }
}
