using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Desktop.Host;

internal sealed partial class DesktopHostApplication
{
    private static CacheSearchScope ParseSearchScope(
        JsonElement parameters,
        DesktopSettings settings)
    {
        var values = parameters.OptionalArray("scope");
        if (values.Count > 0)
        {
            var scope = CacheSearchScope.None;
            foreach (var value in values)
            {
                if (value.ValueKind != JsonValueKind.String)
                {
                    throw new RpcException("invalid_params", "Every scope item must be a string.");
                }

                scope |= value.GetString()?.Trim().ToLowerInvariant() switch
                {
                    "title" => CacheSearchScope.Title,
                    "partname" or "part" => CacheSearchScope.PartName,
                    "ownername" or "owner" => CacheSearchScope.OwnerName,
                    "bvid" => CacheSearchScope.Bvid,
                    "avid" => CacheSearchScope.Avid,
                    _ => throw new RpcException(
                        "invalid_params",
                        $"Unsupported search scope '{value.GetString()}'.")
                };
            }

            if (scope == CacheSearchScope.None)
            {
                throw new RpcException("invalid_params", "Search scope must not be empty.");
            }

            return scope;
        }

        var result = CacheSearchScope.Title;
        if (parameters.OptionalBoolean("includePartName") ?? settings.IncludePartName)
        {
            result |= CacheSearchScope.PartName;
        }

        if (parameters.OptionalBoolean("includeOwnerName") ?? settings.IncludeOwnerName)
        {
            result |= CacheSearchScope.OwnerName;
        }

        if (parameters.OptionalBoolean("includeBvid") ?? settings.IncludeBvid)
        {
            result |= CacheSearchScope.Bvid;
        }

        if (parameters.OptionalBoolean("includeAvid") ?? settings.IncludeAvid)
        {
            result |= CacheSearchScope.Avid;
        }

        return result;
    }

    private static IReadOnlyList<SelectionTargetRequest> ParseSelectionTargets(JsonElement parameters)
    {
        var targetElements = parameters.OptionalArray("targets");
        if (targetElements.Count is 0 or > 1000)
        {
            throw new RpcException("invalid_params", "targets must contain between 1 and 1000 items.");
        }

        var targets = targetElements.Select(target =>
        {
            if (target.ValueKind != JsonValueKind.Object)
            {
                throw new RpcException("invalid_params", "Every selection target must be an object.");
            }

            var avid = ParseAvid(target.RequireString("avid"));
            var pageElements = target.OptionalArray("pageIndexes");
            IReadOnlyList<int>? pageIndexes = null;
            if (pageElements.Count > 0)
            {
                if (pageElements.Count > 10_000)
                {
                    throw new RpcException("invalid_params", "pageIndexes may not exceed 10000 items.");
                }

                pageIndexes = pageElements.Select(page =>
                {
                    if (page.ValueKind != JsonValueKind.Number ||
                        !page.TryGetInt32(out var pageIndex) ||
                        pageIndex < 0)
                    {
                        throw new RpcException(
                            "invalid_params",
                            "Every pageIndexes item must be a non-negative integer.");
                    }

                    return pageIndex;
                }).Distinct().ToArray();
            }

            return new SelectionTargetRequest(avid, pageIndexes);
        }).Distinct().ToArray();
        return targets;
    }

    private IReadOnlyList<ExportTargetRequest> ExpandExportTargets(
        CacheIndex index,
        IReadOnlyList<SelectionTargetRequest> selections)
    {
        var targets = new List<ExportTargetRequest>();
        foreach (var selection in selections)
        {
            if (!index.ByAvid.TryGetValue(selection.Avid, out var cache))
            {
                continue;
            }

            var pageIndexes = selection.PageIndexes is { Count: > 0 }
                ? selection.PageIndexes
                : _playbackService.CreatePagePlans(cache).Select(plan => plan.PageIndex).ToArray();
            targets.AddRange(pageIndexes.Select(pageIndex => new ExportTargetRequest(
                selection.Avid,
                pageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        return targets.Distinct().ToArray();
    }

    private static IReadOnlyList<string> ParseRequiredStringArray(
        JsonElement parameters,
        string propertyName,
        int maximumCount)
    {
        var values = ParseOptionalStringArray(parameters, propertyName, maximumCount);
        if (values.Count == 0)
        {
            throw new RpcException("invalid_params", $"{propertyName} must contain at least one item.");
        }

        return values;
    }

    private static IReadOnlyList<string> ParseOptionalStringArray(
        JsonElement parameters,
        string propertyName,
        int maximumCount)
    {
        var elements = parameters.OptionalArray(propertyName);
        if (elements.Count > maximumCount)
        {
            throw new RpcException(
                "invalid_params",
                $"{propertyName} may not exceed {maximumCount} items.");
        }

        return elements.Select(element =>
        {
            if (element.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(element.GetString()))
            {
                throw new RpcException(
                    "invalid_params",
                    $"Every {propertyName} item must be a non-empty string.");
            }

            return element.GetString()!.Trim();
        }).ToArray();
    }

    private static long ParseAvid(string value)
    {
        var normalized = value.StartsWith("av", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;
        if (!long.TryParse(
                normalized,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var avid) || avid < 0)
        {
            throw new RpcException("invalid_params", $"Invalid avid '{value}'.");
        }

        return avid;
    }

    private static CacheSearchMatchMode ParseWireMatchMode(
        string? value,
        CacheSearchMatchMode defaultValue)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" => defaultValue,
            "contains" => CacheSearchMatchMode.Contains,
            "prefix" => CacheSearchMatchMode.StartsWith,
            "exact" => CacheSearchMatchMode.Equals,
            _ => throw new RpcException("invalid_params", $"Unsupported matchMode '{value}'.")
        };
    }

    private static PlaybackPlayerPreference ParseWirePlayerPreference(
        string? value,
        PlaybackPlayerPreference defaultValue)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" => defaultValue,
            "system" => PlaybackPlayerPreference.SystemDefaultFirst,
            "mpv" => PlaybackPlayerPreference.Mpv,
            "vlc" => PlaybackPlayerPreference.Vlc,
            _ => throw new RpcException("invalid_params", $"Unsupported playerPreference '{value}'.")
        };
    }
}
