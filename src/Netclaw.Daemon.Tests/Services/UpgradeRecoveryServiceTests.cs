// -----------------------------------------------------------------------
// <copyright file="UpgradeRecoveryServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class UpgradeRecoveryServiceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly UpgradeStateStore _stateStore;
    private readonly AgentUpgradeState _upgradeState = new();

    public UpgradeRecoveryServiceTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _stateStore = new UpgradeStateStore(_paths);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Recovery_marks_success_when_running_version_matches_target()
    {
        await _stateStore.WriteAsync(State(target: BuildInfo.FullVersion), TestContext.Current.CancellationToken);

        await CreateService().StartAsync(TestContext.Current.CancellationToken);

        var snapshot = _upgradeState.Current;
        Assert.Equal(AgentUpgradeStates.Succeeded, snapshot.State);
        Assert.Equal(BuildInfo.FullVersion, snapshot.CurrentVersion);
        Assert.Equal(BuildInfo.FullVersion, snapshot.TargetVersion);
        Assert.Null(snapshot.Error);
        Assert.Null(await _stateStore.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Recovery_marks_failure_when_running_version_differs_from_target()
    {
        await _stateStore.WriteAsync(State(target: "999.0.0"), TestContext.Current.CancellationToken);

        await CreateService().StartAsync(TestContext.Current.CancellationToken);

        var snapshot = _upgradeState.Current;
        Assert.Equal(AgentUpgradeStates.Failed, snapshot.State);
        Assert.Contains("did not complete", snapshot.Error);
        Assert.Null(await _stateStore.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Recovery_surfaces_the_recorded_error_when_restart_was_declined()
    {
        var state = State(target: "999.0.0") with { Phase = AgentUpgradeStates.Failed, Error = "restart declined" };
        await _stateStore.WriteAsync(state, TestContext.Current.CancellationToken);

        await CreateService().StartAsync(TestContext.Current.CancellationToken);

        var snapshot = _upgradeState.Current;
        Assert.Equal(AgentUpgradeStates.Failed, snapshot.State);
        Assert.Contains("restart declined", snapshot.Error);
        Assert.Null(await _stateStore.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Recovery_is_a_noop_without_a_state_file()
    {
        await CreateService().StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AgentUpgradeStates.Idle, _upgradeState.Current.State);
    }

    [Fact]
    public async Task StateStore_write_read_delete_roundtrips()
    {
        var state = State(target: "1.2.3") with { BytesTotal = 50_000_000 };

        await _stateStore.WriteAsync(state, TestContext.Current.CancellationToken);
        var read = await _stateStore.ReadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal("1.2.3", read!.TargetVersion);
        Assert.Equal(50_000_000, read.BytesTotal);

        await _stateStore.DeleteAsync();
        Assert.Null(await _stateStore.ReadAsync(TestContext.Current.CancellationToken));
    }

    private UpgradeRecoveryService CreateService()
        => new(_stateStore, _upgradeState, NullLogger<UpgradeRecoveryService>.Instance);

    private static PersistedUpgradeState State(string target) => new()
    {
        Phase = AgentUpgradeStates.Restarting,
        FromVersion = "0.9.0",
        TargetVersion = target,
        StartedAtUtc = DateTimeOffset.UtcNow,
        BytesTotal = 50_000_000,
    };
}
