using System.Text.Json;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;

namespace BiliBiliLocalCacheManager.Desktop.Host;

internal sealed partial class DesktopHostApplication
{
    // Preparation holds only requested IDs. Restore still enumerates and checks the live trash
    // under its mutation gate. No preparation request can touch a trash entry.
    private readonly Dictionary<string, PreparedTrashRestore> _preparedTrashRestores = new();
    private sealed class PreparedTrashRestore(string root, int total)
    {
        public string Root { get; } = root;
        public int Total { get; } = total;
        public List<string> EntryIds { get; } = [];
        public long ResponseBudget { get; set; }
        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
    }

    private object PrepareTrashRestore(JsonElement parameters, CancellationToken cancellationToken)
    {
        var root = ResolveExplicitRequiredRoot(parameters);
        var requestId = parameters.RequireString("restoreRequestId");
        var offset = parameters.OptionalInt32("offset") ?? -1;
        var total = parameters.OptionalInt32("total") ?? 0;
        var ids = ParseRequiredStringArray(parameters, "entryIds", 20_000);
        if (!Guid.TryParseExact(requestId, "D", out _) || total is < 1 or > 20_000 ||
            offset < 0 || offset > total - ids.Count)
            throw new RpcException("invalid_params", "Invalid prepared restore range.");

        lock (_preparedTrashRestores)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExpirePreparedRestores();
            if (!_preparedTrashRestores.TryGetValue(requestId, out var prepared))
            {
                if (offset != 0) throw InvalidPreparedRestore();
                prepared = new(root, total);
            }
            if (!PathsEqual(prepared.Root, root) || prepared.Total != total || prepared.EntryIds.Count != offset)
                throw InvalidPreparedRestore();
            var budget = ValidateTrashMutationResponseBudget(ids, prepared.ResponseBudget);
            if (offset == 0 && _preparedTrashRestores.Count >= 8)
                _preparedTrashRestores.Remove(_preparedTrashRestores.MinBy(pair => pair.Value.LastUsed).Key);
            prepared.EntryIds.AddRange(ids);
            prepared.ResponseBudget = budget;
            prepared.LastUsed = DateTimeOffset.UtcNow;
            _preparedTrashRestores[requestId] = prepared;
            return new { receivedCount = prepared.EntryIds.Count };
        }
    }

    private IReadOnlyList<string> TakePreparedTrashRestore(string root, string requestId, int count, JsonElement parameters)
    {
        lock (_preparedTrashRestores)
        {
            ExpirePreparedRestores();
            if (parameters.TryGetPropertyIgnoreCase("entryIds", out _) ||
                !_preparedTrashRestores.TryGetValue(requestId, out var prepared) || !PathsEqual(prepared.Root, root) ||
                prepared.Total != count || prepared.EntryIds.Count != count)
                throw InvalidPreparedRestore();
            _preparedTrashRestores.Remove(requestId);
            return prepared.EntryIds;
        }
    }

    private void ExpirePreparedRestores()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
        foreach (var pair in _preparedTrashRestores.Where(pair => pair.Value.LastUsed <= cutoff).ToArray())
            _preparedTrashRestores.Remove(pair.Key);
    }

    private static RpcException InvalidPreparedRestore() =>
        new("invalid_params", "The exact restore selection is incomplete, expired, or belongs to another request or root.");
}
