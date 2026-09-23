using System.Text.Json;
using BiliBiliLocalCacheManager.Desktop.Host.Services;

namespace BiliBiliLocalCacheManager.Desktop.Host.Tests;

public sealed class SettingsDefaultsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blcm-settings-defaults-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FreshSettings_EnableStartupScanAndKeepPartNameSearchOptIn()
    {
        var settings = new SettingsStore(Path.Combine(_root, "settings.json")).GetState().Settings;
        Assert.True(settings.ScanOnStartup);
        Assert.False(settings.IncludePartName);
        Assert.True(settings.RememberRootPath);
        Assert.Empty(settings.RootPath);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    public void ExistingFilesWithMissingFields_KeepTheirHistoricalDefaults(int schemaVersion)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        var content = JsonSerializer.Serialize(new { SchemaVersion = schemaVersion, RootPath = _root });
        File.WriteAllText(path, content);

        var settings = new SettingsStore(path).GetState().Settings;

        Assert.False(settings.ScanOnStartup);
        Assert.True(settings.IncludePartName);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ExistingFiles_PreserveExplicitChoices(bool startupScan, bool partNameSearch)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            SchemaVersion = 2, RootPath = _root, ScanOnStartup = startupScan, IncludePartName = partNameSearch
        }));
        var settings = new SettingsStore(path).GetState().Settings;
        Assert.Equal(startupScan, settings.ScanOnStartup);
        Assert.Equal(partNameSearch, settings.IncludePartName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
