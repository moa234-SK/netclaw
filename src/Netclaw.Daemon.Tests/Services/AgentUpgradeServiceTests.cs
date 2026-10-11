// -----------------------------------------------------------------------
// <copyright file="AgentUpgradeServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class AgentUpgradeServiceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly AgentUpgradeState _upgradeState = new();
    private readonly UpgradeStateStore _stateStore;
    private readonly FakeTimeProvider _time = new();
    private readonly StubRestartCoordinator _restartCoordinator = new();

    public AgentUpgradeServiceTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _stateStore = new UpgradeStateStore(_paths);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NETCLAW_CONTAINER_SUPERVISOR", null);
        _dir.Dispose();
    }

    [Fact]
    public async Task StartAsync_is_blocked_when_self_update_is_disabled()
    {
        var service = CreateService(new DaemonConfig { DisableSelfUpdate = true });

        var outcome = await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(outcome.Started);
        Assert.False(outcome.UpToDate);
        Assert.Contains("Self-update is disabled", outcome.BlockedReason);
    }

    [Fact]
    public async Task StartAsync_is_blocked_under_an_external_container_supervisor()
    {
        Environment.SetEnvironmentVariable("NETCLAW_CONTAINER_SUPERVISOR", "docker");
        var service = CreateService(new DaemonConfig());

        var outcome = await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(outcome.Started);
        Assert.Contains("container supervisor", outcome.BlockedReason);
    }

    [Fact]
    public async Task StartAsync_is_blocked_while_an_upgrade_is_already_in_flight()
    {
        var service = CreateService(new DaemonConfig());
        Assert.True(_upgradeState.TryBegin()); // simulate an in-flight upgrade

        var outcome = await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(outcome.Started);
        Assert.Equal("An upgrade is already in progress.", outcome.BlockedReason);
        _upgradeState.Complete();
    }

    [Fact]
    public async Task StartAsync_is_blocked_when_the_update_check_fails()
    {
        // 500 for the manifest fetch: CheckForUpdateAsync never throws, it
        // reports a failed check which must surface as a blocked outcome.
        var service = CreateService(new DaemonConfig(), handler: new StubHttpHandler(_ => 500));

        var outcome = await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(outcome.Started);
        Assert.False(outcome.UpToDate);
        Assert.StartsWith("The update check failed:", outcome.BlockedReason);
        // The in-flight gate must be released so a retry is possible.
        Assert.True(_upgradeState.TryBegin());
        _upgradeState.Complete();
    }

    private AgentUpgradeService CreateService(DaemonConfig config, StubHttpHandler? handler = null)
    {
        var httpClient = new HttpClient(handler ?? new StubHttpHandler(_ => 503));
        return new AgentUpgradeService(
            httpClient,
            config,
            _paths,
            _upgradeState,
            _stateStore,
            _restartCoordinator,
            _time,
            NullLogger<AgentUpgradeService>.Instance);
    }

    private sealed class StubHttpHandler(Func<int, int> statusCode) : HttpMessageHandler
    {
        private int _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests++;
            return Task.FromResult(new HttpResponseMessage(
                (HttpStatusCode)statusCode(_requests)));
        }
    }

    private sealed class StubRestartCoordinator : IDaemonRestartCoordinator
    {
        public Task RequestConfigRestartAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException("Upgrades never request a config restart.");

        public Task<bool> RequestUpdateExitAsync(CancellationToken cancellationToken, Func<Task>? onDrained = null)
            => throw new NotSupportedException("Not reached by these tests.");
    }
}
