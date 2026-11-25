using System.Text;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Services;

namespace BiliBiliLocalCacheManager.Desktop.Host.Tests;

internal static class ExportHistoryTestHost
{
    private static async Task Main(string[] args)
    {
        if (args is not ["--json-lines"]) return;
        var destination = Environment.GetEnvironmentVariable("BLCM_E2E_EXPORT_DIRECTORY")
            ?? throw new InvalidOperationException("Missing the isolated E2E export directory.");
        var transcode = Environment.GetEnvironmentVariable("BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT")
            ?? throw new InvalidOperationException("Missing the isolated E2E transcode directory.");
        var application = new DesktopHostApplication(exportService: new CacheExportService(
            new PlaybackArtifactStore(transcode), new DesktopHostContractTests.FixtureCopyProcessor()));
        application.ProgressReported += (_, progress) =>
        {
            // Both files are staged; the second file has not yet been opened for final verification.
            if (progress.Phase != "final-output-verify" || progress.Current != 3 || progress.Total != 4) return;
            var staging = Directory.EnumerateDirectories(destination, "*.staging").Single();
            var video = Directory.EnumerateDirectories(staging, "*_AV990002").Single();
            var part = Directory.EnumerateFiles(video, "P002_*.mp4").Single();
            File.AppendAllText(part, "changed after copy");
        };
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var output = Console.OpenStandardOutput();
        await new JsonLineRpcServer(application, input, output).RunAsync();
    }
}
