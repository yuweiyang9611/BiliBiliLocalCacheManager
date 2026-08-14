using System.Globalization;
using BiliBiliLocalCacheManager.Cli.Commands;
using BiliBiliLocalCacheManager.Core.Infrastructure.Management;
using Xunit;

namespace BiliBiliLocalCacheManager.Cli.Tests;

/// <summary>
/// CLI 的删除必须与图形界面一样安全：默认进回收站、可还原、
/// 永久删除需显式 --permanent，非交互环境下没有 --yes 一律不执行。
/// </summary>
public sealed class CliDeleteSafetyTests
{
    [Fact]
    public void Delete_ShouldMoveToTrash_InsteadOfDeletingPermanently()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 100);
            var cacheDirectory = Path.Combine(root, "100");
            Assert.True(Directory.Exists(cacheDirectory));

            var exitCode = new DeleteCommand().Execute(["100", "--root", root, "--yes"]);

            Assert.Equal(0, exitCode);
            Assert.False(Directory.Exists(cacheDirectory));

            // 关键：内容仍在回收站里，可以找回来。
            var entries = new FileSystemCacheTrashService().ListEntries(root);
            var entry = Assert.Single(entries);
            Assert.Equal(100, entry.Avid);
            Assert.True(entry.IsRestorable);
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void Delete_ShouldDoNothing_WhenNotConfirmedInNonInteractiveShell()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 100);
            var cacheDirectory = Path.Combine(root, "100");

            // 测试进程不是交互式终端，没有 --yes 时必须拒绝执行。
            var exitCode = new DeleteCommand().Execute(["100", "--root", root]);

            Assert.Equal(0, exitCode);
            Assert.True(
                Directory.Exists(cacheDirectory),
                "未确认时缓存目录不应被动过。");
            Assert.Empty(new FileSystemCacheTrashService().ListEntries(root));
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void Delete_ShouldDeletePermanently_OnlyWithExplicitFlag()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 100);
            var cacheDirectory = Path.Combine(root, "100");

            var exitCode = new DeleteCommand().Execute(
                ["100", "--root", root, "--permanent", "--yes"]);

            Assert.Equal(0, exitCode);
            Assert.False(Directory.Exists(cacheDirectory));
            // 永久删除不进回收站。
            Assert.Empty(new FileSystemCacheTrashService().ListEntries(root));
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void Delete_DryRun_ShouldNotTouchAnything()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 100);
            var cacheDirectory = Path.Combine(root, "100");

            var exitCode = new DeleteCommand().Execute(["100", "--root", root, "--dry-run"]);

            Assert.Equal(0, exitCode);
            Assert.True(Directory.Exists(cacheDirectory));
            Assert.Empty(new FileSystemCacheTrashService().ListEntries(root));
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void Delete_ShouldAcceptAvPrefixedId()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 170001);

            var exitCode = new DeleteCommand().Execute(["av170001", "--root", root, "--yes"]);

            Assert.Equal(0, exitCode);
            Assert.False(Directory.Exists(Path.Combine(root, "170001")));
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void TrashRestore_ShouldBringTheCacheBack()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 100);
            var cacheDirectory = Path.Combine(root, "100");

            Assert.Equal(0, new DeleteCommand().Execute(["100", "--root", root, "--yes"]));
            Assert.False(Directory.Exists(cacheDirectory));

            var exitCode = new TrashCommand().Execute(["restore", "100", "--root", root]);

            Assert.Equal(0, exitCode);
            Assert.True(Directory.Exists(cacheDirectory));
            Assert.True(File.Exists(Path.Combine(cacheDirectory, "c_1", "entry.json")));
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void TrashRestore_ShouldFail_WhenNothingMatches()
    {
        var root = CreateTempRoot();
        try
        {
            var exitCode = new TrashCommand().Execute(["restore", "999", "--root", root]);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void TrashList_ShouldSucceed_OnEmptyTrash()
    {
        var root = CreateTempRoot();
        try
        {
            Assert.Equal(0, new TrashCommand().Execute(["list", "--root", root]));
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void TrashPurge_ShouldRequireConfirmation_InNonInteractiveShell()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 100);
            Assert.Equal(0, new DeleteCommand().Execute(["100", "--root", root, "--yes"]));
            Assert.Single(new FileSystemCacheTrashService().ListEntries(root));

            // 没有 --yes：非交互环境下应当取消，回收站内容保持不变。
            Assert.Equal(0, new TrashCommand().Execute(["purge", "--root", root]));
            Assert.Single(new FileSystemCacheTrashService().ListEntries(root));

            Assert.Equal(0, new TrashCommand().Execute(["purge", "--root", root, "--yes"]));
            Assert.Empty(new FileSystemCacheTrashService().ListEntries(root));
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void Trash_ShouldReportUnknownSubCommand()
    {
        Assert.Equal(1, new TrashCommand().Execute(["lst", "--root", "x"]));
        Assert.Equal(1, new TrashCommand().Execute([]));
        Assert.Equal(0, new TrashCommand().Execute(["--help"]));
    }

    [Fact]
    public void TrashStats_ShouldSucceed()
    {
        var root = CreateTempRoot();
        try
        {
            Assert.Equal(0, new TrashCommand().Execute(["stats", "--root", root]));
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Theory]
    [InlineData("serach", "search")]
    [InlineData("dlete", "delete")]
    [InlineData("tras", "trash")]
    public void CommandSuggestion_ShouldFindTheIntendedCommand(string typo, string expected)
    {
        Assert.Equal(expected, CommandSuggestion.FindClosest(typo, UnknownCommand.KnownCommands));
    }

    [Fact]
    public void CommandSuggestion_ShouldReturnNull_WhenNothingIsClose()
    {
        Assert.Null(CommandSuggestion.FindClosest("zzzzzzzz", UnknownCommand.KnownCommands));
    }

    [Theory]
    [InlineData("170001", 170001L)]
    [InlineData("av170001", 170001L)]
    [InlineData("AV170001", 170001L)]
    [InlineData(" 170001 ", 170001L)]
    public void AvidParser_ShouldAcceptCommonForms(string input, long expected)
    {
        Assert.True(AvidParser.TryParse(input, out var avid));
        Assert.Equal(expected, avid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("BV1xx411c7mD")]
    public void AvidParser_ShouldRejectInvalidInput(string input)
    {
        Assert.False(AvidParser.TryParse(input, out _));
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "blcm-cli-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateEntry(string root, long avid)
    {
        var segmentDirectory = Path.Combine(
            root,
            avid.ToString(CultureInfo.InvariantCulture),
            "c_1");
        Directory.CreateDirectory(segmentDirectory);
        File.WriteAllText(Path.Combine(segmentDirectory, "1.mp4"), "dummy");
        File.WriteAllText(
            Path.Combine(segmentDirectory, "entry.json"),
            $$"""
              {
                "is_completed": true,
                "total_bytes": 5,
                "downloaded_bytes": 5,
                "title": "CLI 删除测试",
                "type_tag": "80",
                "cover": "cover",
                "prefered_video_quality": 80,
                "total_time_milli": 1000,
                "danmaku_count": 0,
                "avid": {{avid}},
                "page_data": { "cid": 1, "page": 1, "part": "P1" }
              }
              """);
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
