namespace BiliBiliLocalCacheManager.Desktop.Host.Services;

internal sealed class DiagnosticEventRecorder
{
    private const int Capacity = 100;
    private const int TextLimit = 4096;
    private readonly object _syncRoot = new();
    private readonly Queue<DiagnosticEvent> _events = new(Capacity);

    public void Record(string category, string level, string message, Exception? exception = null)
    {
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
