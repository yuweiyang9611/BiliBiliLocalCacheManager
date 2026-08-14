using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Wpf.Commands;
using BiliBiliLocalCacheManager.Wpf.Models;
using BiliBiliLocalCacheManager.Wpf.Services;
using PlaybackContracts = BiliBiliLocalCacheManager.Playback.Contracts;

namespace BiliBiliLocalCacheManager.Wpf.ViewModels;

public sealed partial class MainViewModel
{
    private const int SessionSensitiveMediaValueCapacity = 1_000;
    private const int DiagnosticSensitiveMediaValueCapacity =
        SensitiveDataRedactionContext.MaximumKnownSensitiveValueCount;

    private readonly DateTimeOffset _applicationStartedAtUtc = DateTimeOffset.UtcNow;
    private IFileSaveDialogService? _fileSaveDialogService;
    private IDiagnosticReportService? _diagnosticReportService;
    private IDiagnosticEventRecorder? _diagnosticEventRecorder;
    private PlaybackContracts.IFfmpegDiagnosticsProvider? _ffmpegDiagnosticsProvider;
    private AppSettingsLoadKind? _settingsLoadKind;
    private int? _sourceSettingsSchemaVersion;
    private string? _lastPlaybackFailure;
    private readonly object _sensitiveMediaValuesSync = new();
    private readonly LinkedList<string> _sensitiveMediaValueOrder = new();
    private readonly Dictionary<string, LinkedListNode<string>> _sensitiveMediaValueNodes = new(
        StringComparer.OrdinalIgnoreCase);

    public AsyncRelayCommand ExportDiagnosticsCommand { get; private set; } = null!;

    public bool IsDiagnosticExportBusy
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                ExportDiagnosticsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private void InitializeDiagnostics(
        IFileSaveDialogService? fileSaveDialogService,
        IDiagnosticReportService? diagnosticReportService,
        IDiagnosticEventRecorder? diagnosticEventRecorder,
        PlaybackContracts.IFfmpegDiagnosticsProvider? ffmpegDiagnosticsProvider)
    {
        _fileSaveDialogService = fileSaveDialogService;
        _diagnosticReportService = diagnosticReportService;
        _diagnosticEventRecorder = diagnosticEventRecorder;
        _ffmpegDiagnosticsProvider = ffmpegDiagnosticsProvider;
        ExportDiagnosticsCommand = new AsyncRelayCommand(
            ExportDiagnosticsAsync,
            CanExportDiagnostics);
    }

    private bool CanExportDiagnostics()
    {
        return _fileSaveDialogService is not null &&
            _diagnosticReportService is not null &&
            !IsDiagnosticExportBusy;
    }

