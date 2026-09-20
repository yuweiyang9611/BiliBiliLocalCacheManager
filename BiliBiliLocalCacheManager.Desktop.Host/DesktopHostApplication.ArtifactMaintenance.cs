using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Desktop.Host;

internal sealed partial class DesktopHostApplication
{
    private async Task<PlaybackArtifactCleanupResult> RunArtifactMaintenanceAsync(
        Func<PlaybackArtifactCleanupResult> operation,
        CancellationToken cancellationToken)
    {
        await _artifactMaintenanceGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            }, cancellationToken);
        }
        finally
        {
            _artifactMaintenanceGate.Release();
        }
    }

    private void QueueInitialBackgroundWork()
    {
        if (Interlocked.CompareExchange(ref _initialBackgroundWorkStarted, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(PrewarmFfmpegInBackgroundAsync);
        QueueBackgroundArtifactCleanup("Startup policy cleanup");
    }

    private async Task PrewarmFfmpegInBackgroundAsync()
    {
        try
        {
            var result = await _ffmpegPrewarmService.PrewarmAsync(CancellationToken.None);
            _eventRecorder.Record(
                "FFmpeg",
                result.Succeeded ? "Information" : "Warning",
                result.Succeeded
                    ? $"Background prewarm completed: {result.Message}"
                    : $"Background prewarm was not available: {result.Message}");
        }
        catch (Exception exception)
        {
            _eventRecorder.Record(
                "FFmpeg",
                "Warning",
                $"Background prewarm failed without interrupting the desktop session: {exception.Message}",
                exception);
        }
    }

    private void QueueBackgroundArtifactCleanup(
        string reason,
        IReadOnlyCollection<string>? immediatelyProtectedPaths = null)
    {
        if (immediatelyProtectedPaths is { Count: > 0 })
        {
            foreach (var path in immediatelyProtectedPaths)
            {
                ProtectLaunchedArtifact(path);
            }
        }

        if (Interlocked.CompareExchange(ref _backgroundArtifactCleanupQueued, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var cleanupOptions = CreateCleanupOptions(_settingsStore.GetState().Settings);
                var result = await RunArtifactMaintenanceAsync(
                    () => _artifactStore.Cleanup(cleanupOptions),
                    CancellationToken.None);
                RecordArtifactMaintenance(reason, result);
            }
            catch (Exception exception)
            {
                _eventRecorder.Record(
                    "Artifacts",
                    "Warning",
                    $"{reason} failed without interrupting the active operation: {exception.Message}",
                    exception);
            }
            finally
            {
                Interlocked.Exchange(ref _backgroundArtifactCleanupQueued, 0);
            }
        });
    }

    private PlaybackArtifactCleanupOptions CreateCleanupOptions(DesktopSettings settings)
    {
        var options = settings.CreateCleanupOptions();
        options.ProtectedPaths = SnapshotSessionProtectedArtifacts(options.MaxTotalBytes);
        return options;
    }

    private PlaybackArtifactCleanupOptions CreateClearOptions() => new()
    {
        MaxAge = TimeSpan.Zero,
        MaxTotalBytes = 0,
        CapacityEvictionGracePeriod = TimeSpan.Zero,
        ProtectedPaths = SnapshotSessionProtectedArtifacts(long.MaxValue)
    };

    private void ProtectLaunchedArtifact(string path)
    {
        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        lock (_artifactProtectionSync)
        {
            for (var node = _sessionProtectedArtifacts.First; node is not null;)
            {
                var next = node.Next;
                if (PathComparer.Equals(node.Value.Path, normalizedPath))
                {
                    _sessionProtectedArtifacts.Remove(node);
                }

                node = next;
            }

            _sessionProtectedArtifacts.AddFirst(
                new SessionProtectedArtifact(normalizedPath, DateTimeOffset.UtcNow));
            TrimSessionProtection(DateTimeOffset.UtcNow);
        }
    }

    private IReadOnlyList<string> SnapshotSessionProtectedArtifacts(long capacityLimitBytes)
    {
        lock (_artifactProtectionSync)
        {
            var now = DateTimeOffset.UtcNow;
            TrimSessionProtection(now);
            var protectedPaths = new List<string>();
            long protectedBytes = 0;
            for (var node = _sessionProtectedArtifacts.First; node is not null;)
            {
                var next = node.Next;
                long length;
                try
                {
                    var file = new FileInfo(node.Value.Path);
                    if (!file.Exists)
                    {
                        _sessionProtectedArtifacts.Remove(node);
                        node = next;
                        continue;
                    }

                    length = file.Length;
                }
                catch
                {
                    _sessionProtectedArtifacts.Remove(node!);
                    node = next;
                    continue;
                }

                if (length > capacityLimitBytes || protectedBytes > capacityLimitBytes - length)
                {
                    _sessionProtectedArtifacts.Remove(node);
                    node = next;
                    continue;
                }

                protectedPaths.Add(node.Value.Path);
                protectedBytes = SaturatingAdd(protectedBytes, length);
                node = next;
            }

            return protectedPaths;
        }
    }

    private void TrimSessionProtection(DateTimeOffset now)
    {
        while (_sessionProtectedArtifacts.Last is { } last &&
               (now - last.Value.ProtectedAtUtc > MaximumSessionProtectionAge ||
                _sessionProtectedArtifacts.Count > MaximumSessionProtectedArtifactCount))
        {
            _sessionProtectedArtifacts.RemoveLast();
        }
    }

    private void RecordArtifactMaintenance(
        string reason,
        PlaybackArtifactCleanupResult result)
    {
        _eventRecorder.Record(
            "Artifacts",
            result.FailedFileCount == 0 ? "Information" : "Warning",
            $"{reason}: deleted {result.DeletedFileCount} files, freed {result.FreedBytes} bytes, " +
            $"{result.FailedFileCount} failures, {result.RemainingBytes} bytes remain.");
    }

    private static object ToWireArtifactCleanupResult(PlaybackArtifactCleanupResult result)
    {
        return new
        {
            deletedFileCount = result.DeletedFileCount,
            freedBytes = result.FreedBytes,
            failedFileCount = result.FailedFileCount,
            remainingBytes = result.RemainingBytes,
            cancelled = result.Cancelled,
            unprocessedFileCount = result.UnprocessedFileCount,
            remainingBytesEstimated = result.RemainingBytesEstimated
        };
    }
}
