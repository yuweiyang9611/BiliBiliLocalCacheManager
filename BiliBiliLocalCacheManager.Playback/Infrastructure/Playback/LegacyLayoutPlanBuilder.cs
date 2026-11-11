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

        var blvFiles = SelectVariant(".blv");
        var mp4Files = SelectVariant(".mp4");

        IReadOnlyList<string> SelectVariant(string extension) => luaDirectoryNames
            .Select(name => (Name: name, Files: PlaybackPathHelpers.GetFilesUnderDirectory(probe, name, extension)))
            .Where(candidate => candidate.Files.Count > 0)
            .OrderByDescending(candidate => string.Equals(candidate.Name, segment.TypeTag, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(candidate => int.TryParse(candidate.Name.AsSpan(candidate.Name.LastIndexOf('.') + 1), out var quality)
                && quality == segment.VideoQuality)
            .ThenByDescending(candidate => PlaybackPathHelpers.GetTotalBytes(candidate.Files))
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .Select(candidate => candidate.Files)
            .FirstOrDefault() ?? [];

        if (blvFiles.Count == 0 && mp4Files.Count == 0)
        {
            var dash = DashLayoutPlanBuilder.Build(probe, isHybrid ? "NewDash" : "MidDash");
            if (dash.IsPlayable) return dash;
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
            message,
            segment.TotalDuration);
    }
}