    private async Task ExportDiagnosticsAsync()
    {
        if (_fileSaveDialogService is null || _diagnosticReportService is null)
        {
            SetStatus("诊断导出服务不可用。", isError: true);
            return;
        }

        var defaultFileName =
            $"BiliBiliLocalCacheManager-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        var destinationPath = _fileSaveDialogService.PickSavePath(
            "导出诊断信息",
            defaultFileName,
            ".zip",
            "诊断压缩包 (*.zip)|*.zip");
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            SetStatus("已取消导出诊断信息。", isError: false);
            return;
        }

        IsDiagnosticExportBusy = true;
        try
        {
            SetStatus("正在导出诊断信息，请稍候…", isError: false);
            var request = new DiagnosticReportRequest(
                destinationPath,
                CreateDiagnosticReportContext(),
                CreateSensitiveDataRedactionContext());
            var result = await Task.Run(
                () => _diagnosticReportService.ExportAsync(request));
            SetStatus(
                $"诊断信息已导出：{result.OutputPath}（{result.EventCount} 条近期事件）。",
                isError: false);
        }
        catch (OperationCanceledException)
        {
            SetStatus("诊断信息导出已取消，未生成文件。", isError: false);
        }
        catch (Exception ex)
        {
            RecordDiagnosticEvent(
                "Export",
                DiagnosticEventLevel.Error,
                $"诊断信息导出失败：{ex.Message}",
                ex);
            SetStatus($"导出诊断信息失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsDiagnosticExportBusy = false;
        }
    }

    private DiagnosticReportContext CreateDiagnosticReportContext()
    {
        FfmpegDiagnosticSnapshot ffmpeg;
        try
        {
            // Diagnostics must be observational: GetSnapshot never initializes or downloads FFmpeg.
            ffmpeg = _ffmpegDiagnosticsProvider?.GetSnapshot() ??
                FfmpegDiagnosticSnapshot.NotInitialized;
        }
        catch (Exception ex)
        {
            ffmpeg = FfmpegDiagnosticSnapshot.NotInitialized;
            RecordDiagnosticEvent(
                "FFmpeg",
                DiagnosticEventLevel.Warning,
                $"读取 FFmpeg 诊断快照失败：{ex.Message}",
                ex);
        }

        var assembly = typeof(MainViewModel).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";
        return new DiagnosticReportContext
        {
            ProductName = assembly.GetName().Name ?? "BiliBiliLocalCacheManager",
            InformationalVersion = informationalVersion,
            Uptime = DateTimeOffset.UtcNow - _applicationStartedAtUtc,
            OperatingSystem = RuntimeInformation.OSDescription,
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            OperatingSystemArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Culture = CultureInfo.CurrentCulture.Name,
            SettingsSchemaVersion = AppSettings.CurrentSchemaVersion,
            SettingsLoadKind = _settingsLoadKind,
            SourceSettingsSchemaVersion = _sourceSettingsSchemaVersion,
            SettingsSaveEnabled = _settingsCanSave,
            AutomaticTranscodeCacheMaintenanceEnabled =
                _canRunAutomaticTranscodeCacheMaintenance,
            PreferredPlayer = PreferredPlayer.ToString(),
            IncludeIncompleteCache = IncludeIncomplete,
            TranscodeCacheRetentionDays = TranscodeCacheRetentionDays,
            TranscodeCacheMaxSizeGigabytes = TranscodeCacheMaxSizeGigabytes,
            CacheRoot = string.IsNullOrWhiteSpace(RootPath) ? null : RootPath.Trim(),
            StorageOverview = StorageOverview,
            LastStorageMaintenance = LastStorageMaintenanceSummary,
            Ffmpeg = ffmpeg,
            LastPlaybackFailure = _lastPlaybackFailure
        };
    }

    private SensitiveDataRedactionContext CreateSensitiveDataRedactionContext()
    {
        var mediaValues = new List<string>(DiagnosticSensitiveMediaValueCapacity);
        var seenMediaValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddMediaValue(string? candidate)
        {
            if (mediaValues.Count >= DiagnosticSensitiveMediaValueCapacity)
            {
                return;
            }

            var normalized = candidate?.Trim();
            if (!string.IsNullOrWhiteSpace(normalized) && seenMediaValues.Add(normalized))
            {
                mediaValues.Add(normalized);
            }
        }

        lock (_sensitiveMediaValuesSync)
        {
            foreach (var value in _sensitiveMediaValueOrder)
            {
                AddMediaValue(value);
            }
        }

        AddMediaValue(SelectedItem?.Title);
        foreach (var item in SelectedItems)
        {
            AddMediaValue(item.Title);
        }

        AddMediaValue(SelectedSegmentDetail?.PartName);
        foreach (var item in SelectedSegmentDetails)
        {
            AddMediaValue(item.PartName);
        }

        foreach (var item in SegmentDetails)
        {
            if (mediaValues.Count >= DiagnosticSensitiveMediaValueCapacity)
            {
                break;
            }

            AddMediaValue(item.PartName);
        }

        foreach (var item in Items)
        {
            if (mediaValues.Count >= DiagnosticSensitiveMediaValueCapacity)
            {
                break;
            }

            AddMediaValue(item.Title);
        }

        return new SensitiveDataRedactionContext(
            CacheRoot: string.IsNullOrWhiteSpace(RootPath) ? null : RootPath.Trim(),
            UserProfileDirectory: Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
            LocalApplicationDataDirectory: Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            TemporaryDirectory: Path.GetTempPath(),
            KnownSensitiveValues: mediaValues);
    }

    private void RememberPlaybackTarget(BiliVideoCache cache, string? partName)
    {
        RememberSensitiveMediaValue(cache.Title);
        RememberSensitiveMediaValue(partName);
    }

    private void RememberSensitiveMediaValue(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        lock (_sensitiveMediaValuesSync)
        {
            if (_sensitiveMediaValueNodes.TryGetValue(normalized, out var existingNode))
            {
                _sensitiveMediaValueOrder.Remove(existingNode);
                _sensitiveMediaValueOrder.AddLast(existingNode);
                return;
            }

            var addedNode = _sensitiveMediaValueOrder.AddLast(normalized);
            _sensitiveMediaValueNodes.Add(normalized, addedNode);
            while (_sensitiveMediaValueOrder.Count > SessionSensitiveMediaValueCapacity)
            {
                var oldestNode = _sensitiveMediaValueOrder.First!;
                _sensitiveMediaValueOrder.RemoveFirst();
                _sensitiveMediaValueNodes.Remove(oldestNode.Value);
            }
        }
    }

    private void RecordStatusForDiagnostics(string message, bool isError)
    {
        if (isError && message.Contains("播放失败", StringComparison.Ordinal))
        {
            _lastPlaybackFailure = message;
        }

        var category = message.Contains("设置", StringComparison.Ordinal)
            ? "Settings"
            : message.Contains("播放", StringComparison.Ordinal)
                ? "Playback"
                : message.Contains("诊断", StringComparison.Ordinal)
                    ? "Export"
                    : message.Contains("缓存", StringComparison.Ordinal) ||
                      message.Contains("回收站", StringComparison.Ordinal) ||
                      message.Contains("存储", StringComparison.Ordinal)
                        ? "Storage"
                        : "Application";
        RecordDiagnosticEvent(
            category,
            isError ? DiagnosticEventLevel.Error : DiagnosticEventLevel.Information,
            message);
    }

    private void RecordDiagnosticEvent(
        string category,
        DiagnosticEventLevel level,
        string message,
        Exception? exception = null)
    {
        try
        {
            _diagnosticEventRecorder?.Record(new DiagnosticEvent(
                DateTimeOffset.UtcNow,
                category,
                level,
                message,
                exception?.GetType().FullName));
        }
        catch
        {
            // Diagnostics are best effort and must never affect the user operation.
        }
    }
}
