using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Playback.Services;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackReparsePointSafetyTests
{
    [Fact]
    public async Task CreatePlan_ShouldNotFollowLoopOrUseExternalMediaFromLinkedChild()
    {
        var root = CreateTempRoot("playback-root");
        var external = CreateTempRoot("playback-external");
        var segmentDirectory = Path.Combine(root, "100", "c_1");
        var qualityLink = Path.Combine(segmentDirectory, "80");
        var loopLink = Path.Combine(segmentDirectory, "loop");

        try
        {
            Directory.CreateDirectory(segmentDirectory);
            File.WriteAllText(Path.Combine(external, "video.m4s"), "external-video");
            File.WriteAllText(Path.Combine(external, "audio.m4s"), "external-audio");
            if (!TryCreateDirectoryLink(qualityLink, external) ||
                !TryCreateDirectoryLink(loopLink, segmentDirectory))
            {
                return;
            }

            var task = Task.Run(() => new CachePlaybackService().CreatePlan(CreateSegment(segmentDirectory)));

            var completedTask = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(task, completedTask);
            var plan = await task;
            Assert.False(plan.IsPlayable);
            Assert.DoesNotContain(plan.MediaFiles, path => IsInside(external, path));
        }
        finally
        {
            DeleteDirectoryLink(loopLink);
            DeleteDirectoryLink(qualityLink);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(external);
        }
    }

    [Fact]
    public void CreatePlan_ShouldRejectLinkedSegmentRoot()
    {
        var root = CreateTempRoot("linked-segment-root");
        var externalSegment = CreateTempRoot("linked-segment-target");
        var segmentLink = Path.Combine(root, "100", "c_1");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(segmentLink)!);
            var quality = Path.Combine(externalSegment, "80");
            Directory.CreateDirectory(quality);
            File.WriteAllText(Path.Combine(quality, "video.m4s"), "external-video");
            File.WriteAllText(Path.Combine(quality, "audio.m4s"), "external-audio");
            if (!TryCreateDirectoryLink(segmentLink, externalSegment))
            {
                return;
            }

            var plan = new CachePlaybackService().CreatePlan(CreateSegment(segmentLink));

            Assert.False(plan.IsPlayable);
            Assert.Equal("UnsafePath", plan.StructureKind);
            Assert.Empty(plan.MediaFiles);
        }
        finally
        {
            DeleteDirectoryLink(segmentLink);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(externalSegment);
        }
    }

    private static BiliSegment CreateSegment(string directory)
    {
        return new BiliSegment(
            100,
            1,
            null,
            1,
            "P1",
            "Reparse test",
            CacheVersion.Modern,
            "80",
            null,
            80,
            null,
            true,
            1,
            1,
            TimeSpan.FromSeconds(1),
            0,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            directory,
            Path.Combine(directory, "entry.json"),
            Array.Empty<string>(),
            string.Empty,
            null,
            null);
    }

    private static bool IsInside(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(candidate));
        return relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or
                                       UnauthorizedAccessException or
                                       PlatformNotSupportedException or
                                       NotSupportedException)
        {
            DeleteDirectoryLink(linkPath);
            return false;
        }
    }

    private static string CreateTempRoot(string purpose)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bili_{purpose}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryLink(string path)
    {
        try
        {
            if (Directory.Exists(path) &&
                File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(path);
            }
        }
        catch
        {
            // Best effort: never recursively delete a link during test cleanup.
        }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) &&
                !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Test cleanup must not mask the safety assertion.
        }
    }
}
