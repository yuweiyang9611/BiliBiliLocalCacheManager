using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Services;

public sealed class CacheExportService(IPlaybackArtifactStore artifactStore, IExportMediaProcessor? processor = null)
    : ICacheExportMaterializationService
{
    private readonly IExportMediaProcessor _processor = processor ?? new FfmpegCoreTranscoder();
    private readonly byte[] _approvalKey = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, PreparedArtifact> _prepared = new();
    private readonly ConcurrentDictionary<string, ExportTranscodeRequirement> _requirements = new();
    private readonly ConditionalWeakTable<PlaybackMaterializationResult, PreparationReceipt> _receipts = new();

    public async Task<PlaybackMaterializationResult> MaterializeAsync(CachePlaybackPlan plan,
        IReadOnlyCollection<string> approvals, IProgress<PlaybackPreparationProgress>? progress, CancellationToken cancellationToken)
    {
        await using var protection = new PlaybackPreparationProtection(new PlaybackArtifactStore(artifactStore.RootDirectory), cancellationToken);
        try
        {
            var result = await MaterializeCoreAsync(plan, approvals, progress, protection).ConfigureAwait(false);
            await protection.StopAsync().ConfigureAwait(false);
            if (protection.Failure is not null) throw new IOException("Export artifact protection failed.", protection.Failure);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception exception) when (protection.Failure is not null)
        {
            throw new IOException("Export artifact protection failed: " + protection.Failure.Message, exception);
        }
    }

    private async Task<PlaybackMaterializationResult> MaterializeCoreAsync(CachePlaybackPlan plan,
        IReadOnlyCollection<string> approvals, IProgress<PlaybackPreparationProgress>? progress, PlaybackPreparationProtection protection)
    {
        var cancellationToken = protection.Token;
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPlayable) throw new IOException(plan.Message ?? "The selected media is unavailable.");
        var sourceIdentity = await SourceIdentityAsync(plan, progress, "source-hash", cancellationToken).ConfigureAwait(false);
        void Require(ExportTranscodeRequirement requirement)
        {
            if (!approvals.Contains(requirement.ApprovalToken, StringComparer.Ordinal))
                throw new ExportTranscodeRequiredException(requirement);
        }
        if (_requirements.TryGetValue(sourceIdentity, out var knownRequirement)) Require(knownRequirement);
        if (_prepared.TryGetValue(sourceIdentity, out var cached))
        {
            if (cached.Requirement is not null) Require(cached.Requirement);
            if (File.Exists(cached.Path)) protection.Register(cached.Path);
            var valid = false;
            try
            {
                valid = cached.Digest == await FileDigestAsync(cached.Path, progress, "artifact-verify", cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) { }
            if (valid)
            {
                if (sourceIdentity != await SourceIdentityAsync(plan, progress, "source-verify", cancellationToken).ConfigureAwait(false))
                    throw new IOException("Source media changed during export verification; retry the complete batch.");
                return RecordReceipt(cached.Path, sourceIdentity, cached.Digest);
            }
            _prepared.TryRemove(sourceIdentity, out _);
        }

        ExportTranscodeRequirement? usedRequirement = null;
        void RequireAudioTranscode(string reason)
        {
            var payload = JsonSerializer.Serialize(new { sourceIdentity, processingKind = "audio-aac", reason, profile = "aac-192k-video-copy-v1" });
            var token = Convert.ToHexString(HMACSHA256.HashData(_approvalKey, Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
            var requirement = new ExportTranscodeRequirement(token, "audio-aac", reason,
                "The audio track will be re-encoded as AAC and its quality may change. The video track is copied without re-encoding.");
            _requirements[sourceIdentity] = requirement;
            Require(requirement);
            usedRequirement = requirement;
        }

        // Playback artifacts predating this service have no trusted processing provenance.
        // A fresh profile avoids accepting them; verified outputs can be reused within this Host session.
        var exportPlan = CachePlaybackPlan.Playable(plan.Avid, plan.Title, plan.PageIndex, plan.PartName,
            plan.SegmentName, plan.SegmentDirectory, "export-v1-" + Guid.NewGuid().ToString("N"), plan.MaterialKind,
            plan.MediaFiles, duration: plan.Duration);
        var artifact = await artifactStore.GetOrCreateAsync(exportPlan, ".mp4", async output =>
        {
            await _processor.ProcessAsync(plan, output, RequireAudioTranscode, progress, cancellationToken).ConfigureAwait(false);
            if (sourceIdentity != await SourceIdentityAsync(plan, progress, "source-verify", cancellationToken).ConfigureAwait(false))
                throw new IOException("Source media changed during export preparation; retry the complete batch.");
        }, cancellationToken, PlaybackArtifactWaitProgress.Create(progress)).ConfigureAwait(false);
        protection.Register(artifact.OutputPath);
        var digest = await FileDigestAsync(artifact.OutputPath, progress, "artifact-verify", cancellationToken).ConfigureAwait(false);
        if (_prepared.Count >= 1000) _prepared.Clear();
        if (_requirements.Count >= 10000) _requirements.Clear();
        _prepared[sourceIdentity] = new PreparedArtifact(artifact.OutputPath, digest, usedRequirement);
        return RecordReceipt(artifact.OutputPath, sourceIdentity, digest);
    }

    public async Task ValidatePreparedAsync(CachePlaybackPlan plan, PlaybackMaterializationResult materialization,
        IProgress<PlaybackPreparationProgress>? progress, CancellationToken cancellationToken, string? stagedOutputPath = null)
    {
        if (!_receipts.TryGetValue(materialization, out var receipt) ||
            receipt.SourceIdentity != await SourceIdentityAsync(plan, progress, "final-source-verify", cancellationToken).ConfigureAwait(false) ||
            receipt.Digest != await FileDigestAsync(materialization.OutputPath!, progress, "final-artifact-verify", cancellationToken).ConfigureAwait(false))
            throw new IOException("Source media or its prepared export changed before batch publication; retry the complete batch.");
        if (stagedOutputPath is not null && receipt.Digest !=
            await FileDigestAsync(stagedOutputPath, progress, "final-output-verify", cancellationToken).ConfigureAwait(false))
            throw new IOException("Staged export media changed before batch publication; retry the complete batch.");
    }

    private PlaybackMaterializationResult RecordReceipt(string path, string sourceIdentity, string digest)
    {
        var result = PlaybackMaterializationResult.Success(path, true, "Prepared verified export media.", nameof(CacheExportService));
        _receipts.Add(result, new PreparationReceipt(sourceIdentity, digest));
        return result;
    }

    private static async Task<string> SourceIdentityAsync(CachePlaybackPlan plan,
        IProgress<PlaybackPreparationProgress>? progress, string phase, CancellationToken cancellationToken)
    {
        var files = new List<object>();
        long bytes = 0;
        foreach (var file in plan.MediaFiles)
        {
            var path = Path.GetFullPath(file);
            var digest = await FileDigestAsync(path, progress, phase, cancellationToken, bytes).ConfigureAwait(false);
            bytes += new FileInfo(path).Length;
            files.Add(new { path, digest });
        }
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            plan.Avid, plan.PageIndex, plan.MaterialKind, plan.StructureKind, files
        })));
    }

    private static async Task<string> FileDigestAsync(string path, IProgress<PlaybackPreparationProgress>? progress,
        string phase, CancellationToken cancellationToken, long bytes = 0)
    {
        PlaybackBatchLauncher.ValidateLocalFile(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer.AsSpan(0, count));
            bytes += count;
            progress?.Report(new PlaybackPreparationProgress("Verifying export media", null, TimeSpan.Zero, null,
                Phase: phase, ProcessedBytes: bytes));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed record PreparedArtifact(string Path, string Digest, ExportTranscodeRequirement? Requirement);
    private sealed record PreparationReceipt(string SourceIdentity, string Digest);
}
