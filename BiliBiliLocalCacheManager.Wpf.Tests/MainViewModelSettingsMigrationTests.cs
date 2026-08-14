using System.IO;
using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;
using BiliBiliLocalCacheManager.Wpf.Models;
using BiliBiliLocalCacheManager.Wpf.Services;
using BiliBiliLocalCacheManager.Wpf.ViewModels;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class MainViewModelSettingsMigrationTests
{
    [Fact]
    public void LegacySettings_ShouldBeSavedOnceAfterLoading_AndNotMigratedAgain()
    {
        using var workspace = new SettingsWorkspace();
        File.WriteAllText(
            workspace.SettingsPath,
            """
            {
              "RootPath": "D:\\LegacyCache",
              "TranscodeCacheRetentionDays": 3650,
              "TranscodeCacheMaxSizeGigabytes": 4096
            }
            """);
        var settingsService = new CountingSettingsService(
            new JsonAppSettingsService(workspace.SettingsPath));

        var viewModel = CreateViewModel(
            new RecordingArtifactStore(),
            settingsService);

        Assert.Equal(1, settingsService.SaveCallCount);
        Assert.Equal(@"D:\LegacyCache", viewModel.RootPath);
        Assert.Equal(
            PlaybackArtifactCleanupOptions.MaximumRetentionDays,
            viewModel.TranscodeCacheRetentionDays);
        Assert.Equal(
            PlaybackArtifactCleanupOptions.MaximumMaxSizeGigabytes,
            viewModel.TranscodeCacheMaxSizeGigabytes);
        Assert.Contains("旧值已调整", viewModel.StatusMessage, StringComparison.Ordinal);

        viewModel.AnyKeywords = true;

        Assert.Equal(2, settingsService.SaveCallCount);
        var secondSessionSettings = new CountingSettingsService(
            new JsonAppSettingsService(workspace.SettingsPath));
        _ = CreateViewModel(
            new RecordingArtifactStore(),
            secondSessionSettings);
        Assert.Equal(0, secondSessionSettings.SaveCallCount);
    }

    [Fact]
    public async Task FutureSettings_ShouldRemainUnchanged_DisableAutomaticCleanup_AndAllowManualCleanup()
    {
        using var workspace = new SettingsWorkspace();
        var futureBytes =
            "{\r\n  \"SchemaVersion\": 2,\r\n  \"FutureSetting\": \"keep me\"\r\n}"u8.ToArray();
        File.WriteAllBytes(workspace.SettingsPath, futureBytes);
        var settingsService = new CountingSettingsService(
            new JsonAppSettingsService(workspace.SettingsPath));
        var artifactStore = new RecordingArtifactStore();
        var storageOverviewService = new RecordingStorageOverviewService();
        var viewModel = CreateViewModel(
            artifactStore,
            settingsService,
            storageOverviewService);

        Assert.Equal(0, settingsService.SaveCallCount);
        Assert.Contains("较新版本", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(futureBytes, File.ReadAllBytes(workspace.SettingsPath));

        await viewModel.StartBackgroundTranscodeCacheMaintenance();

        Assert.Equal(0, artifactStore.CleanupCallCount);
        Assert.Equal(1, storageOverviewService.GetSnapshotCallCount);
        Assert.NotNull(viewModel.StorageOverview);

        viewModel.TranscodeCacheRetentionDays = 100;

        Assert.Equal(0, settingsService.SaveCallCount);
        Assert.Contains("设置未保存", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(futureBytes, File.ReadAllBytes(workspace.SettingsPath));

        viewModel.CleanupTranscodeCacheCommand.Execute(null);
        await (viewModel.CleanupTranscodeCacheCommand.ExecutionTask ?? Task.CompletedTask);

        Assert.Equal(1, artifactStore.CleanupCallCount);
        Assert.Equal(futureBytes, File.ReadAllBytes(workspace.SettingsPath));
    }

    [Fact]
    public async Task CorruptSettings_ShouldBeBackedUpAndSavedOnce_WithoutRunningAutomaticCleanup()
    {
        using var workspace = new SettingsWorkspace();
        var corruptBytes = "{ definitely not valid JSON"u8.ToArray();
        File.WriteAllBytes(workspace.SettingsPath, corruptBytes);
        var settingsService = new CountingSettingsService(
            new JsonAppSettingsService(workspace.SettingsPath));
        var artifactStore = new RecordingArtifactStore();
        var storageOverviewService = new RecordingStorageOverviewService();

        var viewModel = CreateViewModel(
            artifactStore,
            settingsService,
            storageOverviewService);

        Assert.Equal(1, settingsService.SaveCallCount);
        Assert.Contains("设置文件损坏", viewModel.StatusMessage, StringComparison.Ordinal);
        var backup = Assert.Single(Directory.GetFiles(
            workspace.RootDirectory,
            "settings.*.corrupt.json"));
        Assert.Equal(corruptBytes, File.ReadAllBytes(backup));
        using (var savedDocument = JsonDocument.Parse(
                   File.ReadAllText(workspace.SettingsPath)))
        {
            Assert.Equal(
                AppSettings.CurrentSchemaVersion,
                savedDocument.RootElement
                    .GetProperty(nameof(AppSettings.SchemaVersion))
                    .GetInt32());
        }

        await viewModel.StartBackgroundTranscodeCacheMaintenance();

        Assert.Equal(0, artifactStore.CleanupCallCount);
        Assert.Equal(1, storageOverviewService.GetSnapshotCallCount);
        Assert.NotNull(viewModel.StorageOverview);
    }

    [Fact]
    public async Task SettingsLoadFailure_ShouldDisableSavingAndAutomaticCleanup_ButRefreshOverview()
    {
        var settingsService = new ThrowingSettingsService();
        var artifactStore = new RecordingArtifactStore();
        var storageOverviewService = new RecordingStorageOverviewService();

        var viewModel = CreateViewModel(
            artifactStore,
            settingsService,
            storageOverviewService);

        Assert.Contains("读取设置失败", viewModel.StatusMessage, StringComparison.Ordinal);
        viewModel.AnyKeywords = true;
        Assert.Equal(0, settingsService.SaveCallCount);

        await viewModel.StartBackgroundTranscodeCacheMaintenance();

        Assert.Equal(0, artifactStore.CleanupCallCount);
        Assert.Equal(1, storageOverviewService.GetSnapshotCallCount);
        Assert.NotNull(viewModel.StorageOverview);
    }

    [Fact]
    public async Task AutomaticMaintenance_ShouldRecheckSettingsAndSkipWhenAnotherInstanceWritesFutureSchema()
    {
        using var workspace = new SettingsWorkspace();
        var olderService = new JsonAppSettingsService(workspace.SettingsPath);
        olderService.Save(new AppSettings());
        var artifactStore = new RecordingArtifactStore();
        var storageOverviewService = new RecordingStorageOverviewService();
        var viewModel = CreateViewModel(
            artifactStore,
            olderService,
            storageOverviewService);
        var futureBytes =
            "{\r\n  \"SchemaVersion\": 2,\r\n  \"FutureSetting\": \"preserve\"\r\n}"u8.ToArray();
        File.WriteAllBytes(workspace.SettingsPath, futureBytes);
        var newerService = new JsonAppSettingsService(workspace.SettingsPath);
        Assert.Equal(
            AppSettingsLoadKind.FutureVersion,
            newerService.CheckAutomaticMaintenanceEligibility().LoadKind);

        await viewModel.StartBackgroundTranscodeCacheMaintenance();

        Assert.Equal(0, artifactStore.CleanupCallCount);
        Assert.Equal(1, storageOverviewService.GetSnapshotCallCount);
        Assert.Contains("已跳过自动转码缓存维护", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);
        Assert.Equal(futureBytes, File.ReadAllBytes(workspace.SettingsPath));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*.corrupt.json"));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*.writing"));

        viewModel.AnyKeywords = true;
        Assert.Contains("设置未保存", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(futureBytes, File.ReadAllBytes(workspace.SettingsPath));
    }

    [Fact]
    public async Task AutomaticMaintenance_ShouldSkipWhenCompatibleSettingsChangeExternally()
    {
        using var workspace = new SettingsWorkspace();
        var activeService = new JsonAppSettingsService(workspace.SettingsPath);
        activeService.Save(new AppSettings());
        var artifactStore = new RecordingArtifactStore();
        var storageOverviewService = new RecordingStorageOverviewService();
        var viewModel = CreateViewModel(
            artifactStore,
            activeService,
            storageOverviewService);
        var otherService = new JsonAppSettingsService(workspace.SettingsPath);
        var otherSettings = otherService.LoadWithReport().Settings;
        otherSettings.TranscodeCacheRetentionDays =
            PlaybackArtifactCleanupOptions.MaximumRetentionDays;
        otherSettings.TranscodeCacheMaxSizeGigabytes =
            PlaybackArtifactCleanupOptions.MaximumMaxSizeGigabytes;
        otherService.Save(otherSettings);
        var otherWriterBytes = File.ReadAllBytes(workspace.SettingsPath);

        await viewModel.StartBackgroundTranscodeCacheMaintenance();

        Assert.Equal(0, artifactStore.CleanupCallCount);
        Assert.Null(artifactStore.LastCleanupOptions);
        Assert.Equal(1, storageOverviewService.GetSnapshotCallCount);
        Assert.Contains("其他实例更改", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);
        Assert.Contains("重启", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);
        Assert.Equal(
            PlaybackArtifactCleanupOptions.DefaultRetentionDays,
            viewModel.TranscodeCacheRetentionDays);
        Assert.Equal(
            PlaybackArtifactCleanupOptions.DefaultMaxSizeGigabytes,
            viewModel.TranscodeCacheMaxSizeGigabytes);
        Assert.Equal(otherWriterBytes, File.ReadAllBytes(workspace.SettingsPath));

        viewModel.PreferredPlayer = PlaybackPlayerPreference.Vlc;

        Assert.Contains("设置未保存", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(otherWriterBytes, File.ReadAllBytes(workspace.SettingsPath));
    }

    [Fact]
    public async Task AutomaticMaintenance_ShouldSkipWhenLoadedSettingsFileIsDeleted()
    {
        using var workspace = new SettingsWorkspace();
        var settingsService = new JsonAppSettingsService(workspace.SettingsPath);
        settingsService.Save(new AppSettings());
        var artifactStore = new RecordingArtifactStore();
        var storageOverviewService = new RecordingStorageOverviewService();
        var viewModel = CreateViewModel(
            artifactStore,
            settingsService,
            storageOverviewService);
        File.Delete(workspace.SettingsPath);

        await viewModel.StartBackgroundTranscodeCacheMaintenance();

        Assert.Equal(0, artifactStore.CleanupCallCount);
        Assert.Equal(1, storageOverviewService.GetSnapshotCallCount);
        Assert.Contains("被删除", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);
        Assert.Contains("重启", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);
        Assert.False(File.Exists(workspace.SettingsPath));
    }

    [Fact]
    public async Task AutomaticMaintenance_ShouldUseDefaultsWhenSettingsWereInitiallyMissing()
    {
        using var workspace = new SettingsWorkspace();
        var artifactStore = new RecordingArtifactStore();
        var viewModel = CreateViewModel(
            artifactStore,
            new JsonAppSettingsService(workspace.SettingsPath));

        await viewModel.StartBackgroundTranscodeCacheMaintenance();

        Assert.Equal(1, artifactStore.CleanupCallCount);
        var options = Assert.IsType<PlaybackArtifactCleanupOptions>(
            artifactStore.LastCleanupOptions);
        Assert.Equal(
            TimeSpan.FromDays(PlaybackArtifactCleanupOptions.DefaultRetentionDays),
            options.MaxAge);
        Assert.Equal(
            PlaybackArtifactCleanupOptions.DefaultMaxSizeGigabytes * 1024L * 1024L * 1024L,
            options.MaxTotalBytes);
        Assert.False(File.Exists(workspace.SettingsPath));
    }

    [Fact]
    public void ConcurrentViewModels_ShouldNotOverwriteAnotherInstancesSettings()
    {
        using var workspace = new SettingsWorkspace();
        new JsonAppSettingsService(workspace.SettingsPath).Save(new AppSettings());
        var firstViewModel = CreateViewModel(
            new RecordingArtifactStore(),
            new JsonAppSettingsService(workspace.SettingsPath));
        var secondViewModel = CreateViewModel(
            new RecordingArtifactStore(),
            new JsonAppSettingsService(workspace.SettingsPath));

        firstViewModel.TranscodeCacheRetentionDays = 7;
        var firstWriterBytes = File.ReadAllBytes(workspace.SettingsPath);
        secondViewModel.PreferredPlayer = PlaybackPlayerPreference.Vlc;

        Assert.Contains("保存设置失败", secondViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("其他实例更改", secondViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(firstWriterBytes, File.ReadAllBytes(workspace.SettingsPath));
        var persisted = new JsonAppSettingsService(workspace.SettingsPath).Load();
        Assert.Equal(7, persisted.TranscodeCacheRetentionDays);
        Assert.Equal(
            PlaybackPlayerPreference.SystemDefaultFirst,
            persisted.PreferredPlayer);

        secondViewModel.AnyKeywords = !secondViewModel.AnyKeywords;

        Assert.Contains("设置未保存", secondViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(firstWriterBytes, File.ReadAllBytes(workspace.SettingsPath));
    }

    [Fact]
    public async Task MigrationSaveConflict_ShouldDisableFurtherSavesAndAutomaticCleanup()
    {
        var settingsService = new FutureSchemaConflictSettingsService(
            requiresMigration: true);
        var artifactStore = new RecordingArtifactStore();
        var storageOverviewService = new RecordingStorageOverviewService();

        var viewModel = CreateViewModel(
            artifactStore,
            settingsService,
            storageOverviewService);

        Assert.Equal(1, settingsService.SaveCallCount);
        Assert.Contains(
            "迁移后的设置保存失败",
            viewModel.StatusMessage,
            StringComparison.Ordinal);

        viewModel.AnyKeywords = !viewModel.AnyKeywords;

        Assert.Equal(1, settingsService.SaveCallCount);
        Assert.Contains("设置未保存", viewModel.StatusMessage, StringComparison.Ordinal);

        await viewModel.StartBackgroundTranscodeCacheMaintenance();

        Assert.Equal(0, artifactStore.CleanupCallCount);
        Assert.Equal(1, storageOverviewService.GetSnapshotCallCount);
        Assert.NotNull(viewModel.StorageOverview);
    }

    [Fact]
    public async Task RuntimeSaveConflict_ShouldDisableFurtherSavesAndAutomaticCleanup()
    {
        var settingsService = new FutureSchemaConflictSettingsService(
            requiresMigration: false);
        var artifactStore = new RecordingArtifactStore();
        var storageOverviewService = new RecordingStorageOverviewService();
        var viewModel = CreateViewModel(
            artifactStore,
            settingsService,
            storageOverviewService);

        viewModel.AnyKeywords = !viewModel.AnyKeywords;

        Assert.Equal(1, settingsService.SaveCallCount);
        Assert.Contains("保存设置失败", viewModel.StatusMessage, StringComparison.Ordinal);

        viewModel.CaseSensitive = !viewModel.CaseSensitive;

        Assert.Equal(1, settingsService.SaveCallCount);
        Assert.Contains("设置未保存", viewModel.StatusMessage, StringComparison.Ordinal);

        await viewModel.StartBackgroundTranscodeCacheMaintenance();

        Assert.Equal(0, artifactStore.CleanupCallCount);
        Assert.Equal(1, storageOverviewService.GetSnapshotCallCount);
        Assert.NotNull(viewModel.StorageOverview);
    }

    private static MainViewModel CreateViewModel(
        IPlaybackArtifactStore artifactStore,
        IAppSettingsService settingsService,
        IStorageOverviewService? storageOverviewService = null)
    {
        return new MainViewModel(
            new CacheManager(),
            new CachePlaybackService(artifactStore),
            new ConfirmingDialogService(),
            new NoOpHelpService(),
            new NoOpExplorerService(),
            settingsService: settingsService,
            playbackArtifactStore: artifactStore,
            storageOverviewService: storageOverviewService);
    }

    private sealed class CountingSettingsService(IAppSettingsService inner) : IAppSettingsService
    {
        public int SaveCallCount { get; private set; }

        public AppSettings Load() => inner.Load();

        public AppSettingsLoadResult LoadWithReport() => inner.LoadWithReport();

        public void Save(AppSettings settings)
        {
            SaveCallCount++;
            inner.Save(settings);
        }
    }

    private sealed class ThrowingSettingsService : IAppSettingsService
    {
        public int SaveCallCount { get; private set; }

        public AppSettings Load()
        {
            throw new IOException("Injected settings read failure.");
        }

        public AppSettingsLoadResult LoadWithReport()
        {
            throw new IOException("Injected settings read failure.");
        }

        public void Save(AppSettings settings)
        {
            SaveCallCount++;
        }
    }

    private sealed class FutureSchemaConflictSettingsService(
        bool requiresMigration) : IAppSettingsService
    {
        public int SaveCallCount { get; private set; }

        public AppSettings Load() => LoadWithReport().Settings;

        public AppSettingsLoadResult LoadWithReport()
        {
            return new AppSettingsLoadResult(
                requiresMigration
                    ? AppSettingsLoadKind.LegacyVersion
                    : AppSettingsLoadKind.CurrentVersion,
                new AppSettings { RootPath = @"D:\Cache" },
                SourceSchemaVersion: requiresMigration ? 0 : AppSettings.CurrentSchemaVersion,
                RequiresSave: requiresMigration,
                IsUnsupported: false,
                CanSave: true,
                CanRunAutomaticMaintenance: true,
                Array.Empty<string>(),
                requiresMigration ? "旧设置需要迁移。" : null);
        }

        public void Save(AppSettings settings)
        {
            SaveCallCount++;
            throw new InvalidOperationException(
                "现有设置文件版本 v2 高于当前支持版本，已拒绝覆盖。");
        }
    }

    private sealed class RecordingArtifactStore : IPlaybackArtifactStore
    {
        private int _cleanupCallCount;

        public string RootDirectory { get; } = Path.GetTempPath();

        public int CleanupCallCount => Volatile.Read(ref _cleanupCallCount);

        public PlaybackArtifactCleanupOptions? LastCleanupOptions { get; private set; }

        public PlaybackArtifactMaterialization GetOrCreate(
            CachePlaybackPlan plan,
            string extension,
            Action<string> producer,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public PlaybackArtifactCacheStatistics GetStatistics()
        {
            return new PlaybackArtifactCacheStatistics(RootDirectory, 1, 128);
        }

        public PlaybackArtifactCleanupPreview PreviewCleanup(
            PlaybackArtifactCleanupOptions? options = null)
        {
            return new PlaybackArtifactCleanupPreview(0, 0, 128);
        }

        public PlaybackArtifactCleanupResult Cleanup(
            PlaybackArtifactCleanupOptions? options = null)
        {
            LastCleanupOptions = options;
            Interlocked.Increment(ref _cleanupCallCount);
            return new PlaybackArtifactCleanupResult(0, 0, 0, 128);
        }

        public PlaybackArtifactCleanupResult Clear()
        {
            return new PlaybackArtifactCleanupResult(1, 128, 0, 0);
        }
    }

    private sealed class RecordingStorageOverviewService : IStorageOverviewService
    {
        private int _getSnapshotCallCount;

        public int GetSnapshotCallCount => Volatile.Read(ref _getSnapshotCallCount);

        public StorageOverviewSnapshot GetSnapshot(
            string? cacheRoot,
            PlaybackArtifactCleanupOptions cleanupOptions,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _getSnapshotCallCount);
            return CreateSnapshot(cacheRoot);
        }

        public StorageOverviewSnapshot RefreshTranscode(
            StorageOverviewSnapshot snapshot,
            PlaybackArtifactCleanupOptions cleanupOptions,
            CancellationToken cancellationToken = default)
        {
            return CreateSnapshot(snapshot.CacheRoot);
        }

        private static StorageOverviewSnapshot CreateSnapshot(string? cacheRoot)
        {
            return new StorageOverviewSnapshot(
                cacheRoot,
                OriginalCache: null,
                new PlaybackArtifactCacheStatistics(Path.GetTempPath(), 1, 128),
                new PlaybackArtifactCleanupPreview(0, 0, 128),
                Trash: null,
                ManagedTotalBytes: 128,
                ReclaimableBytes: 0,
                DateTimeOffset.Now,
                Array.Empty<string>());
        }
    }

    private sealed class ConfirmingDialogService : IDialogService
    {
        public bool Confirm(string message, string title) => true;

        public string? PickFolder(string title, string? initialPath) => null;
    }

    private sealed class NoOpHelpService : IHelpService
    {
        public void OpenHelp()
        {
        }
    }

    private sealed class NoOpExplorerService : IExplorerService
    {
        public void OpenFolder(string folderPath)
        {
        }
    }

    private sealed class SettingsWorkspace : IDisposable
    {
        public SettingsWorkspace()
        {
            RootDirectory = Path.Combine(
                Path.GetTempPath(),
                $"bili_vm_settings_migration_{Guid.NewGuid():N}");
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
