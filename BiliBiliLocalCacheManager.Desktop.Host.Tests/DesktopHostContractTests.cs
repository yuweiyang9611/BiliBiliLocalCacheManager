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
        File.WriteAllBytes(
            Path.Combine(workspace.TranscodeRoot, "must-not-be-measured.bin"),
            new byte[32]);
        var application = workspace.CreateApplication();

        var result = await DispatchAsync(application, "initialState", "{}");

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal(2, result.GetProperty("protocolVersion").GetInt32());
        var settings = result.GetProperty("settings");
        Assert.Equal(string.Empty, settings.GetProperty("rootPath").GetString());
        Assert.True(settings.GetProperty("rememberRootPath").GetBoolean());
        Assert.False(settings.GetProperty("scanOnStartup").GetBoolean());
        Assert.True(settings.GetProperty("includePartName").GetBoolean());
        Assert.True(settings.GetProperty("includeOwnerName").GetBoolean());
        Assert.True(settings.GetProperty("includeBvid").GetBoolean());
        Assert.True(settings.GetProperty("includeAvid").GetBoolean());
        Assert.Equal("contains", settings.GetProperty("matchMode").GetString());
        Assert.Equal("system", settings.GetProperty("playerPreference").GetString());

        Assert.Equal(JsonValueKind.Array, result.GetProperty("items").ValueKind);
        Assert.Empty(result.GetProperty("trash").EnumerateArray());
        var storage = result.GetProperty("storage");
        Assert.Equal(0, storage.GetProperty("originalCache").GetProperty("bytes").GetInt64());
        Assert.Equal(0, storage.GetProperty("transcodeCache").GetProperty("bytes").GetInt64());
        Assert.Equal(0, storage.GetProperty("trash").GetProperty("itemCount").GetInt32());
        var settingsState = result.GetProperty("settingsState");
        Assert.True(settingsState.GetProperty("canSave").GetBoolean());
        Assert.Equal(JsonValueKind.Null, settingsState.GetProperty("sourceSchemaVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, settingsState.GetProperty("message").ValueKind);

        var capabilities = result.GetProperty("capabilities");
        Assert.Equal(OperatingSystem.IsWindows(), capabilities.GetProperty("trashPurge").GetBoolean());
        Assert.False(capabilities.GetProperty("nativeWayland").GetBoolean());
    }

    [Fact]
    public async Task InitialState_ReportsSchemaOneMigrationWithoutSavingIt()
    {
        using var workspace = new HostTestWorkspace();
        File.WriteAllText(
            workspace.SettingsPath,
            JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                RootPath = workspace.CacheRoot,
                IncludeIncomplete = true
            }));

        var result = await DispatchAsync(workspace.CreateApplication(), "initialState", "{}");

        var settings = result.GetProperty("settings");
        Assert.Equal(workspace.CacheRoot, settings.GetProperty("rootPath").GetString());
        Assert.True(settings.GetProperty("rememberRootPath").GetBoolean());
        Assert.False(settings.GetProperty("scanOnStartup").GetBoolean());
        var settingsState = result.GetProperty("settingsState");
        Assert.True(settingsState.GetProperty("canSave").GetBoolean());
        Assert.Equal(1, settingsState.GetProperty("sourceSchemaVersion").GetInt32());
        Assert.Contains(
            "migrated from schema v1",
            settingsState.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);

        using var persisted = JsonDocument.Parse(File.ReadAllBytes(workspace.SettingsPath));
        Assert.Equal(1, persisted.RootElement.GetProperty("SchemaVersion").GetInt32());
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
    public async Task SettingsUpdate_DisablingRememberRootClearsPrivatePathAndStartupScan()
    {
        using var workspace = new HostTestWorkspace();
        var application = workspace.CreateApplication();
        var parameters = JsonSerializer.Serialize(new
        {
            patch = new
            {
                rootPath = workspace.CacheRoot,
                rememberRootPath = false,
                scanOnStartup = true,
                includeIncomplete = true
            }
        });

        var updated = await DispatchAsync(application, "settings.update", parameters);

        Assert.Equal(string.Empty, updated.GetProperty("rootPath").GetString());
        Assert.False(updated.GetProperty("rememberRootPath").GetBoolean());
        Assert.False(updated.GetProperty("scanOnStartup").GetBoolean());
        Assert.True(updated.GetProperty("includeIncomplete").GetBoolean());
        using var persisted = JsonDocument.Parse(File.ReadAllBytes(workspace.SettingsPath));
        Assert.Equal(DesktopSettings.CurrentSchemaVersion, persisted.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(string.Empty, persisted.RootElement.GetProperty("RootPath").GetString());
        Assert.False(persisted.RootElement.GetProperty("RememberRootPath").GetBoolean());
        Assert.False(persisted.RootElement.GetProperty("ScanOnStartup").GetBoolean());
    }

    [Fact]
    public async Task Scan_PersistSettingsFalseLeavesSettingsUntouched()
    {
        using var workspace = new HostTestWorkspace();
        var application = workspace.CreateApplication();
        var parameters = JsonSerializer.Serialize(new
        {
            rootPath = workspace.CacheRoot,
            includeIncomplete = true,
            persistSettings = false
        });

        await DispatchAsync(application, "scan", parameters);

        var settings = await DispatchAsync(application, "settings.get", "{}");
        Assert.Equal(string.Empty, settings.GetProperty("rootPath").GetString());
        Assert.False(settings.GetProperty("includeIncomplete").GetBoolean());
        Assert.False(File.Exists(workspace.SettingsPath));
    }

    [Fact]
    public async Task Scan_WhenRootIsNotRememberedPersistsOnlyNonSensitiveOptions()
    {
        using var workspace = new HostTestWorkspace();
        var application = workspace.CreateApplication();
        await DispatchAsync(
            application,
            "settings.update",
            """{"patch":{"rememberRootPath":false}}""");
        var parameters = JsonSerializer.Serialize(new
        {
            rootPath = workspace.CacheRoot,
            includeIncomplete = true
        });

        await DispatchAsync(application, "scan", parameters);

        var settings = await DispatchAsync(application, "settings.get", "{}");
        Assert.Equal(string.Empty, settings.GetProperty("rootPath").GetString());
        Assert.False(settings.GetProperty("rememberRootPath").GetBoolean());
        Assert.False(settings.GetProperty("scanOnStartup").GetBoolean());
        Assert.True(settings.GetProperty("includeIncomplete").GetBoolean());
        using var persisted = JsonDocument.Parse(File.ReadAllBytes(workspace.SettingsPath));
        Assert.Equal(string.Empty, persisted.RootElement.GetProperty("RootPath").GetString());
    }

    [Fact]
    public async Task SettingsUpdate_PreservesTheIndexFromTheValidationScan()
    {
        using var workspace = new HostTestWorkspace();
        var application = workspace.CreateApplication();
        await DispatchAsync(
            application,
            "scan",
            JsonSerializer.Serialize(new
            {
                rootPath = workspace.CacheRoot,
                includeIncomplete = true,
                persistSettings = false
            }));

        await DispatchAsync(
            application,
            "settings.update",
            JsonSerializer.Serialize(new
            {
                patch = new
                {
                    rootPath = workspace.CacheRoot,
                    includeIncomplete = true
                }
            }));

        var destination = Path.Combine(workspace.Root, "index-session.zip");
        await DispatchAsync(
            application,
            "diagnostics.export",
            JsonSerializer.Serialize(new
            {
                outputPath = destination,
                rootPath = workspace.CacheRoot
            }));
        using var archive = ZipFile.OpenRead(destination);
        var diagnosticsEntry = Assert.Single(
            archive.Entries,
            entry => entry.FullName == "diagnostics.json");
        using var diagnosticsStream = diagnosticsEntry.Open();
        using var diagnostics = await JsonDocument.ParseAsync(diagnosticsStream);
        Assert.True(
            diagnostics.RootElement
                .GetProperty("session")
                .GetProperty("includeIncomplete")
                .GetBoolean());
    }

    [Fact]
    public async Task InitialState_DoesNotLoadExistingTrash()
    {
        using var workspace = new HostTestWorkspace();
        var application = workspace.CreateApplication();
        await DispatchAsync(
            application,
            "settings.update",
            JsonSerializer.Serialize(new
            {
                patch = new { rootPath = workspace.CacheRoot }
            }));
        Directory.CreateDirectory(Path.Combine(workspace.CacheRoot, "123"));
        var moved = await DispatchAsync(
            application,
            "trash.move",
            JsonSerializer.Serialize(new
            {
                rootPath = workspace.CacheRoot,
                avids = new[] { "123" }
            }));
        Assert.Contains("123", moved.GetProperty("moved").EnumerateArray().Select(item => item.GetString()));

        var result = await DispatchAsync(workspace.CreateApplication(), "initialState", "{}");

        Assert.Empty(result.GetProperty("trash").EnumerateArray());
        Assert.Equal(0, result.GetProperty("storage").GetProperty("trash").GetProperty("itemCount").GetInt32());
        var listed = await DispatchAsync(
            application,
            "trash.list",
            JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        Assert.Single(listed.EnumerateArray());
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
        Assert.Equal(0, scan.GetProperty("totalItems").GetInt32());
        Assert.False(scan.GetProperty("hasMore").GetBoolean());
        var indexToken = scan.GetProperty("indexToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(indexToken));

        var searchParameters = JsonSerializer.Serialize(new
        {
            indexToken,
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
        Assert.Equal(JsonValueKind.Object, search.ValueKind);
        Assert.Equal(indexToken, search.GetProperty("indexToken").GetString());
        Assert.Empty(search.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task ScanAndSearch_PageCacheSummariesWithoutSegments()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "First cache");
        workspace.CreateCache(202, new string('T', 5000));
        workspace.CreateCache(303, "Third cache");
        var application = workspace.CreateApplication();

        var scan = await DispatchAsync(
            application,
            "scan",
            JsonSerializer.Serialize(new
            {
                rootPath = workspace.CacheRoot,
                includeIncomplete = false,
                offset = 0,
                pageSize = 2
            }));

        Assert.Equal(3, scan.GetProperty("includedEntries").GetInt32());
        Assert.Equal(0, scan.GetProperty("offset").GetInt32());
        Assert.Equal(2, scan.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, scan.GetProperty("totalItems").GetInt32());
        Assert.True(scan.GetProperty("hasMore").GetBoolean());
        var firstPage = scan.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, firstPage.Length);
        Assert.All(firstPage, item => Assert.False(item.TryGetProperty("segments", out _)));
        Assert.All(
            firstPage,
            item => Assert.InRange(item.GetProperty("title").GetString()!.Length, 0, 4096));

        var indexToken = scan.GetProperty("indexToken").GetString()!;
        var secondPage = await DispatchAsync(
            application,
            "search",
            JsonSerializer.Serialize(new
            {
                indexToken,
                keyword = string.Empty,
                offset = 2,
                pageSize = 2
            }));

        Assert.Equal(indexToken, secondPage.GetProperty("indexToken").GetString());
        Assert.Equal(3, secondPage.GetProperty("totalItems").GetInt32());
        Assert.False(secondPage.GetProperty("hasMore").GetBoolean());
        Assert.Single(secondPage.GetProperty("items").EnumerateArray());
        Assert.False(
            secondPage.GetProperty("items")[0].TryGetProperty("segments", out _));
    }

    [Fact]
    public async Task CacheDetails_PagesSegmentsAndBuildsPlansOnDemand()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(404, "Paged details", segmentCount: 3);
        var application = workspace.CreateApplication();
        var scan = await DispatchAsync(
            application,
            "scan",
            JsonSerializer.Serialize(new
            {
                rootPath = workspace.CacheRoot,
                pageSize = 1
            }));
        var indexToken = scan.GetProperty("indexToken").GetString()!;

        var firstPage = await DispatchAsync(
            application,
            "cache.details",
            JsonSerializer.Serialize(new
            {
                indexToken,
                avid = "404",
                offset = 0,
                pageSize = 2
            }));

        Assert.Equal("404", firstPage.GetProperty("avid").GetString());
        Assert.Equal(3, firstPage.GetProperty("totalItems").GetInt32());
        Assert.True(firstPage.GetProperty("hasMore").GetBoolean());
        Assert.False(firstPage.GetProperty("item").TryGetProperty("segments", out _));
        var segments = firstPage.GetProperty("segments").EnumerateArray().ToArray();
        Assert.Equal(2, segments.Length);
        Assert.All(segments, segment =>
        {
            Assert.NotEqual("Unknown", segment.GetProperty("structureKind").GetString());
            Assert.True(segment.GetProperty("isPlayable").GetBoolean());
        });

        var secondPage = await DispatchAsync(
            application,
            "cache.details",
            JsonSerializer.Serialize(new
            {
                indexToken,
                avid = "404",
                offset = 2,
                pageSize = 2
            }));
        Assert.Single(secondPage.GetProperty("segments").EnumerateArray());
        Assert.False(secondPage.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task SearchAndDetails_RejectMissingOrStaleIndexTokens()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(505, "Token protected");
        var application = workspace.CreateApplication();
        var scan = await DispatchAsync(
            application,
            "scan",
            JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));

        var missing = await Assert.ThrowsAsync<RpcException>(() =>
            DispatchAsync(application, "search", "{}"));
        Assert.Equal("invalid_params", missing.Code);

        var staleSearch = await Assert.ThrowsAsync<RpcException>(() =>
            DispatchAsync(
                application,
                "search",
                JsonSerializer.Serialize(new { indexToken = "not-current" })));
        Assert.Equal("stale_index", staleSearch.Code);

        var staleDetails = await Assert.ThrowsAsync<RpcException>(() =>
            DispatchAsync(
                application,
                "cache.details",
                JsonSerializer.Serialize(new
                {
                    indexToken = "not-current",
                    avid = "505"
                })));
        Assert.Equal("stale_index", staleDetails.Code);
        Assert.False(string.IsNullOrWhiteSpace(scan.GetProperty("indexToken").GetString()));
    }

    [Fact]
    public async Task CacheMutation_InvalidatesTheCurrentIndexToken()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(606, "Mutation invalidates");
        var application = workspace.CreateApplication();
        var scan = await DispatchAsync(
            application,
            "scan",
            JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        var indexToken = scan.GetProperty("indexToken").GetString()!;

        await DispatchAsync(
            application,
            "trash.move",
            JsonSerializer.Serialize(new
            {
                rootPath = workspace.CacheRoot,
                avids = new[] { "606" }
            }));

        var stale = await Assert.ThrowsAsync<RpcException>(() =>
            DispatchAsync(
                application,
                "search",
                JsonSerializer.Serialize(new { indexToken })));
        Assert.Equal("stale_index", stale.Code);
    }

    [Fact]
    public async Task BatchExport_CancellationRemovesStagingAndDoesNotPublishFinalDirectory()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(707, "Cancelled batch", segmentCount: 2);
        var exportParent = Path.Combine(workspace.Root, "cancelled-export");
        Directory.CreateDirectory(exportParent);
        var application = workspace.CreateApplication();
        await DispatchAsync(
            application,
            "scan",
            JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DispatchAsync(
                application,
                "export",
                JsonSerializer.Serialize(new
                {
                    rootPath = workspace.CacheRoot,
                    targets = new[]
                    {
                        new { avid = "707", pageIndexes = new[] { 1, 2 } }
                    },
                    outputPath = exportParent
                }),
                cancellation.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(exportParent));
    }

    [Fact]
    public async Task BatchExport_PublishesCompletedStagingDirectory()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(717, "Successful batch", segmentCount: 2);
        var exportParent = Path.Combine(workspace.Root, "successful-export");
        Directory.CreateDirectory(exportParent);
        var application = workspace.CreateApplication();

        var result = await DispatchAsync(
            application,
            "export",
            JsonSerializer.Serialize(new
            {
                rootPath = workspace.CacheRoot,
                targets = new[]
                {
                    new { avid = "717", pageIndexes = new[] { 1, 2 } }
                },
                outputPath = exportParent
            }));

        var publishedDirectory = result.GetProperty("outputPath").GetString()!;
        Assert.Equal(Path.Combine(exportParent, "cache-export"), publishedDirectory);
        Assert.True(Directory.Exists(publishedDirectory));
        Assert.Equal(2, Directory.EnumerateFiles(publishedDirectory, "*.mp4").Count());
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(exportParent),
            path => Path.GetFileName(path).EndsWith(".staging", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BatchExport_FailureRemovesStagingAndDoesNotPublishPartialResults()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(808, "Failed batch", segmentCount: 2);
        var exportParent = Path.Combine(workspace.Root, "failed-export");
        Directory.CreateDirectory(exportParent);
        var application = workspace.CreateApplication();
        await DispatchAsync(
            application,
            "scan",
            JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));

        var failure = await Assert.ThrowsAsync<RpcException>(() =>
            DispatchAsync(
                application,
                "export",
                JsonSerializer.Serialize(new
                {
                    rootPath = workspace.CacheRoot,
                    targets = new[]
                    {
                        new { avid = "808", pageIndexes = new[] { 1, 999 } }
                    },
                    outputPath = exportParent
                })));

        Assert.Equal("operation_failed", failure.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(exportParent));
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
    public async Task TrashPurge_RequiresExplicitRootAndNonEmptyCompleteSelection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new HostTestWorkspace();
        var application = workspace.CreateApplication();
        await DispatchAsync(
            application,
            "settings.update",
            JsonSerializer.Serialize(new
            {
                patch = new { rootPath = workspace.CacheRoot }
            }));

        var missingRoot = await Assert.ThrowsAsync<RpcException>(() =>
            DispatchAsync(
                application,
                "trash.purge",
                """{"confirmed":true,"entryIds":["not-used"]}"""));
        Assert.Equal("invalid_params", missingRoot.Code);

        var missingIds = await Assert.ThrowsAsync<RpcException>(() =>
            DispatchAsync(
                application,
                "trash.purge",
                JsonSerializer.Serialize(new
                {
                    rootPath = workspace.CacheRoot,
                    confirmed = true
                })));
        Assert.Equal("invalid_params", missingIds.Code);

        Directory.CreateDirectory(Path.Combine(workspace.CacheRoot, "321"));
        Directory.CreateDirectory(Path.Combine(workspace.CacheRoot, "654"));
        await DispatchAsync(
            application,
            "trash.move",
            JsonSerializer.Serialize(new
            {
                rootPath = workspace.CacheRoot,
                avids = new[] { "321", "654" }
            }));
        var entries = await DispatchAsync(
            application,
            "trash.list",
            JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        var entryIds = entries.EnumerateArray()
            .Select(entry => entry.GetProperty("id").GetString()!)
            .ToArray();
        Assert.Equal(2, entryIds.Length);

        var trashDirectory = Path.GetDirectoryName(entryIds[0])!;
        var aboveLegacyBatchLimit = new[] { entryIds[0] }
            .Concat(Enumerable.Range(0, 1000).Select(index =>
                Path.Combine(trashDirectory, $"snapshot-placeholder-{index}")))
            .ToArray();
        var acceptedCompleteSnapshotSize = await Assert.ThrowsAsync<RpcException>(() =>
            DispatchAsync(
                application,
                "trash.purge",
                JsonSerializer.Serialize(new
                {
                    rootPath = workspace.CacheRoot,
                    confirmed = true,
                    entryIds = aboveLegacyBatchLimit
                })));
        Assert.Equal("unsupported_operation", acceptedCompleteSnapshotSize.Code);

        var partialSelection = await Assert.ThrowsAsync<RpcException>(() =>
            DispatchAsync(
                application,
                "trash.purge",
                JsonSerializer.Serialize(new
                {
                    rootPath = workspace.CacheRoot,
                    confirmed = true,
                    entryIds = new[] { entryIds[0] }
                })));
        Assert.Equal("unsupported_operation", partialSelection.Code);
        var partialDetails = JsonSerializer.SerializeToElement(partialSelection.Details);
        Assert.Equal(
            "trash_snapshot_changed",
            partialDetails.GetProperty("reason").GetString());
        var remaining = await DispatchAsync(
            application,
            "trash.list",
            JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        Assert.Equal(2, remaining.GetArrayLength());

        Directory.CreateDirectory(Path.Combine(workspace.CacheRoot, "987"));
        await DispatchAsync(
            application,
            "trash.move",
            JsonSerializer.Serialize(new
            {
                rootPath = workspace.CacheRoot,
                avids = new[] { "987" }
            }));
        var staleSnapshot = await Assert.ThrowsAsync<RpcException>(() =>
            DispatchAsync(
                application,
                "trash.purge",
                JsonSerializer.Serialize(new
                {
                    rootPath = workspace.CacheRoot,
                    confirmed = true,
                    entryIds
                })));
        Assert.Equal("unsupported_operation", staleSnapshot.Code);
        var staleDetails = JsonSerializer.SerializeToElement(staleSnapshot.Details);
        Assert.Equal(
            "trash_snapshot_changed",
            staleDetails.GetProperty("reason").GetString());
        remaining = await DispatchAsync(
            application,
            "trash.list",
            JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        Assert.Equal(3, remaining.GetArrayLength());
        entryIds = remaining.EnumerateArray()
            .Select(entry => entry.GetProperty("id").GetString()!)
            .ToArray();

        var purged = await DispatchAsync(
            application,
            "trash.purge",
            JsonSerializer.Serialize(new
            {
                rootPath = workspace.CacheRoot,
                confirmed = true,
                entryIds
            }));
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        Assert.Equal(
            entryIds.OrderBy(value => value, pathComparer),
            purged.GetProperty("purged").EnumerateArray()
                .Select(item => item.GetString()!)
                .OrderBy(value => value, pathComparer));
        Assert.Empty(purged.GetProperty("failed").EnumerateArray());
        var empty = await DispatchAsync(
            application,
            "trash.list",
            JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        Assert.Empty(empty.EnumerateArray());
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
    public async Task JsonLinesServer_InitialStateIncludesNullableSettingsMetadata()
    {
        using var workspace = new HostTestWorkspace();
        var input = new StringReader(
            """{"id":"initial","method":"initialState","params":{}}""" + "\n");
        var output = new StringWriter();
        var server = new JsonLineRpcServer(workspace.CreateApplication(), input, output);

        await server.RunAsync();

        var message = Assert.Single(ParseLines(output.ToString()));
        var settingsState = message.GetProperty("result").GetProperty("settingsState");
        Assert.True(settingsState.GetProperty("canSave").GetBoolean());
        Assert.Equal(JsonValueKind.Null, settingsState.GetProperty("sourceSchemaVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, settingsState.GetProperty("message").ValueKind);
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
                new DesktopSettings { RememberRootPath = false },
                CanSave: true,
                SourceSchemaVersion: DesktopSettings.CurrentSchemaVersion,
                Message: $"Settings backup under {workspace.CacheRoot}; api_key=diagnostic-secret"),
            sessionState: new { rootConfigured = true },
            sessionRootPath: workspace.CacheRoot,
            cancellationToken: CancellationToken.None);

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
        Assert.Equal(
            DesktopHostApplication.ProtocolVersion,
            diagnosticsDocument.RootElement
                .GetProperty("application")
                .GetProperty("protocolVersion")
                .GetInt32());
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
        string parameterJson,
        CancellationToken cancellationToken = default)
    {
        using var parameters = JsonDocument.Parse(parameterJson);
        var result = await application.DispatchAsync(
            Guid.NewGuid().ToString("N"),
            method,
            parameters.RootElement,
            cancellationToken);
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
            SettingsPath = Path.Combine(Root, "settings.json");
            Directory.CreateDirectory(CacheRoot);
            Directory.CreateDirectory(TranscodeRoot);

            _previousSettingsPath = Environment.GetEnvironmentVariable(
                "BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH");
            _previousTranscodeRoot = Environment.GetEnvironmentVariable(
                "BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT");
            Environment.SetEnvironmentVariable(
                "BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH",
                SettingsPath);
            Environment.SetEnvironmentVariable(
                "BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT",
                TranscodeRoot);
        }

        public string Root { get; }

        public string CacheRoot { get; }

        public string TranscodeRoot { get; }

        public string SettingsPath { get; }

        public DesktopHostApplication CreateApplication() => new();

        public void CreateCache(long avid, string title, int segmentCount = 1)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (var pageIndex = 1; pageIndex <= segmentCount; pageIndex++)
            {
                var segmentDirectory = Path.Combine(
                    CacheRoot,
                    avid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    $"c_{pageIndex}");
                var mediaDirectory = Path.Combine(segmentDirectory, "lua.flv.bb2api.80");
                Directory.CreateDirectory(mediaDirectory);
                File.WriteAllText(
                    Path.Combine(segmentDirectory, "entry.json"),
                    JsonSerializer.Serialize(new
                    {
                        is_completed = true,
                        total_bytes = 1000,
                        downloaded_bytes = 1000,
                        title,
                        type_tag = "type",
                        cover = "cover",
                        prefered_video_quality = 80,
                        guessed_total_bytes = 1000,
                        total_time_milli = 60_000,
                        danmaku_count = 0,
                        time_update_stamp = timestamp + pageIndex,
                        time_create_stamp = timestamp,
                        avid,
                        bvid = $"BV{avid}",
                        owner_name = "Test owner",
                        spid = 0,
                        seasion_id = 0,
                        page_data = new
                        {
                            cid = avid * 100 + pageIndex,
                            page = pageIndex,
                            from = "local",
                            part = $"Part {pageIndex}",
                            vid = "vid",
                            has_alias = false,
                            tid = 0
                        }
                    }));
                File.WriteAllText(Path.Combine(mediaDirectory, "0.mp4"), $"media-{avid}-{pageIndex}");
            }
        }

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
