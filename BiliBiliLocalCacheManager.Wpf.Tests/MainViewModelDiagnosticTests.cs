using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;
using BiliBiliLocalCacheManager.Wpf.Models;
using BiliBiliLocalCacheManager.Wpf.Services;
using BiliBiliLocalCacheManager.Wpf.ViewModels;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class MainViewModelDiagnosticTests
{
    [Fact]
    public async Task ExportDiagnostics_WhenSaveDialogIsCanceled_DoesNotCreateOrExportAnything()
    {
        using var directory = new TemporaryDirectory();
        var wouldBeOutput = Path.Combine(directory.Path, "should-not-exist.zip");
        var reportService = new RecordingReportService();
        var ffmpeg = new SnapshotOnlyFfmpegProvider();
        var viewModel = CreateViewModel(
            new RecordingSaveDialogService(null),
            reportService,
            new InMemoryDiagnosticEventRecorder(),
            ffmpeg);

        await ExecuteAsync(viewModel.ExportDiagnosticsCommand);

        Assert.False(File.Exists(wouldBeOutput));
        Assert.Equal(0, reportService.CallCount);
        Assert.Equal(0, ffmpeg.GetSnapshotCallCount);
        Assert.Contains("已取消", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportDiagnostics_WithUninitializedFfmpeg_OnlyReadsSnapshotAndIncludesSettingsLoadMetadata()
    {
        using var directory = new TemporaryDirectory();
        var outputPath = Path.Combine(directory.Path, "diagnostics.zip");
        var reportService = new RecordingReportService();
        var ffmpeg = new SnapshotOnlyFfmpegProvider();
        var settings = new LegacySettingsService();
        var viewModel = CreateViewModel(
            new RecordingSaveDialogService(outputPath),
            reportService,
            new InMemoryDiagnosticEventRecorder(),
            ffmpeg,
            settings);

        await ExecuteAsync(viewModel.ExportDiagnosticsCommand);

        Assert.Equal(1, reportService.CallCount);
        Assert.Equal(1, ffmpeg.GetSnapshotCallCount);
        var context = Assert.IsType<DiagnosticReportContext>(reportService.LastRequest?.Context);
        Assert.False(context.Ffmpeg.IsInitialized);
        Assert.Equal(FfmpegResolutionSource.NotInitialized, context.Ffmpeg.Source);
        Assert.Equal(AppSettings.CurrentSchemaVersion, context.SettingsSchemaVersion);
        Assert.Equal(AppSettingsLoadKind.LegacyVersion, context.SettingsLoadKind);
        Assert.Equal(0, context.SourceSettingsSchemaVersion);
        Assert.True(context.SettingsSaveEnabled);
        Assert.True(context.AutomaticTranscodeCacheMaintenanceEnabled);
        Assert.Equal(1, settings.SaveCallCount);
    }

    [Fact]
    public async Task ExportDiagnostics_WhenReportFails_ShowsFriendlyStatusAndReenablesCommand()
    {
        using var directory = new TemporaryDirectory();
        var reportService = new RecordingReportService
        {
            Failure = new IOException("disk is unavailable")
        };
        var viewModel = CreateViewModel(
            new RecordingSaveDialogService(Path.Combine(directory.Path, "diagnostics.zip")),
            reportService,
            new InMemoryDiagnosticEventRecorder(),
            new SnapshotOnlyFfmpegProvider());

        await ExecuteAsync(viewModel.ExportDiagnosticsCommand);

        Assert.Contains("导出诊断信息失败", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("disk is unavailable", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.IsDiagnosticExportBusy);
        Assert.True(viewModel.ExportDiagnosticsCommand.CanExecute(null));
    }

    [Fact]
    public async Task ExportDiagnostics_RunsReportWorkOffTheCallingThread()
    {
        using var directory = new TemporaryDirectory();
        using var reportService = new SynchronouslyBlockingReportService();
        var viewModel = CreateViewModel(
            new RecordingSaveDialogService(Path.Combine(directory.Path, "diagnostics.zip")),
            reportService,
            new InMemoryDiagnosticEventRecorder(),
            new SnapshotOnlyFfmpegProvider());
        var callingThreadId = Environment.CurrentManagedThreadId;

        viewModel.ExportDiagnosticsCommand.Execute(null);
        var exportTask = viewModel.ExportDiagnosticsCommand.ExecutionTask;
        Assert.NotNull(exportTask);
        try
        {
            Assert.True(reportService.Started.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotEqual(callingThreadId, reportService.ExecutionThreadId);
            Assert.False(exportTask.IsCompleted);
            Assert.True(viewModel.IsDiagnosticExportBusy);
        }
        finally
        {
            reportService.Release.Set();
        }

        await exportTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(viewModel.IsDiagnosticExportBusy);
    }

    [Fact]
    public async Task ExportDiagnostics_AfterPlaybackFailureLargeScanAndListClear_StillRedactsSessionMediaNames()
    {
        using var directory = new TemporaryDirectory();
        var outputPath = Path.Combine(directory.Path, "diagnostics.zip");
        var title = "清空列表后仍需保密的标题";
        var partName = "清空列表后仍需保密的分P";
        var recorder = new InMemoryDiagnosticEventRecorder();
        var reportService = new DiagnosticReportService(recorder, new SensitiveDataRedactor());
        var viewModel = CreateViewModel(
            new RecordingSaveDialogService(outputPath),
            reportService,
            recorder,
            new SnapshotOnlyFfmpegProvider());
        var cache = CreateCache(title, partName);
        InvokePrivate(viewModel, "RememberPlaybackTarget", cache, partName);
        InvokePrivate(viewModel, "SetStatus", $"播放失败：{title} / {partName}", true);
        var unrelatedCaches = Enumerable.Range(1_000, 5_000)
            .Select(avid => CreateCache($"无关标题-{avid}", $"无关分P-{avid}", avid))
            .ToArray();
        SetPrivateField(viewModel, "_currentIndex", new CacheIndex(unrelatedCaches));
        InvokePrivate(viewModel, "UpdateItems", (object)unrelatedCaches);

        viewModel.ClearCommand.Execute(null);
        Assert.Empty(viewModel.Items);
        Assert.Empty(viewModel.SegmentDetails);
        var redactionContext = Assert.IsType<SensitiveDataRedactionContext>(
            InvokePrivate(viewModel, "CreateSensitiveDataRedactionContext"));
        Assert.Equal(2, redactionContext.KnownSensitiveValues.Count);
        Assert.Contains(title, redactionContext.KnownSensitiveValues);
        Assert.Contains(partName, redactionContext.KnownSensitiveValues);
        await ExecuteAsync(viewModel.ExportDiagnosticsCommand);

        using var archive = ZipFile.OpenRead(outputPath);
        var contents = string.Join("\n", archive.Entries.Select(ReadEntry));
        Assert.DoesNotContain(title, contents, StringComparison.Ordinal);
        Assert.DoesNotContain(partName, contents, StringComparison.Ordinal);
        Assert.Contains("[MEDIA]", contents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportDiagnostics_ReplayedOldestMediaValue_RemainsRedactedAfterLruEviction()
    {
        using var directory = new TemporaryDirectory();
        var outputPath = Path.Combine(directory.Path, "diagnostics.zip");
        const string replayedPart = "重新播放后仍需保密的最旧分P";
        const string newestPart = "触发淘汰的新分P";
        var recorder = new InMemoryDiagnosticEventRecorder();
        var viewModel = CreateViewModel(
            new RecordingSaveDialogService(outputPath),
            new DiagnosticReportService(recorder, new SensitiveDataRedactor()),
            recorder,
            new SnapshotOnlyFfmpegProvider());

        InvokePrivate(viewModel, "RememberSensitiveMediaValue", replayedPart);
        for (var index = 0; index < 999; index++)
        {
            InvokePrivate(
                viewModel,
                "RememberSensitiveMediaValue",
                $"填充媒体值-{index:D4}");
        }

        InvokePrivate(viewModel, "RememberSensitiveMediaValue", replayedPart);
        InvokePrivate(viewModel, "RememberSensitiveMediaValue", newestPart);
        InvokePrivate(viewModel, "SetStatus", $"播放失败：{replayedPart}", true);
        viewModel.ClearCommand.Execute(null);

        var redactionContext = Assert.IsType<SensitiveDataRedactionContext>(
            InvokePrivate(viewModel, "CreateSensitiveDataRedactionContext"));
        Assert.Contains(replayedPart, redactionContext.KnownSensitiveValues);
        Assert.Contains(newestPart, redactionContext.KnownSensitiveValues);
        Assert.DoesNotContain("填充媒体值-0000", redactionContext.KnownSensitiveValues);

        await ExecuteAsync(viewModel.ExportDiagnosticsCommand);

        using var archive = ZipFile.OpenRead(outputPath);
        var contents = string.Join("\n", archive.Entries.Select(ReadEntry));
        Assert.DoesNotContain(replayedPart, contents, StringComparison.Ordinal);
        Assert.Contains("[MEDIA]", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticMediaValues_AreBoundedAndKeepPlaybackHistoryBeforeVisibleItems()
    {
        var viewModel = CreateViewModel(
            new RecordingSaveDialogService(null),
            new RecordingReportService(),
            new InMemoryDiagnosticEventRecorder(),
            new SnapshotOnlyFfmpegProvider());
        const string playedTitle = "必须优先保留的播放标题";
        const string playedPart = "必须优先保留的播放分P";
        InvokePrivate(
            viewModel,
            "RememberPlaybackTarget",
            CreateCache(playedTitle, playedPart),
            playedPart);
        for (var index = 0; index < 2_500; index++)
        {
            viewModel.Items.Add(new CacheItem
            {
                Avid = index + 10_000,
                Title = $"可见标题-{index:D4}"
            });
        }

        var context = Assert.IsType<SensitiveDataRedactionContext>(
            InvokePrivate(viewModel, "CreateSensitiveDataRedactionContext"));

        Assert.Equal(
            SensitiveDataRedactionContext.MaximumKnownSensitiveValueCount,
            context.KnownSensitiveValues.Count);
        Assert.Contains(playedTitle, context.KnownSensitiveValues);
        Assert.Contains(playedPart, context.KnownSensitiveValues);
        Assert.Contains("可见标题-0000", context.KnownSensitiveValues);
        Assert.DoesNotContain("可见标题-2499", context.KnownSensitiveValues);
    }

    [Fact]
    public void StatusRecording_ClassifiesSettingsPlaybackStorageAndExportFailures()
    {
        var recorder = new InMemoryDiagnosticEventRecorder();
        var viewModel = CreateViewModel(
            new RecordingSaveDialogService(null),
            new RecordingReportService(),
            recorder,
            new SnapshotOnlyFfmpegProvider());

        InvokePrivate(viewModel, "SetStatus", "保存设置失败：test", true);
        InvokePrivate(viewModel, "SetStatus", "播放失败：test", true);
        InvokePrivate(viewModel, "SetStatus", "存储统计失败：test", true);
        InvokePrivate(viewModel, "SetStatus", "导出诊断信息失败：test", true);

        var recent = recorder.GetRecentEvents().TakeLast(4).ToList();
        Assert.Equal(
            ["Settings", "Playback", "Storage", "Export"],
            recent.Select(item => item.Category));
        Assert.All(recent, item => Assert.Equal(DiagnosticEventLevel.Error, item.Level));
    }

    private static MainViewModel CreateViewModel(
        IFileSaveDialogService saveDialog,
        IDiagnosticReportService reportService,
        IDiagnosticEventRecorder recorder,
        IFfmpegDiagnosticsProvider ffmpeg,
        IAppSettingsService? settingsService = null)
    {
        return new MainViewModel(
            new CacheManager(),
            new CachePlaybackService(),
            new SilentDialogService(),
            new SilentHelpService(),
            new SilentExplorerService(),
            settingsService: settingsService,
            fileSaveDialogService: saveDialog,
            diagnosticReportService: reportService,
            diagnosticEventRecorder: recorder,
            ffmpegDiagnosticsProvider: ffmpeg);
    }

    private static BiliVideoCache CreateCache(string title, string partName, long avid = 42)
    {
        var segment = new BiliSegment(
            avid,
            cid: 1,
            bvid: null,
            pageIndex: 1,
            partName,
            title,
            CacheVersion.Modern,
            typeTag: "80",
            mediaType: null,
            videoQuality: 80,
            qualityDescription: "test",
            isCompleted: true,
            totalBytes: 1_024,
            downloadedBytes: 1_024,
            totalDuration: TimeSpan.FromSeconds(1),
            danmakuCount: 0,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            segmentDirectory: Path.Combine(Path.GetTempPath(), avid.ToString(), "1"),
            entryJsonPath: Path.Combine(Path.GetTempPath(), avid.ToString(), "1", "entry.json"),
            videoFiles: ["video.mp4"],
            coverUrl: string.Empty,
            ownerName: null,
            ownerId: null);
        return new BiliVideoCache(avid, [segment]);
    }

    private static async Task ExecuteAsync(BiliBiliLocalCacheManager.Wpf.Commands.AsyncRelayCommand command)
    {
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        var task = command.ExecutionTask;
        Assert.NotNull(task);
        await task;
    }

    private static object? InvokePrivate(
        object target,
        string methodName,
        params object?[] arguments)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(target, arguments);
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class RecordingSaveDialogService(string? result) : IFileSaveDialogService
    {
        public string? PickSavePath(
            string title,
            string defaultFileName,
            string defaultExtension,
            string filter) => result;
    }

    private sealed class RecordingReportService : IDiagnosticReportService
    {
        public int CallCount { get; private set; }

        public DiagnosticReportRequest? LastRequest { get; private set; }

        public Exception? Failure { get; init; }

        public Task<DiagnosticReportResult> ExportAsync(
            DiagnosticReportRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            if (Failure is not null)
            {
                return Task.FromException<DiagnosticReportResult>(Failure);
            }

            return Task.FromResult(new DiagnosticReportResult(
                request.DestinationPath,
                FileSizeBytes: 100,
                EventCount: 0));
        }
    }

    private sealed class SynchronouslyBlockingReportService :
        IDiagnosticReportService,
        IDisposable
    {
        public ManualResetEventSlim Started { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public int ExecutionThreadId { get; private set; }

        public Task<DiagnosticReportResult> ExportAsync(
            DiagnosticReportRequest request,
            CancellationToken cancellationToken = default)
        {
            ExecutionThreadId = Environment.CurrentManagedThreadId;
            Started.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(5), cancellationToken))
            {
                throw new TimeoutException("The test report service was not released.");
            }

            return Task.FromResult(new DiagnosticReportResult(
                request.DestinationPath,
                FileSizeBytes: 100,
                EventCount: 0));
        }

        public void Dispose()
        {
            Started.Dispose();
            Release.Dispose();
        }
    }

    private sealed class SnapshotOnlyFfmpegProvider : IFfmpegDiagnosticsProvider
    {
        public int GetSnapshotCallCount { get; private set; }

        public FfmpegDiagnosticSnapshot GetSnapshot()
        {
            GetSnapshotCallCount++;
            return FfmpegDiagnosticSnapshot.NotInitialized;
        }
    }

    private sealed class LegacySettingsService : IAppSettingsService
    {
        public int SaveCallCount { get; private set; }

        public AppSettings Load() => LoadWithReport().Settings;

        public AppSettingsLoadResult LoadWithReport()
        {
            return new AppSettingsLoadResult(
                AppSettingsLoadKind.LegacyVersion,
                new AppSettings(),
                SourceSchemaVersion: 0,
                RequiresSave: true,
                IsUnsupported: false,
                CanSave: true,
                CanRunAutomaticMaintenance: true,
                Adjustments: ["migrated"],
                UserMessage: "设置已迁移。");
        }

        public void Save(AppSettings settings)
        {
            SaveCallCount++;
        }
    }

    private sealed class SilentDialogService : IDialogService
    {
        public bool Confirm(string message, string title) => false;

        public string? PickFolder(string title, string? initialPath) => null;
    }

    private sealed class SilentHelpService : IHelpService
    {
        public void OpenHelp()
        {
        }
    }

    private sealed class SilentExplorerService : IExplorerService
    {
        public void OpenFolder(string folderPath)
        {
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"BiliBiliLocalCacheManager.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best effort test cleanup.
            }
        }
    }
}
