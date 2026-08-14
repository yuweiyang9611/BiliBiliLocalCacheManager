using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Services;

public sealed partial class CachePlaybackService : ICachePlaybackService
{
    private readonly IReadOnlyList<ICachePlaybackLayoutHandler> _handlers;
    private readonly IPlaybackMaterializer _materializer;
    private readonly IPlaybackLauncher _launcher;

    public CachePlaybackService()
        : this(
            [
                new HybridCLegacyCachePlaybackLayoutHandler(),
                new NewDashCachePlaybackLayoutHandler(),
                new MidDashCachePlaybackLayoutHandler(),
                new LegacyLuaCachePlaybackLayoutHandler()
            ],
            new CompositePlaybackMaterializer(
            [
                new SingleFilePlaybackMaterializer(),
                    new OrderedPairPlaybackMaterializer(),
                    new DashPairPlaybackMaterializer()
            ]),
            new SystemPlaybackLauncher())
    {
    }

    public CachePlaybackService(
        IEnumerable<ICachePlaybackLayoutHandler> handlers,
        IPlaybackMaterializer materializer,
        IPlaybackLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        _handlers = handlers
            .OrderByDescending(handler => handler.Priority)
            .ToList();

        if (_handlers.Count == 0)
        {
            throw new ArgumentException("At least one playback layout handler is required.", nameof(handlers));
        }

        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    }

    public CachePlaybackPlan CreatePlan(BiliSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        if (!IsPhysicalDirectory(segment.SegmentDirectory))
        {
            return CachePlaybackPlan.Unavailable(
                segment.Avid,
                segment.Title,
                segment.PageIndex,
                segment.PartName,
                Path.GetFileName(Path.TrimEndingDirectorySeparator(segment.SegmentDirectory)),
                segment.SegmentDirectory,
                "UnsafePath",
                "分段目录是符号链接、目录联接或不可访问目录，已拒绝从缓存范围外读取媒体。",
                segment.TotalDuration);
        }

        var probe = BuildProbe(segment);
        var handler = _handlers.FirstOrDefault(candidate => candidate.CanHandle(probe));

        if (handler is null)
        {
            return CachePlaybackPlan.Unavailable(
                segment.Avid,
                segment.Title,
                segment.PageIndex,
                segment.PartName,
                probe.SegmentName,
                segment.SegmentDirectory,
                "Unknown",
                $"无法识别缓存结构：{segment.SegmentDirectory}");
        }

        return handler.BuildPlan(probe);
    }

    public CachePlaybackPlan CreatePlan(BiliVideoCache cache, string? segmentKey = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        return CreatePagePlan(cache, segmentKey).SelectedPlan;
    }

    public CachePlaybackPagePlan CreatePagePlan(BiliVideoCache cache, string? segmentKey = null)
    {
        ArgumentNullException.ThrowIfNull(cache);

        var pageGroup = ResolvePageGroup(cache, segmentKey);
        return BuildPagePlan(pageGroup.Key, pageGroup.ToList(), segmentKey);
    }

    public IReadOnlyList<CachePlaybackPagePlan> CreatePagePlans(BiliVideoCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        return cache.Segments
            .GroupBy(segment => segment.PageIndex)
            .OrderBy(group => group.Key)
            .Select(group => BuildPagePlan(group.Key, group.ToList(), null))
            .ToList();
    }

    public PlaybackLaunchResult Play(BiliSegment segment, PlaybackLaunchOptions? launchOptions = null)
    {
        var plan = CreatePlan(segment);
        if (!plan.IsPlayable)
        {
            return PlaybackLaunchResult.Failure(plan.Message ?? "当前分段不可播放。");
        }

        var materializationResult = _materializer.Materialize(plan);
        if (!materializationResult.Succeeded)
        {
            return PlaybackLaunchResult.Failure(materializationResult.Message);
        }

        return Launch(materializationResult, launchOptions);
    }

