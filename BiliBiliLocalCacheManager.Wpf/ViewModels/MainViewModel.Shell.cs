using System.IO;
using System.Reflection;

namespace BiliBiliLocalCacheManager.Wpf.ViewModels;

/// <summary>
/// 窗口标题、空状态引导、启动自动扫描与搜索防抖。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// 用户停止输入后触发增量搜索的等待时间。
    /// </summary>
    internal static TimeSpan SearchDebounceDelay { get; set; } = TimeSpan.FromMilliseconds(350);

    private CancellationTokenSource? _searchDebounceCts;
    private bool _startupScanAttempted;

    public string WindowTitle { get; } = BuildWindowTitle();

    /// <summary>列表为空时显示引导层而不是一张空表格。</summary>
    public bool ShowEmptyState
    {
        get;
        private set => SetField(ref field, value);
    } = true;

    public string EmptyStateTitle
    {
        get;
        private set => SetField(ref field, value);
    } = "尚未加载缓存";

    public string EmptyStateHint
    {
        get;
        private set => SetField(ref field, value);
    } = "点击上方「浏览」选择 B 站缓存根目录并「扫描」；右键条目可播放或导出 MP4。";

    /// <summary>未选中缓存时，分段详情区显示提示而不是空表头。</summary>
    public bool ShowSegmentEmptyState
    {
        get;
        private set => SetField(ref field, value);
    } = true;

    /// <summary>
    /// 启动时若已经记住了缓存目录就直接扫描，省掉每次都要手点一次「扫描」。
    /// </summary>
    public Task TryAutoScanOnStartupAsync()
    {
        if (_startupScanAttempted)
        {
            return Task.CompletedTask;
        }

        _startupScanAttempted = true;

        var root = RootPath.Trim();
        if (string.IsNullOrWhiteSpace(root))
        {
            return Task.CompletedTask;
        }

        try
        {
            if (!Directory.Exists(root))
            {
                SetStatus($"上次使用的缓存目录不存在，请重新选择：{root}", isError: true);
                return Task.CompletedTask;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.CompletedTask;
        }

        ScanCommand.Execute(null);
        return ScanCommand.ExecutionTask ?? Task.CompletedTask;
    }

    private void UpdateEmptyState()
    {
        ShowEmptyState = Items.Count == 0;
        ShowSegmentEmptyState = SegmentDetails.Count == 0;

        if (Items.Count > 0)
        {
            return;
        }

        if (_currentIndex is null)
        {
            EmptyStateTitle = "尚未加载缓存";
            EmptyStateHint =
                "点击上方「浏览」选择 B 站缓存根目录并「扫描」；右键条目可播放或导出 MP4。";
            return;
        }

        if (!string.IsNullOrWhiteSpace(Keyword))
        {
            EmptyStateTitle = "没有匹配的缓存";
            EmptyStateHint = "清空关键字可以显示全部结果，或调整下方的搜索范围选项。";
            return;
        }

        EmptyStateTitle = "这个目录里没有找到缓存";
        EmptyStateHint = IncludeIncomplete
            ? "确认所选目录是 B 站客户端的下载根目录（其下应有以数字命名的子目录）。"
            : "可以勾选「包含未完成缓存」后重新扫描，未下载完的条目默认不会显示。";
    }

    /// <summary>
    /// 索引已就绪时，输入关键字即增量过滤，无需再点搜索按钮。
    /// </summary>
    private void ScheduleIncrementalSearch()
    {
        if (_loadingSettings || _currentIndex is null)
        {
            return;
        }

        CancelSearchDebounce();

        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        _ = RunDebouncedSearchAsync(cts);
    }

    private async Task RunDebouncedSearchAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(SearchDebounceDelay, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || !ReferenceEquals(_searchDebounceCts, cts))
            {
                return;
            }

            await SearchAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_searchDebounceCts, cts))
            {
                _searchDebounceCts = null;
            }

            cts.Dispose();
        }
    }

    private void CancelSearchDebounce()
    {
        var previous = _searchDebounceCts;
        _searchDebounceCts = null;
        if (previous is null)
        {
            return;
        }

        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string BuildWindowTitle()
    {
        const string baseTitle = "BiliBili 本地缓存管理器";
        var assembly = typeof(MainViewModel).Assembly;
        var version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(version))
        {
            version = assembly.GetName().Version?.ToString();
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            return baseTitle;
        }

        // InformationalVersion 常带 "+<commit>" 后缀，标题栏里没必要显示。
        var plusIndex = version.IndexOf('+');
        if (plusIndex > 0)
        {
            version = version[..plusIndex];
        }

        return $"{baseTitle} v{version}";
    }
}
