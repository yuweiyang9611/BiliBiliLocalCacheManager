using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;
using BiliBiliLocalCacheManager.Desktop.Host.Services;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

namespace BiliBiliLocalCacheManager.Desktop.Host.Tests;

public sealed class DesktopHostContractTests
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task InitialState_UsesTheElectronWireContract()
    {
        using var workspace = new HostTestWorkspace();
        var application = workspace.CreateApplication();

        var result = await DispatchAsync(application, "initialState", "{}");

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        var settings = result.GetProperty("settings");
        Assert.Equal(string.Empty, settings.GetProperty("rootPath").GetString());
        Assert.True(settings.GetProperty("includePartName").GetBoolean());
        Assert.True(settings.GetProperty("includeOwnerName").GetBoolean());
        Assert.True(settings.GetProperty("includeBvid").GetBoolean());
        Assert.True(settings.GetProperty("includeAvid").GetBoolean());
        Assert.Equal("contains", settings.GetProperty("matchMode").GetString());
        Assert.Equal("system", settings.GetProperty("playerPreference").GetString());

        Assert.Equal(JsonValueKind.Array, result.GetProperty("items").ValueKind);
        Assert.Equal(JsonValueKind.Array, result.GetProperty("trash").ValueKind);
        var storage = result.GetProperty("storage");
        Assert.Equal(0, storage.GetProperty("originalCache").GetProperty("bytes").GetInt64());
        Assert.Equal(0, storage.GetProperty("trash").GetProperty("itemCount").GetInt32());

        var capabilities = result.GetProperty("capabilities");
        Assert.Equal(OperatingSystem.IsWindows(), capabilities.GetProperty("trashPurge").GetBoolean());
        Assert.False(capabilities.GetProperty("nativeWayland").GetBoolean());
    }

    [Fact]
    public async Task SettingsUpdate_PersistsPortableWireValues()
    {
        using var workspace = new HostTestWorkspace();
        var application = workspace.CreateApplication();
        var parameters = JsonSerializer.Serialize(new
        {
            patch = new
            {
                rootPath = workspace.CacheRoot,
                includeIncomplete = true,
                keyword = "UP 主",
                matchMode = "prefix",
                playerPreference = "mpv",
                transcodeCacheRetentionDays = 45,
                transcodeCacheMaxSizeGigabytes = 12
            }
        });

        var updated = await DispatchAsync(application, "settings.update", parameters);

        Assert.Equal(workspace.CacheRoot, updated.GetProperty("rootPath").GetString());
        Assert.True(updated.GetProperty("includeIncomplete").GetBoolean());
        Assert.Equal("UP 主", updated.GetProperty("keyword").GetString());
        Assert.Equal("prefix", updated.GetProperty("matchMode").GetString());
        Assert.Equal("mpv", updated.GetProperty("playerPreference").GetString());

        var reloaded = workspace.CreateApplication();
        var persisted = await DispatchAsync(reloaded, "settings.get", "{}");
        Assert.Equal(workspace.CacheRoot, persisted.GetProperty("rootPath").GetString());
        Assert.Equal(45, persisted.GetProperty("transcodeCacheRetentionDays").GetInt32());
        Assert.Equal(12, persisted.GetProperty("transcodeCacheMaxSizeGigabytes").GetInt32());
    }

    [Fact]
    public async Task EmptyCacheRoot_ScansAndSearchesWithoutShapeDrift()
    {
        using var workspace = new HostTestWorkspace();
        var application = workspace.CreateApplication();
        var scanParameters = JsonSerializer.Serialize(new
        {
            rootPath = workspace.CacheRoot,
            includeIncomplete = true
        });

        var scan = await DispatchAsync(application, "scan", scanParameters);
        Assert.Empty(scan.GetProperty("items").EnumerateArray());
        Assert.Equal(0, scan.GetProperty("includedEntries").GetInt32());

        var searchParameters = JsonSerializer.Serialize(new
        {
            rootPath = workspace.CacheRoot,
            includeIncomplete = true,
            keyword = string.Empty,
            matchMode = "contains",
            splitKeywords = true,
            anyKeywords = false,
            includePartName = true,
            includeOwnerName = true,
            includeBvid = true,
            includeAvid = true,
            caseSensitive = false
        });
        var search = await DispatchAsync(application, "search", searchParameters);
        Assert.Equal(JsonValueKind.Array, search.ValueKind);
        Assert.Empty(search.EnumerateArray());
    }

    [Fact]
    public async Task ArtifactMaintenance_UsesStableWireShapeAndRequiresClearConfirmation()
    {
        using var workspace = new HostTestWorkspace();
        var application = workspace.CreateApplication();

        var cleanup = await DispatchAsync(application, "artifacts.cleanup", "{}");
        Assert.Equal(0, cleanup.GetProperty("deletedFileCount").GetInt32());
        Assert.Equal(0, cleanup.GetProperty("freedBytes").GetInt64());
        Assert.Equal(0, cleanup.GetProperty("failedFileCount").GetInt32());
        Assert.Equal(0, cleanup.GetProperty("remainingBytes").GetInt64());

        var exception = await Assert.ThrowsAsync<RpcException>(
            () => DispatchAsync(application, "artifacts.clear", "{}"));
        Assert.Equal("confirmation_required", exception.Code);

        var cleared = await DispatchAsync(
            application,
            "artifacts.clear",
            "{\"confirmed\":true}");
        Assert.Equal(0, cleared.GetProperty("failedFileCount").GetInt32());
        Assert.Equal(0, cleared.GetProperty("remainingBytes").GetInt64());
    }

    [Fact]
    public async Task JsonLinesServer_ReturnsStructuredErrors()
    {
        using var workspace = new HostTestWorkspace();
        var input = new StringReader(
            "{not-json}\n" +
            "{\"id\":\"missing\",\"method\":\"not.a.method\",\"params\":{}}\n");
        var output = new StringWriter();
        var server = new JsonLineRpcServer(workspace.CreateApplication(), input, output);

        await server.RunAsync();

        var messages = ParseLines(output.ToString());
        Assert.Contains(messages, message =>
            message.GetProperty("error").GetProperty("code").GetString() == "parse_error");
        var missing = Assert.Single(messages, message =>
            message.TryGetProperty("id", out var id) && id.GetString() == "missing");
        Assert.Equal("method_not_found", missing.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task JsonLinesServer_RejectsOversizedInputWithoutEchoingIt()
    {
        using var workspace = new HostTestWorkspace();
        var input = new StringReader(new string('x', 1024 * 1024 + 1) + "\n");
        var output = new StringWriter();
        var server = new JsonLineRpcServer(workspace.CreateApplication(), input, output);

        await server.RunAsync();

        var message = Assert.Single(ParseLines(output.ToString()));
        Assert.Equal(string.Empty, message.GetProperty("id").GetString());
        Assert.Equal("request_too_large", message.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(new string('x', 256), output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticExport_RedactsKnownPathsUrlsAndCredentials()
    {
        using var workspace = new HostTestWorkspace();
        var recorder = new DiagnosticEventRecorder();
        recorder.Record(
            "Playback",
            "Error",
            $"Failed at {workspace.CacheRoot}: https://example.invalid/media?token=query token=top-secret");
        var exporter = new DiagnosticExporter(
            recorder,
            new BundledFfmpegDiagnosticsProvider(),
            new PlaybackArtifactStore(workspace.TranscodeRoot));
        var destination = Path.Combine(workspace.Root, "diagnostics.zip");

        await exporter.ExportAsync(
            destination,
            new SettingsState(
                new DesktopSettings { RootPath = workspace.CacheRoot },
                CanSave: true,
                SourceSchemaVersion: DesktopSettings.CurrentSchemaVersion,
                Message: $"Settings backup under {workspace.CacheRoot}; api_key=diagnostic-secret"),
            sessionState: new { rootConfigured = true },
            CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        var entry = Assert.Single(archive.Entries, item => item.FullName == "recent-events.json");
        using var stream = entry.Open();
        using var document = await JsonDocument.ParseAsync(stream);
        var message = document.RootElement[0].GetProperty("message").GetString()!;
        Assert.DoesNotContain(workspace.CacheRoot, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.invalid", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("top-secret", message, StringComparison.Ordinal);
        Assert.Contains("[CACHE_ROOT]", message, StringComparison.Ordinal);
        Assert.Contains("[URL]", message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", message, StringComparison.Ordinal);

        var diagnosticsEntry = Assert.Single(
            archive.Entries,
            item => item.FullName == "diagnostics.json");
        using var diagnosticsStream = diagnosticsEntry.Open();
        using var diagnosticsDocument = await JsonDocument.ParseAsync(diagnosticsStream);
        var settingsMessage = diagnosticsDocument.RootElement
            .GetProperty("settings")
            .GetProperty("message")
            .GetString()!;
        Assert.DoesNotContain(workspace.CacheRoot, settingsMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diagnostic-secret", settingsMessage, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> DispatchAsync(
        DesktopHostApplication application,
        string method,
        string parameterJson)
    {
        using var parameters = JsonDocument.Parse(parameterJson);
        var result = await application.DispatchAsync(
            Guid.NewGuid().ToString("N"),
            method,
            parameters.RootElement,
            CancellationToken.None);
        return JsonSerializer.SerializeToElement(result, WireOptions);
    }

    private static IReadOnlyList<JsonElement> ParseLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToArray();

    private sealed class HostTestWorkspace : IDisposable
    {
        private readonly string? _previousSettingsPath;
        private readonly string? _previousTranscodeRoot;

        public HostTestWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"bili_desktop_host_tests_{Guid.NewGuid():N}");
            CacheRoot = Path.Combine(Root, "cache");
            TranscodeRoot = Path.Combine(Root, "transcode");
            Directory.CreateDirectory(CacheRoot);
            Directory.CreateDirectory(TranscodeRoot);

            _previousSettingsPath = Environment.GetEnvironmentVariable(
                "BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH");
            _previousTranscodeRoot = Environment.GetEnvironmentVariable(
                "BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT");
            Environment.SetEnvironmentVariable(
                "BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH",
                Path.Combine(Root, "settings.json"));
            Environment.SetEnvironmentVariable(
                "BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT",
                TranscodeRoot);
        }

        public string Root { get; }

        public string CacheRoot { get; }

        public string TranscodeRoot { get; }

        public DesktopHostApplication CreateApplication() => new();

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(
                "BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH",
                _previousSettingsPath);
            Environment.SetEnvironmentVariable(
                "BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT",
                _previousTranscodeRoot);
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
