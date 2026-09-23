namespace BiliBiliLocalCacheManager.Core.Application.Models;

/// <summary>A source identity captured by the trusted index, never supplied by the renderer.</summary>
public sealed class CacheTrashPartTarget
{
    internal CacheTrashPartTarget(long avid, int pageIndex, string sourcePath, string entryHash,
        string directoryIdentity)
    {
        Avid = avid;
        PageIndex = pageIndex;
        SourcePath = sourcePath;
        EntryHash = entryHash;
        DirectoryIdentity = directoryIdentity;
    }

    public long Avid { get; }
    public int PageIndex { get; }
    public string SourcePath { get; }
    internal string EntryHash { get; }
    internal string DirectoryIdentity { get; }
}
