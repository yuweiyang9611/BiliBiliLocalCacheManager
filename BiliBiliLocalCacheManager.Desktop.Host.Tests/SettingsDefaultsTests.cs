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

    [Fact]
    public void FutureSettingsWithUnknownValuesRemainReadOnlyAndKeepCompatibleFields()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        var content = JsonSerializer.Serialize(new { SchemaVersion = 3, RootPath = _root,
            Keyword = "keep", MatchMode = "future-mode", ScanOnStartup = new { mode = "on-demand" },
            PreferredPlayer = 9999, TranscodeCacheRetentionDays = -3 });
        File.WriteAllText(path, content);
        var store = new SettingsStore(path);
        var state = store.GetState();
        Assert.False(state.CanSave);
        Assert.Equal(3, state.SourceSchemaVersion);
        Assert.Equal(_root, state.Settings.RootPath);
        Assert.Equal("keep", state.Settings.Keyword);
        Assert.False(state.Settings.ScanOnStartup);
        Assert.True(Enum.IsDefined(state.Settings.PreferredPlayer));
        Assert.True(state.Settings.TranscodeCacheRetentionDays > 0);
        Assert.Throws<Rpc.RpcException>(() => store.Update(JsonSerializer.SerializeToElement(new { keyword = "replace" })));
        Assert.Equal(content, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void CurrentSettingsWithUtf8BomLoadAndRoundTrip()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"SchemaVersion":2,"Keyword":"已保存","ScanOnStartup":false}""",
            new System.Text.UTF8Encoding(true));
        var store = new SettingsStore(path);
        Assert.Equal("已保存", store.GetState().Settings.Keyword);
        Assert.False(store.GetState().Settings.ScanOnStartup);
        store.Update(JsonSerializer.SerializeToElement(new { anyKeywords = true }));
        Assert.Equal("已保存", new SettingsStore(path).GetState().Settings.Keyword);
        Assert.Single(Directory.GetFiles(_root));
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
