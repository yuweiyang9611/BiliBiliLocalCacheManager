namespace BiliBiliLocalCacheManager.Desktop.Host.Services;

internal sealed class DiagnosticEventRecorder
{
    private const int Capacity = 100;
    private const int TextLimit = 4096;
    private readonly object _syncRoot = new();
    private readonly Queue<DiagnosticEvent> _events = new(Capacity);
    private readonly AsyncLocal<string[]?> _requestRoots = new();

    public IDisposable WithCacheRoots(params string?[] roots)
    {
        var previous = _requestRoots.Value;
        _requestRoots.Value = roots.OfType<string>().Where(root => root.Length > 0)
            .Distinct().OrderByDescending(root => root.Length).ToArray();
        return new RestoreScope(() => _requestRoots.Value = previous);
    }

    private sealed class RestoreScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    public void Record(string category, string level, string message, Exception? exception = null)
    {
        foreach (var root in _requestRoots.Value ?? [])
            message = DiagnosticExporter.ReplaceKnownPath(message, root, "[CACHE_ROOT]");
        var item = new DiagnosticEvent(
            DateTimeOffset.UtcNow,
            Limit(category),
            Limit(level),
            Limit(message),
            exception?.GetType().FullName);
        lock (_syncRoot)
        {
            while (_events.Count >= Capacity)
            {
                _events.Dequeue();
            }

            _events.Enqueue(item);
        }
    }

    public IReadOnlyList<DiagnosticEvent> Snapshot()
    {
        lock (_syncRoot)
        {
            return _events.ToArray();
        }
    }

    private static string Limit(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= TextLimit ? value : value[..(TextLimit - 1)] + "…";
    }
}
