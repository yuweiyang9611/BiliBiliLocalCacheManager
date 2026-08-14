using BiliBiliLocalCacheManager.Wpf.Models;

namespace BiliBiliLocalCacheManager.Wpf.Services;

public interface ISensitiveDataRedactor
{
    string Redact(string value, SensitiveDataRedactionContext context);
}
