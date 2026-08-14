using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed partial class PlaybackArtifactStore
{
    private static readonly TimeSpan StaleBuildMaxAge = TimeSpan.FromHours(1);

    internal Action<string>? BeforeCleanupCandidateLockForTesting { get; set; }

    public PlaybackArtifactCleanupPreview PreviewCleanup(
        PlaybackArtifactCleanupOptions? options = null)
    {
        var policy = CreateCleanupPolicy(options);
        if (!Directory.Exists(RootDirectory))
        {
            return new PlaybackArtifactCleanupPreview(0, 0, 0);
        }

        var plan = CreateCleanupPlan(policy, DateTime.UtcNow);
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

        var nowUtc = DateTime.UtcNow;
        var plan = CreateCleanupPlan(policy, nowUtc);
        var deletedCount = 0;
        var failedCount = 0;
        var freedBytes = 0L;
        var attemptedManagedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        }

        DeleteAdditionalCapacityCandidates(
            policy,
            nowUtc,
            attemptedManagedPaths,
            ref deletedCount,
            ref failedCount,
            ref freedBytes);
        DeleteEmptyDirectories();
        var remainingFiles = SnapshotAllManagedFiles();
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

    private CleanupPolicy CreateCleanupPolicy(PlaybackArtifactCleanupOptions? options)
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
            SnapshotProtectedPaths(effectiveOptions.ProtectedPaths));
    }

    private CleanupPlan CreateCleanupPlan(CleanupPolicy policy, DateTime nowUtc)
    {
        return CreateCleanupPlan(policy, nowUtc, SnapshotAllManagedFiles());
    }

    private CleanupPlan CreateCleanupPlan(
        CleanupPolicy policy,
        DateTime nowUtc,
        IReadOnlyList<ManagedFileSnapshot> allManagedFiles)
    {
        var candidates = new List<CleanupCandidate>();
        var staleBuildCutoff = nowUtc - StaleBuildMaxAge;
        foreach (var file in allManagedFiles.Where(file => IsManagedBuildFile(file.File)))
        {
            if (file.LastWriteTimeUtc < staleBuildCutoff)
            {
                candidates.Add(new CleanupCandidate(
                    file.File,
                    file.Length,
                    IsStaleBuild: true,
                    staleBuildCutoff));
            }
        }

        var managedFiles = allManagedFiles
            .Where(file => IsManagedArtifactFile(file.File))
            .ToList();
        var projectedRemainingBytes = SumLengths(allManagedFiles);
        foreach (var staleBuild in candidates)
        {
            projectedRemainingBytes = SubtractFloor(
                projectedRemainingBytes,
                staleBuild.Length);
        }

        var retentionCutoff = nowUtc - policy.MaxAge;
        var retentionCandidates = managedFiles
            .Where(file =>
                !policy.ProtectedPaths.Contains(file.File.FullName) &&
                file.LastWriteTimeUtc < retentionCutoff)
            .ToList();
        foreach (var file in retentionCandidates)
        {
            candidates.Add(new CleanupCandidate(
                file.File,
                file.Length,
                IsStaleBuild: false,
                retentionCutoff));
            projectedRemainingBytes = SubtractFloor(projectedRemainingBytes, file.Length);
        }

        var retentionPaths = retentionCandidates
            .Select(file => file.File.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var capacityCutoff = nowUtc - policy.CapacityEvictionGracePeriod;
        foreach (var file in managedFiles
                     .Where(file =>
                         !retentionPaths.Contains(file.File.FullName) &&
                         !policy.ProtectedPaths.Contains(file.File.FullName) &&
                         file.LastWriteTimeUtc < capacityCutoff)
                     .OrderBy(file => file.LastWriteTimeUtc)
                     .ThenBy(file => file.File.FullName, StringComparer.OrdinalIgnoreCase))
        {
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

    private void DeleteAdditionalCapacityCandidates(
        CleanupPolicy policy,
        DateTime nowUtc,
        HashSet<string> attemptedPaths,
        ref int deletedCount,
        ref int failedCount,
        ref long freedBytes)
    {
        var allManagedFiles = SnapshotAllManagedFiles();
        var managedFiles = allManagedFiles
            .Where(file => IsManagedArtifactFile(file.File))
            .ToList();
        var totalBytes = SumLengths(allManagedFiles);
        if (totalBytes <= policy.MaxTotalBytes)
        {
            return;
        }

        var capacityCutoff = nowUtc - policy.CapacityEvictionGracePeriod;
        foreach (var file in managedFiles
                     .Where(file =>
                         !attemptedPaths.Contains(file.File.FullName) &&
                         !policy.ProtectedPaths.Contains(file.File.FullName) &&
                         file.LastWriteTimeUtc < capacityCutoff)
                     .OrderBy(file => file.LastWriteTimeUtc)
                     .ThenBy(file => file.File.FullName, StringComparer.OrdinalIgnoreCase))
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
        }
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

    private static long SumCandidateLengths(IEnumerable<CleanupCandidate> candidates)
    {
        var total = 0L;
        foreach (var candidate in candidates)
        {
            total = candidate.Length > long.MaxValue - total
                ? long.MaxValue
                : total + candidate.Length;
        }

        return total;
    }

    private static PlaybackArtifactCleanupPreview CreateCleanupPreview(CleanupPlan plan)
    {
        return new PlaybackArtifactCleanupPreview(
            plan.Candidates.Count,
            SumCandidateLengths(plan.Candidates),
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
