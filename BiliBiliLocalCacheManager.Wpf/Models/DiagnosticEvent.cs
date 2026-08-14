namespace BiliBiliLocalCacheManager.Wpf.Models;

public sealed record DiagnosticEvent(
    DateTimeOffset TimestampUtc,
    string Category,
    DiagnosticEventLevel Level,
    string Message,
    string? ExceptionType = null);
