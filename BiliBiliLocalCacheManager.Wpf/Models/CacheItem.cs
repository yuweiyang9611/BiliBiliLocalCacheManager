namespace BiliBiliLocalCacheManager.Wpf.Models;

/// <summary>
/// DataGrid 展示用的轻量视图模型（避免直接绑定领域对象）。
/// </summary>
public sealed class CacheItem : System.ComponentModel.INotifyPropertyChanged
{
    public long Avid { get; init; }
    public string Title { get; init; } = string.Empty;

    /// <summary>UP 主名称。旧版缓存可能缺失，此时为空字符串。</summary>
    public string OwnerName { get; init; } = string.Empty;

    /// <summary>BV 号。旧版缓存可能缺失，此时为空字符串。</summary>
    public string Bvid { get; init; } = string.Empty;

    /// <summary>总时长的展示文本，未知时为空字符串。</summary>
    public string Duration { get; init; } = string.Empty;

    public TimeSpan DurationValue { get; init; }

    public int SegmentCount { get; init; }
    public string SizeMb { get; init; } = string.Empty;
    public double SizeMbValue { get; init; }
    public long SizeBytes { get; init; }
    public string IsAllCompleted { get; init; } = string.Empty;
    public bool IsAllCompletedValue { get; init; }
    public string LastUpdated { get; init; } = string.Empty;
    public DateTimeOffset LastUpdatedValue { get; init; }

    private bool _isSelected;

    /// <summary>
    /// 是否被选中（用于 DataGrid 勾选框与行选中状态同步）。
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
