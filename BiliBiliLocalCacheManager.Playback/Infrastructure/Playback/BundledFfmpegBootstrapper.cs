using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using BiliBiliLocalCacheManager.Playback.Models;
using FFMpegCore;

[assembly: InternalsVisibleTo("BiliBiliLocalCacheManager.Playback.Tests")]

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

internal static class BundledFfmpegBootstrapper
{
    private const string ExtractionMarkerFileName = ".install-complete";
    private const string ExtractionMarkerVersion = "ffmpeg-bootstrap-v2";
    private const string ArchivePathOverrideEnvVar = "BILIBILI_LOCAL_CACHE_MANAGER_FFMPEG_ARCHIVE_PATH";
    private const string DownloadUrlOverrideEnvVar = "BILIBILI_LOCAL_CACHE_MANAGER_FFMPEG_DOWNLOAD_URL";
    private const string UseSystemFfmpegEnvVar = "BILIBILI_LOCAL_CACHE_MANAGER_USE_SYSTEM_FFMPEG";
    private const string ProcessMutexName = @"Local\BiliBiliLocalCacheManager.FFmpegBootstrap";
    private static readonly TimeSpan ProcessMutexTimeout = TimeSpan.FromMinutes(10);

    private static readonly SemaphoreSlim SyncRoot = new(1, 1);
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static volatile bool _initialized;

    internal static FfmpegDiagnosticState DiagnosticState { get; } = new();

    internal static FfmpegBundleManifest BundleManifest => FfmpegBundleManifest.Current;

    public static void EnsureConfigured(
        CancellationToken cancellationToken = default,
        Action<string, double?>? reportProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_initialized)
        {
            return;
        }

