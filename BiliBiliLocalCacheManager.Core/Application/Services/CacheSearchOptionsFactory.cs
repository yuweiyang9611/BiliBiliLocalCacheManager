using BiliBiliLocalCacheManager.Core.Application.Models;

namespace BiliBiliLocalCacheManager.Core.Application.Services;

/// <summary>
/// Helpers for building and parsing search options shared by CLI/WPF.
/// </summary>
public static class CacheSearchOptionsFactory
{
    public static CacheSearchOptions Create(
        string keyword,
        CacheSearchMatchMode matchMode,
        bool caseSensitive,
        bool splitKeywords,
        bool requireAllKeywords,
        CacheSearchScope scope,
        char[]? keywordSeparators = null)
    {
        var options = new CacheSearchOptions
        {
            Keyword = keyword,
            MatchMode = matchMode,
            CaseSensitive = caseSensitive,
            SplitKeywords = splitKeywords,
            RequireAllKeywords = requireAllKeywords,
            Scope = scope
        };

        if (keywordSeparators is { Length: > 0 })
        {
            options.KeywordSeparators = keywordSeparators;
        }

        return options;
    }

    public static CacheSearchScope BuildScope(
        bool includePartName,
        bool includeOwnerName,
        bool includeBvid,
        bool includeAvid)
    {
        var scope = CacheSearchScope.Title;

        if (includePartName)
        {
            scope |= CacheSearchScope.PartName;
        }

        if (includeOwnerName)
        {
            scope |= CacheSearchScope.OwnerName;
        }

        if (includeBvid)
        {
            scope |= CacheSearchScope.Bvid;
        }

        if (includeAvid)
        {
            scope |= CacheSearchScope.Avid;
        }

        return scope;
    }

    public static CacheSearchMatchMode ParseMatchMode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Match mode must not be empty.", nameof(value));
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "contains" => CacheSearchMatchMode.Contains,
            "equals" => CacheSearchMatchMode.Equals,
            "startswith" => CacheSearchMatchMode.StartsWith,
            "endswith" => CacheSearchMatchMode.EndsWith,
            _ => throw new ArgumentException($"Invalid match mode: {value}", nameof(value))
        };
    }

    public static CacheSearchScope ParseScope(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Scope value must not be empty.", nameof(value));
        }

        var tokens = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            throw new ArgumentException("Scope value must contain at least one token.", nameof(value));
        }

        var scope = CacheSearchScope.None;
        foreach (var token in tokens)
        {
            scope |= token.ToLowerInvariant() switch
            {
                "title" => CacheSearchScope.Title,
                "part" => CacheSearchScope.PartName,
                "owner" => CacheSearchScope.OwnerName,
                "bvid" => CacheSearchScope.Bvid,
                "avid" => CacheSearchScope.Avid,
                _ => throw new ArgumentException($"Invalid scope token: {token}", nameof(value))
            };
        }

        return scope;
    }
}
