using System.Security.Cryptography;
using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;

namespace BiliBiliLocalCacheManager.Desktop.Host;

internal sealed partial class DesktopHostApplication
{
    private readonly LinkedList<TrashSnapshot> _trashSnapshots = new();
    private const long MaximumTrashIdentityBytes = 16 * 1024 * 1024;

    private async Task<object> ListTrashPageAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var root = ResolveRequiredRoot(parameters, _settingsStore.GetState().Settings);
        var pagination = ParsePagination(parameters);
        var token = parameters.OptionalString("snapshotToken");
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = token is null ? null : ResolveTrashSnapshot(root, token);
            IReadOnlyList<TrashEntryDto> page;
            if (snapshot?.Entries is { } cached)
                page = cached.Skip(pagination.Offset).Take(pagination.PageSize).ToArray();
            else
            {
                var entries = await Task.Run(() => _trashService.ListEntries(root, cancellationToken), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.Count > 200_000)
                    throw new RpcException("trash_too_large", "The trash snapshot exceeds the desktop entry limit.");
                long identityBytes = 0;
                long totalBytes = 0;
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateTrashWirePath(entry.TrashPath);
                    ValidateTrashWirePath(entry.OriginalPath);
                    identityBytes += TrashIdentityByteBudget(entry.TrashPath);
                    if (entry.TotalBytes < 0 || entry.TotalBytes > MaximumWireBytes - totalBytes)
                        throw new RpcException("trash_too_large", "The trash size exceeds the desktop numeric safety limit.");
                    totalBytes += entry.TotalBytes;
                }
                bool complete = identityBytes <= MaximumTrashIdentityBytes;
                // Oversized snapshots retain only a digest. Re-enumerate on page requests and reject
                // changes to entry order, identities or restore availability before publishing the page.
                var fingerprint = complete ? null : FingerprintTrashEntries(entries, cancellationToken);
                if (snapshot is null)
                {
                    snapshot = new TrashSnapshot(Guid.NewGuid().ToString("N"), root,
                        complete ? MapTrashEntries(entries) : null, fingerprint, entries.Count, totalBytes);
                    _trashSnapshots.AddFirst(snapshot);
                    if (_trashSnapshots.Count > 8) _trashSnapshots.RemoveLast();
                }
                else if (snapshot.Fingerprint != fingerprint)
                {
                    _trashSnapshots.Remove(snapshot);
                    throw new RpcException("stale_trash", "The trash contents changed. Reload the trash before continuing.");
                }
                page = snapshot.Entries is { } rows
                    ? rows.Skip(pagination.Offset).Take(pagination.PageSize).ToArray()
                    : MapTrashEntries(entries.Skip(pagination.Offset).Take(pagination.PageSize));
            }
            return new
            {
                snapshotToken = snapshot.Token,
                pagination.Offset,
                pagination.PageSize,
                totalItems = snapshot.TotalItems,
                hasMore = HasMore(pagination.Offset, pagination.PageSize, snapshot.TotalItems),
                canPurgeSnapshot = snapshot.Entries is not null,
                totalSizeBytes = snapshot.TotalSizeBytes,
                items = page
            };
        }
        finally { _mutationGate.Release(); }
    }

    private async Task<object> PurgeTrashSnapshotAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new RpcException("unsupported_platform", "Permanent trash purge is disabled outside Windows.");
        if (!HasTrashPurgeConfirmation(parameters))
            throw new RpcException("confirmation_required", "trash.purgeSnapshot requires params.confirmed=true because it is irreversible.");
        var root = ResolveExplicitRequiredRoot(parameters);
        var token = parameters.RequireString("snapshotToken");
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = ResolveTrashSnapshot(root, token);
            if (snapshot.Entries is null) throw OversizedPurgeError();
            try
            {
                return await PurgeTrashEntriesAsync(root, snapshot.Entries.Select(entry => entry.Id).ToArray(), cancellationToken);
            }
            finally { InvalidateTrashSnapshots(); }
        }
        catch (RpcException exception) when (exception.Code == "unsupported_operation")
        {
            throw new RpcException("stale_trash", exception.Message, exception.Details);
        }
        finally { _mutationGate.Release(); }
    }

    private static long TrashIdentityByteBudget(string id) => (long)id.Length * 6 + 3;

    private static RpcException OversizedPurgeError() => new("trash_too_large",
        "The full trash identity list exceeds the permanent deletion limit. Restore entries in batches before retrying.");

    private static string FingerprintTrashEntries(IReadOnlyList<CacheTrashEntry> entries, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(entry));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private TrashSnapshot ResolveTrashSnapshot(string root, string token)
    {
        if (token.Length > MaximumIndexTokenLength) throw new RpcException("invalid_params", "The trash snapshot token is too long.");
        var node = _trashSnapshots.First;
        while (node is not null)
        {
            if (node.Value.Token == token && PathsEqual(node.Value.Root, root))
            {
                var snapshot = node.Value;
                _trashSnapshots.Remove(node);
                _trashSnapshots.AddFirst(node);
                return snapshot;
            }
            node = node.Next;
        }
        throw new RpcException("stale_trash", "The trash snapshot is no longer current. Reload the trash before continuing.");
    }

    private static void ValidateTrashWirePath(string path)
    {
        if (path.Length > MaximumWireTextLength)
            throw new RpcException("unsafe_path", "A trash path exceeds the desktop protocol limit; its identity cannot be truncated safely.");
    }

    // Callers hold the mutation gate, so no page can capture a partially committed host mutation.
    private void InvalidateTrashSnapshots() => _trashSnapshots.Clear();

    private sealed record TrashSnapshot(string Token, string Root, IReadOnlyList<TrashEntryDto>? Entries,
        string? Fingerprint, int TotalItems, long TotalSizeBytes);
}
