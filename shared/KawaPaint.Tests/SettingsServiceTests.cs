using KawaPaint.App.Core;
using Xunit;

namespace KawaPaint.Tests;

/// <summary>Issue #16: an unreadable settings file is kept aside instead of silently replaced.</summary>
public class SettingsServiceTests
{
    [Fact]
    public void CorruptSettingsAreBackedUpBeforeDefaultsAreUsed()
    {
        var store = new MemorySettingsStore();
        store.Write("settings.json", "{ this is not json");

        var service = new SettingsService(store);

        Assert.NotNull(service.CorruptSettingsBackup);
        Assert.True(store.TryRead(service.CorruptSettingsBackup!, out string kept));
        Assert.Equal("{ this is not json", kept);
    }

    [Fact]
    public void MissingSettingsAreNotReportedAsCorrupt()
    {
        var service = new SettingsService(new MemorySettingsStore());
        Assert.Null(service.CorruptSettingsBackup);
    }

    [Fact]
    public void ValidSettingsAreNotReportedAsCorrupt()
    {
        var store = new MemorySettingsStore();
        new SettingsService(store).Save();

        Assert.Null(new SettingsService(store).CorruptSettingsBackup);
    }
}
