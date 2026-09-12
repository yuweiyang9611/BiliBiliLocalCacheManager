namespace BiliBiliLocalCacheManager.Playback.Models;

public sealed record PlaybackQueueItem(string Path, string Title, TimeSpan Duration);
