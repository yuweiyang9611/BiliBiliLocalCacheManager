using System.Text.Json;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;

namespace BiliBiliLocalCacheManager.Desktop.Host.Tests;

public sealed partial class DesktopHostContractTests
{
    [Theory]
    [InlineData("play")]
    [InlineData("export")]
    public async Task MediaRequest_RejectsStaleIndexWithoutRescanning(string method)
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "First");
        var launcher = new QueueRecorder();
        var application = new DesktopHostApplication(launcher);
        var scanRequest = JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot });
        var scan = await DispatchAsync(application, "scan", scanRequest);
        await DispatchAsync(application, "scan", scanRequest);
        var exception = await Assert.ThrowsAsync<BiliBiliLocalCacheManager.Desktop.Host.Rpc.RpcException>(() =>
            DispatchAsync(application, method, JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot,
                outputPath = workspace.Root, indexToken = scan.GetProperty("indexToken").GetString(),
                targets = new[] { new { avid = "101" } } })));
        Assert.Equal("stale_index", exception.Code);
        Assert.Equal(0, launcher.Calls);
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "cache-export")));
    }

    [Fact]
    public async Task SingleExport_AlwaysUsesNumberedBatchVideoAndPartDirectories()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "Title: invalid/characters");
        var application = workspace.CreateApplication();
        var request = JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, outputPath = workspace.Root,
            targets = new[] { new { avid = "101", pageIndexes = new[] { 1 } } } });
        var first = await DispatchAsync(application, "export", request);
        var second = await DispatchAsync(application, "export", request);
        Assert.True(first.GetProperty("published").GetBoolean(), first.GetRawText());
        Assert.True(second.GetProperty("published").GetBoolean(), second.GetRawText());
        Assert.Equal(Path.Combine(workspace.Root, "cache-export"), first.GetProperty("outputPath").GetString());
        Assert.Equal(Path.Combine(workspace.Root, "cache-export (2)"), second.GetProperty("outputPath").GetString());
        foreach (var batch in new[] { "cache-export", "cache-export (2)" })
            Assert.Equal("media-101-1", File.ReadAllText(Path.Combine(workspace.Root, batch,
                "Title invalid characters_AV101", "P001_Part 1.mp4")));
    }

    [Fact]
    public async Task SingleExport_RejectsLegacyFileDestinationWithoutOverwriting()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "Title");
        var path = Path.Combine(workspace.Root, "existing.mp4");
        File.WriteAllText(path, "keep");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => DispatchAsync(workspace.CreateApplication(), "export",
            JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, outputPath = path,
                targets = new[] { new { avid = "101" } } })));
        Assert.Equal("keep", File.ReadAllText(path));
    }

    [Fact]
    public async Task ExportTranscodeConsent_PublishesNothingUntilWholeBatchIsApproved()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "First", 2);
        var destination = Path.Combine(workspace.Root, "exports");
        Directory.CreateDirectory(destination);
        var processor = new ExportProcessor();
        var service = new CacheExportService(new PlaybackArtifactStore(workspace.TranscodeRoot), processor);
        var application = new DesktopHostApplication(exportService: service);
        string Request(string[] approvals) => JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot,
            outputPath = destination, targets = new[] { new { avid = "101" } }, transcodeApprovals = approvals });
        var result = await DispatchAsync(application, "export", Request([]));
        Assert.False(result.GetProperty("published").GetBoolean());
        Assert.Equal(2, result.GetProperty("transcodeRequirements").GetArrayLength());
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        Assert.Empty(processor.Encoded);
        var approvals = result.GetProperty("transcodeRequirements").EnumerateArray()
            .Select(item => item.GetProperty("approvalToken").GetString()!).ToArray();
        var retry = await DispatchAsync(application, "export", Request(approvals));
        Assert.True(retry.GetProperty("published").GetBoolean());
        Assert.Equal(2, retry.GetProperty("exportedCount").GetInt32());
        Assert.Equal(new[] { 1, 2 }, processor.Encoded);
        var unapprovedReuse = await DispatchAsync(application, "export", Request([]));
        Assert.False(unapprovedReuse.GetProperty("published").GetBoolean());
        Assert.Equal(2, unapprovedReuse.GetProperty("transcodeRequirements").GetArrayLength());
        Assert.Single(Directory.EnumerateDirectories(destination));
    }

    [Fact]
    public async Task Export_SourceChangedDuringLaterPartPreventsWholeBatchPublication()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "First", 2);
        var destination = Path.Combine(workspace.Root, "exports");
        Directory.CreateDirectory(destination);
        var processor = new ExportProcessor { NeedsTranscode = false, AfterProcess = plan =>
        {
            if (plan.PageIndex == 2) File.WriteAllText(Path.Combine(workspace.CacheRoot, "101", "c_1", "lua.flv.bb2api.80", "0.mp4"), "replaced");
        } };
        var service = new CacheExportService(new PlaybackArtifactStore(workspace.TranscodeRoot), processor);
        var result = await DispatchAsync(new DesktopHostApplication(exportService: service), "export",
            JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, outputPath = destination,
                targets = new[] { new { avid = "101" } } }));
        Assert.False(result.GetProperty("published").GetBoolean());
        Assert.Equal(0, result.GetProperty("exportedCount").GetInt32());
        Assert.Contains("changed", Assert.Single(result.GetProperty("failures").EnumerateArray()).GetProperty("message").GetString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task Export_DestinationCreatedDuringPreparationIsNeverOverwritten()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "First");
        var application = workspace.CreateApplication();
        application.ProgressReported += (_, progress) =>
        {
            if (progress.Stage == "copying")
            {
                Directory.CreateDirectory(Path.Combine(workspace.Root, "cache-export"));
                File.WriteAllText(Path.Combine(workspace.Root, "cache-export", "keep.txt"), "keep");
            }
        };
        var result = await DispatchAsync(application, "export", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot,
            outputPath = workspace.Root, targets = new[] { new { avid = "101" } } }));
        Assert.True(result.GetProperty("published").GetBoolean());
        Assert.Equal(Path.Combine(workspace.Root, "cache-export (2)"), result.GetProperty("outputPath").GetString());
        Assert.Equal("keep", File.ReadAllText(Path.Combine(workspace.Root, "cache-export", "keep.txt")));
    }

    [Fact]
    public async Task Export_ChangedStagedBytesPreventBatchPublication()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "First", 2);
        var destination = Directory.CreateDirectory(Path.Combine(workspace.Root, "exports")).FullName;
        var application = workspace.CreateApplication();
        application.ProgressReported += (_, progress) =>
        {
            if (progress.Current == 2 && progress.Phase == "source-hash")
            {
                var staged = Directory.EnumerateFiles(destination, "P001_*.mp4", SearchOption.AllDirectories).Single();
                File.WriteAllText(staged, "changed after copy");
            }
        };
        var result = await DispatchAsync(application, "export", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot,
            outputPath = destination, targets = new[] { new { avid = "101" } } }));
        Assert.False(result.GetProperty("published").GetBoolean());
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task Export_ReplacedStagingDirectoryIsNeitherWrittenNorCleaned()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "First", 2);
        var destination = Directory.CreateDirectory(Path.Combine(workspace.Root, "exports")).FullName;
        var application = workspace.CreateApplication();
        string? replaced = null;
        application.ProgressReported += (_, progress) =>
        {
            if (replaced is null && progress.Current == 2 && progress.Phase == "source-hash")
            {
                replaced = Directory.EnumerateDirectories(destination, "*.staging").Single();
                Directory.Move(replaced, replaced + ".original");
                Directory.CreateDirectory(replaced);
                File.WriteAllText(Path.Combine(replaced, "keep.txt"), "keep");
            }
        };
        var result = await DispatchAsync(application, "export", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot,
            outputPath = destination, targets = new[] { new { avid = "101" } } }));
        Assert.NotNull(replaced);
        Assert.False(result.GetProperty("published").GetBoolean());
        Assert.Equal("keep", File.ReadAllText(Path.Combine(replaced, "keep.txt")));
        Assert.Empty(Directory.EnumerateFiles(replaced, "*.mp4", SearchOption.AllDirectories));
    }

    private sealed class ExportProcessor : IExportMediaProcessor
    {
        public List<int> Encoded { get; } = [];
        public bool NeedsTranscode { get; init; } = true;
        public Action<CachePlaybackPlan>? AfterProcess { get; init; }
        public async Task ProcessAsync(CachePlaybackPlan plan, string outputPath, Action<string> requireAudioTranscode,
            IProgress<PlaybackPreparationProgress>? progress, CancellationToken cancellationToken)
        {
            if (NeedsTranscode) requireAudioTranscode("Audio codec requires AAC conversion.");
            Encoded.Add(plan.PageIndex);
            await File.WriteAllTextAsync(outputPath, "processed media", cancellationToken);
            AfterProcess?.Invoke(plan);
        }
    }
}
