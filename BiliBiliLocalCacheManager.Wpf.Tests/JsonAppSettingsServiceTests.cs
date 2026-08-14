using System.IO;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Wpf.Models;
using BiliBiliLocalCacheManager.Wpf.Services;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class JsonAppSettingsServiceTests
{
    [Fact]
    public void SaveAndLoad_ShouldPreserveUserPreferences()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bili_settings_test_{Guid.NewGuid():N}");
        var path = Path.Combine(root, "settings.json");
        try
        {
            var service = new JsonAppSettingsService(path);
            service.Save(new AppSettings
            {
                RootPath = "D:\\Cache",
                IncludeIncomplete = true,
                IncludeOwnerName = true,
                PreferredPlayer = PlaybackPlayerPreference.Vlc
            });

            var loaded = service.Load();

            Assert.Equal("D:\\Cache", loaded.RootPath);
            Assert.True(loaded.IncludeIncomplete);
            Assert.True(loaded.IncludeOwnerName);
            Assert.Equal(PlaybackPlayerPreference.Vlc, loaded.PreferredPlayer);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
