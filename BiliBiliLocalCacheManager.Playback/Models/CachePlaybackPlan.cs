using System.Collections.ObjectModel;

namespace BiliBiliLocalCacheManager.Playback.Models;

public sealed class CachePlaybackPlan
{
    private readonly ReadOnlyCollection<string> _mediaFiles;

    private CachePlaybackPlan(
        long avid,
        string title,
        int pageIndex,
        string partName,
        string segmentName,
        string segmentDirectory,
        string structureKind,
        CachePlaybackMaterialKind materialKind,
        IReadOnlyList<string> mediaFiles,
        bool isPlayable,
        string? message,
        TimeSpan duration)
    {
        Avid = avid;
        Title = title;
        PageIndex = pageIndex;
        PartName = partName;
        SegmentName = segmentName;
        SegmentDirectory = segmentDirectory;
        StructureKind = structureKind;
        MaterialKind = materialKind;
        _mediaFiles = new ReadOnlyCollection<string>(mediaFiles.ToList());
        IsPlayable = isPlayable;
        Message = message;
        Duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }

    public long Avid { get; }

    public string Title { get; }

    public int PageIndex { get; }

    public string PartName { get; }

    public string SegmentName { get; }

    public string SegmentDirectory { get; }

    public string StructureKind { get; }

    public CachePlaybackMaterialKind MaterialKind { get; }

    public IReadOnlyList<string> MediaFiles => _mediaFiles;

    public bool IsPlayable { get; }

    public string? Message { get; }
    public TimeSpan Duration { get; }

    public bool RequiresFfmpegPreparation =>
        IsPlayable &&
        MaterialKind switch
        {
            CachePlaybackMaterialKind.DashPair or CachePlaybackMaterialKind.OrderedPair => true,
            CachePlaybackMaterialKind.SingleFile =>
                _mediaFiles.Count > 0 &&
                !string.Equals(Path.GetExtension(_mediaFiles[0]), ".mp4", StringComparison.OrdinalIgnoreCase),
            _ => false
        };


    public static CachePlaybackPlan Playable(
        long avid,
        string title,
        int pageIndex,
        string partName,
        string segmentName,
        string segmentDirectory,
        string structureKind,
        CachePlaybackMaterialKind materialKind,
        IEnumerable<string> mediaFiles,
        string? message = null,
        TimeSpan? duration = null)
    {
        ArgumentNullException.ThrowIfNull(mediaFiles);

        var fileList = mediaFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PlaybackFileSystem.PathComparer)
            .ToList();

        if (fileList.Count == 0)
        {
            throw new ArgumentException("Playable plan must contain at least one media file.", nameof(mediaFiles));
        }

        return new CachePlaybackPlan(
            avid,
            title,
            pageIndex,
            partName,
            segmentName,
            segmentDirectory,
            structureKind,
            materialKind,
            fileList,
            isPlayable: true,
            message,
            duration.GetValueOrDefault());
    }

    public static CachePlaybackPlan Unavailable(
        long avid,
        string title,
        int pageIndex,
        string partName,
        string segmentName,
        string segmentDirectory,
        string structureKind,
        string message,
        TimeSpan? duration = null)
    {
        return new CachePlaybackPlan(
            avid,
            title,
            pageIndex,
            partName,
            segmentName,
            segmentDirectory,
            structureKind,
            CachePlaybackMaterialKind.Unavailable,
            Array.Empty<string>(),
            isPlayable: false,
            message,
            duration.GetValueOrDefault());
    }

    public CachePlaybackPlan WithMessage(string? message)
    {
        if (string.Equals(Message, message, StringComparison.Ordinal))
        {
            return this;
        }

        return new CachePlaybackPlan(
            Avid,
            Title,
            PageIndex,
            PartName,
            SegmentName,
            SegmentDirectory,
            StructureKind,
            MaterialKind,
            _mediaFiles,
            IsPlayable,
            message,
            Duration);
    }
}
