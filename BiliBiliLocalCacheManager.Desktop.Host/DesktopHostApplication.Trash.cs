using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;

namespace BiliBiliLocalCacheManager.Desktop.Host;

internal sealed partial class DesktopHostApplication
{
    private async Task<object> MoveToTrashAsync(
        string requestId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var root = ResolveRequiredRoot(parameters, _settingsStore.GetState().Settings);
        CurrentIndexSnapshot? snapshot = null;
        IReadOnlyList<TrashMoveTarget> targets;
        if (parameters.TryGetProperty("targets", out _))
        {
            snapshot = ResolveCurrentIndex(RequireIndexToken(parameters));
            if (!PathsEqual(snapshot.Root, root)) throw StaleIndexException();
            var selections = ParseSelectionTargets(parameters);
            if (selections.Any(target => target.PageIndexes is { Count: > 0 }) &&
                selections.Any(target => target.PageIndexes is not { Count: > 0 }))
                throw new RpcException("invalid_params", "Whole-video and part selections cannot be mixed.");
            var resolved = new List<TrashMoveTarget>();
            foreach (var selection in selections)
            {
                if (selection.PageIndexes is { Count: > 0 } pages)
                {
                    foreach (var page in pages)
                    {
                        var captured = snapshot.GetTrashPart(selection.Avid, page);
                        resolved.Add(new(selection.Avid, page, captured.Target, captured.Error));
                    }
                }
                else resolved.Add(new(selection.Avid, null, null,
                    snapshot.Index.ByAvid.ContainsKey(selection.Avid) ? null : "当前索引中不存在该视频。"));
                if (resolved.Count > 20_000) throw new RpcException("invalid_params", "Trash selection may not exceed 20000 items.");
            }
            targets = resolved.DistinctBy(target => (target.Avid, target.PageIndex)).ToArray();
        }
        else
        {
            targets = ParseRequiredStringArray(parameters, "avids", maximumCount: 1000)
                .Select(ParseAvid).Distinct().Select(avid => new TrashMoveTarget(avid, null, null, null)).ToArray();
        }
        ValidateTrashMutationResponseBudget(Enumerable.Repeat(
            Path.Combine(_trashService.GetTrashDirectory(root), new string('x', 150)), targets.Count));
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (snapshot is not null) EnsureIndexStillCurrent(snapshot);
            var result = await Task.Run(() =>
            {
                var moved = new List<string>();
                var failed = new List<string>();
                var entryIds = new List<string>();
                var items = new List<TrashMoveItem>();
                foreach (var target in targets)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    var avidText = target.Avid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    CacheTrashOperationResult item;
                    try
                    {
                        if (target.Error is not null)
                        {
                            failed.Add(target.Id);
                            items.Add(new(avidText, target.PageIndex, false, null, BoundTrashError(target.Error)));
                            ReportTrashMutationProgress(requestId, "trash.move", moved.Count + failed.Count, targets.Count);
                            continue;
                        }
                        item = target.PageIndex is null
                            ? _trashService.MoveToTrash(root, target.Avid)
                            : _trashService.MovePartToTrash(root, target.Part!);
                    }
                    catch (Exception exception) when (IsTrashItemFailure(exception))
                    {
                        failed.Add(target.Id);
                        items.Add(new(avidText, target.PageIndex, false, null, BoundTrashError(exception.Message)));
                        _eventRecorder.Record("Trash", "Warning", $"Failed to move {avidText}: {exception.Message}", exception);
                        ReportTrashMutationProgress(requestId, "trash.move", moved.Count + failed.Count, targets.Count);
                        continue;
                    }

                    if (item.Succeeded)
                    {
                        moved.Add(target.Id);
                        if (item.TrashPath is not null) entryIds.Add(item.TrashPath);
                        items.Add(new(avidText, target.PageIndex, true, item.TrashPath, BoundTrashError(item.ErrorMessage)));
                        ClearCurrentIndex();
                    }
                    else
                    {
                        failed.Add(target.Id);
                        items.Add(new(avidText, target.PageIndex, false, null, BoundTrashError(item.ErrorMessage ?? "缓存目录不存在或无法移动。")));
                    }
                    ReportTrashMutationProgress(requestId, "trash.move", moved.Count + failed.Count, targets.Count);
                }

                var unprocessed = targets.Skip(moved.Count + failed.Count).Select(target => target.Id).ToArray();
                return new { moved, failed, unprocessed, cancelled = unprocessed.Length > 0, entryIds, items };
            }, cancellationToken);

