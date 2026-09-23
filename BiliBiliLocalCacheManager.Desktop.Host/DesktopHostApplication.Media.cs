using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;
using BiliBiliLocalCacheManager.Desktop.Host.Services;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;

namespace BiliBiliLocalCacheManager.Desktop.Host;

internal sealed partial class DesktopHostApplication
{
    private ICacheExportMaterializationService? _exportService;

    private async Task<object> PlayAsync(
        string requestId, JsonElement parameters, CancellationToken cancellationToken)
    {
        var settings = _settingsStore.GetState().Settings;
        var root = ResolveRequiredRoot(parameters, settings);
        var includeIncomplete = parameters.OptionalBoolean("includeIncomplete") ?? settings.IncludeIncomplete;
        var selections = ParseSelectionTargets(parameters);
        var player = ParseWirePlayerPreference(parameters.OptionalString("playerPreference"), settings.PreferredPlayer);
        var index = await ResolveMediaIndexAsync(requestId, "play", parameters, root, includeIncomplete, cancellationToken);
        var failures = new List<MediaFailureDto>();
        var targets = OrderMediaTargets(index, selections, failures);
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
        if (!Directory.Exists(requestedOutputPath))
            throw new DirectoryNotFoundException($"Export destination must be an existing directory: {requestedOutputPath}");
        var approvals = ParseOptionalStringArray(parameters, "transcodeApprovals", 10000);
        if (approvals.Any(value => value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character))))
            throw new RpcException("invalid_params", "Every transcode approval must be a 64-character hexadecimal token.");
        var index = await ResolveMediaIndexAsync(requestId, "export", parameters, root, includeIncomplete, cancellationToken);
        var failures = new List<MediaFailureDto>();
        var requirements = new List<ExportTranscodeRequirementDto>();
        var requests = OrderMediaTargets(index, selections, failures);
        if (requests.Count == 0 || failures.Count > 0) return new ExportBatchResultDto(null, 0, failures, false, requirements);
        using var destinationGuard = new ExportDirectoryGuard(requestedOutputPath, allowRename: false);
        var destination = ResolveBatchExportDirectory(requestedOutputPath);
        string? stagingDirectory = CreateBatchExportStagingDirectory(destination);
        using var stagingGuard = new ExportDirectoryGuard(stagingDirectory, allowRename: true);
        var exportService = _exportService ??= new CacheExportService(_artifactStore);
        var store = new PlaybackArtifactStore(_artifactStore.RootDirectory);
        await using var protection = new PlaybackPreparationProtection(store, cancellationToken);
        var preparedCount = 0;
        var prepared = new List<(CachePlaybackPlan Plan, PlaybackMaterializationResult Result, string Output)>();
        var outputLeases = new List<FileStream>();
        try
        {
            for (var ordinal = 0; ordinal < requests.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = requests[ordinal];
                var cache = target.Cache;
                destinationGuard.Validate();
                stagingGuard.Validate();
                try
                {
                    var plan = _playbackService.CreatePagePlan(cache, target.Page.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var progress = new InlineProgress<PlaybackPreparationProgress>(value =>
                        ReportProgress(new HostProgressEvent(requestId, "export", value.Stage, value.Percentage,
                            ordinal + 1, requests.Count, $"av{cache.Avid} P{plan.PageIndex}",
                            new { processedSeconds = value.ProcessedSeconds, bytesProcessed = value.ProcessedBytes }, Phase: value.Phase)));
                    var materialization = await exportService.MaterializeAsync(plan.SelectedPlan, approvals, progress, protection.Token);
                    if (!materialization.Succeeded || string.IsNullOrWhiteSpace(materialization.OutputPath))
                        throw new IOException(materialization.Message);
                    protection.Register(materialization.OutputPath);
                    destinationGuard.Validate();
                    stagingGuard.Validate();
                    var videoDirectory = Path.Combine(stagingDirectory, PortableFileNaming.Build(cache.Title,
                        cache.Avid, plan.PageIndex, null, false) + "_AV" + cache.Avid);
                    Directory.CreateDirectory(videoDirectory);
                    using var videoGuard = new ExportDirectoryGuard(videoDirectory, allowRename: false);
                    var partName = PortableFileNaming.Build(plan.PartName, cache.Avid, plan.PageIndex, null, false);
                    var output = Path.Combine(videoDirectory, $"P{plan.PageIndex:D3}_{partName}.mp4");
                    await CopyAtomicallyAsync(materialization.OutputPath, output, protection.Token, bytes =>
                        ReportProgress(new HostProgressEvent(requestId, "export", "copying", Current: ordinal + 1,
                            Total: requests.Count, Details: new { bytesCopied = bytes }, Phase: "copy")));
                    preparedCount++;
                    prepared.Add((plan.SelectedPlan, materialization, output));
                }
                catch (ExportTranscodeRequiredException exception)
                {
                    var requirement = exception.Requirement;
                    requirements.Add(new(cache.Avid.ToString(), target.Page, BoundWireString(cache.Title),
                        requirement.ApprovalToken, requirement.ProcessingKind, BoundWireString(requirement.Reason), BoundWireString(requirement.Impact)));
                }
                catch (Exception) when (protection.Failure is not null)
                {
                    throw new IOException("Export preparation protection failed: " + protection.Failure.Message, protection.Failure);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    failures.Add(new(cache.Avid.ToString(), target.Page,
                        BoundWireString(cache.Title), BoundWireString(exception.Message)));
                }
            }
            if (failures.Count > 0 || requirements.Count > 0) return new ExportBatchResultDto(null, 0, failures, false, requirements);
            for (var ordinal = 0; ordinal < prepared.Count; ordinal++)
            {
                var item = prepared[ordinal];
                PlaybackBatchLauncher.ValidateLocalFile(item.Output);
                outputLeases.Add(new FileStream(item.Output, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
                var progress = new InlineProgress<PlaybackPreparationProgress>(value =>
                    ReportProgress(new HostProgressEvent(requestId, "export", value.Stage, Current: requests.Count + ordinal + 1,
                        Total: requests.Count * 2, Details: new { bytesProcessed = value.ProcessedBytes }, Phase: value.Phase)));
                await exportService.ValidatePreparedAsync(item.Plan, item.Result, progress, protection.Token, item.Output);
            }
            await protection.StopAsync();
            if (protection.Failure is not null) throw new IOException("Export preparation protection failed: " + protection.Failure.Message);
            cancellationToken.ThrowIfCancellationRequested();
            // Windows can refuse a parent-directory rename while a child file is open.
            // Keep outputs read-only through verification, then release just before publication.
            foreach (var lease in outputLeases) lease.Dispose();
            outputLeases.Clear();
            while (true)
            {
                try
                {
                    destinationGuard.Validate();
                    stagingGuard.Validate();
                    foreach (var item in prepared) PlaybackBatchLauncher.ValidateLocalFile(item.Output);
                    Directory.Move(stagingDirectory!, destination);
                    stagingDirectory = null;
                    break;
                }
                catch (IOException) when (Directory.Exists(destination) || File.Exists(destination))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    destination = ResolveBatchExportDirectory(requestedOutputPath);
                }
            }
            return new ExportBatchResultDto(destination, preparedCount, failures, true, requirements);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            failures.Add(new("", null, "", BoundWireString(exception.Message)));
            return new ExportBatchResultDto(null, 0, failures, false, requirements);
        }
        finally
        {
            foreach (var lease in outputLeases) lease.Dispose();
            if (stagingDirectory is not null && destinationGuard.IsCurrent && stagingGuard.IsCurrent)
                TryDeleteDirectory(stagingDirectory);
        }
    }

    private static List<(BiliVideoCache Cache, int Page)> OrderMediaTargets(CacheIndex index,
        IReadOnlyList<SelectionTargetRequest> selections, List<MediaFailureDto> failures)
    {
        var grouped = selections.GroupBy(selection => selection.Avid).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var missing in grouped.Keys.Where(avid => !index.ByAvid.ContainsKey(avid)))
            failures.Add(new(missing.ToString(), null, "", "Cache not found."));
        var targets = new List<(BiliVideoCache, int)>();
        foreach (var cache in index.VideoCaches)
        {
            if (!grouped.TryGetValue(cache.Avid, out var selected)) continue;
            var pages = selected.Any(selection => selection.PageIndexes is not { Count: > 0 })
                ? cache.Segments.Select(segment => segment.PageIndex)
                : selected.SelectMany(selection => selection.PageIndexes!);
            foreach (var page in pages.Distinct().Order())
            {
                if (targets.Count == 10000) throw new RpcException("invalid_params", "A media batch may contain at most 10000 parts; no items were submitted.");
                targets.Add((cache, page));
            }
        }
        return targets;
    }

    private Task<CacheIndex> ResolveMediaIndexAsync(string requestId, string operation, JsonElement parameters,
        string root, bool includeIncomplete, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!parameters.TryGetProperty("indexToken", out _))
            return ResolveIndexAsync(requestId, operation, root, includeIncomplete, cancellationToken);
        var token = RequireIndexToken(parameters);
        lock (_stateSync)
        {
            var snapshot = ResolveCurrentIndex(token);
            if (!PathsEqual(snapshot.Root, root) || _currentIncludeIncomplete != includeIncomplete)
                throw StaleIndexException();
            return Task.FromResult(snapshot.Index);
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
