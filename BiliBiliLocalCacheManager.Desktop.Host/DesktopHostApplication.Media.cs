using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;
using BiliBiliLocalCacheManager.Desktop.Host.Services;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;

namespace BiliBiliLocalCacheManager.Desktop.Host;

internal sealed partial class DesktopHostApplication
{
    private async Task<object> PlayAsync(
        string requestId, JsonElement parameters, CancellationToken cancellationToken)
    {
        var settings = _settingsStore.GetState().Settings;
        var root = ResolveRequiredRoot(parameters, settings);
        var includeIncomplete = parameters.OptionalBoolean("includeIncomplete") ?? settings.IncludeIncomplete;
        var selections = ParseSelectionTargets(parameters);
        var player = ParseWirePlayerPreference(parameters.OptionalString("playerPreference"), settings.PreferredPlayer);
        var index = await ResolveIndexAsync(requestId, "play", root, includeIncomplete, cancellationToken);
        var failures = new List<MediaFailureDto>();
        var targets = new List<(BiliVideoCache Cache, int Page)>();
        var seen = new HashSet<(long Avid, int Page)>();
        foreach (var selection in selections)
        {
            if (!index.ByAvid.TryGetValue(selection.Avid, out var cache))
            {
                failures.Add(new(selection.Avid.ToString(), null, "", "Cache not found."));
                continue;
            }
            var pages = selection.PageIndexes is { Count: > 0 }
                ? selection.PageIndexes : cache.Segments.Select(segment => segment.PageIndex).Distinct().ToArray();
            foreach (var page in pages.Order())
                if (seen.Add((cache.Avid, page))) targets.Add((cache, page));
        }
        var prepared = new List<(PlaybackQueueItem Item, BiliVideoCache Cache, int Page)>();
        var store = new PlaybackArtifactStore(_artifactStore.RootDirectory);
        await using var protection = new PlaybackPreparationProtection(store, cancellationToken);
        PlaybackBatchResultDto ProtectionFailure() => new(0, failures.Concat(targets.Select(target =>
            new MediaFailureDto(target.Cache.Avid.ToString(), target.Page, BoundWireString(target.Cache.Title),
                BoundWireString("Playback preparation protection failed: " + protection.Failure?.Message)))).ToArray());
        for (var ordinal = 0; ordinal < targets.Count; ordinal++)
        {
            if (protection.Failure is not null) return ProtectionFailure();
            cancellationToken.ThrowIfCancellationRequested();
            var target = targets[ordinal];
            try
            {
                var plan = _playbackService.CreatePagePlan(target.Cache, target.Page.ToString());
                var progress = new InlineProgress<PlaybackPreparationProgress>(value =>
                    ReportProgress(new HostProgressEvent(requestId, "play", value.Stage, value.Percentage,
                        ordinal + 1, targets.Count, $"av{target.Cache.Avid} P{target.Page}",
                        new { processedSeconds = value.ProcessedSeconds, bytesProcessed = value.ProcessedBytes }, Phase: value.Phase)));
                var materialization = await _playbackService.MaterializeAsync(plan.SelectedPlan, progress, protection.Token);
                if (!materialization.Succeeded || string.IsNullOrWhiteSpace(materialization.OutputPath))
                    throw new IOException(materialization.Message);
                PlaybackBatchLauncher.ValidateLocalFile(materialization.OutputPath);
                protection.Register(materialization.OutputPath);
                prepared.Add((new PlaybackQueueItem(materialization.OutputPath, target.Cache.Title + " - " + plan.PartName,
                    plan.SelectedPlan.Duration), target.Cache, target.Page));
            }
            catch (Exception) when (protection.Failure is not null) { return ProtectionFailure(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                failures.Add(new(target.Cache.Avid.ToString(), target.Page, BoundWireString(target.Cache.Title), BoundWireString(exception.Message)));
            }
        }
        await protection.StopAsync();
        if (protection.Failure is not null) return ProtectionFailure();
        cancellationToken.ThrowIfCancellationRequested();
        var queued = 0;
        if (prepared.Count > 0)
        {
            try
            {
                var launcher = new PlaybackBatchLauncher(store, _desktopPlaybackLauncher);
                var result = launcher.LaunchBatch(prepared.Select(value => value.Item).ToArray(),
                    new PlaybackLaunchOptions { PreferredPlayer = player }, protection.Token);
                if (!result.Succeeded) throw new IOException(result.Message);
                queued = prepared.Count;
            }
            catch (Exception) when (protection.Failure is not null) { return ProtectionFailure(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                failures.AddRange(prepared.Select(value => new MediaFailureDto(value.Cache.Avid.ToString(),
                    value.Page, BoundWireString(value.Item.Title), BoundWireString(exception.Message))));
            }
        }
        if (queued > 0) QueueBackgroundArtifactCleanup("Post-playback policy cleanup");
        return new PlaybackBatchResultDto(queued, failures);
    }

    private async Task<object> ExportAsync(string requestId, JsonElement parameters, CancellationToken cancellationToken)
    {
        var settings = _settingsStore.GetState().Settings;
        var root = ResolveRequiredRoot(parameters, settings);
        var includeIncomplete = parameters.OptionalBoolean("includeIncomplete") ?? settings.IncludeIncomplete;
        var selections = ParseSelectionTargets(parameters);
        var requestedOutputPath = Path.GetFullPath(parameters.RequireString("outputPath"));
        var requestedParent = Path.GetDirectoryName(requestedOutputPath);
        if (string.IsNullOrWhiteSpace(requestedParent) || !Directory.Exists(requestedParent))
            throw new DirectoryNotFoundException($"Export destination directory not found: {requestedParent}");
        var index = await ResolveIndexAsync(requestId, "export", root, includeIncomplete, cancellationToken);
        var requests = ExpandExportTargets(index, selections);
        var failures = selections.Where(selection => !index.ByAvid.ContainsKey(selection.Avid))
            .Select(selection => new MediaFailureDto(selection.Avid.ToString(), null, "", "Cache not found.")).ToList();
        if (requests.Count == 0) return new ExportBatchResultDto(null, 0, failures, false);
        var destinationIsDirectory = requests.Count > 1 || Directory.Exists(requestedOutputPath);
        var destination = destinationIsDirectory ? ResolveBatchExportDirectory(requestedOutputPath) : requestedOutputPath;
        string? stagingDirectory = destinationIsDirectory ? CreateBatchExportStagingDirectory(destination) : null;
        string? stagingFile = destinationIsDirectory ? null : Path.Combine(requestedParent,
            "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".exporting");
        var workingDestination = stagingDirectory ?? destination;
        var preparedCount = 0;
        try
        {
            // Missing selections must also prevent a single-file export from committing.
            if (failures.Count > 0) return new ExportBatchResultDto(null, 0, failures, false);
            for (var ordinal = 0; ordinal < requests.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = requests[ordinal];
                var cache = index.ByAvid[target.Avid];
                try
                {
                    var plan = _playbackService.CreatePagePlan(cache, target.SegmentKey);
                    var progress = new InlineProgress<PlaybackPreparationProgress>(value =>
                        ReportProgress(new HostProgressEvent(requestId, "export", value.Stage, value.Percentage,
                            ordinal + 1, requests.Count, $"av{target.Avid} P{plan.PageIndex}",
                            new { processedSeconds = value.ProcessedSeconds, bytesProcessed = value.ProcessedBytes }, Phase: value.Phase)));
                    var materialization = await _playbackService.MaterializeAsync(plan.SelectedPlan, progress, cancellationToken);
                    if (!materialization.Succeeded || string.IsNullOrWhiteSpace(materialization.OutputPath))
                        throw new IOException(materialization.Message);
                    if (!destinationIsDirectory && PathsEqual(materialization.OutputPath, destination))
                        throw new IOException("The export source and destination are the same file.");
                    var output = destinationIsDirectory
                        ? PortableFileNaming.EnsureUnique(workingDestination, PortableFileNaming.Build(cache.Title,
                            cache.Avid, plan.PageIndex, plan.PartName, cache.Segments.Select(segment => segment.PageIndex).Distinct().Count() > 1), ".mp4")
                        : stagingFile!;
                    await CopyAtomicallyAsync(materialization.OutputPath, output, cancellationToken, bytes =>
                        ReportProgress(new HostProgressEvent(requestId, "export", "copying", Current: ordinal + 1,
                            Total: requests.Count, Details: new { bytesCopied = bytes }, Phase: "copy")));
                    preparedCount++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    failures.Add(new(target.Avid.ToString(), int.TryParse(target.SegmentKey, out var page) ? page : null,
                        BoundWireString(cache.Title), BoundWireString(exception.Message)));
                }
            }
            if (failures.Count > 0) return new ExportBatchResultDto(null, 0, failures, false);
            cancellationToken.ThrowIfCancellationRequested();
            if (stagingDirectory is not null)
            {
                Directory.Move(stagingDirectory, destination);
                stagingDirectory = null;
            }
            else if (stagingFile is not null)
            {
                File.Move(stagingFile, destination, overwrite: true);
                stagingFile = null;
            }
            return new ExportBatchResultDto(destination, preparedCount, failures, true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            failures.Add(new("", null, "", BoundWireString(exception.Message)));
            return new ExportBatchResultDto(null, 0, failures, false);
        }
        finally
        {
            if (stagingDirectory is not null) TryDeleteDirectory(stagingDirectory);
            if (stagingFile is not null) TryDelete(stagingFile);
        }
    }

    private async Task<object> ExportDiagnosticsAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var destination = parameters.RequireString("outputPath");
        var sessionRootPath = parameters.OptionalString("rootPath");
        return await _diagnosticExporter.ExportAsync(
            destination,
            _settingsStore.GetState(),
            GetSessionState(),
            sessionRootPath,
            cancellationToken);
    }
}
