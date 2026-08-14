using System.IO;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Wpf.Models;
using BiliBiliLocalCacheManager.Wpf.Services;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class TranscodeCacheSettingsTests
{
    [Fact]
    public void SaveAndLoad_ShouldPreserveTranscodeCachePolicy()
    {
        var root = CreateTempRoot();
        try
        {
            var path = Path.Combine(root, "settings.json");
            var service = new JsonAppSettingsService(path);
            service.Save(new AppSettings
            {
                RootPath = @"D:\Cache",
                TranscodeCacheRetentionDays = 45,
                TranscodeCacheMaxSizeGigabytes = 128
            });

            var loaded = service.Load();

            Assert.Equal(@"D:\Cache", loaded.RootPath);
            Assert.Equal(45, loaded.TranscodeCacheRetentionDays);
            Assert.Equal(128, loaded.TranscodeCacheMaxSizeGigabytes);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Load_ShouldApplyDefaultsToLegacySettingsWithoutPolicyFields()
    {
        var root = CreateTempRoot();
        try
        {
            var path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, """{ "RootPath": "D:\\LegacyCache" }""");

            var loaded = new JsonAppSettingsService(path).Load();

            Assert.Equal(@"D:\LegacyCache", loaded.RootPath);
            Assert.Equal(
                PlaybackArtifactCleanupOptions.DefaultRetentionDays,
                loaded.TranscodeCacheRetentionDays);
            Assert.Equal(
                PlaybackArtifactCleanupOptions.DefaultMaxSizeGigabytes,
                loaded.TranscodeCacheMaxSizeGigabytes);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Load_ShouldResetOnlyInvalidPolicyFields()
    {
        var root = CreateTempRoot();
        try
        {
            var path = Path.Combine(root, "settings.json");
            File.WriteAllText(
                path,
                """
                {
                  "RootPath": "D:\\Cache",
                  "TranscodeCacheRetentionDays": 0,
                  "TranscodeCacheMaxSizeGigabytes": 128
                }
                """);

            var loaded = new JsonAppSettingsService(path).Load();

            Assert.Equal(@"D:\Cache", loaded.RootPath);
            Assert.Equal(
                PlaybackArtifactCleanupOptions.DefaultRetentionDays,
                loaded.TranscodeCacheRetentionDays);
            Assert.Equal(128, loaded.TranscodeCacheMaxSizeGigabytes);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }
    [Fact]
    public void Load_ShouldClampLegacyMaximumSizeWithoutChangingValidRetention()
    {
        var root = CreateTempRoot();
        try
        {
            var path = Path.Combine(root, "settings.json");
            File.WriteAllText(
                path,
                """
                {
                  "RootPath": "D:\\Cache",
                  "TranscodeCacheRetentionDays": 45,
                  "TranscodeCacheMaxSizeGigabytes": 129
                }
                """);

            var loaded = new JsonAppSettingsService(path).Load();

            Assert.Equal(@"D:\Cache", loaded.RootPath);
            Assert.Equal(45, loaded.TranscodeCacheRetentionDays);
            Assert.Equal(
                PlaybackArtifactCleanupOptions.MaximumMaxSizeGigabytes,
                loaded.TranscodeCacheMaxSizeGigabytes);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }


    [Fact]
    public void Save_ShouldRejectInvalidPolicyFields()
    {
        var root = CreateTempRoot();
        try
        {
            var path = Path.Combine(root, "settings.json");
            var service = new JsonAppSettingsService(path);

            Assert.Throws<ArgumentOutOfRangeException>(() => service.Save(new AppSettings
            {
                TranscodeCacheRetentionDays = 0
            }));
            Assert.False(File.Exists(path));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"bili_transcode_settings_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void SafeDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
