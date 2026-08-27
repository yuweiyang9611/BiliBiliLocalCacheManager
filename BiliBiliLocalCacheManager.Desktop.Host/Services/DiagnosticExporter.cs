using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BiliBiliLocalCacheManager.Playback.Contracts;

namespace BiliBiliLocalCacheManager.Desktop.Host.Services;

internal sealed class DiagnosticExporter(
    DiagnosticEventRecorder eventRecorder,
    IFfmpegDiagnosticsProvider ffmpegDiagnosticsProvider,
    IPlaybackArtifactStore artifactStore)
{
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s\""'<>]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CredentialPattern = new(
        @"\b(token|authorization|cookie|secret|api[-_]?key)\b\s*[:=]\s*[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<object> ExportAsync(
        string destinationPath,
        SettingsState settingsState,
        object? sessionState,
        CancellationToken cancellationToken)
    {
        var outputPath = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException($"Diagnostic destination directory not found: {parent}");
        }

        var temporaryPath = $"{outputPath}.{Guid.NewGuid():N}.writing";
        var events = eventRecorder.Snapshot()
            .Select(item => item with
            {
                Message = RedactText(
                    item.Message,
                    settingsState.Settings.RootPath,
                    artifactStore.RootDirectory)
            })
            .ToArray();
        var artifactStatistics = artifactStore.GetStatistics();
        try
        {
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             64 * 1024,
                             useAsync: true))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var diagnostics = new
                    {
                        generatedAtUtc = DateTimeOffset.UtcNow,
                        application = new
                        {
                            name = "BiliBili Local Cache Manager Desktop Host",
                            version = typeof(DiagnosticExporter).Assembly.GetName().Version?.ToString() ?? "unknown",
                            protocolVersion = 1
                        },
                        runtime = new
                        {
                            framework = RuntimeInformation.FrameworkDescription,
                            os = RuntimeInformation.OSDescription,
                            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                            osArchitecture = RuntimeInformation.OSArchitecture.ToString()
                        },
                        capabilities = new
                        {
                            trashPurge = OperatingSystem.IsWindows(),
                            nativeDialogs = false
                        },
                        settings = new
                        {
                            settingsState.Settings.SchemaVersion,
                            rootPath = string.IsNullOrWhiteSpace(settingsState.Settings.RootPath)
                                ? string.Empty
                                : "[CACHE_ROOT]",
                            settingsState.Settings.IncludeIncomplete,
                            settingsState.Settings.PreferredPlayer,
                            settingsState.Settings.TranscodeCacheRetentionDays,
                            settingsState.Settings.TranscodeCacheMaxSizeGigabytes,
                            settingsState.CanSave,
                            settingsState.SourceSchemaVersion,
                            message = string.IsNullOrWhiteSpace(settingsState.Message)
                                ? settingsState.Message
                                : RedactText(
                                    settingsState.Message,
                                    settingsState.Settings.RootPath,
                                    artifactStore.RootDirectory)
                        },
                        ffmpeg = RedactFfmpeg(ffmpegDiagnosticsProvider.GetSnapshot()),
                        transcodeCache = new
                        {
                            rootDirectory = "[TRANSCODE_CACHE_ROOT]",
                            artifactStatistics.FileCount,
                            artifactStatistics.TotalBytes
                        },
                        session = sessionState
                    };

                    await WriteJsonEntryAsync(
                        archive,
                        "diagnostics.json",
                        diagnostics,
                        cancellationToken);
                    await WriteJsonEntryAsync(
                        archive,
                        "recent-events.json",
                        events,
                        cancellationToken);
                }

                await output.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath, overwrite: true);
            return new
            {
                outputPath,
                sizeBytes = new FileInfo(outputPath).Length,
                eventCount = events.Length
            };
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static object RedactFfmpeg(BiliBiliLocalCacheManager.Playback.Models.FfmpegDiagnosticSnapshot snapshot)
    {
        return new
        {
            snapshot.IsInitialized,
            snapshot.Source,
            binaryFolder = string.IsNullOrWhiteSpace(snapshot.BinaryFolder)
                ? null
                : "[FFMPEG_BINARY_FOLDER]",
            snapshot.Version
        };
    }

    private static string RedactText(
        string value,
        string? cacheRoot,
        string? transcodeCacheRoot)
    {
        var redacted = ReplaceKnownPath(value, cacheRoot, "[CACHE_ROOT]");
        redacted = ReplaceKnownPath(redacted, transcodeCacheRoot, "[TRANSCODE_CACHE_ROOT]");
        redacted = ReplaceKnownPath(
            redacted,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "[USER_HOME]");
        redacted = UrlPattern.Replace(redacted, "[URL]");
        return CredentialPattern.Replace(redacted, "$1=[REDACTED]");
    }

    private static string ReplaceKnownPath(string value, string? path, string marker)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return value;
        }

        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return value;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return value.Replace(normalized, marker, comparison);
    }

    private static async Task WriteJsonEntryAsync<T>(
        ZipArchive archive,
        string name,
        T value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(
            stream,
            value,
            SerializerOptions,
            cancellationToken);
    }

    private static void TryDelete(string path)
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
        }
    }
}
