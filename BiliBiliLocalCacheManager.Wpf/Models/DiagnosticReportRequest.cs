namespace BiliBiliLocalCacheManager.Wpf.Models;

public sealed record DiagnosticReportRequest(
    string DestinationPath,
    DiagnosticReportContext Context,
    SensitiveDataRedactionContext RedactionContext);
