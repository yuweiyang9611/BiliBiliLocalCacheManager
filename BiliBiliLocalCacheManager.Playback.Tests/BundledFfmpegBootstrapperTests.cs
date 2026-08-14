using System.IO.Compression;
using System.Text;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class BundledFfmpegBootstrapperTests
{
    [Fact]
    public void EmbeddedBundleManifest_ShouldUseExactVerifiedReleaseAsset()
    {
        var manifest = BundledFfmpegBootstrapper.BundleManifest;

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("BtbN/FFmpeg-Builds", manifest.Provider);
        Assert.Equal("autobuild-2026-06-30-13-34", manifest.Tag);
        Assert.Equal("ffmpeg-n8.1.2-21-gce3c09c101-win64-lgpl-8.1.zip", manifest.Asset);
        Assert.Equal(
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-06-30-13-34/ffmpeg-n8.1.2-21-gce3c09c101-win64-lgpl-8.1.zip",
            manifest.Url);
        Assert.Equal(
            "3b9eceb438016b647e0755a51ce3a388cd4ed5679e2427cb83a01e1ae2cd0eba",
            manifest.Sha256);
    }

    [Fact]
    public void BundleManifest_ShouldRejectMutableOrMismatchedReleaseUrl()
    {
        const string json =
            """
            {
              "schemaVersion": 1,
              "provider": "BtbN/FFmpeg-Builds",
              "tag": "autobuild-2026-06-30-13-34",
              "asset": "ffmpeg-fixed.zip",
              "url": "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-fixed.zip",
              "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        Assert.Throws<InvalidDataException>(() => FfmpegBundleManifest.Load(stream));
    }

    [Fact]
    public void SystemFfmpeg_ShouldRequireExplicitOptIn()
    {
        const string variableName = "BILIBILI_LOCAL_CACHE_MANAGER_USE_SYSTEM_FFMPEG";
        var previousValue = Environment.GetEnvironmentVariable(variableName);
        try
        {
            Environment.SetEnvironmentVariable(variableName, null);
            Assert.False(BundledFfmpegBootstrapper.IsSystemFfmpegOptInConfigured());

            Environment.SetEnvironmentVariable(variableName, "0");
            Assert.False(BundledFfmpegBootstrapper.IsSystemFfmpegOptInConfigured());

            Environment.SetEnvironmentVariable(variableName, "true");
            Assert.True(BundledFfmpegBootstrapper.IsSystemFfmpegOptInConfigured());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previousValue);
        }
    }

    [Fact]
    public async Task InitializationGateWait_ShouldObserveCancellation()
    {
        using var heldGate = BundledFfmpegBootstrapper.EnterInitializationGate(CancellationToken.None);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var waitTask = Task.Run(() =>
        {
            using var ignored = BundledFfmpegBootstrapper.EnterInitializationGate(cancellationSource.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            waitTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ProcessMutexWait_ShouldObserveCancellation()
    {
        var mutexName = $@"Local\BiliBiliLocalCacheManager.FFmpegBootstrap.Tests.{Guid.NewGuid():N}";
        using var holderReady = new ManualResetEventSlim();
        using var releaseHolder = new ManualResetEventSlim();
        var holderThread = new Thread(() =>
        {
            using var holderMutex = new Mutex(false, mutexName);
            holderMutex.WaitOne();
            holderReady.Set();
            releaseHolder.Wait();
            holderMutex.ReleaseMutex();
        });
        holderThread.IsBackground = true;
        holderThread.Start();

        try
        {
            Assert.True(holderReady.Wait(TimeSpan.FromSeconds(5)));
            using var waitingMutex = new Mutex(false, mutexName);
            using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            Assert.ThrowsAny<OperationCanceledException>(() =>
                BundledFfmpegBootstrapper.WaitForProcessMutex(
                    waitingMutex,
                    TimeSpan.FromSeconds(5),
                    cancellationSource.Token));
        }
        finally
        {
            releaseHolder.Set();
            Assert.True(holderThread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void Sha256_ShouldObserveCancellationBetweenChunks()
    {
        var root = CreateTempRoot();
        try
        {
            var sourcePath = Path.Combine(root, "bundle.zip");
            File.WriteAllBytes(sourcePath, new byte[1024 * 1024]);
            using var cancellationSource = new CancellationTokenSource();

            Assert.ThrowsAny<OperationCanceledException>(() =>
                BundledFfmpegBootstrapper.ComputeSha256(
                    sourcePath,
                    cancellationSource.Token,
                    percentage =>
                    {
                        if (percentage > 0d)
                        {
                            cancellationSource.Cancel();
                        }
                    }));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task StreamCopy_ShouldObserveCancellationBetweenChunks()
    {
        await using var input = new MemoryStream(new byte[1024 * 1024]);
        await using var output = new MemoryStream();
        using var cancellationSource = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BundledFfmpegBootstrapper.CopyStreamAsync(
                input,
                output,
                input.Length,
                cancellationSource.Token,
                percentage =>
                {
                    if (percentage > 0d)
                    {
                        cancellationSource.Cancel();
                    }
                }));
    }

    [Fact]
    public void ExtractEntryCancellation_ShouldRemovePartialFiles()
    {
        var root = CreateTempRoot();
        try
        {
            var archivePath = Path.Combine(root, "bundle.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("ffmpeg/bin/ffmpeg.exe", CompressionLevel.NoCompression);
                using var entryStream = entry.Open();
                entryStream.Write(new byte[1024 * 1024]);
            }

            using var readArchive = ZipFile.OpenRead(archivePath);
            var sourceEntry = Assert.Single(readArchive.Entries);
            var destinationPath = Path.Combine(root, "output", "ffmpeg.exe");
            using var cancellationSource = new CancellationTokenSource();

            Assert.ThrowsAny<OperationCanceledException>(() =>
                BundledFfmpegBootstrapper.ExtractEntry(
                    sourceEntry,
                    destinationPath,
                    cancellationSource.Token,
                    _ => cancellationSource.Cancel()));

            Assert.False(File.Exists(destinationPath));
            Assert.False(File.Exists(destinationPath + ".extracting"));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void ExtractEntrySuccess_ShouldMoveCompletedFileAndRemoveTemporaryFile()
    {
        var root = CreateTempRoot();
        try
        {
            var archivePath = Path.Combine(root, "bundle.zip");
            var expectedBytes = Enumerable.Range(0, 1024 * 1024)
                .Select(index => (byte)(index % 251))
                .ToArray();
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("ffmpeg/bin/ffmpeg.exe", CompressionLevel.NoCompression);
                using var entryStream = entry.Open();
                entryStream.Write(expectedBytes);
            }

            using var readArchive = ZipFile.OpenRead(archivePath);
            var sourceEntry = Assert.Single(readArchive.Entries);
            var destinationPath = Path.Combine(root, "output", "ffmpeg.exe");

            BundledFfmpegBootstrapper.ExtractEntry(
                sourceEntry,
                destinationPath,
                CancellationToken.None);

            Assert.Equal(expectedBytes, File.ReadAllBytes(destinationPath));
            Assert.False(File.Exists(destinationPath + ".extracting"));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void ExtractionCompletion_ShouldRequireVersionedHashMarker()
    {
        var root = CreateTempRoot();
        try
        {
            var binaryFolder = Path.Combine(root, "bin");
            Directory.CreateDirectory(binaryFolder);
            File.WriteAllText(Path.Combine(binaryFolder, "ffmpeg.exe"), "ffmpeg");
            File.WriteAllText(Path.Combine(binaryFolder, "ffprobe.exe"), "ffprobe");
            File.WriteAllText(Path.Combine(root, "LICENSE.txt"), "license");
            const string bundleHash = "abc123";
            var markerPath = Path.Combine(root, ".install-complete");

            Assert.False(BundledFfmpegBootstrapper.IsExtractionComplete(root, bundleHash));

            File.WriteAllText(markerPath, "wrong-version\nabc123");
            Assert.False(BundledFfmpegBootstrapper.IsExtractionComplete(root, bundleHash));

            File.WriteAllText(markerPath, "ffmpeg-bootstrap-v2\nabc123");
            Assert.True(BundledFfmpegBootstrapper.IsExtractionComplete(root, bundleHash));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void InitializedDiagnostic_ShouldRemainInitialized_WhenVersionMetadataIsUnavailable()
    {
        var snapshot = BundledFfmpegBootstrapper.CreateInitializedDiagnosticSnapshot(
            FfmpegResolutionSource.DownloadedBundle,
            Path.GetTempPath(),
            _ => null);

        Assert.True(snapshot.IsInitialized);
        Assert.Null(snapshot.Version);
    }

    [Fact]
    public void InitializedDiagnostic_ShouldIgnoreNonFatalVersionReaderFailures()
    {
        var failures = new Exception[]
        {
            new ArgumentException("Injected invalid version metadata."),
            new NotSupportedException("Injected unsupported metadata format."),
            new System.ComponentModel.Win32Exception("Injected native metadata failure.")
        };

        foreach (var failure in failures)
        {
            var snapshot = BundledFfmpegBootstrapper.CreateInitializedDiagnosticSnapshot(
                FfmpegResolutionSource.Path,
                Path.GetTempPath(),
                _ => throw failure);

            Assert.True(snapshot.IsInitialized);
            Assert.Null(snapshot.Version);
        }
    }

    [Fact]
    public void ArchiveOverride_ShouldBeRecognizedAsConfigured()
    {
        const string variableName =
            "BILIBILI_LOCAL_CACHE_MANAGER_FFMPEG_ARCHIVE_PATH";
        var previousValue = Environment.GetEnvironmentVariable(variableName);
        try
        {
            Environment.SetEnvironmentVariable(variableName, @"D:\fixed-ffmpeg.zip");

            Assert.True(BundledFfmpegBootstrapper.IsArchiveOverrideConfigured());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previousValue);
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bili_ffmpeg_bootstrap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore best-effort test cleanup failures.
        }
    }
}
