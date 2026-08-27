using System.Collections.ObjectModel;
using BiliBiliLocalCacheManager.Core.Domain.Models;

namespace BiliBiliLocalCacheManager.Playback.Models;

public sealed class CachePlaybackProbe
{
    private readonly ReadOnlyCollection<string> _rootFiles;
    private readonly ReadOnlyCollection<string> _childDirectories;
    private readonly ReadOnlyCollection<string> _childDirectoryNames;
    private readonly ReadOnlyCollection<string> _nestedFiles;

    public CachePlaybackProbe(
        BiliSegment segment,
        IEnumerable<string> rootFiles,
        IEnumerable<string> childDirectories,
        IEnumerable<string> nestedFiles)
    {
        Segment = segment ?? throw new ArgumentNullException(nameof(segment));

        var childDirectoryList = (childDirectories ?? Enumerable.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PlaybackFileSystem.PathComparer)
            .ToList();

        _rootFiles = new ReadOnlyCollection<string>(
            (rootFiles ?? Enumerable.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PlaybackFileSystem.PathComparer)
            .ToList());
        _childDirectories = new ReadOnlyCollection<string>(childDirectoryList);
        _childDirectoryNames = new ReadOnlyCollection<string>(
            childDirectoryList.Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)).ToList()!);
        _nestedFiles = new ReadOnlyCollection<string>(
            (nestedFiles ?? Enumerable.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PlaybackFileSystem.PathComparer)
            .ToList());
    }

    public BiliSegment Segment { get; }

    public string SegmentDirectory => Segment.SegmentDirectory;

    public string SegmentName => Path.GetFileName(Segment.SegmentDirectory);

    public IReadOnlyList<string> RootFiles => _rootFiles;

    public IReadOnlyList<string> ChildDirectories => _childDirectories;

    public IReadOnlyList<string> ChildDirectoryNames => _childDirectoryNames;

    public IReadOnlyList<string> NestedFiles => _nestedFiles;
}
