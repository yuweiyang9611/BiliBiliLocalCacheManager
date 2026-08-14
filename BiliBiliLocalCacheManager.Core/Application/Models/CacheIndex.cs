using System.Collections.ObjectModel;
using BiliBiliLocalCacheManager.Core.Domain.Models;

namespace BiliBiliLocalCacheManager.Core.Application.Models;

/// <summary>
/// 完整缓存索引：包含根目录下扫描到的所有 B 站缓存（按 avid 聚合）。
/// </summary>
public sealed class CacheIndex
{
    private readonly ReadOnlyCollection<BiliVideoCache> _videoCaches;
    private readonly ReadOnlyDictionary<long, BiliVideoCache> _byAvid;

    public IReadOnlyCollection<BiliVideoCache> VideoCaches => _videoCaches;

    /// <summary>
    /// 通过 avid 快速索引对应的缓存。
    /// </summary>
    public IReadOnlyDictionary<long, BiliVideoCache> ByAvid => _byAvid;

    public CacheIndex(IEnumerable<BiliVideoCache> videoCaches)
    {
        ArgumentNullException.ThrowIfNull(videoCaches);

        var list = videoCaches.ToList();
        _videoCaches = new ReadOnlyCollection<BiliVideoCache>(list);
        _byAvid = new ReadOnlyDictionary<long, BiliVideoCache>(
            list.ToDictionary(v => v.Avid, v => v)
        );
    }

    /// <summary>
    /// Search by title/keyword with a simple default configuration.
    /// This keeps the call-site minimal while delegating logic to the options-based search.
    /// </summary>
    public IReadOnlyCollection<BiliVideoCache> SearchByTitle(
        string keyword,
        bool includeSegmentTitles = true,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var options = new CacheSearchOptions
        {
            Keyword = keyword,
            MatchMode = CacheSearchMatchMode.Contains,
            CaseSensitive = comparison == StringComparison.Ordinal,
            Scope = includeSegmentTitles
                ? CacheSearchScope.Title | CacheSearchScope.PartName
                : CacheSearchScope.Title,
            SplitKeywords = false,
            RequireAllKeywords = true
        };

        return Search(options);
    }

    /// <summary>
    /// Search with rich options:
    /// - supports multiple keywords
    /// - supports different match modes (contains/equals/starts/ends)
    /// - can target specific fields (title, part name, owner, bvid, avid)
    /// - can be case-sensitive or case-insensitive
    /// </summary>
    public IReadOnlyCollection<BiliVideoCache> Search(CacheSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Validate and normalize keyword input early to avoid silent "match everything" bugs.
        if (string.IsNullOrWhiteSpace(options.Keyword))
        {
            throw new ArgumentException("Keyword must not be null or empty.", nameof(options));
        }

        var comparison = options.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        // Split keywords if requested; otherwise treat the whole keyword as a single token.
        var tokens = options.SplitKeywords
            ? options.Keyword.Split(options.KeywordSeparators, StringSplitOptions.RemoveEmptyEntries)
            : new[] { options.Keyword };

        // If the input is all whitespace or separators, tokens can still be empty.
        if (tokens.Length == 0)
        {
            throw new ArgumentException("Keyword must contain at least one searchable token.", nameof(options));
        }

        // Local helper that applies the selected match mode on a candidate string.
        static bool Matches(string candidate, string token, CacheSearchMatchMode mode, StringComparison cmp)
        {
            return mode switch
            {
                CacheSearchMatchMode.Contains => candidate.Contains(token, cmp),
                CacheSearchMatchMode.Equals => string.Equals(candidate, token, cmp),
                CacheSearchMatchMode.StartsWith => candidate.StartsWith(token, cmp),
                CacheSearchMatchMode.EndsWith => candidate.EndsWith(token, cmp),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown match mode.")
            };
        }

        // Local helper that checks a single token against a single cache.
        bool MatchesToken(BiliVideoCache cache, string token)
        {
            // Title matching: the primary video title.
            if (options.Scope.HasFlag(CacheSearchScope.Title) &&
                Matches(cache.Title, token, options.MatchMode, comparison))
            {
                return true;
            }

            // Part name matching: any segment part name in the cache.
            if (options.Scope.HasFlag(CacheSearchScope.PartName) &&
                cache.Segments.Any(seg => Matches(seg.PartName, token, options.MatchMode, comparison)))
            {
                return true;
            }

            // Owner name matching: optional field, skip if null.
            if (options.Scope.HasFlag(CacheSearchScope.OwnerName) &&
                cache.OwnerName is not null &&
                Matches(cache.OwnerName, token, options.MatchMode, comparison))
            {
                return true;
            }

            // Bvid matching: optional field, skip if null.
            if (options.Scope.HasFlag(CacheSearchScope.Bvid) &&
                cache.Bvid is not null &&
                Matches(cache.Bvid, token, options.MatchMode, comparison))
            {
                return true;
            }

            // Avid matching: numeric field, compared as invariant string for consistency.
            if (options.Scope.HasFlag(CacheSearchScope.Avid))
            {
                var avidText = cache.Avid.ToString();
                if (Matches(avidText, token, options.MatchMode, comparison))
                {
                    return true;
                }
            }

            return false;
        }

        // Evaluate tokens with AND/OR semantics.
        var matched = _videoCaches.Where(cache =>
        {
            if (options.RequireAllKeywords)
            {
                // Every token must match at least one field in the cache.
                return tokens.All(token => MatchesToken(cache, token));
            }

            // At least one token must match.
            return tokens.Any(token => MatchesToken(cache, token));
        }).ToList();

        return new ReadOnlyCollection<BiliVideoCache>(matched);
    }
}
