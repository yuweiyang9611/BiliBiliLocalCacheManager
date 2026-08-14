using System.Globalization;
using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Infrastructure.Management;
using BiliBiliLocalCacheManager.Core.Infrastructure.Scanning;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class FileSystemCacheTrashServiceTests
{
    [Fact]
    public void MoveAndRestore_ShouldRoundTripWithoutAppearingInIndex()
    {
        var root = CreateTempRoot();
        try
        {
            var source = Path.Combine(root, "100");
            var segment = Path.Combine(source, "c_1");
            Directory.CreateDirectory(segment);
            File.WriteAllText(Path.Combine(segment, "entry.json"), BuildEntryJson());

            var service = new FileSystemCacheTrashService();
            var moved = service.MoveToTrash(root, 100);

            Assert.True(moved.Succeeded);
            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(moved.TrashPath));
            Assert.Empty(new FileSystemCacheIndexBuilder().BuildIndex(root).VideoCaches);

            var restored = service.Restore(root, 100, moved.TrashPath!);

            Assert.True(restored.Succeeded);
            Assert.True(Directory.Exists(source));
            Assert.Single(new FileSystemCacheIndexBuilder().BuildIndex(root).VideoCaches);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void MoveToTrash_ShouldCreateVersionedMetadataMatchingDirectoryIdentity()
    {
        var root = CreateTempRoot();
        try
        {
            CreateMinimalCache(root, 100);
            var service = new FileSystemCacheTrashService();

            var moved = service.MoveToTrash(root, 100);

            Assert.True(moved.Succeeded);
            var parts = Path.GetFileName(moved.TrashPath!).Split('_');
            Assert.Equal(4, parts.Length);
            Assert.Equal("v1", parts[0]);
            Assert.Equal("100", parts[1]);

            var metadataPath = Path.Combine(moved.TrashPath!, ".trash-info.json");
            Assert.True(File.Exists(metadataPath));
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var metadata = document.RootElement;
            Assert.Equal(1, metadata.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(100, metadata.GetProperty("Avid").GetInt64());
            Assert.Equal("100", metadata.GetProperty("OriginalRelativePath").GetString());
            Assert.Equal(
                parts[2],
                metadata.GetProperty("DeletedAtUtc")
                    .GetDateTimeOffset()
                    .UtcDateTime
                    .ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture));
            Assert.Equal(
                parts[3],
                metadata.GetProperty("EntryId").GetGuid().ToString("N"));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void MoveToTrash_WhenMetadataCommitFails_ShouldLeaveOriginalDirectoryInPlace()
    {
        var root = CreateTempRoot();
        try
        {
            var source = CreateMinimalCache(root, 100);
            var sentinel = Path.Combine(source, "sentinel.bin");
            File.WriteAllText(sentinel, "keep");
            var service = new FileSystemCacheTrashService
            {
                BeforeTrashMetadataWriteForTesting = _ =>
                    throw new IOException("simulated metadata failure")
            };

            var moved = service.MoveToTrash(root, 100);

            Assert.True(moved.Found);
            Assert.False(moved.Succeeded);
            Assert.Contains("simulated metadata failure", moved.ErrorMessage);
            Assert.True(Directory.Exists(source));
            Assert.True(File.Exists(sentinel));
            Assert.False(Directory.Exists(moved.TrashPath));
            Assert.Empty(Directory.EnumerateFileSystemEntries(service.GetTrashDirectory(root)));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void MoveToTrash_ShouldNotOverwriteReservedMetadataFile()
    {
        var root = CreateTempRoot();
        try
        {
            var source = CreateMinimalCache(root, 100);
            var reservedPath = Path.Combine(source, ".trash-info.json");
            File.WriteAllText(reservedPath, "reserved");
            var service = new FileSystemCacheTrashService();

            var moved = service.MoveToTrash(root, 100);

            Assert.False(moved.Succeeded);
            Assert.True(Directory.Exists(source));
            Assert.Equal("reserved", File.ReadAllText(reservedPath));
            Assert.False(Directory.Exists(moved.TrashPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void MoveAndRestore_ShouldRejectNonPositiveAvid(long avid)
    {
        var root = CreateTempRoot();
        try
        {
            var source = Path.Combine(root, avid.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(source);
            var service = new FileSystemCacheTrashService();

            Assert.Throws<ArgumentOutOfRangeException>(() => service.MoveToTrash(root, avid));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                service.Restore(root, avid, Path.Combine(root, "unused-trash-entry")));
            Assert.True(Directory.Exists(source));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void MoveToTrash_ShouldResumeValidPrecommittedMetadata()
    {
        var root = CreateTempRoot();
        try
        {
            var source = CreateMinimalCache(root, 100);
            var deletedAt = DateTimeOffset.UtcNow;
            var entryId = Guid.NewGuid();
            File.WriteAllText(
                Path.Combine(source, ".trash-info.json"),
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1,
                    Avid = 100L,
                    OriginalRelativePath = "100",
                    DeletedAtUtc = deletedAt,
                    EntryId = entryId
                }));
            var service = new FileSystemCacheTrashService();

            var moved = service.MoveToTrash(root, 100);

            Assert.True(moved.Succeeded);
            Assert.Equal(
                $"v1_100_{deletedAt.UtcDateTime:yyyyMMddHHmmssfff}_{entryId:N}",
                Path.GetFileName(moved.TrashPath));
            Assert.True(File.Exists(Path.Combine(moved.TrashPath!, ".trash-info.json")));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_WhenMetadataCleanupFails_ShouldWarnAndRemainMovable()
    {
        var root = CreateTempRoot();
        try
        {
            CreateMinimalCache(root, 100);
            var service = new FileSystemCacheTrashService();
            var moved = service.MoveToTrash(root, 100);
            Assert.True(moved.Succeeded);
            service.BeforeRestoredMetadataDeleteForTesting = _ =>
                throw new IOException("simulated cleanup failure");

            var restored = service.Restore(root, 100, moved.TrashPath!);

            Assert.True(restored.Succeeded);
            Assert.Contains("simulated cleanup failure", restored.ErrorMessage);
            Assert.True(File.Exists(Path.Combine(root, "100", ".trash-info.json")));

            service.BeforeRestoredMetadataDeleteForTesting = null;
            var movedAgain = service.MoveToTrash(root, 100);
            Assert.True(movedAgain.Succeeded);
            Assert.True(File.Exists(Path.Combine(movedAgain.TrashPath!, ".trash-info.json")));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_ShouldRejectManagedLookingDirectoryWithoutMetadata()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashPath = Path.Combine(
                service.GetTrashDirectory(root),
                $"100_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(trashPath);

            Assert.ThrowsAny<IOException>(() => service.Restore(root, 100, trashPath));
            Assert.True(Directory.Exists(trashPath));
            Assert.False(Directory.Exists(Path.Combine(root, "100")));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_ShouldRejectCorruptMetadata()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashPath = Path.Combine(
                service.GetTrashDirectory(root),
                $"100_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(trashPath);
            File.WriteAllText(Path.Combine(trashPath, ".trash-info.json"), "not-json");

            Assert.Throws<InvalidDataException>(() => service.Restore(root, 100, trashPath));
            Assert.True(Directory.Exists(trashPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_ShouldRejectVersionedMetadataIdentityMismatch()
    {
        var root = CreateTempRoot();
        try
        {
            CreateMinimalCache(root, 100);
            var service = new FileSystemCacheTrashService();
            var moved = service.MoveToTrash(root, 100);
            Assert.True(moved.Succeeded);
            var metadataPath = Path.Combine(moved.TrashPath!, ".trash-info.json");
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var metadata = document.RootElement;
            File.WriteAllText(
                metadataPath,
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = metadata.GetProperty("SchemaVersion").GetInt32(),
                    Avid = metadata.GetProperty("Avid").GetInt64(),
                    OriginalRelativePath = metadata.GetProperty("OriginalRelativePath").GetString(),
                    DeletedAtUtc = metadata.GetProperty("DeletedAtUtc").GetDateTimeOffset(),
                    EntryId = Guid.NewGuid()
                }));

            Assert.Throws<InvalidDataException>(() =>
                service.Restore(root, 100, moved.TrashPath!));
            Assert.True(Directory.Exists(moved.TrashPath));
            Assert.False(Directory.Exists(Path.Combine(root, "100")));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_ShouldAcceptValidLegacyMetadataWithoutSchemaVersion()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var deletedAt = DateTimeOffset.UtcNow;
            var trashPath = Path.Combine(
                service.GetTrashDirectory(root),
                $"100_{deletedAt.UtcDateTime:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(trashPath);
            File.WriteAllText(
                Path.Combine(trashPath, ".trash-info.json"),
                JsonSerializer.Serialize(new
                {
                    Avid = 100L,
                    OriginalPath = Path.Combine(root, "100"),
                    DeletedAt = deletedAt
                }));

            var restored = service.Restore(root, 100, trashPath);

            Assert.True(restored.Succeeded);
            Assert.True(Directory.Exists(Path.Combine(root, "100")));
            Assert.False(File.Exists(Path.Combine(root, "100", ".trash-info.json")));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_ShouldSupportRelocatedRootForValidLegacyEntry()
    {
        var parent = CreateTempRoot();
        try
        {
            var originalRoot = Path.Combine(parent, "legacy-original-root");
            Directory.CreateDirectory(originalRoot);
            var service = new FileSystemCacheTrashService();
            var deletedAt = DateTimeOffset.UtcNow;
            var entryName = $"100_{deletedAt.UtcDateTime:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
            var trashPath = Path.Combine(service.GetTrashDirectory(originalRoot), entryName);
            Directory.CreateDirectory(trashPath);
            File.WriteAllText(Path.Combine(trashPath, "cache.bin"), "cache");
            File.WriteAllText(
                Path.Combine(trashPath, ".trash-info.json"),
                JsonSerializer.Serialize(new
                {
                    Avid = 100L,
                    OriginalPath = Path.Combine(originalRoot, "100"),
                    DeletedAt = deletedAt
                }));

            var relocatedRoot = Path.Combine(parent, "legacy-relocated-root");
            Directory.Move(originalRoot, relocatedRoot);
            var relocatedTrashPath = Path.Combine(
                service.GetTrashDirectory(relocatedRoot),
                entryName);

            var restored = service.Restore(relocatedRoot, 100, relocatedTrashPath);

            Assert.True(restored.Succeeded);
            Assert.True(Directory.Exists(Path.Combine(relocatedRoot, "100")));
        }
        finally
        {
            SafeDeleteDirectory(parent);
        }
    }

    [Fact]
    public void Restore_ShouldSupportRelocatedRootForVersionedEntry()
    {
        var parent = CreateTempRoot();
        try
        {
            var originalRoot = Path.Combine(parent, "original-root");
            Directory.CreateDirectory(originalRoot);
            CreateMinimalCache(originalRoot, 100);
            var service = new FileSystemCacheTrashService();
            var moved = service.MoveToTrash(originalRoot, 100);
            Assert.True(moved.Succeeded);

            var relocatedRoot = Path.Combine(parent, "relocated-root");
            Directory.Move(originalRoot, relocatedRoot);
            var relocatedTrashPath = Path.Combine(
                service.GetTrashDirectory(relocatedRoot),
                Path.GetFileName(moved.TrashPath!));

            var restored = service.Restore(relocatedRoot, 100, relocatedTrashPath);

            Assert.True(restored.Succeeded);
            Assert.True(Directory.Exists(Path.Combine(relocatedRoot, "100")));
        }
        finally
        {
            SafeDeleteDirectory(parent);
        }
    }

    [Fact]
    public void Restore_ShouldRejectPathOutsideManagedTrash()
    {
        var root = CreateTempRoot();
        var outside = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            Assert.Throws<InvalidOperationException>(() => service.Restore(root, 100, outside));
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(outside);
        }
    }

    [Fact]
    public void MoveToTrash_ShouldRejectReservedPurgeMarkerFile()
    {
        var root = CreateTempRoot();
        try
        {
            var source = CreateMinimalCache(root, 100);
            var reservedPath = Path.Combine(source, ".purge-in-progress.json");
            File.WriteAllText(reservedPath, "user content");
            var service = new FileSystemCacheTrashService();

            var moved = service.MoveToTrash(root, 100);

            Assert.True(moved.Found);
            Assert.False(moved.Succeeded);
            Assert.Null(moved.TrashPath);
            Assert.True(Directory.Exists(source));
            Assert.Equal("user content", File.ReadAllText(reservedPath));
            Assert.Empty(Directory.EnumerateFileSystemEntries(service.GetTrashDirectory(root)));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void MoveToTrash_ShouldRejectReservedPurgeMarkerDirectory()
    {
        var root = CreateTempRoot();
        try
        {
            var source = CreateMinimalCache(root, 100);
            var reservedPath = Path.Combine(source, ".purge-in-progress.json");
            Directory.CreateDirectory(reservedPath);
            var sentinel = Path.Combine(reservedPath, "keep.txt");
            File.WriteAllText(sentinel, "keep");
            var service = new FileSystemCacheTrashService();

            var moved = service.MoveToTrash(root, 100);

            Assert.True(moved.Found);
            Assert.False(moved.Succeeded);
            Assert.Null(moved.TrashPath);
            Assert.True(Directory.Exists(source));
            Assert.True(Directory.Exists(reservedPath));
            Assert.Equal("keep", File.ReadAllText(sentinel));
            Assert.Empty(Directory.EnumerateFileSystemEntries(service.GetTrashDirectory(root)));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_ShouldBlockTrashEntryReplacementAfterHandleValidation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempRoot();
        try
        {
            CreateMinimalCache(root, 100);
            var service = new FileSystemCacheTrashService();
            var moved = service.MoveToTrash(root, 100);
            Assert.True(moved.Succeeded);
            var displacedPath = $"{moved.TrashPath}.displaced";
            var hookCalled = false;
            Exception? replacementError = null;
            service.BeforeRestoreRenameForTesting = path =>
            {
                hookCalled = true;
                try
                {
                    Directory.Move(path, displacedPath);
                }
                catch (Exception ex)
                {
                    replacementError = ex;
                }
            };

            var restored = service.Restore(root, 100, moved.TrashPath!);

            Assert.True(hookCalled);
            Assert.NotNull(replacementError);
            Assert.True(restored.Succeeded, restored.ErrorMessage);
            Assert.True(Directory.Exists(Path.Combine(root, "100")));
            Assert.True(File.Exists(Path.Combine(root, "100", "cache.bin")));
            Assert.False(File.Exists(Path.Combine(root, "100", ".trash-info.json")));
            Assert.False(Directory.Exists(displacedPath));
            Assert.False(Directory.Exists(moved.TrashPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_ShouldBlockRestoredDirectoryReplacementDuringMetadataCleanup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempRoot();
        try
        {
            CreateMinimalCache(root, 100);
            var service = new FileSystemCacheTrashService();
            var moved = service.MoveToTrash(root, 100);
            Assert.True(moved.Succeeded);
            var restoredPath = Path.Combine(root, "100");
            var displacedPath = $"{restoredPath}.displaced";
            var hookCalled = false;
            Exception? replacementError = null;
            service.BeforeRestoredMetadataDeleteForTesting = path =>
            {
                hookCalled = true;
                try
                {
                    Directory.Move(path, displacedPath);
                }
                catch (Exception ex)
                {
                    replacementError = ex;
                }
            };

            var restored = service.Restore(root, 100, moved.TrashPath!);

            Assert.True(hookCalled);
            Assert.NotNull(replacementError);
            Assert.True(restored.Succeeded, restored.ErrorMessage);
            Assert.Null(restored.ErrorMessage);
            Assert.True(Directory.Exists(restoredPath));
            Assert.True(File.Exists(Path.Combine(restoredPath, "cache.bin")));
            Assert.False(File.Exists(Path.Combine(restoredPath, ".trash-info.json")));
            Assert.False(Directory.Exists(displacedPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void MoveToTrash_ShouldBlockSourceReplacementBeforeMetadataWrite()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempRoot();
        try
        {
            var source = CreateMinimalCache(root, 100);
            var displacedPath = $"{source}.displaced";
            var hookCalled = false;
            Exception? replacementError = null;
            var service = new FileSystemCacheTrashService
            {
                BeforeTrashMetadataWriteForTesting = path =>
                {
                    hookCalled = true;
                    try
                    {
                        Directory.Move(path, displacedPath);
                    }
                    catch (Exception ex)
                    {
                        replacementError = ex;
                    }
                }
            };

            var moved = service.MoveToTrash(root, 100);

            Assert.True(hookCalled);
            Assert.NotNull(replacementError);
            Assert.True(moved.Succeeded, moved.ErrorMessage);
            Assert.False(Directory.Exists(source));
            Assert.False(Directory.Exists(displacedPath));
            Assert.True(File.Exists(Path.Combine(moved.TrashPath!, "cache.bin")));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void MoveToTrash_ShouldBlockSourceAndTrashRootReplacementBeforeRename()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempRoot();
        try
        {
            var source = CreateMinimalCache(root, 100);
            var service = new FileSystemCacheTrashService();
            var trashRoot = service.GetTrashDirectory(root);
            var displacedSource = $"{source}.displaced";
            var displacedTrashRoot = $"{trashRoot}.displaced";
            var hookCalled = false;
            Exception? sourceReplacementError = null;
            Exception? trashReplacementError = null;
            service.BeforeMoveRenameForTesting = path =>
            {
                hookCalled = true;
                try
                {
                    Directory.Move(path, displacedSource);
                }
                catch (Exception ex)
                {
                    sourceReplacementError = ex;
                }

                try
                {
                    Directory.Move(trashRoot, displacedTrashRoot);
                }
                catch (Exception ex)
                {
                    trashReplacementError = ex;
                }
            };

            var moved = service.MoveToTrash(root, 100);

            Assert.True(hookCalled);
            Assert.NotNull(sourceReplacementError);
            Assert.NotNull(trashReplacementError);
            Assert.True(moved.Succeeded, moved.ErrorMessage);
            Assert.False(Directory.Exists(source));
            Assert.False(Directory.Exists(displacedSource));
            Assert.False(Directory.Exists(displacedTrashRoot));
            Assert.True(File.Exists(Path.Combine(moved.TrashPath!, "cache.bin")));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_WhenMetadataBecomesDirectory_ShouldSucceedWithWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempRoot();
        try
        {
            CreateMinimalCache(root, 100);
            var service = new FileSystemCacheTrashService();
            var moved = service.MoveToTrash(root, 100);
            Assert.True(moved.Succeeded);
            service.BeforeRestoreRenameForTesting = path =>
            {
                var metadataPath = Path.Combine(path, ".trash-info.json");
                File.Delete(metadataPath);
                Directory.CreateDirectory(metadataPath);
            };

            var restored = service.Restore(root, 100, moved.TrashPath!);

            Assert.True(restored.Succeeded, restored.ErrorMessage);
            Assert.NotNull(restored.ErrorMessage);
            Assert.Contains("元数据", restored.ErrorMessage);
            Assert.True(Directory.Exists(Path.Combine(root, "100")));
            Assert.True(File.Exists(Path.Combine(root, "100", "cache.bin")));
            Assert.True(Directory.Exists(Path.Combine(root, "100", ".trash-info.json")));
            Assert.False(Directory.Exists(moved.TrashPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    private static string BuildEntryJson() =>
        """
        {
          "is_completed": true,
          "total_bytes": 1,
          "downloaded_bytes": 1,
          "title": "Trash Test",
          "type_tag": "80",
          "cover": "cover",
          "prefered_video_quality": 80,
          "guessed_total_bytes": 1,
          "total_time_milli": 1,
          "danmaku_count": 0,
          "time_update_stamp": 0,
          "time_create_stamp": 0,
          "avid": 100,
          "spid": 0,
          "seasion_id": 0,
          "page_data": { "cid": 1, "page": 1, "from": "local", "part": "P1", "vid": "", "has_alias": false, "tid": 0 }
        }
        """;

    private static string CreateMinimalCache(string root, long avid)
    {
        var path = Path.Combine(root, avid.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "cache.bin"), "cache");
        return path;
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bili_trash_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Ignore test cleanup failures.
        }
    }
}
