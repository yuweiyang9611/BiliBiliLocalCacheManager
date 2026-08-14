using System.IO;
using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Wpf.Models;
using BiliBiliLocalCacheManager.Wpf.Services;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class JsonAppSettingsMigrationTests
{
    [Fact]
    public void LoadWithReport_ShouldReturnCurrentDefaultsWithoutCreatingAFile_WhenMissing()
    {
        using var workspace = new SettingsWorkspace();

        var result = new JsonAppSettingsService(workspace.SettingsPath)
            .LoadWithReport();

        Assert.Equal(AppSettingsLoadKind.MissingFile, result.LoadKind);
        Assert.Null(result.SourceSchemaVersion);
        Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
        Assert.False(result.RequiresSave);
        Assert.True(result.CanSave);
        Assert.True(result.CanRunAutomaticMaintenance);
        Assert.False(File.Exists(workspace.SettingsPath));
    }

    [Fact]
    public void Save_ShouldWriteCurrentSchemaVersion()
    {
        using var workspace = new SettingsWorkspace();
        var service = new JsonAppSettingsService(workspace.SettingsPath);

        service.Save(new AppSettings { RootPath = @"D:\Cache" });

        using var document = JsonDocument.Parse(
            File.ReadAllText(workspace.SettingsPath));
        Assert.Equal(
            AppSettings.CurrentSchemaVersion,
            document.RootElement.GetProperty(nameof(AppSettings.SchemaVersion)).GetInt32());
    }

    [Fact]
    public void LoadWithReport_ShouldMigrateLegacyValuesAndClampFormerLimits()
    {
        using var workspace = new SettingsWorkspace();
        File.WriteAllText(
            workspace.SettingsPath,
            """
            {
              "RootPath": "D:\\LegacyCache",
              "MatchMode": 99,
              "PreferredPlayer": "UnknownPlayer",
              "TranscodeCacheRetentionDays": 3650,
              "TranscodeCacheMaxSizeGigabytes": 4096
            }
            """);
        var originalBytes = File.ReadAllBytes(workspace.SettingsPath);
        var service = new JsonAppSettingsService(workspace.SettingsPath);

        var result = service.LoadWithReport();

        Assert.Equal(0, result.SourceSchemaVersion);
        Assert.Equal(AppSettingsLoadKind.LegacyVersion, result.LoadKind);
        Assert.True(result.RequiresSave);
        Assert.False(result.IsUnsupported);
        Assert.True(result.CanSave);
        Assert.True(result.CanRunAutomaticMaintenance);
        Assert.Equal(@"D:\LegacyCache", result.Settings.RootPath);
        Assert.Equal(
            PlaybackArtifactCleanupOptions.MaximumRetentionDays,
            result.Settings.TranscodeCacheRetentionDays);
        Assert.Equal(
            PlaybackArtifactCleanupOptions.MaximumMaxSizeGigabytes,
            result.Settings.TranscodeCacheMaxSizeGigabytes);
        Assert.Equal(CacheSearchMatchMode.Contains, result.Settings.MatchMode);
        Assert.Equal(
            PlaybackPlayerPreference.SystemDefaultFirst,
            result.Settings.PreferredPlayer);
        Assert.Contains(
            result.Adjustments,
            value => value.Contains("版本 0", StringComparison.Ordinal));
        Assert.Contains(
            result.Adjustments,
            value => value.Contains("3650", StringComparison.Ordinal) &&
                value.Contains("1825", StringComparison.Ordinal));
        Assert.Contains(
            result.Adjustments,
            value => value.Contains("4096", StringComparison.Ordinal) &&
                value.Contains("128", StringComparison.Ordinal));
        Assert.Contains("旧值已调整", result.UserMessage, StringComparison.Ordinal);
        Assert.Equal(originalBytes, File.ReadAllBytes(workspace.SettingsPath));
    }

    [Fact]
    public void LoadWithReport_ShouldDefaultUninterpretableValuesAndReportEveryAdjustment()
    {
        using var workspace = new SettingsWorkspace();
        File.WriteAllText(
            workspace.SettingsPath,
            """
            {
              "SchemaVersion": 1,
              "MatchMode": "NotAMode",
              "PreferredPlayer": -1,
              "TranscodeCacheRetentionDays": -7,
              "TranscodeCacheMaxSizeGigabytes": "very large"
            }
            """);

        var result = new JsonAppSettingsService(workspace.SettingsPath)
            .LoadWithReport();

        Assert.Equal(1, result.SourceSchemaVersion);
        Assert.Equal(AppSettingsLoadKind.CurrentVersion, result.LoadKind);
        Assert.True(result.RequiresSave);
        Assert.Equal(CacheSearchMatchMode.Contains, result.Settings.MatchMode);
        Assert.Equal(
            PlaybackPlayerPreference.SystemDefaultFirst,
            result.Settings.PreferredPlayer);
        Assert.Equal(
            PlaybackArtifactCleanupOptions.DefaultRetentionDays,
            result.Settings.TranscodeCacheRetentionDays);
        Assert.Equal(
            PlaybackArtifactCleanupOptions.DefaultMaxSizeGigabytes,
            result.Settings.TranscodeCacheMaxSizeGigabytes);
        Assert.Contains(
            result.Adjustments,
            value => value.Contains("搜索匹配模式", StringComparison.Ordinal));
        Assert.Contains(
            result.Adjustments,
            value => value.Contains("首选播放器", StringComparison.Ordinal));
        Assert.Contains(
            result.Adjustments,
            value => value.Contains("-7", StringComparison.Ordinal));
        Assert.Contains(
            result.Adjustments,
            value => value.Contains("无法识别", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadWithReport_ShouldLeaveValidCurrentSettingsUnchanged()
    {
        using var workspace = new SettingsWorkspace();
        var service = new JsonAppSettingsService(workspace.SettingsPath);
        service.Save(new AppSettings
        {
            RootPath = @"D:\Current",
            MatchMode = CacheSearchMatchMode.Equals,
            PreferredPlayer = PlaybackPlayerPreference.Vlc,
            TranscodeCacheRetentionDays = 60,
            TranscodeCacheMaxSizeGigabytes = 64
        });
        var originalBytes = File.ReadAllBytes(workspace.SettingsPath);

        var result = service.LoadWithReport();

        Assert.Equal(1, result.SourceSchemaVersion);
        Assert.Equal(AppSettingsLoadKind.CurrentVersion, result.LoadKind);
        Assert.False(result.RequiresSave);
        Assert.Empty(result.Adjustments);
        Assert.Null(result.UserMessage);
        Assert.Equal(originalBytes, File.ReadAllBytes(workspace.SettingsPath));
    }

    [Fact]
    public void FutureSchema_ShouldRemainByteForByteUnchangedAndBlockSave()
    {
        using var workspace = new SettingsWorkspace();
        var futureJson =
            "{\r\n  \"SchemaVersion\": 2,\r\n  \"FutureSetting\": \"keep me\"\r\n}";
        File.WriteAllText(workspace.SettingsPath, futureJson);
        var originalBytes = File.ReadAllBytes(workspace.SettingsPath);
        var service = new JsonAppSettingsService(workspace.SettingsPath);

        var result = service.LoadWithReport();

        Assert.Equal(2, result.SourceSchemaVersion);
        Assert.Equal(AppSettingsLoadKind.FutureVersion, result.LoadKind);
        Assert.True(result.IsUnsupported);
        Assert.False(result.RequiresSave);
        Assert.False(result.CanSave);
        Assert.False(result.CanRunAutomaticMaintenance);
        Assert.Contains("较新版本", result.UserMessage, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => service.Save(new AppSettings()));
        Assert.Equal(originalBytes, File.ReadAllBytes(workspace.SettingsPath));
    }

    [Fact]
    public void AutomaticMaintenanceEligibility_FutureSchema_IsReadOnlyAndIneligible()
    {
        using var workspace = new SettingsWorkspace();
        var futureBytes =
            "{\r\n  \"SchemaVersion\": 2,\r\n  \"FutureSetting\": true\r\n}"u8.ToArray();
        File.WriteAllBytes(workspace.SettingsPath, futureBytes);
        var service = new JsonAppSettingsService(workspace.SettingsPath);

        var eligibility = service.CheckAutomaticMaintenanceEligibility();

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AppSettingsLoadKind.FutureVersion, eligibility.LoadKind);
        Assert.Equal(2, eligibility.SourceSchemaVersion);
        Assert.Equal(futureBytes, File.ReadAllBytes(workspace.SettingsPath));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*.corrupt.json"));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*.writing"));
    }

    [Fact]
    public void AutomaticMaintenanceEligibility_CorruptFile_IsReadOnlyAndDoesNotRecoverOrBackup()
    {
        using var workspace = new SettingsWorkspace();
        var corruptBytes = "{ invalid settings"u8.ToArray();
        File.WriteAllBytes(workspace.SettingsPath, corruptBytes);
        var service = new JsonAppSettingsService(workspace.SettingsPath);

        var eligibility = service.CheckAutomaticMaintenanceEligibility();

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AppSettingsLoadKind.CorruptFile, eligibility.LoadKind);
        Assert.Null(eligibility.SourceSchemaVersion);
        Assert.Equal(corruptBytes, File.ReadAllBytes(workspace.SettingsPath));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*.corrupt.json"));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*.writing"));
    }

    [Fact]
    public void AutomaticMaintenanceEligibility_CurrentAndMissingSettings_AreEligibleWithoutWrites()
    {
        using var workspace = new SettingsWorkspace();
        var service = new JsonAppSettingsService(workspace.SettingsPath);

        var missing = service.CheckAutomaticMaintenanceEligibility();
        service.Save(new AppSettings());
        var savedBytes = File.ReadAllBytes(workspace.SettingsPath);
        var current = service.CheckAutomaticMaintenanceEligibility();

        Assert.True(missing.IsEligible);
        Assert.Equal(AppSettingsLoadKind.MissingFile, missing.LoadKind);
        Assert.True(current.IsEligible);
        Assert.Equal(AppSettingsLoadKind.CurrentVersion, current.LoadKind);
        Assert.Equal(AppSettings.CurrentSchemaVersion, current.SourceSchemaVersion);
        Assert.Equal(savedBytes, File.ReadAllBytes(workspace.SettingsPath));
    }

    [Fact]
    public void SaveWithoutLoad_ShouldNotOverwriteFutureSchema()
    {
        using var workspace = new SettingsWorkspace();
        var futureBytes =
            "{\r\n  \"SchemaVersion\": 2,\r\n  \"FutureSetting\": true\r\n}"u8.ToArray();
        File.WriteAllBytes(workspace.SettingsPath, futureBytes);
        var service = new JsonAppSettingsService(workspace.SettingsPath);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            service.Save(new AppSettings()));

        Assert.Contains("v2", exception.Message, StringComparison.Ordinal);
        Assert.Equal(futureBytes, File.ReadAllBytes(workspace.SettingsPath));
    }

    [Fact]
    public void Save_ShouldNotOverwriteFutureSchemaWrittenAfterCompatibleLoad()
    {
        using var workspace = new SettingsWorkspace();
        var olderService = new JsonAppSettingsService(workspace.SettingsPath);
        olderService.Save(new AppSettings { RootPath = @"D:\Version1" });
        var loaded = olderService.LoadWithReport();
        Assert.Equal(AppSettingsLoadKind.CurrentVersion, loaded.LoadKind);

        var futureBytes =
            "{\n  \"SchemaVersion\": 2,\n  \"FutureSetting\": \"preserve\"\n}"u8.ToArray();
        File.WriteAllBytes(workspace.SettingsPath, futureBytes);
        var newerService = new JsonAppSettingsService(workspace.SettingsPath);
        Assert.Equal(
            AppSettingsLoadKind.FutureVersion,
            newerService.LoadWithReport().LoadKind);

        Assert.Throws<InvalidOperationException>(() =>
            olderService.Save(new AppSettings { RootPath = @"D:\StaleWriter" }));
        Assert.Equal(futureBytes, File.ReadAllBytes(workspace.SettingsPath));
    }

    [Fact]
    public void Save_ShouldRejectStaleCompatibleSettingsFromAnotherService()
    {
        using var workspace = new SettingsWorkspace();
        new JsonAppSettingsService(workspace.SettingsPath).Save(new AppSettings());
        var firstService = new JsonAppSettingsService(workspace.SettingsPath);
        var secondService = new JsonAppSettingsService(workspace.SettingsPath);
        var firstSettings = firstService.LoadWithReport().Settings;
        var staleSecondSettings = secondService.LoadWithReport().Settings;

        firstSettings.TranscodeCacheRetentionDays = 7;
        firstService.Save(firstSettings);
        var firstWriterBytes = File.ReadAllBytes(workspace.SettingsPath);

        var eligibility = secondService.CheckAutomaticMaintenanceEligibility();
        Assert.False(eligibility.IsEligible);
        Assert.Contains("其他实例更改", eligibility.Reason, StringComparison.Ordinal);
        Assert.Contains("重启", eligibility.Reason, StringComparison.Ordinal);

        staleSecondSettings.PreferredPlayer = PlaybackPlayerPreference.Vlc;
        var exception = Assert.Throws<InvalidOperationException>(() =>
            secondService.Save(staleSecondSettings));

        Assert.Contains("其他实例更改", exception.Message, StringComparison.Ordinal);
        Assert.Equal(firstWriterBytes, File.ReadAllBytes(workspace.SettingsPath));
        var persisted = new JsonAppSettingsService(workspace.SettingsPath).Load();
        Assert.Equal(7, persisted.TranscodeCacheRetentionDays);
        Assert.Equal(
            PlaybackPlayerPreference.SystemDefaultFirst,
            persisted.PreferredPlayer);
    }

    [Fact]
    public void Save_ShouldNotOverwriteFutureSchemaChangedDuringReplaceRetry()
    {
        using var workspace = new SettingsWorkspace();
        new JsonAppSettingsService(workspace.SettingsPath)
            .Save(new AppSettings { RootPath = @"D:\Version1" });
        var futureBytes =
            "{\n  \"SchemaVersion\": 2,\n  \"FutureSetting\": \"changed during retry\"\n}"u8
                .ToArray();
        var injectedAttemptCount = 0;
        var service = new JsonAppSettingsService(
            workspace.SettingsPath,
            attempt =>
            {
                if (attempt != 1 ||
                    Interlocked.Exchange(ref injectedAttemptCount, 1) != 0)
                {
                    return;
                }

                File.WriteAllBytes(workspace.SettingsPath, futureBytes);
                throw new IOException("Simulated transient replace failure.");
            });
        _ = service.LoadWithReport();

        var exception = Record.Exception(() =>
            service.Save(new AppSettings { RootPath = @"D:\StaleWriter" }));

        Assert.Equal(1, injectedAttemptCount);
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("v2", invalidOperation.Message, StringComparison.Ordinal);
        Assert.Equal(futureBytes, File.ReadAllBytes(workspace.SettingsPath));
        Assert.Empty(Directory.GetFiles(
            workspace.RootDirectory,
            "settings.json.*.writing"));
    }

    [Fact]
    public void CorruptJson_ShouldBeBackedUpBeforeDefaultsAreSaved()
    {
        using var workspace = new SettingsWorkspace();
        var corruptBytes = "{ this is not valid JSON"u8.ToArray();
        File.WriteAllBytes(workspace.SettingsPath, corruptBytes);
        var service = new JsonAppSettingsService(workspace.SettingsPath);

        var result = service.LoadWithReport();

        Assert.Null(result.SourceSchemaVersion);
        Assert.Equal(AppSettingsLoadKind.CorruptFile, result.LoadKind);
        Assert.True(result.RequiresSave);
        Assert.True(result.CanSave);
        Assert.False(result.CanRunAutomaticMaintenance);
        Assert.Contains("已备份", result.UserMessage, StringComparison.Ordinal);
        var backup = Assert.Single(Directory.GetFiles(
            workspace.RootDirectory,
            "settings.*.corrupt.json"));
        Assert.Equal(corruptBytes, File.ReadAllBytes(backup));
        Assert.Equal(corruptBytes, File.ReadAllBytes(workspace.SettingsPath));

        service.Save(result.Settings);

        Assert.Equal(corruptBytes, File.ReadAllBytes(backup));
        using var document = JsonDocument.Parse(
            File.ReadAllText(workspace.SettingsPath));
        Assert.Equal(
            AppSettings.CurrentSchemaVersion,
            document.RootElement.GetProperty(nameof(AppSettings.SchemaVersion)).GetInt32());
    }

    [Fact]
    public void CorruptJson_ShouldOnlyAuthorizeTheExactBackedUpContents()
    {
        using var workspace = new SettingsWorkspace();
        var originalCorruptBytes = "{ first corrupt value"u8.ToArray();
        File.WriteAllBytes(workspace.SettingsPath, originalCorruptBytes);
        var service = new JsonAppSettingsService(workspace.SettingsPath);
        var result = service.LoadWithReport();
        Assert.True(result.RequiresSave);

        var replacementCorruptBytes = "{ changed by another instance"u8.ToArray();
        File.WriteAllBytes(workspace.SettingsPath, replacementCorruptBytes);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            service.Save(result.Settings));

        Assert.Contains("已发生变化", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            replacementCorruptBytes,
            File.ReadAllBytes(workspace.SettingsPath));
        var backup = Assert.Single(Directory.GetFiles(
            workspace.RootDirectory,
            "settings.*.corrupt.json"));
        Assert.Equal(originalCorruptBytes, File.ReadAllBytes(backup));
    }

    [Fact]
    public async Task ConcurrentServices_ShouldSerializeSavesAndUseUniqueTemporaryFiles()
    {
        using var workspace = new SettingsWorkspace();
        new JsonAppSettingsService(workspace.SettingsPath)
            .Save(new AppSettings { RootPath = @"D:\Initial" });
        var legacyTemporaryPath = workspace.SettingsPath + ".writing";
        var sentinelBytes = "do not overwrite another temporary file"u8.ToArray();
        File.WriteAllBytes(legacyTemporaryPath, sentinelBytes);
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 12)
            .Select(index => Task.Run(() =>
            {
                var service = new JsonAppSettingsService(workspace.SettingsPath);
                start.Wait(TimeSpan.FromSeconds(5));
                service.Save(new AppSettings { RootPath = $@"D:\Concurrent-{index}" });
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(sentinelBytes, File.ReadAllBytes(legacyTemporaryPath));
        Assert.Empty(Directory.GetFiles(
            workspace.RootDirectory,
            "settings.json.*.writing"));
        using var document = JsonDocument.Parse(
            File.ReadAllText(workspace.SettingsPath));
        Assert.Equal(
            AppSettings.CurrentSchemaVersion,
            document.RootElement
                .GetProperty(nameof(AppSettings.SchemaVersion))
                .GetInt32());
        Assert.StartsWith(
            @"D:\Concurrent-",
            document.RootElement.GetProperty(nameof(AppSettings.RootPath)).GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SavedMigration_ShouldNotBeRequestedAgain()
    {
        using var workspace = new SettingsWorkspace();
        File.WriteAllText(
            workspace.SettingsPath,
            """{ "RootPath": "D:\\Legacy" }""");
        var service = new JsonAppSettingsService(workspace.SettingsPath);

        var first = service.LoadWithReport();
        Assert.True(first.RequiresSave);
        service.Save(first.Settings);

        var second = service.LoadWithReport();

        Assert.False(second.RequiresSave);
        Assert.Equal(1, second.SourceSchemaVersion);
        Assert.Empty(second.Adjustments);
    }

    private sealed class SettingsWorkspace : IDisposable
    {
        public SettingsWorkspace()
        {
            RootDirectory = Path.Combine(
                Path.GetTempPath(),
                $"bili_settings_migration_{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootDirectory);
            SettingsPath = Path.Combine(RootDirectory, "settings.json");
        }

        public string RootDirectory { get; }

        public string SettingsPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
    }
}
