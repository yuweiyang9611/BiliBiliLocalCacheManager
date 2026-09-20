using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;

namespace BiliBiliLocalCacheManager.Desktop.Host;

internal sealed partial class DesktopHostApplication
{
    private CurrentIndexSnapshot? _indexSnapshot;

    private long ReadIndexGeneration() { lock (_stateSync) return _indexGeneration; }

    private void EnsureIndexGeneration(long generation)
    {
        lock (_stateSync) { if (generation != _indexGeneration) throw StaleIndexException(); }
    }

    private object GetScanIssueLocation(JsonElement parameters)
    {
        var snapshot = ResolveCurrentIndex(RequireIndexToken(parameters));
        var id = parameters.OptionalInt32("issueId") ?? -1;
        if (id < 0 || id >= snapshot.Issues.Count) throw new RpcException("invalid_params", "Unknown scan issue.");
        var root = Path.TrimEndingDirectorySeparator(snapshot.Root);
        var issuePath = Path.GetFullPath(snapshot.Issues[id].Path);
        if (!PathsEqual(issuePath, root) && !issuePath.StartsWith(root + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new RpcException("unsafe_path", "The scan issue is outside the current root.");
        // Only traverse existing physical ancestors; links and inaccessible targets resolve to their safe parent.
        var location = root;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new RpcException("unsafe_path", "The cache root is no longer a physical directory.");
        foreach (var part in Path.GetRelativePath(root, issuePath).Split(Path.DirectorySeparatorChar))
        {
            if (part == ".") break;
            var candidate = Path.Combine(location, part);
            try
            {
                var attributes = File.GetAttributes(candidate);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) break;
                location = candidate;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { break; }
        }
        EnsureIndexStillCurrent(snapshot);
        return new { path = location };
    }

    internal sealed class CurrentIndexSnapshot
    {
        private readonly object _sync = new();
        private readonly LinkedList<(string Key, IReadOnlyCollection<BiliVideoCache> Values)> _queries = new();
        private readonly LinkedList<(long Avid, DetailCache Cache)> _details = new();
        private bool _invalid;
        internal int SearchExecutions { get; private set; }
        internal int DetailSortExecutions { get; private set; }
        public CacheIndex Index { get; }
        public string IndexToken { get; }
        public string Root { get; }
        public IReadOnlyList<CacheScanIssue> Issues { get; }
        public IReadOnlyDictionary<long, CacheSummaryDto> Summaries { get; }

        public CurrentIndexSnapshot(CacheIndex index, string token, string root, IReadOnlyList<CacheScanIssue> issues)
        {
            Index = index;
            IndexToken = token;
            Root = root;
            Issues = issues.Take(100).ToArray();
            Summaries = index.VideoCaches.ToDictionary(cache => cache.Avid, MapCacheSummary);
        }

        public IReadOnlyCollection<BiliVideoCache> Search(CacheSearchOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(options.Keyword))
            {
                lock (_sync) { if (_invalid) throw StaleIndexException(); return Index.VideoCaches; }
            }
            var key = JsonSerializer.Serialize(new { options.Keyword, options.MatchMode, options.Scope,
                options.CaseSensitive, options.SplitKeywords, options.RequireAllKeywords, options.KeywordSeparators });
            lock (_sync)
            {
                if (_invalid) throw StaleIndexException();
                var node = _queries.First;
                while (node is not null)
                {
                    if (node.Value.Key == key)
                    {
                        _queries.Remove(node);
                        _queries.AddFirst(node);
                        return node.Value.Values;
                    }
                    node = node.Next;
                }
            }
            var matches = Index.Search(options, cancellationToken);
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_invalid) throw StaleIndexException();
                SearchExecutions++;
                var duplicate = _queries.First;
                while (duplicate is not null)
                {
                    var next = duplicate.Next;
                    if (duplicate.Value.Key == key) _queries.Remove(duplicate);
                    duplicate = next;
                }
                _queries.AddFirst((key, matches));
                if (_queries.Count > 8) _queries.RemoveLast();
                return matches;
            }
        }

        public void Invalidate()
        {
            lock (_sync) { _invalid = true; _queries.Clear(); _details.Clear(); }
        }

        public CacheDetailsDto GetDetailsPage(BiliVideoCache cache, int offset, int pageSize,
            Func<BiliSegment, SegmentDto> map, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DetailCache details;
            lock (_sync)
            {
                if (_invalid) throw StaleIndexException();
                var node = _details.First;
                while (node is not null && node.Value.Avid != cache.Avid) node = node.Next;
                if (node is not null)
                {
                    details = node.Value.Cache;
                    _details.Remove(node);
                    _details.AddFirst(node);
                }
                else
                {
                    details = new DetailCache(cache.Segments.OrderBy(segment => segment.PageIndex)
                        .ThenBy(segment => segment.SegmentDirectory, PathComparer).ToArray());
                    DetailSortExecutions++;
                    _details.AddFirst((cache.Avid, details));
                    if (_details.Count > 8) _details.RemoveLast();
                }
            }
            var segments = details.GetPage(offset, pageSize, map, cancellationToken);
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_invalid) throw StaleIndexException();
            }
            return new CacheDetailsDto(IndexToken, cache.Avid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Summaries[cache.Avid], offset, pageSize, details.Count, HasMore(offset, pageSize, details.Count), segments);
        }

        private sealed class DetailCache(BiliSegment[] orderedSegments)
        {
            private readonly object _sync = new();
            private readonly LinkedList<(int Offset, int Size, SegmentDto[] Segments)> _pages = new();
            public int Count => orderedSegments.Length;

            public SegmentDto[] GetPage(int offset, int size, Func<BiliSegment, SegmentDto> map, CancellationToken token)
            {
                lock (_sync)
                {
                    token.ThrowIfCancellationRequested();
                    var node = _pages.First;
                    while (node is not null)
                    {
                        if (node.Value.Offset == offset && node.Value.Size == size)
                        {
                            _pages.Remove(node);
                            _pages.AddFirst(node);
                            return node.Value.Segments;
                        }
                        node = node.Next;
                    }
                    var page = orderedSegments.Skip(offset).Take(size).Select(segment =>
                    {
                        token.ThrowIfCancellationRequested();
                        return map(segment);
                    }).ToArray();
                    token.ThrowIfCancellationRequested();
                    _pages.AddFirst((offset, size, page));
                    if (_pages.Count > 8) _pages.RemoveLast();
                    return page;
                }
            }
        }
    }
}
