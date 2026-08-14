using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Application.Services;
using Xunit;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class CacheSearchOptionsFactoryTests
{
    [Theory]
    [InlineData("contains", CacheSearchMatchMode.Contains)]
    [InlineData("Contains", CacheSearchMatchMode.Contains)]
    [InlineData("equals", CacheSearchMatchMode.Equals)]
    [InlineData("StartsWith", CacheSearchMatchMode.StartsWith)]
    [InlineData("endswith", CacheSearchMatchMode.EndsWith)]
    public void ParseMatchMode_ShouldParseValidValues(string value, CacheSearchMatchMode expected)
    {
        var result = CacheSearchOptionsFactory.ParseMatchMode(value);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown")]
    public void ParseMatchMode_ShouldThrow_WhenInvalid(string value)
    {
        Assert.Throws<ArgumentException>(() => CacheSearchOptionsFactory.ParseMatchMode(value));
    }

    [Fact]
    public void ParseScope_ShouldParseCommaSeparatedTokens()
    {
        var result = CacheSearchOptionsFactory.ParseScope("title,part,owner,bvid,avid");

        var expected = CacheSearchScope.Title
            | CacheSearchScope.PartName
            | CacheSearchScope.OwnerName
            | CacheSearchScope.Bvid
            | CacheSearchScope.Avid;

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("title,unknown")]
    public void ParseScope_ShouldThrow_WhenInvalid(string value)
    {
        Assert.Throws<ArgumentException>(() => CacheSearchOptionsFactory.ParseScope(value));
    }

    [Fact]
    public void BuildScope_ShouldIncludeTitleByDefault()
    {
        var result = CacheSearchOptionsFactory.BuildScope(
            includePartName: false,
            includeOwnerName: false,
            includeBvid: false,
            includeAvid: false);

        Assert.Equal(CacheSearchScope.Title, result);
    }

    [Fact]
    public void BuildScope_ShouldIncludeSelectedFlags()
    {
        var result = CacheSearchOptionsFactory.BuildScope(
            includePartName: true,
            includeOwnerName: true,
            includeBvid: false,
            includeAvid: true);

        var expected = CacheSearchScope.Title
            | CacheSearchScope.PartName
            | CacheSearchScope.OwnerName
            | CacheSearchScope.Avid;

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Create_ShouldApplySeparators_WhenProvided()
    {
        var separators = new[] { '|', ',' };

        var options = CacheSearchOptionsFactory.Create(
            keyword: "alpha",
            matchMode: CacheSearchMatchMode.Contains,
            caseSensitive: false,
            splitKeywords: true,
            requireAllKeywords: true,
            scope: CacheSearchScope.Title,
            keywordSeparators: separators);

        Assert.Equal(separators, options.KeywordSeparators);
    }
}