    public PlaybackLaunchResult Play(BiliVideoCache cache, string? segmentKey = null, PlaybackLaunchOptions? launchOptions = null)
    {
        var pagePlan = CreatePagePlan(cache, segmentKey);
        if (!pagePlan.IsPlayable)
        {
            return PlaybackLaunchResult.Failure(pagePlan.SelectedPlan.Message ?? pagePlan.Message ?? "当前页面不可播放。");
        }

        var materializationResult = _materializer.Materialize(pagePlan.SelectedPlan);
        if (!materializationResult.Succeeded)
        {
            return PlaybackLaunchResult.Failure(materializationResult.Message);
        }

        return Launch(materializationResult, launchOptions);
    }

    public Task<PlaybackLaunchResult> PlayAsync(
        BiliSegment segment,
        PlaybackLaunchOptions? launchOptions = null,
        IProgress<PlaybackPreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var plan = CreatePlan(segment);
        return PlayPlanAsync(
            plan,
            launchOptions,
            progress,
            cancellationToken,
            plan.Message ?? "\u5f53\u524d\u5206\u6bb5\u4e0d\u53ef\u64ad\u653e\u3002");
    }

    public Task<PlaybackLaunchResult> PlayAsync(
        BiliVideoCache cache,
        string? segmentKey = null,
        PlaybackLaunchOptions? launchOptions = null,
        IProgress<PlaybackPreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var pagePlan = CreatePagePlan(cache, segmentKey);
        return PlayPlanAsync(
            pagePlan.SelectedPlan,
            launchOptions,
            progress,
            cancellationToken,
            pagePlan.SelectedPlan.Message ??
            pagePlan.Message ??
            "\u5f53\u524d\u9875\u9762\u4e0d\u53ef\u64ad\u653e\u3002");
    }

