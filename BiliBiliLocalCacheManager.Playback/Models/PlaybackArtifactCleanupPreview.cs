namespace BiliBiliLocalCacheManager.Playback.Models;

public sealed record PlaybackArtifactCleanupPreview(
    int CandidateFileCount,
    long ReclaimableBytes,
    long RemainingBytes);
