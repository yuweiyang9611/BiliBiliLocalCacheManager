namespace BiliBiliLocalCacheManager.Core.Application.Models;

/// <summary>
/// Match mode for keyword search.
/// </summary>
public enum CacheSearchMatchMode
{
    Contains,
    Equals,
    StartsWith,
    EndsWith
}

/// <summary>
/// Search scope flags to control which fields are considered.
/// </summary>
[Flags]
public enum CacheSearchScope
{
    None = 0,
    Title = 1 << 0,
    PartName = 1 << 1,
    OwnerName = 1 << 2,
    Bvid = 1 << 3,
    Avid = 1 << 4
}

/// <summary>
/// Options for searching cache index by keyword(s).
/// </summary>
public sealed class CacheSearchOptions
{
    /// <summary>
    /// The raw keyword text provided by the caller.
    /// </summary>
    public string Keyword { get; set; } = string.Empty;

    /// <summary>
    /// When true, Keyword will be split into multiple tokens and searched individually.
    /// When false, the whole Keyword is treated as a single token.
    /// </summary>
    public bool SplitKeywords { get; set; } = true;

    /// <summary>
    /// Token separators used when SplitKeywords is true.
    /// </summary>
    public char[] KeywordSeparators { get; set; } = { ' ', '\t', '\r', '\n' };

    /// <summary>
    /// When true, all tokens must match (logical AND).
    /// When false, any token match is enough (logical OR).
    /// </summary>
    public bool RequireAllKeywords { get; set; } = true;

    /// <summary>
    /// The matching rule to apply to each field.
    /// </summary>
    public CacheSearchMatchMode MatchMode { get; set; } = CacheSearchMatchMode.Contains;

    /// <summary>
    /// If true, matching is case-sensitive; otherwise it is case-insensitive.
    /// </summary>
    public bool CaseSensitive { get; set; } = false;

    /// <summary>
    /// Which fields are included in the search.
    /// </summary>
    public CacheSearchScope Scope { get; set; } =
        CacheSearchScope.Title | CacheSearchScope.PartName;
}
