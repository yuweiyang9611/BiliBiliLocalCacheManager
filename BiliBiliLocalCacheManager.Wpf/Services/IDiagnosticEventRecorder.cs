using BiliBiliLocalCacheManager.Wpf.Models;

namespace BiliBiliLocalCacheManager.Wpf.Services;

public interface IDiagnosticEventRecorder
{
    void Record(DiagnosticEvent diagnosticEvent);

    IReadOnlyList<DiagnosticEvent> GetRecentEvents();
}