            return result;
        }
        finally
        {
            InvalidateTrashSnapshots();
            _mutationGate.Release();
        }
    }

    private sealed record TrashMoveTarget(long Avid, int? PageIndex, CacheTrashPartTarget? Part, string? Error)
    {
        public string Id => PageIndex is { } page
            ? $"{Avid.ToString(System.Globalization.CultureInfo.InvariantCulture)}:P{page}"
            : Avid.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record TrashMoveItem(string Avid, int? PageIndex, bool Succeeded, string? EntryId, string? Error);

    private static string? BoundTrashError(string? error)
    {
        if (error is not { Length: > 128 }) return error;
        var length = char.IsHighSurrogate(error[126]) ? 126 : 127;
        return error[..length] + "…";
    }

    private static bool HasTrashPurgeConfirmation(JsonElement parameters)
        => parameters.OptionalBoolean("confirmed") == true &&
            parameters.TryGetProperty("confirmationText", out var text) &&
            text.ValueKind == JsonValueKind.String && text.GetString() == "永久删除";

    private static void ValidateTrashMutationResponseBudget(IEnumerable<string> entryIds)
    {
        long maximumBytes = 0;
        foreach (var id in entryIds)
        {
            ValidateTrashWirePath(id);
            // Each identity can appear in both the summary and detail; assume JSON Unicode escaping.
            maximumBytes += id.Length * 12L + 128 * 6 + 256;
            if (maximumBytes > 48L * 1024 * 1024)
                throw new RpcException("invalid_params", "The selected trash operation is too large to report safely. Select a smaller batch.");
        }
    }

    private async Task<object> ListTrashAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var root = ResolveRequiredRoot(parameters, _settingsStore.GetState().Settings);
        return await Task.Run<object>(() =>
        {
            var entries = _trashService.ListEntries(root, cancellationToken);
            if (entries.Count > 1000) throw new RpcException("pagination_required", "The trash contains more than 1000 entries. Use trash.page to load it safely.");
            return MapTrashEntries(entries);
        }, cancellationToken);
    }

    private async Task<object> RestoreTrashAsync(
        string requestId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var root = ResolveRequiredRoot(parameters, _settingsStore.GetState().Settings);
        var entryIds = ParseRequiredStringArray(parameters, "entryIds", maximumCount: 20_000)
            .Distinct(PathComparer)
            .ToArray();
        ValidateTrashMutationResponseBudget(entryIds);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var result = await Task.Run(() =>
            {
                var entries = _trashService.ListEntries(root, cancellationToken)
                    .ToDictionary(entry => entry.TrashPath, PathComparer);
                var restored = new List<string>();
                var failed = new List<string>();
                var items = new List<TrashRestoreItem>();
                foreach (var entryId in entryIds)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    if (!entries.TryGetValue(entryId, out var entry) || !entry.IsRestorable)
                    {
                        failed.Add(entryId);
                        items.Add(new(entryId, entry?.Avid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            entry?.PageIndex, false, BoundTrashError(entry?.UnavailableReason ?? "回收站条目已不存在。")));
                        ReportTrashMutationProgress(requestId, "trash.restore", restored.Count + failed.Count, entryIds.Length);
                        continue;
                    }

                    CacheTrashOperationResult operation;
                    try
                    {
                        operation = _trashService.Restore(root, entry.Avid, entry.TrashPath);
                    }
                    catch (Exception exception) when (IsTrashItemFailure(exception))
                    {
                        failed.Add(entryId);
                        items.Add(new(entryId, entry.Avid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            entry.PageIndex, false, BoundTrashError(exception.Message)));
                        _eventRecorder.Record("Trash", "Warning", $"Failed to restore {entryId}: {exception.Message}", exception);
                        ReportTrashMutationProgress(requestId, "trash.restore", restored.Count + failed.Count, entryIds.Length);
                        continue;
                    }

                    if (operation.Succeeded)
                    {
                        restored.Add(entryId);
                        items.Add(new(entryId, entry.Avid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            entry.PageIndex, true, BoundTrashError(operation.ErrorMessage)));
                        ClearCurrentIndex();
                    }
                    else
                    {
                        failed.Add(entryId);
                        items.Add(new(entryId, entry.Avid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            entry.PageIndex, false, BoundTrashError(operation.ErrorMessage ?? "无法恢复该条目。")));
                    }
                    ReportTrashMutationProgress(requestId, "trash.restore", restored.Count + failed.Count, entryIds.Length);
                }

                var unprocessed = entryIds.Skip(restored.Count + failed.Count).ToArray();
                return new { restored, failed, unprocessed, cancelled = unprocessed.Length > 0, items };
            }, cancellationToken);

            return result;
        }
        finally
        {
            InvalidateTrashSnapshots();
            _mutationGate.Release();
        }
    }

    private void ReportTrashMutationProgress(string requestId, string operation, int processed, int total)
        => ReportProgress(new HostProgressEvent(requestId, operation, "Processed cache entry", Current: processed,
            Total: total, Details: new { itemsProcessed = processed }, Phase: "measure"));

    private static bool IsTrashItemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or TimeoutException or System.Security.SecurityException ||
        // The trash service uses InvalidOperationException to reject unsafe filesystem identities.
        exception.GetType() == typeof(InvalidOperationException);

    private sealed record TrashRestoreItem(string EntryId, string? Avid, int? PageIndex, bool Succeeded, string? Error);

    private async Task<object> PurgeTrashAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new RpcException(
                "unsupported_platform",
                "Permanent trash purge is disabled outside Windows until Unix physical-directory safety is implemented.",
                new { capability = "trashPurge", supported = false });
        }

        if (!HasTrashPurgeConfirmation(parameters))
        {
            throw new RpcException(
                "confirmation_required",
                "trash.purge requires confirmed=true and the exact confirmation text because it is irreversible.");
        }

        var root = ResolveExplicitRequiredRoot(parameters);
        var requestedIds = ParseRequiredStringArray(parameters, "entryIds", maximumCount: 10_000)
            .Distinct(PathComparer)
            .ToArray();
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            return await PurgeTrashEntriesAsync(root, requestedIds, cancellationToken);
        }
        finally
        {
            InvalidateTrashSnapshots();
            _mutationGate.Release();
        }
    }

    private async Task<object> PurgeTrashEntriesAsync(string root, string[] requestedIds, CancellationToken cancellationToken)
    {
        return await Task.Run<object>(() =>
            {
                var selectedIds = requestedIds.ToHashSet(PathComparer);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _trashService.Purge(
                        root,
                        includeUntrustedLegacyEntries: false,
                        expectedEntryIds: requestedIds);
                }
                catch (CacheTrashSnapshotMismatchException exception)
                {
                    throw new RpcException(
                        "unsupported_operation",
                        "The trash contents changed or the selection is incomplete. " +
                        "Reload the trash and confirm permanent deletion again.",
                        new
                        {
                            reason = "trash_snapshot_changed",
                            exception.ExpectedEntryCount,
                            exception.ActualEntryCount
                        });
                }

                // Purge is a non-cancellable transaction. Reconcile its committed
                // outcome even if cancellation arrived while deletion was running.
                var remaining = _trashService.ListEntries(root, CancellationToken.None)
                    .Select(entry => entry.TrashPath)
                    .ToHashSet(PathComparer);
                var purged = selectedIds.Where(id => !remaining.Contains(id)).ToArray();
                var failed = selectedIds.Where(remaining.Contains).ToArray();
                return new { purged, failed, unprocessed = Array.Empty<string>(), cancelled = false };
            }, cancellationToken);
    }
}
