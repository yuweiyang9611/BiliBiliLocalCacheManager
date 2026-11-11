using System.Diagnostics;
using System.Text.Json;
using FFMpegCore;

namespace BiliBiliLocalCacheManager.Playback.Tests;

[Collection(FfmpegIntegrationCollection.Name)]
public sealed class FfmpegHostExportIntegrationTests(FfmpegIntegrationFixture fixture)
{
    [FfmpegIntegrationFact]
    [Trait("Category", "FFmpegIntegration")]
    public async Task Export_SelectsPageAndQualityAndRollsBackBatchWithDamagedMp4()
    {
        using var workspace = fixture.CreateWorkspace();
        var cacheRoot = Directory.CreateDirectory(Path.Combine(workspace.RootDirectory, "cache")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(workspace.RootDirectory, "export")).FullName;
        string Segment(long avid, string name, int page)
        {
            var path = Directory.CreateDirectory(Path.Combine(cacheRoot, avid.ToString(), name)).FullName;
            File.WriteAllText(Path.Combine(path, "entry.json"), JsonSerializer.Serialize(new
            {
                avid, title = "Fixture", is_completed = true, total_bytes = 1000, downloaded_bytes = 1000,
                type_tag = "lua.mp4.bapi.80", prefered_video_quality = 80, total_time_milli = 2000,
                page_data = new { cid = 100 + page, page, part = $"Part {page}" }
            }));
            return path;
        }

        // P1 has an unreadable numeric candidate and two complete alternate qualities.
        Segment(101, "1", 1);
        var page1 = Segment(101, "c_1", 1);
        var low = Path.Combine(Directory.CreateDirectory(Path.Combine(page1, "lua.mp4.bapi.64")).FullName, "0.mp4");
        var high = Path.Combine(Directory.CreateDirectory(Path.Combine(page1, "lua.mp4.bapi.80")).FullName, "0.mp4");
        fixture.RunFfmpeg("-i", workspace.VideoPath, "-t", "1", "-c", "copy", low);
        fixture.RunFfmpeg("-i", workspace.VideoPath, "-i", workspace.AacAudioPath, "-c", "copy", high);

        var page2 = Segment(101, "c_2", 2);
        Directory.CreateDirectory(Path.Combine(page2, "lua.flv.bapi.9"));
        var dash = Directory.CreateDirectory(Path.Combine(page2, "80")).FullName;
        File.Copy(workspace.VideoPath, Path.Combine(dash, "video.m4s"));
        File.Copy(workspace.AacAudioPath, Path.Combine(dash, "audio.m4s"));

        var page3 = Segment(101, "c_3", 3);
        var damaged = Path.Combine(Directory.CreateDirectory(Path.Combine(page3, "lua.mp4.bapi.80")).FullName, "0.mp4");
        File.WriteAllBytes(damaged, File.ReadAllBytes(high)[..32]);
        var wrongPage = Segment(102, "1", 2);
        File.Copy(high, Path.Combine(Directory.CreateDirectory(Path.Combine(wrongPage, "lua.mp4.bapi.80")).FullName, "0.mp4"));

        async Task<JsonElement> Export(string avid, int[] pages, bool missingTools = false)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            start.ArgumentList.Add(typeof(Desktop.Host.Program).Assembly.Location);
            start.Environment["BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH"] = Path.Combine(workspace.RootDirectory, "settings.json");
            start.Environment["BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT"] = workspace.ArtifactRoot;
            if (missingTools)
                start.Environment["BILIBILI_LOCAL_CACHE_MANAGER_FFMPEG_ARCHIVE_PATH"] = Path.Combine(workspace.RootDirectory, "missing.zip");
            using var host = Process.Start(start)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                var request = new { id = "export", method = "export", @params = new
                {
                    rootPath = cacheRoot, outputPath = destination, targets = new[] { new { avid, pageIndexes = pages } }
                }};
                await host.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
                while (await host.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
                {
                    using var message = JsonDocument.Parse(line);
                    if (message.RootElement.TryGetProperty("id", out _))
                        return message.RootElement.GetProperty("result").Clone();
                }
                throw new IOException("Host exited before replying to export.");
            }
            finally
            {
                host.StandardInput.Close();
                if (!host.WaitForExit(5000)) host.Kill(entireProcessTree: true);
            }
        }

        var success = await Export("101", [1, 2]);
        Assert.True(success.GetProperty("published").GetBoolean(), success.ToString());
        var output = Path.Combine(success.GetProperty("outputPath").GetString()!, "Fixture_AV101");
        var first = Path.Combine(output, "P001_Part 1.mp4");
        var second = Path.Combine(output, "P002_Part 2.mp4");
        Assert.Equal(File.ReadAllBytes(high), File.ReadAllBytes(first));
        Assert.InRange(FFProbe.Analyse(first).Duration.TotalSeconds, 1.9, 2.2);
        Assert.Equal("aac", fixture.GetCodecName(first, "a:0"));
        Assert.Equal(fixture.GetPacketHashes(workspace.VideoPath, "v:0"), fixture.GetPacketHashes(second, "v:0"));
        Assert.Equal(fixture.GetPacketHashes(workspace.AacAudioPath, "a:0"), fixture.GetPacketHashes(second, "a:0"));

        foreach (var (avid, pages, failedPage) in new[] { ("101", new[] { 1, 2, 3 }, 3), ("102", new[] { 1 }, 1) })
        {
            var failure = await Export(avid, pages);
            Assert.False(failure.GetProperty("published").GetBoolean(), failure.ToString());
            Assert.Equal(0, failure.GetProperty("exportedCount").GetInt32());
            Assert.Equal(failedPage, Assert.Single(failure.GetProperty("failures").EnumerateArray()).GetProperty("pageIndex").GetInt32());
        }
        var unavailable = await Export("101", [1], missingTools: true);
        Assert.False(unavailable.GetProperty("published").GetBoolean(), unavailable.ToString());
        Assert.Contains("FFprobe", Assert.Single(unavailable.GetProperty("failures").EnumerateArray()).GetProperty("message").GetString());
        Assert.Equal("cache-export", Path.GetFileName(Assert.Single(Directory.EnumerateFileSystemEntries(destination))));
    }
}
