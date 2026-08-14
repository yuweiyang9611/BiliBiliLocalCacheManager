using System.IO;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Wpf.Services;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class SettingsValidationRegressionTests
{
    [Fact]
    public void Load_ShouldKeepValidFieldsAndResetUnknownEnums()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bili_settings_validation_{Guid.NewGuid():N}");
        var path = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                path,
                """{ "RootPath": "D:\\Cache", "MatchMode": 99, "PreferredPlayer": 99 }""");

            var settings = new JsonAppSettingsService(path).Load();

            Assert.Equal("D:\\Cache", settings.RootPath);
            Assert.Equal(CacheSearchMatchMode.Contains, settings.MatchMode);
            Assert.Equal(PlaybackPlayerPreference.SystemDefaultFirst, settings.PreferredPlayer);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
