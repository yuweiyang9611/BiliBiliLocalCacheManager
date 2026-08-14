using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Contracts;

public interface IFfmpegDiagnosticsProvider
{
    FfmpegDiagnosticSnapshot GetSnapshot();
}
