using System.IO;
using System.IO.Compression;
using System.Text;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Wpf.Models;
using BiliBiliLocalCacheManager.Wpf.Services;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class DiagnosticServicesTests
{
    [Fact]
    public void RedactionContext_PreparesBoundedDistinctLongestFirstValuesOnce()
    {
        var candidates = Enumerable.Range(0, 2_500)
            .Select(index => $"media-{index:D4}")
            .ToList();
        candidates.Insert(0, "longest-sensitive-media-value");
        candidates.Insert(1, " MEDIA-0000 ");
        candidates.Insert(2, string.Empty);

        var context = new SensitiveDataRedactionContext(
            KnownSensitiveValues: candidates);

        Assert.Equal(
            SensitiveDataRedactionContext.MaximumKnownSensitiveValueCount,
            context.KnownSensitiveValues.Count);
        Assert.Contains("longest-sensitive-media-value", context.KnownSensitiveValues);
        Assert.Equal(1, context.KnownSensitiveValues.Count(value =>
            string.Equals(value, "media-0000", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain("media-2499", context.KnownSensitiveValues);
        Assert.True(context.KnownSensitiveValues.SequenceEqual(
            context.KnownSensitiveValues
                .OrderByDescending(value => value.Length)
                .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)));
        Assert.Same(context.KnownSensitiveValues, context.KnownSensitiveValues);
    }

    [Fact]
    public void Redactor_ReplacesKnownMediaValuesLongestFirst_AndUsesBoundariesForSingleLatinValues()
    {
        var redactor = new SensitiveDataRedactor();
        var context = new SensitiveDataRedactionContext(
            KnownSensitiveValues:
            [
                string.Empty,
                " ",
                "X",
                "测试",
                "任意中文标题",
                "任意中文标题：特别篇"
            ]);

        var result = redactor.Redact(
            "正在准备任意中文标题：特别篇；分P名测试；单字符X应保留。",
            context);

        Assert.Equal("正在准备[MEDIA]；分P名[MEDIA]；单字符X应保留。", result);
    }

    [Fact]
    public void Redactor_SingleCharacterMediaValues_RedactsCjkAndStandaloneLatinOnly()
    {
        var redactor = new SensitiveDataRedactor();
        var result = redactor.Redact(
            "正在准备播放：猫；代号 A；Avid 和 DATA 不应被拆散。",
            new SensitiveDataRedactionContext(KnownSensitiveValues: ["猫", "A"]));

        Assert.DoesNotContain("猫", result, StringComparison.Ordinal);
        Assert.Contains("播放：[MEDIA]", result, StringComparison.Ordinal);
        Assert.Contains("代号 [MEDIA]", result, StringComparison.Ordinal);
        Assert.Contains("Avid", result, StringComparison.Ordinal);
        Assert.Contains("DATA", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redactor_PathAndUrlRecognition_RunsBeforePathLikeMediaValues()
    {
        var redactor = new SensitiveDataRedactor();
        var result = redactor.Redact(
            @"诊断信息已导出：C:\Users\Alice Smith\Private Reports\diag.zip（1 条近期事件）；标题 C:",
            new SensitiveDataRedactionContext(
                UserProfileDirectory: @"C:\Users\Alice",
                LocalApplicationDataDirectory: @"C:\Users\Alice\AppData\Local",
                TemporaryDirectory: @"C:\Users\Alice\AppData\Local\Temp",
                KnownSensitiveValues: ["C:"]));

        Assert.DoesNotContain("Alice", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private Reports", result, StringComparison.Ordinal);
        Assert.DoesNotContain("diag.zip", result, StringComparison.Ordinal);
        Assert.Contains("诊断信息已导出：<PATH>", result, StringComparison.Ordinal);
        Assert.EndsWith("标题 [MEDIA]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redactor_UnquotedWindowsPathsWithSpacesParenthesesChineseAndUnc_RedactsWholePathOnly()
    {
        var redactor = new SensitiveDataRedactor();
        const string cacheRoot = @"D:\My Private Videos";
        var input =
            @"根目录 D:\My Private Videos；" +
            @"源 D:\My Private Videos\(收藏)\中文目录\Secret file.m4s 失败：解码错误；" +
            @"网络 \\server\Private Share\中文(秘密)\clip final.mp4 失败：network unavailable；" +
            @"目录 D:\Another Private Root\中文(秘密) 不存在，请检查权限。";

        var result = redactor.Redact(
            input,
            new SensitiveDataRedactionContext(
                CacheRoot: cacheRoot,
                UserProfileDirectory: @"C:\Users\Alice"));

        Assert.Contains("根目录 [CACHE_ROOT]；", result, StringComparison.Ordinal);
        Assert.Contains("源 [CACHE_ROOT]/[PATH] 失败：解码错误", result, StringComparison.Ordinal);
        Assert.Contains("网络 <PATH> 失败：network unavailable", result, StringComparison.Ordinal);
        Assert.Contains("目录 <PATH> 不存在，请检查权限", result, StringComparison.Ordinal);
        Assert.DoesNotContain("My Private Videos", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret file.m4s", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Share", result, StringComparison.Ordinal);
        Assert.DoesNotContain("中文(秘密)", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Another Private Root", result, StringComparison.Ordinal);

        var userPathResult = redactor.Redact(
            @"用户文件 C:\Users\Alice\Documents\Secret Name\clip.m4s 无法访问。",
            new SensitiveDataRedactionContext(UserProfileDirectory: @"C:\Users\Alice"));
        Assert.Contains("用户文件 [USER_PROFILE]/[PATH] 无法访问", userPathResult, StringComparison.Ordinal);
        Assert.DoesNotContain("Documents", userPathResult, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Name", userPathResult, StringComparison.Ordinal);
        Assert.DoesNotContain("clip.m4s", userPathResult, StringComparison.Ordinal);
    }

    [Fact]
    public void Redactor_QuotedAndUnquotedUnixPathsWithSpaces_RedactsWholePathOnly()
    {
        var redactor = new SensitiveDataRedactor();
        var input =
            "源 /home/Alice Smith/视频(私密)/clip final.mkv 失败：decode error；" +
            "目录 /home/Alice Smith/Private Folder 不存在，请检查权限；" +
            "引用 \"/opt/Private Media/中文(秘密)/clip.mp4\" 无法访问。";

        var result = redactor.Redact(input, new SensitiveDataRedactionContext());

        Assert.Contains("源 <PATH> 失败：decode error", result, StringComparison.Ordinal);
        Assert.Contains("目录 <PATH> 不存在，请检查权限", result, StringComparison.Ordinal);
        Assert.Contains("引用 \"<PATH>\" 无法访问", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice Smith", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Folder", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Media", result, StringComparison.Ordinal);
        Assert.DoesNotContain("中文(秘密)", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redactor_UnquotedExtensionlessPathsWithoutKnownStatusWords_DoNotLeakTails()
    {
        var redactor = new SensitiveDataRedactor();
        var windows = redactor.Redact(
            @"失败位置 C:\Users\Alice Smith\秘密目录（3）",
            new SensitiveDataRedactionContext(
                UserProfileDirectory: @"C:\Users\Alice Smith"));
        var unc = redactor.Redact(
            @"UNC \\server\Private Share\目录 (2)",
            new SensitiveDataRedactionContext());
        var unix = redactor.Redact(
            "Unix /home/Alice Smith/秘密目录 (3)",
            new SensitiveDataRedactionContext());

        Assert.Equal("失败位置 [USER_PROFILE]/[PATH]", windows);
        Assert.Equal("UNC <PATH>", unc);
        Assert.Equal("Unix <PATH>", unix);
        var combined = windows + unc + unix;
        Assert.DoesNotContain("Alice Smith", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("秘密目录", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Share", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("目录 (2)", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("（3）", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void Recorder_IsBounded_AndPreservesInsertionOrder()
    {
        var recorder = new InMemoryDiagnosticEventRecorder(capacity: 3);
        for (var index = 1; index <= 5; index++)
        {
            recorder.Record(CreateEvent($"event-{index}"));
        }

        var events = recorder.GetRecentEvents();

        Assert.Equal(["event-3", "event-4", "event-5"], events.Select(item => item.Message));
    }

    [Fact]
    public void Recorder_IsSafeUnderConcurrentWriters_AndRemainsBounded()
    {
        const int capacity = 64;
        var recorder = new InMemoryDiagnosticEventRecorder(capacity);

        Parallel.For(0, 1_000, index =>
            recorder.Record(CreateEvent($"event-{index}")));

        var events = recorder.GetRecentEvents();
        Assert.Equal(capacity, events.Count);
        Assert.Equal(capacity, events.Select(item => item.Message).Distinct().Count());
    }

    [Fact]
    public async Task ExportAsync_WritesExpectedEntries_AndRecursivelyRedactsEveryStringValue()
    {
        using var directory = new TemporaryDirectory();
        var outputPath = Path.Combine(directory.Path, "diagnostics.zip");
        var title = "只有用户知道的中文标题";
        var partName = "幕后花絮与秘密分P";
        var cacheRoot = Path.Combine(directory.Path, "private-cache");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var privateUserPath = Path.Combine(userProfile, "Videos", "private.mp4");
        const string privateUrl =
            "https://user:pass@example.com/%E7%A7%81%E5%AF%86%E6%A0%87%E9%A2%98/account-42" +
            "?token=secret-query#private-fragment";
        var recorder = new InMemoryDiagnosticEventRecorder();
        recorder.Record(new DiagnosticEvent(
            DateTimeOffset.Now,
            $"播放-{title}",
            DiagnosticEventLevel.Error,
            $"播放 {title} / {partName} 失败；路径 {cacheRoot}; 用户文件 {privateUserPath}; " +
            $"来源 {privateUrl}; token=super-secret",
            $"Private.{partName}.Exception"));
        var service = new DiagnosticReportService(recorder, new SensitiveDataRedactor());
        var request = new DiagnosticReportRequest(
            outputPath,
            CreateContext(cacheRoot, title, partName),
            new SensitiveDataRedactionContext(
                CacheRoot: cacheRoot,
                UserProfileDirectory: userProfile,
                LocalApplicationDataDirectory: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                TemporaryDirectory: Path.GetTempPath(),
                KnownSensitiveValues: [title, partName]));

        var result = await service.ExportAsync(request);

        Assert.Equal(outputPath, result.OutputPath);
        Assert.Equal(1, result.EventCount);
        Assert.True(result.FileSizeBytes > 0);
        using var archive = ZipFile.OpenRead(outputPath);
        Assert.Equal(
            ["diagnostics.json", "recent-events.json"],
            archive.Entries.Select(entry => entry.FullName).OrderBy(name => name));
        var contents = string.Join(
            "\n",
            archive.Entries.Select(ReadEntry));
        Assert.DoesNotContain(title, contents, StringComparison.Ordinal);
        Assert.DoesNotContain(partName, contents, StringComparison.Ordinal);
        Assert.DoesNotContain(cacheRoot, contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(userProfile, contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super-secret", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-query", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("private-fragment", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("%E7%A7%81", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("account-42", contents, StringComparison.Ordinal);
        Assert.Contains("[MEDIA]", contents, StringComparison.Ordinal);
        Assert.Contains("[CACHE_ROOT]", contents, StringComparison.Ordinal);
        Assert.Contains("https://example.com/[PATH]", contents, StringComparison.Ordinal);
        Assert.Contains("token=[REDACTED]", contents, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportAsync_WhenSerializationRedactionFails_LeavesNoOutputOrWritingFile()
    {
        using var directory = new TemporaryDirectory();
        var outputPath = Path.Combine(directory.Path, "diagnostics.zip");
        var service = new DiagnosticReportService(
            new InMemoryDiagnosticEventRecorder(),
            new ThrowingRedactor());
        var request = new DiagnosticReportRequest(
            outputPath,
            new DiagnosticReportContext { ProductName = "trigger failure" },
            new SensitiveDataRedactionContext());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(request));

        Assert.False(File.Exists(outputPath));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.writing"));
    }

    [Fact]
    public async Task ExportAsync_RedactsSingleCjkMediaTitleFromEveryZipEntry()
    {
        using var directory = new TemporaryDirectory();
        var outputPath = Path.Combine(directory.Path, "single-title.zip");
        var recorder = new InMemoryDiagnosticEventRecorder();
        recorder.Record(new DiagnosticEvent(
            DateTimeOffset.UtcNow,
            "Playback",
            DiagnosticEventLevel.Error,
            "播放失败：猫"));
        var service = new DiagnosticReportService(recorder, new SensitiveDataRedactor());

        await service.ExportAsync(new DiagnosticReportRequest(
            outputPath,
            new DiagnosticReportContext { LastPlaybackFailure = "播放失败：猫" },
            new SensitiveDataRedactionContext(KnownSensitiveValues: ["猫"])));

        using var archive = ZipFile.OpenRead(outputPath);
        var contents = string.Join("\n", archive.Entries.Select(ReadEntry));
        Assert.DoesNotContain("猫", contents, StringComparison.Ordinal);
        Assert.Contains("[MEDIA]", contents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_WhenCanceled_LeavesExistingDestinationUntouchedAndNoWritingFile()
    {
        using var directory = new TemporaryDirectory();
        var outputPath = Path.Combine(directory.Path, "diagnostics.zip");
        var original = Encoding.UTF8.GetBytes("existing report");
        await File.WriteAllBytesAsync(outputPath, original);
        var service = new DiagnosticReportService(
            new InMemoryDiagnosticEventRecorder(),
            new SensitiveDataRedactor());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ExportAsync(
            new DiagnosticReportRequest(
                outputPath,
                new DiagnosticReportContext(),
                new SensitiveDataRedactionContext()),
            cancellation.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(outputPath));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.writing"));
    }

    private static DiagnosticEvent CreateEvent(string message)
    {
        return new DiagnosticEvent(
            DateTimeOffset.UtcNow,
            "test",
            DiagnosticEventLevel.Information,
            message);
    }

    private static DiagnosticReportContext CreateContext(
        string cacheRoot,
        string title,
        string partName)
    {
        var snapshot = new StorageOverviewSnapshot(
            cacheRoot,
            new CacheStorageStatistics(cacheRoot, 1, 2, 3, 0, 0, null),
            new PlaybackArtifactCacheStatistics(cacheRoot, 1, 4),
            new PlaybackArtifactCleanupPreview(1, 4, 0),
            new CacheTrashStatistics(cacheRoot, 1, 1, 5, 0, 0, null),
            ManagedTotalBytes: 12,
            ReclaimableBytes: 9,
            DateTimeOffset.UtcNow,
            [$"nested error contains {title} and {partName} at {cacheRoot}"]);
        return new DiagnosticReportContext
        {
            ProductName = title,
            InformationalVersion = "1.0-test",
            CacheRoot = cacheRoot,
            StorageOverview = snapshot,
            LastStorageMaintenance = $"cleaned {partName}",
            LastPlaybackFailure = $"failed {title}",
            Ffmpeg = new FfmpegDiagnosticSnapshot(
                true,
                FfmpegResolutionSource.ArchiveOverride,
                cacheRoot,
                "test")
        };
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class ThrowingRedactor : ISensitiveDataRedactor
    {
        public string Redact(string value, SensitiveDataRedactionContext context)
        {
            throw new InvalidOperationException("redaction failed");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"BiliBiliLocalCacheManager.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best effort test cleanup.
            }
        }
    }
}
