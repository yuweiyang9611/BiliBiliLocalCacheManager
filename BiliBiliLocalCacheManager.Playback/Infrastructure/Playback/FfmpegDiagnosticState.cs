using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

internal sealed class FfmpegDiagnosticState
{
    private FfmpegDiagnosticSnapshot _snapshot = FfmpegDiagnosticSnapshot.NotInitialized;

    public FfmpegDiagnosticSnapshot GetSnapshot()
    {
        return Volatile.Read(ref _snapshot);
    }

    public void Publish(FfmpegDiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _snapshot, snapshot);
    }
}
