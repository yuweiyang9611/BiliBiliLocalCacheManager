using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

namespace BiliBiliLocalCacheManager.Playback.Services;

public sealed partial class CachePlaybackService
{
    public CachePlaybackService(IPlaybackArtifactStore artifactStore)
        : this(
            [
                new HybridCLegacyCachePlaybackLayoutHandler(),
                new NewDashCachePlaybackLayoutHandler(),
                new MidDashCachePlaybackLayoutHandler(),
                new LegacyLuaCachePlaybackLayoutHandler()
            ],
            CreateDefaultMaterializer(artifactStore),
            new SystemPlaybackLauncher())
    {
    }

    private static IPlaybackMaterializer CreateDefaultMaterializer(
        IPlaybackArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        var transcoder = new FfmpegCoreTranscoder();
        return new CompositePlaybackMaterializer(
        [
            new SingleFilePlaybackMaterializer(transcoder, artifactStore),
            new OrderedPairPlaybackMaterializer(transcoder, artifactStore),
            new DashPairPlaybackMaterializer(transcoder, artifactStore)
        ]);
    }
}
