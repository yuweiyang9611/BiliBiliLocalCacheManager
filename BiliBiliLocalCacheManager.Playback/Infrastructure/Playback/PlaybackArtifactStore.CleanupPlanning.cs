using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed partial class PlaybackArtifactStore
{
    private static readonly TimeSpan StaleBuildMaxAge = TimeSpan.FromHours(1);

    internal Action<string>? BeforeCleanupCandidateLockForTesting { get; set; }

    public PlaybackArtifactCleanupPreview MeasureStorage(
        PlaybackArtifactCleanupOptions options,
        out PlaybackArtifactCacheStatistics statistics,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = SnapshotAllManagedFilesStrict(cancellationToken);
        // Storage can still report the measured usage if evaluating the cleanup policy fails.
        statistics = CreateCacheStatistics(files, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var policy = CreateCleanupPolicy(options, cancellationToken);
        var plan = CreateCleanupPlan(policy, _timeProvider.GetUtcNow().UtcDateTime, files, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return CreateCleanupPreview(plan, cancellationToken);
    }

    public PlaybackArtifactCleanupPreview PreviewCleanup(
        PlaybackArtifactCleanupOptions? options = null)
    {
        var policy = CreateCleanupPolicy(options);
        if (!Directory.Exists(RootDirectory))
        {
            return new PlaybackArtifactCleanupPreview(0, 0, 0);
        }

        var plan = CreateCleanupPlan(policy, _timeProvider.GetUtcNow().UtcDateTime);
        return CreateCleanupPreview(plan);
    }

    public PlaybackArtifactCleanupResult Cleanup(
        PlaybackArtifactCleanupOptions? options = null)
    {
        var policy = CreateCleanupPolicy(options);
        if (!Directory.Exists(RootDirectory))
        {
            var emptyStatistics = new PlaybackArtifactCacheStatistics(RootDirectory, 0, 0);
            return new PlaybackArtifactCleanupResult(0, 0, 0, 0, emptyStatistics);
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var initialFiles = SnapshotAllManagedFiles();
        var initialBytes = SumLengths(initialFiles);
        var plan = CreateCleanupPlan(policy, nowUtc, initialFiles);
        var deletedCount = 0;
        var failedCount = 0;
        var freedBytes = 0L;
        var unprocessedCount = plan.Candidates.Count;
        var attemptedManagedPaths = new HashSet<string>(PlaybackFileSystem.PathComparer);
        try
        {
            foreach (var candidate in plan.Candidates)
            {
                if (!candidate.IsStaleBuild)
                {
                    attemptedManagedPaths.Add(candidate.File.FullName);
                }

                DeleteCleanupCandidateIfEligible(
                    candidate,
                    ref deletedCount,
                    ref failedCount,
                    ref freedBytes);
                unprocessedCount--;
            }

            var afterInitialCleanup = SnapshotAllManagedFiles();
            var attemptedAdditionalCandidates = DeleteAdditionalCapacityCandidates(
                policy,
                nowUtc,
                afterInitialCleanup,
                attemptedManagedPaths,
                ref deletedCount,
                ref failedCount,
                ref freedBytes,
                ref unprocessedCount);
            DeleteEmptyDirectories();
            var remainingFiles = attemptedAdditionalCandidates ? SnapshotAllManagedFiles() : afterInitialCleanup;
            var statistics = CreateCacheStatistics(remainingFiles);
            var preview = CreateCleanupPreview(CreateCleanupPlan(policy, nowUtc, remainingFiles));
            return new PlaybackArtifactCleanupResult(
                deletedCount,
                freedBytes,
                failedCount,
                statistics.TotalBytes,
                statistics,
                preview);
        }
        catch (OperationCanceledException)
        {
            // Never discard committed deletions because a later candidate or
            // statistics traversal was cancelled. Avoid another slow traversal.
            return new PlaybackArtifactCleanupResult(
                deletedCount,
                freedBytes,
                failedCount,
                SubtractFloor(initialBytes, freedBytes),
                Cancelled: true,
                UnprocessedFileCount: unprocessedCount,
                RemainingBytesEstimated: true);
        }
    }

    private CleanupPolicy CreateCleanupPolicy(PlaybackArtifactCleanupOptions? options, CancellationToken cancellationToken = default)
    {
        var effectiveOptions = options ?? new PlaybackArtifactCleanupOptions();
        if (effectiveOptions.MaxAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxAge must not be negative.");
        }

        if (effectiveOptions.MaxTotalBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxTotalBytes must not be negative.");
        }

        if (effectiveOptions.CapacityEvictionGracePeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "CapacityEvictionGracePeriod must not be negative.");
        }

        if (effectiveOptions.ProtectedPaths is null)
        {
            throw new ArgumentException("ProtectedPaths must not be null.", nameof(options));
        }

        return new CleanupPolicy(
            effectiveOptions.MaxAge,
            effectiveOptions.MaxTotalBytes,
            effectiveOptions.CapacityEvictionGracePeriod,
            SnapshotProtectedPaths(EnumerateWithCancellation(effectiveOptions.ProtectedPaths, cancellationToken)));
    }

    private CleanupPlan CreateCleanupPlan(CleanupPolicy policy, DateTime nowUtc)
    {
        return CreateCleanupPlan(policy, nowUtc, SnapshotAllManagedFiles());
    }

    private CleanupPlan CreateCleanupPlan(
        CleanupPolicy policy,
        DateTime nowUtc,
        IReadOnlyList<ManagedFileSnapshot> allManagedFiles,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<CleanupCandidate>();
        var managedFiles = new List<ManagedFileSnapshot>();
        var staleBuildCutoff = nowUtc - StaleBuildMaxAge;
        foreach (var file in allManagedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsManagedBuildFile(file.File) && file.LastWriteTimeUtc < staleBuildCutoff)
            {
                candidates.Add(new CleanupCandidate(
                    file.File,
                    file.Length,
                    IsStaleBuild: true,
                    staleBuildCutoff));
            }
            else if (IsManagedArtifactFile(file.File) && !HasActiveProtection(file.File.FullName))
            {
                managedFiles.Add(file);
            }
        }

        var projectedRemainingBytes = SumLengths(allManagedFiles, cancellationToken);
        foreach (var staleBuild in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            projectedRemainingBytes = SubtractFloor(
                projectedRemainingBytes,
                staleBuild.Length);
        }

        var retentionCutoff = nowUtc - policy.MaxAge;
        var retentionPaths = new HashSet<string>(PlaybackFileSystem.PathComparer);
        foreach (var file in managedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (policy.ProtectedPaths.Contains(file.File.FullName) || file.LastWriteTimeUtc >= retentionCutoff) continue;
            retentionPaths.Add(file.File.FullName);
            candidates.Add(new CleanupCandidate(
                file.File,
                file.Length,
                IsStaleBuild: false,
                retentionCutoff));
            projectedRemainingBytes = SubtractFloor(projectedRemainingBytes, file.Length);
        }

        if (projectedRemainingBytes <= policy.MaxTotalBytes)
            return new CleanupPlan(candidates, projectedRemainingBytes);

        var capacityCutoff = nowUtc - policy.CapacityEvictionGracePeriod;
        foreach (var file in EnumerateWithCancellation(managedFiles, cancellationToken)
                     .Where(file =>
                         !retentionPaths.Contains(file.File.FullName) &&
                         !policy.ProtectedPaths.Contains(file.File.FullName) &&
                         file.LastWriteTimeUtc < capacityCutoff)
                     .OrderBy(file => file.LastWriteTimeUtc)
                     .ThenBy(file => file.File.FullName, PlaybackFileSystem.PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (projectedRemainingBytes <= policy.MaxTotalBytes)
            {
                break;
            }

            candidates.Add(new CleanupCandidate(
                file.File,
                file.Length,
                IsStaleBuild: false,
                capacityCutoff));
            projectedRemainingBytes = SubtractFloor(projectedRemainingBytes, file.Length);
        }

        return new CleanupPlan(candidates, projectedRemainingBytes);
    }

    private bool DeleteAdditionalCapacityCandidates(
        CleanupPolicy policy,
        DateTime nowUtc,
        IReadOnlyList<ManagedFileSnapshot> allManagedFiles,
        HashSet<string> attemptedPaths,
        ref int deletedCount,
        ref int failedCount,
        ref long freedBytes,
        ref int unprocessedCount)
    {
        var managedFiles = allManagedFiles
            .Where(file => IsManagedArtifactFile(file.File))
            .ToList();
        var totalBytes = SumLengths(allManagedFiles);
        if (totalBytes <= policy.MaxTotalBytes)
        {
            return false;
        }

        var capacityCutoff = nowUtc - policy.CapacityEvictionGracePeriod;
        var candidates = managedFiles.Where(file =>
                         !attemptedPaths.Contains(file.File.FullName) &&
                         !policy.ProtectedPaths.Contains(file.File.FullName) &&
                         file.LastWriteTimeUtc < capacityCutoff)
                     .OrderBy(file => file.LastWriteTimeUtc)
                     .ThenBy(file => file.File.FullName, PlaybackFileSystem.PathComparer).ToArray();
        unprocessedCount = candidates.Length;
        foreach (var file in candidates)
        {
            if (totalBytes <= policy.MaxTotalBytes)
            {
                break;
            }

            attemptedPaths.Add(file.File.FullName);
            if (DeleteCleanupCandidateIfEligible(
                    new CleanupCandidate(
                        file.File,
                        file.Length,
                        IsStaleBuild: false,
                        capacityCutoff),
                    ref deletedCount,
                    ref failedCount,
                    ref freedBytes))
            {
                totalBytes = SubtractFloor(totalBytes, file.Length);
            }
            unprocessedCount--;
        }
        unprocessedCount = 0;
        return candidates.Length > 0;
    }

    private bool DeleteCleanupCandidateIfEligible(
        CleanupCandidate candidate,
        ref int deletedCount,
        ref int failedCount,
        ref long freedBytes)
    {
        BeforeCleanupCandidateLockForTesting?.Invoke(candidate.File.FullName);
        var lockTarget = candidate.IsStaleBuild
            ? TryGetOutputPathForBuildFile(candidate.File)
            : candidate.File.FullName;
        if (lockTarget is null)
        {
            return false;
        }

        using var fileLock = TryAcquireCrossProcessLock(lockTarget);
        if (fileLock is null)
        {
            return false;
        }

        if (!TryGetFileMetadata(candidate.File, out _, out var lastWriteTimeUtc))
        {
            if (File.Exists(candidate.File.FullName))
            {
                failedCount++;
                return false;
            }

            return true;
        }

        if (lastWriteTimeUtc >= candidate.EligibilityCutoffUtc)
        {
            return false;
        }

        return DeleteManagedFile(
            candidate.File,
            ref deletedCount,
            ref failedCount,
            ref freedBytes);
    }

    private static long SumCandidateLengths(IEnumerable<CleanupCandidate> candidates, CancellationToken cancellationToken)
    {
        var total = 0L;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total = candidate.Length > long.MaxValue - total
                ? long.MaxValue
                : total + candidate.Length;
        }

        return total;
    }

    private static PlaybackArtifactCleanupPreview CreateCleanupPreview(CleanupPlan plan, CancellationToken cancellationToken = default)
    {
        return new PlaybackArtifactCleanupPreview(
            plan.Candidates.Count,
            SumCandidateLengths(plan.Candidates, cancellationToken),
            plan.ProjectedRemainingBytes);
    }

    private static long SubtractFloor(long value, long amount)
    {
        return amount >= value ? 0 : value - amount;
    }

    private sealed record CleanupPolicy(
        TimeSpan MaxAge,
        long MaxTotalBytes,
        TimeSpan CapacityEvictionGracePeriod,
        HashSet<string> ProtectedPaths);

    private sealed record CleanupCandidate(
        FileInfo File,
        long Length,
        bool IsStaleBuild,
        DateTime EligibilityCutoffUtc);

    private sealed record CleanupPlan(
        IReadOnlyList<CleanupCandidate> Candidates,
        long ProjectedRemainingBytes);
}