    private async Task<PlaybackLaunchResult> PlayPlanAsync(
        CachePlaybackPlan plan,
        PlaybackLaunchOptions? launchOptions,
        IProgress<PlaybackPreparationProgress>? progress,
        CancellationToken cancellationToken,
        string unavailableMessage)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!plan.IsPlayable)
        {
            return PlaybackLaunchResult.Failure(unavailableMessage);
        }

        var materializationResult = await Task.Run(
                () => _materializer.Materialize(plan, progress, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (!materializationResult.Succeeded)
        {
            return PlaybackLaunchResult.Failure(materializationResult.Message);
        }

        return Launch(materializationResult, launchOptions);
    }

    private PlaybackLaunchResult Launch(
        PlaybackMaterializationResult materializationResult,
        PlaybackLaunchOptions? launchOptions)
    {
        var result = _launcher.Launch(materializationResult, launchOptions);
        if (!result.Succeeded ||
            !materializationResult.IsTemporary ||
            string.IsNullOrWhiteSpace(materializationResult.OutputPath))
        {
            return result;
        }

        return result.WithManagedArtifact(materializationResult.OutputPath);
    }

    private CachePlaybackPagePlan BuildPagePlan(int pageIndex, IReadOnlyList<BiliSegment> segments, string? segmentKey)
    {
        if (segments.Count == 0)
        {
            throw new ArgumentException("Page group must not be empty.", nameof(segments));
        }

        var candidatePlans = segments
            .OrderBy(segment => Path.GetFileName(segment.SegmentDirectory), StringComparer.OrdinalIgnoreCase)
            .Select(CreatePlan)
            .ToList();

        CachePlaybackPlan? selectedPlan = null;
        if (!string.IsNullOrWhiteSpace(segmentKey))
        {
            selectedPlan = candidatePlans.FirstOrDefault(plan =>
                string.Equals(plan.SegmentName, segmentKey.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        selectedPlan ??= candidatePlans
            .OrderByDescending(plan => plan.IsPlayable)
            .ThenByDescending(GetPlanBytes)
            .ThenByDescending(plan => GetMaterialPriority(plan.MaterialKind))
            .ThenByDescending(plan => GetStructurePriority(plan.StructureKind))
            .ThenBy(plan => plan.SegmentName, StringComparer.OrdinalIgnoreCase)
            .First();

        var message = candidatePlans.Count > 1
            ? $"第 {pageIndex} 页存在 {candidatePlans.Count} 个候选分段，当前选择 {selectedPlan.SegmentName}。"
            : null;

        return new CachePlaybackPagePlan(
            segments[0].Avid,
            segments[0].Title,
            pageIndex,
            segments[0].PartName,
            candidatePlans,
            selectedPlan,
            message);
    }

    private static IGrouping<int, BiliSegment> ResolvePageGroup(BiliVideoCache cache, string? segmentKey)
    {
        var orderedGroups = cache.Segments
            .GroupBy(segment => segment.PageIndex)
            .OrderBy(group => group.Key)
            .ToList();

        if (orderedGroups.Count == 0)
        {
            throw new InvalidOperationException("Cache does not contain any segments.");
        }

        if (string.IsNullOrWhiteSpace(segmentKey))
        {
            return orderedGroups[0];
        }

        var normalizedKey = segmentKey.Trim();
        if (int.TryParse(normalizedKey, out var pageIndex))
        {
            var pageMatch = orderedGroups.FirstOrDefault(group => group.Key == pageIndex);
            if (pageMatch is not null)
            {
                return pageMatch;
            }
        }

        var segmentMatch = orderedGroups.FirstOrDefault(group =>
            group.Any(segment =>
                string.Equals(Path.GetFileName(segment.SegmentDirectory), normalizedKey, StringComparison.OrdinalIgnoreCase)));

        if (segmentMatch is not null)
        {
            return segmentMatch;
        }

        throw new ArgumentException($"没有找到分段或页码：{segmentKey}", nameof(segmentKey));
    }

    private static CachePlaybackProbe BuildProbe(BiliSegment segment)
    {
        var segmentDirectory = segment.SegmentDirectory;
        if (!IsPhysicalDirectory(segmentDirectory))
        {
            return new CachePlaybackProbe(
                segment,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>());
        }

        var rootFiles = SafeEnumerateFiles(segmentDirectory, SearchOption.TopDirectoryOnly);
        var childDirectories = SafeEnumerateDirectories(segmentDirectory);
        var nestedFiles = SafeEnumerateFiles(segmentDirectory, SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetDirectoryName(path), segmentDirectory, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new CachePlaybackProbe(segment, rootFiles, childDirectories, nestedFiles);
    }

    private static IReadOnlyList<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            if (!IsPhysicalDirectory(path))
            {
                return Array.Empty<string>();
            }

            return Directory.EnumerateDirectories(
                    path,
                    "*",
                    CreateEnumerationOptions(recurseSubdirectories: false))
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (IsNonFatal(ex))
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> SafeEnumerateFiles(string path, SearchOption searchOption)
    {
        try
        {
            if (!IsPhysicalDirectory(path))
            {
                return Array.Empty<string>();
            }

            return Directory.EnumerateFiles(
                    path,
                    "*",
                    CreateEnumerationOptions(searchOption == SearchOption.AllDirectories))
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (IsNonFatal(ex))
        {
            return Array.Empty<string>();
        }
    }

    private static EnumerationOptions CreateEnumerationOptions(bool recurseSubdirectories)
    {
        return new EnumerationOptions
        {
            RecurseSubdirectories = recurseSubdirectories,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
    }

    private static bool IsPhysicalDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory) &&
                   !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (IsNonFatal(ex))
        {
            return false;
        }
    }

    private static long GetPlanBytes(CachePlaybackPlan plan)
    {
        return plan.MediaFiles.Sum(filePath =>
        {
            try
            {
                return new FileInfo(filePath).Length;
            }
            catch
            {
                return 0L;
            }
        });
    }

    private static int GetMaterialPriority(CachePlaybackMaterialKind materialKind)
    {
        return materialKind switch
        {
            CachePlaybackMaterialKind.SingleFile => 3,
            CachePlaybackMaterialKind.OrderedPair => 2,
            CachePlaybackMaterialKind.DashPair => 1,
            _ => 0
        };
    }

    private static int GetStructurePriority(string structureKind)
    {
        return structureKind switch
        {
            "LegacyBlv" => 5,
            "LegacyMp4" => 4,
            "LegacyMixed" => 4,
            "HybridCLegacy" => 3,
            "NewDash" => 2,
            "MidDash" => 1,
            _ => 0
        };
    }

    private static bool IsNonFatal(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    }
}