        using (EnterInitializationGate(cancellationToken))
        {
            if (_initialized)
            {
                return;
            }

            ReportProgress(reportProgress, "正在等待 FFmpeg 准备锁", percentage: null);
            using var processMutex = new Mutex(false, ProcessMutexName);
            var mutexAcquired = false;
            try
            {
                mutexAcquired = WaitForProcessMutex(
                    processMutex,
                    ProcessMutexTimeout,
                    cancellationToken);

                if (!mutexAcquired)
                {
                    throw new TimeoutException("等待其他应用实例完成 FFmpeg 初始化超时。");
                }

                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(reportProgress, "正在准备 FFmpeg", 0d);
                var resolution = ResolveBinaryFolder(cancellationToken, reportProgress);
                var tempFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BiliBiliLocalCacheManager",
                    "ffmpeg-temp");

                Directory.CreateDirectory(tempFolder);

                GlobalFFOptions.Configure(options =>
                {
                    options.BinaryFolder = resolution.BinaryFolder;
                    options.TemporaryFilesFolder = tempFolder;
                });

                DiagnosticState.Publish(CreateInitializedDiagnosticSnapshot(
                    resolution.Source,
                    resolution.BinaryFolder,
                    ReadFfmpegVersionFromFile));
                _initialized = true;
                ReportProgress(reportProgress, "FFmpeg 准备完成", 100d);
            }
            finally
            {
                if (mutexAcquired)
                {
                    processMutex.ReleaseMutex();
                }
            }
        }
    }

    internal static IDisposable EnterInitializationGate(CancellationToken cancellationToken)
    {
        SyncRoot.Wait(cancellationToken);
        return new SemaphoreReleaser(SyncRoot);
    }

    internal static bool WaitForProcessMutex(
        Mutex processMutex,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processMutex);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var waitResult = WaitHandle.WaitAny(
                new WaitHandle[] { processMutex, cancellationToken.WaitHandle },
                timeout);
            return waitResult switch
            {
                0 => true,
                1 => throw new OperationCanceledException(cancellationToken),
                WaitHandle.WaitTimeout => false,
                _ => throw new InvalidOperationException($"Unexpected wait result: {waitResult}.")
            };
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static BinaryFolderResolution ResolveBinaryFolder(
        CancellationToken cancellationToken,
        Action<string, double?>? reportProgress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsArchiveOverrideConfigured())
        {
            return ResolveBundleBinaryFolder(cancellationToken, reportProgress);
        }

        if (IsSystemFfmpegOptInConfigured() &&
            TryResolveInstalledBinaryFolder(cancellationToken, out var installedResolution))
        {
            return installedResolution;
        }

        return ResolveBundleBinaryFolder(cancellationToken, reportProgress);
    }

    private static BinaryFolderResolution ResolveBundleBinaryFolder(
        CancellationToken cancellationToken,
        Action<string, double?>? reportProgress)
    {
        var bundle = EnsureBundleAvailable(cancellationToken, reportProgress);
        var extractionRoot = EnsureExtracted(bundle, cancellationToken, reportProgress);
        return new BinaryFolderResolution(
            Path.Combine(extractionRoot, "bin"),
            bundle.Source);
    }

    internal static bool IsArchiveOverrideConfigured()
    {
        return !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(ArchivePathOverrideEnvVar));
    }

    internal static bool IsSystemFfmpegOptInConfigured()
    {
        var value = Environment.GetEnvironmentVariable(UseSystemFfmpegEnvVar)?.Trim();
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveInstalledBinaryFolder(
        CancellationToken cancellationToken,
        out BinaryFolderResolution resolution)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in EnumeratePathDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visited.Add(directory) && HasRequiredBinaries(directory))
            {
                resolution = new BinaryFolderResolution(
                    directory,
                    FfmpegResolutionSource.Path);
                return true;
            }
        }

        foreach (var directory in GetFallbackBinaryDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(directory) &&
                visited.Add(directory) &&
                HasRequiredBinaries(directory))
            {
                resolution = new BinaryFolderResolution(
                    directory,
                    FfmpegResolutionSource.KnownInstallation);
                return true;
            }
        }

        resolution = default;
        return false;
    }

    private static bool HasRequiredBinaries(string directory)
    {
        return File.Exists(Path.Combine(directory, "ffmpeg.exe")) &&
            File.Exists(Path.Combine(directory, "ffprobe.exe"));
    }

    private static IEnumerable<string> EnumeratePathDirectories()
    {
        var pathEnvironment = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnvironment))
        {
            yield break;
        }

        foreach (var rawDirectory in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? fullPath = null;

            try
            {
                if (Directory.Exists(rawDirectory))
                {
                    fullPath = Path.GetFullPath(rawDirectory);
                }
            }
            catch
            {
                // Ignore invalid PATH entries.
            }

            if (!string.IsNullOrWhiteSpace(fullPath))
            {
                yield return fullPath;
            }
        }
    }

    private static IEnumerable<string> GetFallbackBinaryDirectories()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return new[]
        {
            Path.Combine(programFiles, "ffmpeg", "bin"),
            Path.Combine(programFiles, "FFmpeg", "bin"),
            Path.Combine(programFilesX86, "ffmpeg", "bin"),
            Path.Combine(programFilesX86, "FFmpeg", "bin"),
            Path.Combine(localAppData, "Microsoft", "WinGet", "Links"),
            Path.Combine(localAppData, "Programs", "ffmpeg", "bin")
        };
    }

    private static BundleInfo EnsureBundleAvailable(
        CancellationToken cancellationToken,
        Action<string, double?>? reportProgress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var overrideArchivePath = Environment.GetEnvironmentVariable(ArchivePathOverrideEnvVar)?.Trim();
        if (!string.IsNullOrWhiteSpace(overrideArchivePath))
        {
            if (File.Exists(overrideArchivePath))
            {
                var overrideHash = ComputeSha256(
                    overrideArchivePath,
                    cancellationToken,
                    percentage => ReportProgress(reportProgress, "正在校验 FFmpeg 压缩包", percentage));
                return new BundleInfo(
                    overrideArchivePath,
                    overrideHash,
                    FfmpegResolutionSource.ArchiveOverride);
            }

            throw new FileNotFoundException(
                $"环境变量 {ArchivePathOverrideEnvVar} 指向的 FFmpeg 压缩包不存在。",
                overrideArchivePath);
        }

        var downloadRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BiliBiliLocalCacheManager",
            "downloads",
            "ffmpeg",
            "win-x64",
            BundleManifest.Tag);
        var bundlePath = Path.Combine(downloadRoot, BundleManifest.Asset);

        if (File.Exists(bundlePath))
        {
            var cachedHash = TryGetVerifiedCachedBundleHash(
                bundlePath,
                cancellationToken,
                reportProgress);
            if (!string.IsNullOrWhiteSpace(cachedHash))
            {
                return new BundleInfo(
                    bundlePath,
                    cachedHash,
                    FfmpegResolutionSource.DownloadedBundle);
            }
        }

        Directory.CreateDirectory(downloadRoot);
        return DownloadBundleWithVerification(
            bundlePath,
            cancellationToken,
            reportProgress);
    }

    private static string? TryGetVerifiedCachedBundleHash(
        string bundlePath,
        CancellationToken cancellationToken,
        Action<string, double?>? reportProgress)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actualHash = ComputeSha256(
                bundlePath,
                cancellationToken,
                percentage => ReportProgress(reportProgress, "正在校验缓存的 FFmpeg", percentage));
            return string.Equals(actualHash, BundleManifest.Sha256, StringComparison.OrdinalIgnoreCase)
                ? actualHash
                : null;
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                System.Security.SecurityException)
        {
            return null;
        }
    }

    private static BundleInfo DownloadBundleWithVerification(
        string bundlePath,
        CancellationToken cancellationToken,
        Action<string, double?>? reportProgress)
    {
        var bundleUrl = Environment.GetEnvironmentVariable(DownloadUrlOverrideEnvVar)?.Trim();
        if (string.IsNullOrWhiteSpace(bundleUrl))
        {
            bundleUrl = BundleManifest.Url;
        }

        var bundleTempPath = bundlePath + ".download";
        TryDeleteFile(bundleTempPath);

        try
        {
            ReportProgress(reportProgress, "正在下载 FFmpeg", 0d);
            DownloadFile(
                bundleUrl,
                bundleTempPath,
                cancellationToken,
                percentage => ReportProgress(reportProgress, "正在下载 FFmpeg", percentage));

            var actualHash = ComputeSha256(
                bundleTempPath,
                cancellationToken,
                percentage => ReportProgress(reportProgress, "正在校验 FFmpeg 压缩包", percentage));
            if (!string.Equals(actualHash, BundleManifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"下载的 FFmpeg 压缩包校验失败。预期 SHA256 为 {BundleManifest.Sha256}，实际为 {actualHash}。请稍后重试。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(bundleTempPath, bundlePath, overwrite: true);
            return new BundleInfo(
                bundlePath,
                actualHash,
                FfmpegResolutionSource.DownloadedBundle);
        }
        catch
        {
            TryDeleteFile(bundleTempPath);
            throw;
        }
    }

    private static string EnsureExtracted(
        BundleInfo bundle,
        CancellationToken cancellationToken,
        Action<string, double?>? reportProgress)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BiliBiliLocalCacheManager",
            "ffmpeg",
            "win-x64",
            bundle.Sha256);
        if (IsExtractionComplete(root, bundle.Sha256))
        {
            return root;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(root);

        using var archive = ZipFile.OpenRead(bundle.Path);
        var entries = new[]
        {
            (
                Entry: FindRequiredEntry(archive, "bin/ffmpeg.exe"),
                Destination: Path.Combine(root, "bin", "ffmpeg.exe")),
            (
                Entry: FindRequiredEntry(archive, "bin/ffprobe.exe"),
                Destination: Path.Combine(root, "bin", "ffprobe.exe")),
            (
                Entry: FindRequiredEntry(archive, "LICENSE.txt"),
                Destination: Path.Combine(root, "LICENSE.txt"))
        };
        var markerPath = Path.Combine(root, ExtractionMarkerFileName);
        if (TryFinalizeLegacyExtraction(entries, markerPath, bundle.Sha256, cancellationToken))
        {
            return root;
        }

        var totalBytes = entries.Sum(item => Math.Max(0L, item.Entry.Length));
        var copiedBytes = 0L;
        TryDeleteFile(markerPath);
        ReportProgress(reportProgress, "正在解压 FFmpeg", 0d);

        foreach (var item in entries)
        {
            ExtractEntry(
                item.Entry,
                item.Destination,
                cancellationToken,
                copied =>
                {
                    copiedBytes += copied;
                    double? percentage = totalBytes > 0
                        ? copiedBytes * 100d / totalBytes
                        : null;
                    ReportProgress(reportProgress, "正在解压 FFmpeg", percentage);
                });
        }

        WriteExtractionMarker(markerPath, bundle.Sha256, cancellationToken);

        ReportProgress(reportProgress, "正在解压 FFmpeg", 100d);
        return root;
    }

    private static bool TryFinalizeLegacyExtraction(
        IReadOnlyList<(ZipArchiveEntry Entry, string Destination)> entries,
        string markerPath,
        string bundleHash,
        CancellationToken cancellationToken)
    {
        if (File.Exists(markerPath))
        {
            return false;
        }

        foreach (var item in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(item.Destination) ||
                new FileInfo(item.Destination).Length != item.Entry.Length)
            {
                return false;
            }
        }

        WriteExtractionMarker(markerPath, bundleHash, cancellationToken);
        return true;
    }

    private static void WriteExtractionMarker(
        string markerPath,
        string bundleHash,
        CancellationToken cancellationToken)
    {
        var markerTempPath = markerPath + ".extracting";
        TryDeleteFile(markerTempPath);

        try
        {
            File.WriteAllTextAsync(
                    markerTempPath,
                    CreateExtractionMarkerContent(bundleHash),
                    cancellationToken)
                .GetAwaiter()
                .GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(markerTempPath, markerPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(markerTempPath);
        }
    }

    internal static bool IsExtractionComplete(string root, string bundleHash)
    {
        var binaryFolder = Path.Combine(root, "bin");
        var markerPath = Path.Combine(root, ExtractionMarkerFileName);
        if (!File.Exists(Path.Combine(binaryFolder, "ffmpeg.exe")) ||
            !File.Exists(Path.Combine(binaryFolder, "ffprobe.exe")) ||
            !File.Exists(Path.Combine(root, "LICENSE.txt")) ||
            !File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            return string.Equals(
                File.ReadAllText(markerPath),
                CreateExtractionMarkerContent(bundleHash),
                StringComparison.Ordinal);
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static void ExtractEntry(
        ZipArchiveEntry entry,
        string destinationPath,
        CancellationToken cancellationToken,
        Action<long>? reportBytesCopied = null)
    {
        ExtractEntryAsync(
                entry,
                destinationPath,
                cancellationToken,
                reportBytesCopied)
            .GetAwaiter()
            .GetResult();
    }

    private static async Task ExtractEntryAsync(
        ZipArchiveEntry entry,
        string destinationPath,
        CancellationToken cancellationToken,
        Action<long>? reportBytesCopied)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tempPath = destinationPath + ".extracting";
        TryDeleteFile(tempPath);

        try
        {
            {
                await using var input = entry.Open();
                await using var output = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    useAsync: true);
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    reportBytesCopied?.Invoke(read);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static ZipArchiveEntry FindRequiredEntry(ZipArchive archive, string relativeEntrySuffix)
    {
        var normalizedSuffix = relativeEntrySuffix.Replace('\\', '/');
        var entry = archive.Entries.FirstOrDefault(candidate =>
            candidate.FullName.EndsWith(normalizedSuffix, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            throw new InvalidDataException($"FFmpeg 压缩包中缺少必要文件：{relativeEntrySuffix}");
        }

        return entry;
    }

    private static void DownloadFile(
        string url,
        string destinationPath,
        CancellationToken cancellationToken,
        Action<double?>? reportProgress)
    {
        DownloadFileAsync(
                url,
                destinationPath,
                cancellationToken,
                reportProgress)
            .GetAwaiter()
            .GetResult();
    }

    private static async Task DownloadFileAsync(
        string url,
        string destinationPath,
        CancellationToken cancellationToken,
        Action<double?>? reportProgress)
    {
        using var request = CreateRequest(HttpMethod.Get, url);
        using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var outputStream = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            useAsync: true);
        await CopyStreamAsync(
                responseStream,
                outputStream,
                response.Content.Headers.ContentLength,
                cancellationToken,
                reportProgress)
            .ConfigureAwait(false);
    }

    internal static async Task CopyStreamAsync(
        Stream input,
        Stream output,
        long? totalLength,
        CancellationToken cancellationToken,
        Action<double?>? reportProgress = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        reportProgress?.Invoke(totalLength > 0 ? 0d : null);

        var buffer = new byte[128 * 1024];
        var copiedBytes = 0L;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copiedBytes += read;
            reportProgress?.Invoke(totalLength > 0
                ? Math.Min(100d, copiedBytes * 100d / totalLength.Value)
                : null);
            cancellationToken.ThrowIfCancellationRequested();
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        reportProgress?.Invoke(100d);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        return new HttpRequestMessage(method, url);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BiliBiliLocalCacheManager", "1.0"));
        return client;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore best-effort cleanup failures.
        }
    }

    internal static string ComputeSha256(
        string filePath,
        CancellationToken cancellationToken,
        Action<double?>? reportProgress = null)
    {
        return ComputeSha256Async(filePath, cancellationToken, reportProgress)
            .GetAwaiter()
            .GetResult();
    }

    private static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken,
        Action<double?>? reportProgress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var totalLength = stream.Length;
        var processedBytes = 0L;
        var buffer = new byte[128 * 1024];
        reportProgress?.Invoke(0d);

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            processedBytes += read;
            reportProgress?.Invoke(totalLength > 0
                ? Math.Min(100d, processedBytes * 100d / totalLength)
                : null);
            cancellationToken.ThrowIfCancellationRequested();
        }

        cancellationToken.ThrowIfCancellationRequested();
        reportProgress?.Invoke(100d);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string CreateExtractionMarkerContent(string bundleHash)
    {
        return $"{ExtractionMarkerVersion}\n{bundleHash}";
    }

    internal static FfmpegDiagnosticSnapshot CreateInitializedDiagnosticSnapshot(
        FfmpegResolutionSource source,
        string binaryFolder,
        Func<string, string?> versionReader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryFolder);
        ArgumentNullException.ThrowIfNull(versionReader);
        return new FfmpegDiagnosticSnapshot(
            IsInitialized: true,
            source,
            binaryFolder,
            ReadFfmpegVersion(binaryFolder, versionReader));
    }

    internal static string? ReadFfmpegVersion(
        string binaryFolder,
        Func<string, string?> versionReader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryFolder);
        ArgumentNullException.ThrowIfNull(versionReader);
        try
        {
            var version = versionReader(Path.Combine(binaryFolder, "ffmpeg.exe"));
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (Exception ex) when (!IsFatalException(ex))
        {
            return null;
        }
    }

    private static string? ReadFfmpegVersionFromFile(string executablePath)
    {
        var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
        return string.IsNullOrWhiteSpace(versionInfo.ProductVersion)
            ? versionInfo.FileVersion
            : versionInfo.ProductVersion;
    }

    private static bool IsFatalException(Exception exception)
    {
        return exception is OutOfMemoryException or
            StackOverflowException or
            AccessViolationException or
            AppDomainUnloadedException;
    }

    private static void ReportProgress(
        Action<string, double?>? reportProgress,
        string stage,
        double? percentage)
    {
        reportProgress?.Invoke(
            stage,
            percentage.HasValue
                ? Math.Clamp(percentage.Value, 0d, 100d)
                : null);
    }

    private readonly record struct BinaryFolderResolution(
        string BinaryFolder,
        FfmpegResolutionSource Source);

    private readonly record struct BundleInfo(
        string Path,
        string Sha256,
        FfmpegResolutionSource Source);

    private sealed class SemaphoreReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
