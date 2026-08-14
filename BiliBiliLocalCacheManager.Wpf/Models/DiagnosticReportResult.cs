namespace BiliBiliLocalCacheManager.Wpf.Models;

public sealed record DiagnosticReportResult(
    string OutputPath,
    long FileSizeBytes,
    int EventCount);
