using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed class BundledFfmpegDiagnosticsProvider : IFfmpegDiagnosticsProvider
{
    private readonly FfmpegDiagnosticState _state;

    public BundledFfmpegDiagnosticsProvider()
        : this(BundledFfmpegBootstrapper.DiagnosticState)
    {
    }

    internal BundledFfmpegDiagnosticsProvider(FfmpegDiagnosticState state)
    {
        _state = state;
    }

    public FfmpegDiagnosticSnapshot GetSnapshot()
    {
        return _state.GetSnapshot();
    }
}
