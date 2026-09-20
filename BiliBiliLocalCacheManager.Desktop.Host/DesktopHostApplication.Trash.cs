using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;

namespace BiliBiliLocalCacheManager.Desktop.Host;

internal sealed partial class DesktopHostApplication
{
    private async Task<object> MoveToTrashAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var root = ResolveRequiredRoot(parameters, _settingsStore.GetState().Settings);
        var avids = ParseRequiredStringArray(parameters, "avids", maximumCount: 1000)
            .Select(ParseAvid)
            .Distinct()
            .ToArray();
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var result = await Task.Run(() =>
            {
                var moved = new List<string>();
                var failed = new List<string>();
                foreach (var avid in avids)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    var avidText = avid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    CacheTrashOperationResult item;
                    try
                    {
                        item = _trashService.MoveToTrash(root, avid);
                    }
                    catch (Exception exception) when (IsTrashItemFailure(exception))
                    {
                        failed.Add(avidText);
                        _eventRecorder.Record("Trash", "Warning", $"Failed to move {avidText}: {exception.Message}", exception);
                        continue;
                    }

                    if (item.Succeeded)
                    {
                        moved.Add(avidText);
                        ClearCurrentIndex();
                    }
                    else
                    {
                        failed.Add(avidText);
                    }
                }

                var unprocessed = avids.Skip(moved.Count + failed.Count)
                    .Select(avid => avid.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                return new { moved, failed, unprocessed, cancelled = unprocessed.Length > 0 };
            }, cancellationToken);

            return result;
        }
        finally
        {
            InvalidateTrashSnapshots();
            _mutationGate.Release();
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
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var root = ResolveRequiredRoot(parameters, _settingsStore.GetState().Settings);
        var entryIds = ParseRequiredStringArray(parameters, "entryIds", maximumCount: 1000)
            .Distinct(PathComparer)
            .ToArray();
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var result = await Task.Run(() =>
            {
                var entries = _trashService.ListEntries(root, cancellationToken)
                    .ToDictionary(entry => entry.TrashPath, PathComparer);
                var restored = new List<string>();
                var failed = new List<string>();
                foreach (var entryId in entryIds)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    if (!entries.TryGetValue(entryId, out var entry) || !entry.IsRestorable)
                    {
                        failed.Add(entryId);
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
                        _eventRecorder.Record("Trash", "Warning", $"Failed to restore {entryId}: {exception.Message}", exception);
                        continue;
                    }

                    if (operation.Succeeded)
                    {
                        restored.Add(entryId);
                        ClearCurrentIndex();
                    }
                    else
                    {
                        failed.Add(entryId);
                    }
                }

                var unprocessed = entryIds.Skip(restored.Count + failed.Count).ToArray();
                return new { restored, failed, unprocessed, cancelled = unprocessed.Length > 0 };
            }, cancellationToken);

            return result;
        }
        finally
        {
            InvalidateTrashSnapshots();
            _mutationGate.Release();
        }
    }

    private static bool IsTrashItemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or TimeoutException or System.Security.SecurityException ||
        // The trash service uses InvalidOperationException to reject unsafe filesystem identities.
        exception.GetType() == typeof(InvalidOperationException);

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

        if (parameters.OptionalBoolean("confirmed") != true)
        {
            throw new RpcException(
                "confirmation_required",
                "trash.purge requires params.confirmed=true because it is irreversible.");
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
