namespace BiliBiliLocalCacheManager.Wpf.Models;

public sealed class SegmentDetailItem
{
    public long Avid { get; init; }

    public string SegmentKey { get; init; } = string.Empty;

    public int PageIndex { get; init; }

    public string PartName { get; init; } = string.Empty;

    public string StructureKind { get; init; } = string.Empty;

    public string MaterialKind { get; init; } = string.Empty;

    public string SizeMb { get; init; } = string.Empty;

    public string Duration { get; init; } = string.Empty;

    public string IsPlayable { get; init; } = string.Empty;

    public string DirectoryPath { get; init; } = string.Empty;
}
