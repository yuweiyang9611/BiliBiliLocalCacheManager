using System;
using System.IO;
using System.Linq;
using BiliBiliLocalCacheManager.Wpf.Services;
using Xunit;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class ExportFileNamingTests
{
    [Fact]
    public void BuildBaseName_ShouldUseTitleOnly_ForSinglePageCache()
    {
        var name = ExportFileNaming.BuildBaseName(
            "普通视频标题",
            avid: 100,
            pageIndex: 1,
            partName: "P1",
            includePageSuffix: false);

        Assert.Equal("普通视频标题", name);
    }

    [Fact]
    public void BuildBaseName_ShouldAppendPageAndPart_ForMultiPageCache()
    {
        var name = ExportFileNaming.BuildBaseName(
            "系列教程",
            avid: 100,
            pageIndex: 3,
            partName: "第三讲",
            includePageSuffix: true);

        Assert.Equal("系列教程 - P3 第三讲", name);
    }

    [Fact]
    public void BuildBaseName_ShouldOmitPartName_WhenIdenticalToTitle()
    {
        var name = ExportFileNaming.BuildBaseName(
            "同名",
            avid: 100,
            pageIndex: 2,
            partName: "同名",
            includePageSuffix: true);

        Assert.Equal("同名 - P2", name);
    }

    [Fact]
    public void BuildBaseName_ShouldOmitPartName_WhenItIsJustThePageToken()
    {
        var name = ExportFileNaming.BuildBaseName(
            "教程",
            avid: 100,
            pageIndex: 1,
            partName: "P1",
            includePageSuffix: true);

        Assert.Equal("教程 - P1", name);
    }

    [Fact]
    public void BuildBaseName_ShouldFallBackToAvid_WhenTitleIsBlank()
    {
        Assert.Equal("av8888", ExportFileNaming.BuildBaseName(
            "   ",
            avid: 8888,
            pageIndex: 1,
            partName: null,
            includePageSuffix: false));
    }

    [Fact]
    public void BuildBaseName_ShouldFallBackToAvid_WhenTitleIsOnlyInvalidCharacters()
    {
        var name = ExportFileNaming.BuildBaseName(
            "///",
            avid: 4242,
            pageIndex: 1,
            partName: null,
            includePageSuffix: false);

        Assert.Equal("av4242", name);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    public void BuildBaseName_ShouldEscapeReservedDeviceNames(string reserved)
    {
        var name = ExportFileNaming.BuildBaseName(
            reserved,
            avid: 100,
            pageIndex: 1,
            partName: null,
            includePageSuffix: false);

        Assert.Equal("_" + reserved, name);
    }

    [Fact]
    public void BuildBaseName_ShouldProduceNameWithoutInvalidCharacters()
    {
        var name = ExportFileNaming.BuildBaseName(
            "a/b\\c:d*e?f\"g<h>i|j",
            avid: 100,
            pageIndex: 1,
            partName: null,
            includePageSuffix: false);

        Assert.All(
            Path.GetInvalidFileNameChars(),
            invalid => Assert.DoesNotContain(invalid, name));
        Assert.Equal("a b c d e f g h i j", name);
    }

    [Fact]
    public void BuildBaseName_ShouldCapLength_ToKeepPathsUsable()
    {
        var name = ExportFileNaming.BuildBaseName(
            new string('长', 400),
            avid: 100,
            pageIndex: 1,
            partName: null,
            includePageSuffix: false);

        Assert.True(name.Length <= 120, $"实际长度 {name.Length} 超过上限。");
    }

    [Fact]
    public void BuildBaseName_ShouldNotEndWithDotOrSpace()
    {
        // Windows 不允许文件名以点或空格结尾。
        var name = ExportFileNaming.BuildBaseName(
            "标题结尾有点.",
            avid: 100,
            pageIndex: 1,
            partName: null,
            includePageSuffix: false);

        Assert.False(name.EndsWith('.'));
        Assert.False(name.EndsWith(' '));
    }

    [Fact]
    public void Sanitize_ShouldCollapseRepeatedWhitespace()
    {
        Assert.Equal("a b", ExportFileNaming.Sanitize("  a \t\n b  "));
    }

    [Fact]
    public void EnsureUniquePath_ShouldReturnPlainName_WhenNoCollision()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = ExportFileNaming.EnsureUniquePath(directory, "视频", ".mp4");
            Assert.Equal(Path.Combine(directory, "视频.mp4"), path);
        }
        finally
        {
            SafeDelete(directory);
        }
    }

    [Fact]
    public void EnsureUniquePath_ShouldAppendCounter_WhenFileAlreadyExists()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "视频.mp4"), "x");
            File.WriteAllText(Path.Combine(directory, "视频 (2).mp4"), "x");

            var path = ExportFileNaming.EnsureUniquePath(directory, "视频", ".mp4");

            Assert.Equal(Path.Combine(directory, "视频 (3).mp4"), path);
        }
        finally
        {
            SafeDelete(directory);
        }
    }

    [Fact]
    public void EnsureUniquePath_ShouldNormalizeExtensionWithoutLeadingDot()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = ExportFileNaming.EnsureUniquePath(directory, "视频", "mp4");
            Assert.Equal(Path.Combine(directory, "视频.mp4"), path);
        }
        finally
        {
            SafeDelete(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "blcm-naming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
