using BiliBiliLocalCacheManager.Wpf.Models;

namespace BiliBiliLocalCacheManager.Wpf.Services;

public interface IDiagnosticReportService
{
    Task<DiagnosticReportResult> ExportAsync(
        DiagnosticReportRequest request,
        CancellationToken cancellationToken = default);
}
