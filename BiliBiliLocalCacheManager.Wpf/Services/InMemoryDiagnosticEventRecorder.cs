using BiliBiliLocalCacheManager.Wpf.Models;

namespace BiliBiliLocalCacheManager.Wpf.Services;

public sealed class InMemoryDiagnosticEventRecorder : IDiagnosticEventRecorder
{
    public const int DefaultCapacity = 100;
    public const int DefaultTextLengthLimit = 4096;

    private readonly object _syncRoot = new();
    private readonly Queue<DiagnosticEvent> _events;
    private readonly int _capacity;
    private readonly int _textLengthLimit;

    public InMemoryDiagnosticEventRecorder(
        int capacity = DefaultCapacity,
        int textLengthLimit = DefaultTextLengthLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(textLengthLimit);
        _capacity = capacity;
        _textLengthLimit = textLengthLimit;
        _events = new Queue<DiagnosticEvent>(Math.Min(capacity, DefaultCapacity));
    }

    public void Record(DiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        var normalized = diagnosticEvent with
        {
            TimestampUtc = diagnosticEvent.TimestampUtc.ToUniversalTime(),
            Category = Limit(diagnosticEvent.Category),
            Message = Limit(diagnosticEvent.Message),
            ExceptionType = LimitNullable(diagnosticEvent.ExceptionType)
        };

        lock (_syncRoot)
        {
            while (_events.Count >= _capacity)
            {
                _events.Dequeue();
            }

            _events.Enqueue(normalized);
        }
    }

    public IReadOnlyList<DiagnosticEvent> GetRecentEvents()
    {
        lock (_syncRoot)
        {
            return _events.ToArray();
        }
    }

    private string Limit(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= _textLengthLimit
            ? value
            : value[..(_textLengthLimit - 1)] + "\u2026";
    }

    private string? LimitNullable(string? value)
    {
        return value is null ? null : Limit(value);
    }
}
