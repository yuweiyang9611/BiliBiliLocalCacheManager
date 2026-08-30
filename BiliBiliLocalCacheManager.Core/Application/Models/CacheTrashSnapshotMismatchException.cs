namespace BiliBiliLocalCacheManager.Core.Application.Models;

/// <summary>
/// 永久清理开始时，回收站的完整条目快照与调用方确认的快照不一致。
/// </summary>
public sealed class CacheTrashSnapshotMismatchException : InvalidOperationException
{
    public CacheTrashSnapshotMismatchException(int expectedEntryCount, int actualEntryCount)
        : base(
            "The application trash changed after confirmation. " +
            "Reload the trash entries and confirm permanent deletion again.")
    {
        ExpectedEntryCount = expectedEntryCount;
        ActualEntryCount = actualEntryCount;
    }

    public int ExpectedEntryCount { get; }

    public int ActualEntryCount { get; }
}
