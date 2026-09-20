using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;

namespace BiliBiliLocalCacheManager.Desktop.Host;

internal sealed partial class DesktopHostApplication
{
    private async Task<object> ScanAsync(
        string requestId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var generation = ReadIndexGeneration();
        await _indexBuildGate.WaitAsync(cancellationToken);
        try
        {
            EnsureIndexGeneration(generation);
            var pagination = ParsePagination(parameters);
            var settings = _settingsStore.GetState().Settings;
            var root = ResolveRequiredRoot(parameters, settings);
            var includeIncomplete = parameters.OptionalBoolean("includeIncomplete") ??
                                    settings.IncludeIncomplete;
            var persistSettings = parameters.OptionalBoolean("persistSettings") ?? true;
            var maxReportedIssues = parameters.OptionalInt32("maxReportedIssues") ?? 100;
            if (maxReportedIssues is < 0 or > 10_000)
            {
                throw new RpcException(
                    "invalid_params",
                    "maxReportedIssues must be between 0 and 10000.");
            }

            var options = new CacheIndexBuildOptions
            {
                IncludeIncompleteEntries = includeIncomplete,
                MaxReportedIssues = maxReportedIssues,
                MaximumCacheBytes = MaximumWireBytes
            };
            var progress = new InlineProgress<CacheScanProgress>(value =>
                ReportProgress(new HostProgressEvent(
                    requestId,
                    "scan",
                    "scanning",
                    Current: value.ProcessedAvidDirectories,
                    Message: value.CurrentPath,
                    Details: new
                    {
                        value.ProcessedAvidDirectories,
                        value.ProcessedSegmentDirectories,
                        value.IncludedEntries
                    }, Phase: "scan")));

            var report = await Task.Run(
                () => _cacheManager.BuildIndexWithReport(
                    root,
                    options,
                    cancellationToken,
                    progress),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var completedAt = DateTimeOffset.UtcNow;
            var snapshot = SetCurrentIndex(report.Index, root, includeIncomplete, completedAt,
                report.Issues, generation, persistSettings);
            var page = CreateCachePage(
                snapshot,
                snapshot.Index.VideoCaches,
                pagination.Offset,
                pagination.PageSize,
                cancellationToken);
            EnsureIndexStillCurrent(snapshot);
            ReportProgress(new HostProgressEvent(
                requestId,
                "scan",
                "completed",
                Percentage: 100,
                Current: page.Items.Count,
                Total: page.TotalItems));

            return new
            {
                rootPath = root,
                includeIncomplete,
                report.ScannedAvidDirectories,
                report.ScannedSegmentDirectories,
                report.IncludedEntries,
                report.SkippedIncompleteEntries,
                report.InvalidEntries,
                report.InaccessibleDirectories,
                report.HasWarnings,
                issues = snapshot.Issues.Select((issue, id) => new { id, kind = issue.Kind.ToString(), path = BoundWireString(issue.Path), message = BoundWireString(issue.Message) }).ToArray(),
                issuesTruncated = report.Issues.Count > 100 || report.InvalidEntries + report.InaccessibleDirectories > report.Issues.Count,
                page.IndexToken,
                page.Offset,
                page.PageSize,
                page.TotalItems,
                page.HasMore,
                page.Items,
                completedAtUtc = completedAt
            };
        }
        finally { _indexBuildGate.Release(); }
    }

    private async Task<object> SearchAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var indexToken = RequireIndexToken(parameters);
        var pagination = ParsePagination(parameters);
        var snapshot = ResolveCurrentIndex(indexToken);
        var keyword = parameters.OptionalString("keyword") ?? string.Empty;
        var settings = _settingsStore.GetState().Settings;

        var options = new CacheSearchOptions
        {
            Keyword = keyword,
            SplitKeywords = parameters.OptionalBoolean("splitKeywords") ?? settings.SplitKeywords,
            RequireAllKeywords = !(parameters.OptionalBoolean("anyKeywords") ?? settings.AnyKeywords),
            CaseSensitive = parameters.OptionalBoolean("caseSensitive") ?? settings.CaseSensitive,
            MatchMode = ParseWireMatchMode(parameters.OptionalString("matchMode"), settings.MatchMode),
            Scope = ParseSearchScope(parameters, settings)
        };

        cancellationToken.ThrowIfCancellationRequested();
        var page = await Task.Run(() => CreateCachePage(
            snapshot, snapshot.Search(options, cancellationToken), pagination.Offset, pagination.PageSize,
            cancellationToken), cancellationToken);
        EnsureIndexStillCurrent(snapshot);
        return page;
    }

    private async Task<object> GetCacheDetailsAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var indexToken = RequireIndexToken(parameters);
        var pagination = ParsePagination(parameters);
        var avid = ParseAvid(parameters.RequireString("avid"));
        var snapshot = ResolveCurrentIndex(indexToken);
        if (!snapshot.Index.ByAvid.TryGetValue(avid, out var cache))
        {
            throw new RpcException(
                "not_found",
                $"Cache av{avid} was not found in the current index.");
        }

        var details = await Task.Run(() => snapshot.GetDetailsPage(cache, pagination.Offset,
            pagination.PageSize, MapSegment, cancellationToken), cancellationToken);
        EnsureIndexStillCurrent(snapshot);
        return details;
    }
}
