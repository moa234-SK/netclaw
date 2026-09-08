// -----------------------------------------------------------------------
// <copyright file="DaemonIdStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class DaemonIdStoreTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public DaemonIdStoreTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    private string IdFilePath => Path.Combine(_paths.RuntimeDirectory, DaemonIdStore.FileName);

    [Fact]
    public void GetOrCreate_MintsOnce_AndPersistsAcrossStoreInstances()
    {
        var first = new DaemonIdStore(_paths, TimeProvider.System).GetOrCreate();

        Assert.Matches(@"^netclaw-[0-9a-f]{12}$", first.DaemonId);
        Assert.True(File.Exists(IdFilePath));

        var second = new DaemonIdStore(_paths, TimeProvider.System).GetOrCreate();
        Assert.Equal(first.DaemonId, second.DaemonId);
    }

    [Fact]
    public void GetOrCreate_ReusesManuallyPersistedIdentity()
    {
        var persisted = new DaemonIdentity("netclaw-custom0001", DateTimeOffset.UtcNow);
        new DaemonIdStore(_paths, TimeProvider.System).Write(persisted);

        var resolved = new DaemonIdStore(_paths, TimeProvider.System).GetOrCreate();

        Assert.Equal("netclaw-custom0001", resolved.DaemonId);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"daemonId\":\"\",\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    public void GetOrCreate_MintsReplacement_ForCorruptOrBlankFile(string fileContent)
    {
        File.WriteAllText(IdFilePath, fileContent);

        var resolved = new DaemonIdStore(_paths, TimeProvider.System).GetOrCreate();

        Assert.Matches(@"^netclaw-[0-9a-f]{12}$", resolved.DaemonId);
        Assert.NotEqual(fileContent, File.ReadAllText(IdFilePath));
    }
}
