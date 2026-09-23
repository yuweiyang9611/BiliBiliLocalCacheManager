using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class ExportMaterializationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "export-consent-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AudioTranscoding_RequiresSourceBoundConsentBeforeEncoding()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "audio.m4s");
        await File.WriteAllTextAsync(source, "original");
        var processor = new AudioProcessor();
        var service = new CacheExportService(new PlaybackArtifactStore(Path.Combine(_root, "artifacts")), processor);
        var plan = Plan(source);
        var required = await Assert.ThrowsAsync<ExportTranscodeRequiredException>(() => service.MaterializeAsync(plan, [], null, default));
        Assert.False(processor.Encoded);
        Assert.Equal("audio-aac", required.Requirement.ProcessingKind);
        var exported = await service.MaterializeAsync(plan, [required.Requirement.ApprovalToken], null, default);
        Assert.True(exported.Succeeded);
        Assert.True(processor.Encoded);
    }

    private CachePlaybackPlan Plan(string source) => CachePlaybackPlan.Playable(123, "Title", 1, "Part", "c_1", _root,
        "Dash", CachePlaybackMaterialKind.DashPair, [source]);

    [Fact]
    public async Task ReusedArtifact_StillRequiresConsentAndRejectsChangedSource()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "audio.m4s");
        await File.WriteAllTextAsync(source, "original");
        var service = new CacheExportService(new PlaybackArtifactStore(Path.Combine(_root, "artifacts")), new AudioProcessor());
        var plan = Plan(source);
        var first = await Assert.ThrowsAsync<ExportTranscodeRequiredException>(() => service.MaterializeAsync(plan, [], null, default));
        var approved = new[] { first.Requirement.ApprovalToken };
        var exported = await service.MaterializeAsync(plan, approved, null, default);
        var cached = await Assert.ThrowsAsync<ExportTranscodeRequiredException>(() => service.MaterializeAsync(plan, [], null, default));
        Assert.Equal(first.Requirement.ApprovalToken, cached.Requirement.ApprovalToken);
        Assert.Equal(exported.OutputPath, (await service.MaterializeAsync(plan, approved, null, default)).OutputPath);
        var previousTime = File.GetLastWriteTimeUtc(source);
        await File.WriteAllTextAsync(source, "replaced");
        File.SetLastWriteTimeUtc(source, previousTime);
        var changed = await Assert.ThrowsAsync<ExportTranscodeRequiredException>(() => service.MaterializeAsync(plan, approved, null, default));
        Assert.NotEqual(first.Requirement.ApprovalToken, changed.Requirement.ApprovalToken);
    }

    [Fact]
    public async Task SourceChangedWhileVerifyingReusedArtifact_IsNotReturned()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "audio.m4s");
        await File.WriteAllTextAsync(source, "original");
        var service = new CacheExportService(new PlaybackArtifactStore(Path.Combine(_root, "artifacts")), new AudioProcessor());
        var plan = Plan(source);
        var required = await Assert.ThrowsAsync<ExportTranscodeRequiredException>(() => service.MaterializeAsync(plan, [], null, default));
        var approved = new[] { required.Requirement.ApprovalToken };
        await service.MaterializeAsync(plan, approved, null, default);
        await Assert.ThrowsAsync<IOException>(() => service.MaterializeAsync(plan, approved, new InlineProgress(value =>
        {
            if (value.Phase == "artifact-verify") File.WriteAllText(source, "replaced");
        }), default));
    }

    private sealed class InlineProgress(Action<PlaybackPreparationProgress> action) : IProgress<PlaybackPreparationProgress>
    {
        public void Report(PlaybackPreparationProgress value) => action(value);
    }

    [Fact]
    public async Task PreparedMedia_IsRevalidatedBeforePublishingBatch()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "audio.m4s");
        await File.WriteAllTextAsync(source, "original");
        var service = new CacheExportService(new PlaybackArtifactStore(Path.Combine(_root, "artifacts")), new AudioProcessor());
        var plan = Plan(source);
        var required = await Assert.ThrowsAsync<ExportTranscodeRequiredException>(() => service.MaterializeAsync(plan, [], null, default));
        var media = await service.MaterializeAsync(plan, [required.Requirement.ApprovalToken], null, default);
        await service.ValidatePreparedAsync(plan, media, null, default);
        await File.WriteAllTextAsync(source, "changed after preparation");
        await Assert.ThrowsAsync<IOException>(() => service.ValidatePreparedAsync(plan, media, null, default));
    }

    private sealed class AudioProcessor : IExportMediaProcessor
    {
        public bool Encoded { get; private set; }
        public string Reason { get; set; } = "Audio codec opus requires AAC conversion.";
        public async Task ProcessAsync(CachePlaybackPlan plan, string outputPath, Action<string> requireAudioTranscode,
            IProgress<PlaybackPreparationProgress>? progress, CancellationToken cancellationToken)
        {
            requireAudioTranscode(Reason);
            Encoded = true;
            await File.WriteAllTextAsync(outputPath, "encoded", cancellationToken);
        }
    }

    [Fact]
    public async Task ChangedProcessingReason_NeedsNewConsentForSameSource()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "audio.m4s");
        await File.WriteAllTextAsync(source, "original");
        var processor = new AudioProcessor();
        var service = new CacheExportService(new PlaybackArtifactStore(Path.Combine(_root, "artifacts")), processor);
        var plan = Plan(source);
        var first = await Assert.ThrowsAsync<ExportTranscodeRequiredException>(() => service.MaterializeAsync(plan, [], null, default));
        processor.Reason = "Stream copy failed with a newly discovered compatibility requirement.";
        var second = await Assert.ThrowsAsync<ExportTranscodeRequiredException>(() => service.MaterializeAsync(plan,
            [first.Requirement.ApprovalToken], null, default));
        Assert.NotEqual(first.Requirement.ApprovalToken, second.Requirement.ApprovalToken);
        Assert.False(processor.Encoded);
    }

    [Fact]
    public async Task ModifiedArtifact_IsSafelyRebuiltInsteadOfReused()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "audio.m4s");
        await File.WriteAllTextAsync(source, "original");
        var service = new CacheExportService(new PlaybackArtifactStore(Path.Combine(_root, "artifacts")), new AudioProcessor());
        var plan = Plan(source);
        var required = await Assert.ThrowsAsync<ExportTranscodeRequiredException>(() => service.MaterializeAsync(plan, [], null, default));
        var approvals = new[] { required.Requirement.ApprovalToken };
        var first = await service.MaterializeAsync(plan, approvals, null, default);
        await File.WriteAllTextAsync(first.OutputPath!, "tampered");
        var second = await service.MaterializeAsync(plan, approvals, null, default);
        Assert.NotEqual(first.OutputPath, second.OutputPath);
        Assert.Equal("encoded", await File.ReadAllTextAsync(second.OutputPath!));
    }

    [Fact]
    public async Task ExportArtifact_RemainsProtectedDuringVerificationByAnotherStore()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "audio.m4s");
        await File.WriteAllTextAsync(source, "original");
        var store = new PlaybackArtifactStore(Path.Combine(_root, "artifacts"));
        var service = new CacheExportService(store, new AudioProcessor());
        var plan = Plan(source);
        var required = await Assert.ThrowsAsync<ExportTranscodeRequiredException>(() => service.MaterializeAsync(plan, [], null, default));
        var approvals = new[] { required.Requirement.ApprovalToken };
        await service.MaterializeAsync(plan, approvals, null, default);
        var result = await service.MaterializeAsync(plan, approvals, new InlineProgress(value =>
        {
            if (value.Phase == "source-verify") new PlaybackArtifactStore(store.RootDirectory).Clear();
        }), default);
        Assert.True(File.Exists(result.OutputPath));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
