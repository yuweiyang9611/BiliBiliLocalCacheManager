using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

internal static class LegacyLayoutPlanBuilder
{
    public static CachePlaybackPlan Build(CachePlaybackProbe probe, bool isHybrid)
    {
        var segment = probe.Segment;
        var luaDirectoryNames = probe.ChildDirectoryNames
            .Where(name => name.StartsWith("lua.", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var blvFiles = luaDirectoryNames
            .SelectMany(name => PlaybackPathHelpers.GetFilesUnderDirectory(probe, name, ".blv"))
            .ToList();
        var mp4Files = luaDirectoryNames
            .SelectMany(name => PlaybackPathHelpers.GetFilesUnderDirectory(probe, name, ".mp4"))
            .ToList();

        if (blvFiles.Count == 0 && mp4Files.Count == 0)
        {
            return CachePlaybackPlan.Unavailable(
                segment.Avid,
                segment.Title,
                segment.PageIndex,
                segment.PartName,
                probe.SegmentName,
                segment.SegmentDirectory,
                isHybrid ? "HybridCLegacy" : "Legacy",
                "未找到可播放的 blv/mp4 媒体文件。");
        }

        if (blvFiles.Count > 0 && mp4Files.Count > 0)
        {
            var selectedFiles = ChoosePreferredVariant(blvFiles, mp4Files, out var selectedKind);
            return CreatePlayablePlan(
                probe,
                "LegacyMixed",
                selectedFiles,
                selectedKind,
                $"检测到 mixed 缓存，已按规则选择 {selectedKind} 变体。");
        }

        if (blvFiles.Count > 0)
        {
            return CreatePlayablePlan(probe, isHybrid ? "HybridCLegacy" : "LegacyBlv", blvFiles, ".blv", null);
        }

        return CreatePlayablePlan(probe, isHybrid ? "HybridCLegacy" : "LegacyMp4", mp4Files, ".mp4", null);
    }

    private static IReadOnlyList<string> ChoosePreferredVariant(
        IReadOnlyList<string> blvFiles,
        IReadOnlyList<string> mp4Files,
        out string selectedKind)
    {
        var blvBytes = PlaybackPathHelpers.GetTotalBytes(blvFiles);
        var mp4Bytes = PlaybackPathHelpers.GetTotalBytes(mp4Files);

        if (blvBytes >= mp4Bytes)
        {
            selectedKind = ".blv";
            return blvFiles;
        }

        selectedKind = ".mp4";
        return mp4Files;
    }

    private static CachePlaybackPlan CreatePlayablePlan(
        CachePlaybackProbe probe,
        string structureKind,
        IReadOnlyList<string> files,
        string selectedKind,
        string? message)
    {
        var segment = probe.Segment;
        var materialKind = files.Count == 1
            ? CachePlaybackMaterialKind.SingleFile
            : CachePlaybackMaterialKind.OrderedPair;

        var finalMessage = structureKind == "LegacyMixed"
            ? message
            : message;

        return CachePlaybackPlan.Playable(
            segment.Avid,
            segment.Title,
            segment.PageIndex,
            segment.PartName,
            probe.SegmentName,
            segment.SegmentDirectory,
            structureKind,
            materialKind,
            files,
            finalMessage,
            segment.TotalDuration);
    }
}
