namespace BiliBiliLocalCacheManager.Core.Application.Models;

public sealed record CacheScanIssue(CacheScanIssueKind Kind, string Path, string Message);
