using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Infrastructure.Management;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class FileSystemCacheTrashPurgeTests
{
    [Fact]
    public void Purge_ShouldReturnZeroForMissingAndEmptyTrash()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();

            AssertZeroResult(service.Purge(root));

            Directory.CreateDirectory(service.GetTrashDirectory(root));
            AssertZeroResult(service.Purge(root));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_ShouldPreserveManagedLookingFileAndCountItAsSkipped()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashRoot = service.GetTrashDirectory(root);
            Directory.CreateDirectory(trashRoot);
            var filePath = Path.Combine(trashRoot, CreateManagedEntryName(100));
            File.WriteAllText(filePath, "keep");

            var result = service.Purge(root);

            Assert.Equal(0, result.DeletedEntryCount);
            Assert.Equal(0, result.FreedBytes);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.Equal(1, result.SkippedEntryCount);
            Assert.Null(result.FirstErrorMessage);
            Assert.True(File.Exists(filePath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_ShouldPreserveManagedLookingDirectoryWithoutMetadataAndCountSkipped()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashPath = Path.Combine(
                service.GetTrashDirectory(root),
                CreateManagedEntryName(100));
            Directory.CreateDirectory(trashPath);
            File.WriteAllText(Path.Combine(trashPath, "keep.bin"), "keep");

            var result = service.Purge(root);

            Assert.Equal(0, result.DeletedEntryCount);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.Equal(1, result.SkippedEntryCount);
            Assert.True(Directory.Exists(trashPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_WithExplicitLegacyConfirmation_ShouldAdoptAndDeleteNameOnlyDirectory()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashPath = Path.Combine(
                service.GetTrashDirectory(root),
                CreateManagedEntryName(100));
            Directory.CreateDirectory(trashPath);
            File.WriteAllText(Path.Combine(trashPath, "payload.bin"), "payload");

            var trashRoot = service.GetTrashDirectory(root);
            var beforePurgeBytes = MeasureFileBytes(trashRoot);
            var result = service.Purge(root, includeUntrustedLegacyEntries: true);

            Assert.Equal(1, result.DeletedEntryCount);
            Assert.Equal(beforePurgeBytes, result.FreedBytes);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.Equal(0, result.SkippedEntryCount);
            Assert.False(Directory.Exists(trashPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_ShouldDeleteValidLegacyMetadataEntry()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashPath = CreateLegacyTrashEntry(root, service, 100, "payload");

            var result = service.Purge(root);

            Assert.Equal(1, result.DeletedEntryCount);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.Equal(0, result.SkippedEntryCount);
            Assert.False(Directory.Exists(trashPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_ShouldManageValidLegacyEntryAfterRootRelocation()
    {
        var root = CreateTempRoot();
        var relocatedRoot = $"{root}-relocated";
        try
        {
            var service = new FileSystemCacheTrashService();
            var originalTrashPath = CreateLegacyTrashEntry(root, service, 100, "payload");
            var entryName = Path.GetFileName(originalTrashPath);
            Directory.Move(root, relocatedRoot);
            var relocatedTrashPath = Path.Combine(
                service.GetTrashDirectory(relocatedRoot),
                entryName);

            var statistics = service.GetStatistics(relocatedRoot);
            var result = service.Purge(relocatedRoot);

            Assert.Equal(1, statistics.ManagedEntryCount);
            Assert.Equal(0, statistics.FailedEntryCount);
            Assert.Equal(0, statistics.PendingPurgeEntryCount);
            Assert.Equal(1, result.DeletedEntryCount);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.Equal(0, result.PartiallyDeletedEntryCount);
            Assert.Equal(0, result.PendingPurgeEntryCount);
            Assert.False(Directory.Exists(relocatedTrashPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(relocatedRoot);
        }
    }

    [Fact]
    public void Purge_ShouldDeleteReadOnlyPayloadByValidatedHandle()
    {
        var root = CreateTempRoot();
        try
        {
            CreateCache(root, 100, 32);
            var payloadPath = Path.Combine(root, "100", "c_1", "video.bin");
            File.SetAttributes(
                payloadPath,
                File.GetAttributes(payloadPath) | FileAttributes.ReadOnly);
            var service = new FileSystemCacheTrashService();
            var moved = service.MoveToTrash(root, 100);
            Assert.True(moved.Succeeded);

            var result = service.Purge(root);

            Assert.Equal(1, result.DeletedEntryCount);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.True(result.FreedBytes >= 32);
            Assert.False(Directory.Exists(moved.TrashPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_ShouldPreserveCorruptMetadataAndReportFailure()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashPath = Path.Combine(
                service.GetTrashDirectory(root),
                CreateManagedEntryName(100));
            Directory.CreateDirectory(trashPath);
            File.WriteAllText(Path.Combine(trashPath, ".trash-info.json"), "not-json");

            var result = service.Purge(root);

            Assert.Equal(0, result.DeletedEntryCount);
            Assert.Equal(1, result.FailedEntryCount);
            Assert.Equal(0, result.SkippedEntryCount);
            Assert.NotNull(result.FirstErrorMessage);
            Assert.True(Directory.Exists(trashPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_ShouldPreserveFutureSchemaMetadata()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var deletedAt = DateTimeOffset.UtcNow;
            var entryId = Guid.NewGuid();
            var trashPath = Path.Combine(
                service.GetTrashDirectory(root),
                $"v2_100_{deletedAt.UtcDateTime:yyyyMMddHHmmssfff}_{entryId:N}");
            Directory.CreateDirectory(trashPath);
            File.WriteAllText(
                Path.Combine(trashPath, ".trash-info.json"),
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = 2,
                    Avid = 100L,
                    OriginalRelativePath = "100",
                    DeletedAtUtc = deletedAt,
                    EntryId = entryId
                }));

            var result = service.Purge(root, includeUntrustedLegacyEntries: true);

            Assert.Equal(0, result.DeletedEntryCount);
            Assert.Equal(1, result.FailedEntryCount);
            Assert.True(Directory.Exists(trashPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_ShouldRejectReparsePointMetadataFile()
    {
        var root = CreateTempRoot();
        var outside = CreateTempRoot();
        try
        {
            CreateCache(root, 100, 32);
            var service = new FileSystemCacheTrashService();
            var moved = service.MoveToTrash(root, 100);
            Assert.True(moved.Succeeded);
            var metadataPath = Path.Combine(moved.TrashPath!, ".trash-info.json");
            var outsideMetadata = Path.Combine(outside, "outside-metadata.json");
            File.Copy(metadataPath, outsideMetadata);
            File.Delete(metadataPath);
            if (!TryCreateFileLink(metadataPath, outsideMetadata))
            {
                return;
            }

            var result = service.Purge(root);

            Assert.Equal(0, result.DeletedEntryCount);
            Assert.Equal(1, result.FailedEntryCount);
            Assert.True(Directory.Exists(moved.TrashPath));
            Assert.True(File.Exists(outsideMetadata));
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(outside);
        }
    }

    [Fact]
    public void Purge_WhenFinalDirectoryDeleteFails_ShouldRestoreMetadataForRetry()
    {
        var root = CreateTempRoot();
        try
        {
            CreateCache(root, 100, 32);
            var service = new FileSystemCacheTrashService();
            var moved = service.MoveToTrash(root, 100);
            Assert.True(moved.Succeeded);
            service.BeforeTrashEntryFinalDeleteForTesting = path =>
                File.WriteAllText(Path.Combine(path, "late-file.bin"), "late");

            var trashRoot = service.GetTrashDirectory(root);
            var beforePurgeBytes = MeasureFileBytes(trashRoot);
            var firstResult = service.Purge(root);
            var afterFailedPurgeBytes = MeasureFileBytes(trashRoot);

            Assert.Equal(0, firstResult.DeletedEntryCount);
            Assert.Equal(
                Math.Max(0, beforePurgeBytes - afterFailedPurgeBytes),
                firstResult.FreedBytes);
            Assert.Equal(1, firstResult.FailedEntryCount);
            Assert.Equal(1, firstResult.PartiallyDeletedEntryCount);
            Assert.Equal(1, firstResult.PendingPurgeEntryCount);
            Assert.True(Directory.Exists(moved.TrashPath));
            Assert.True(File.Exists(Path.Combine(moved.TrashPath!, ".trash-info.json")));
            Assert.True(File.Exists(Path.Combine(moved.TrashPath!, ".purge-in-progress.json")));
            Assert.True(File.Exists(Path.Combine(moved.TrashPath!, "late-file.bin")));
            var pendingStatistics = service.GetStatistics(root);
            Assert.Equal(1, pendingStatistics.PendingPurgeEntryCount);
            Assert.Throws<InvalidOperationException>(() =>
                service.Restore(root, 100, moved.TrashPath!));

            service.BeforeTrashEntryFinalDeleteForTesting = null;
            var retryResult = service.Purge(root);

            Assert.Equal(1, retryResult.DeletedEntryCount);
            Assert.Equal(0, retryResult.FailedEntryCount);
            Assert.Equal(0, retryResult.PartiallyDeletedEntryCount);
            Assert.Equal(0, retryResult.PendingPurgeEntryCount);
            Assert.False(Directory.Exists(moved.TrashPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_ShouldContinueAfterLinkedEntryFails()
    {
        var root = CreateTempRoot();
        var outside = CreateTempRoot();
        try
        {
            CreateCache(root, 100, 32);
            CreateCache(root, 200, 64);
            var service = new FileSystemCacheTrashService();
            var linkedEntry = service.MoveToTrash(root, 100);
            var normalEntry = service.MoveToTrash(root, 200);
            var outsideSentinel = Path.Combine(outside, "outside.txt");
            File.WriteAllText(outsideSentinel, "keep");
            if (!TryCreateDirectoryLink(Path.Combine(linkedEntry.TrashPath!, "outside-link"), outside))
            {
                return;
            }

            var result = service.Purge(root);

            Assert.Equal(1, result.DeletedEntryCount);
            Assert.True(result.FreedBytes >= 64);
            Assert.Equal(1, result.FailedEntryCount);
            Assert.Equal(0, result.SkippedEntryCount);
            Assert.NotNull(result.FirstErrorMessage);
            Assert.True(Directory.Exists(linkedEntry.TrashPath));
            Assert.True(File.Exists(Path.Combine(linkedEntry.TrashPath!, ".trash-info.json")));
            Assert.False(Directory.Exists(normalEntry.TrashPath));
            Assert.True(File.Exists(outsideSentinel));
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(outside);
        }
    }

    [Fact]
    public void Purge_ShouldDeleteManagedEntriesAndPreserveOriginalCache()
    {
        var root = CreateTempRoot();
        try
        {
            CreateCache(root, 100, 32);
            CreateCache(root, 200, 64);
            var service = new FileSystemCacheTrashService();
            var first = service.MoveToTrash(root, 100);
            var second = service.MoveToTrash(root, 200);
            CreateCache(root, 100, 16);

            var trashRoot = service.GetTrashDirectory(root);
            var beforePurgeBytes = MeasureFileBytes(trashRoot);
            var result = service.Purge(root);

            Assert.True(result.DeletedEntryCount == 2, result.FirstErrorMessage);
            Assert.Equal(beforePurgeBytes, result.FreedBytes);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.Equal(0, result.SkippedEntryCount);
            Assert.False(Directory.Exists(first.TrashPath));
            Assert.False(Directory.Exists(second.TrashPath));
            Assert.True(Directory.Exists(Path.Combine(root, "100")));
            Assert.True(Directory.Exists(service.GetTrashDirectory(root)));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_ShouldPreserveUnmanagedTrashContents()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashRoot = service.GetTrashDirectory(root);
            Directory.CreateDirectory(trashRoot);
            var filePath = Path.Combine(trashRoot, "notes.txt");
            var directoryPath = Path.Combine(trashRoot, "manual-folder");
            File.WriteAllText(filePath, "keep");
            Directory.CreateDirectory(directoryPath);

            var result = service.Purge(root);

            Assert.Equal(0, result.DeletedEntryCount);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.Equal(2, result.SkippedEntryCount);
            Assert.True(File.Exists(filePath));
            Assert.True(Directory.Exists(directoryPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_ShouldNotFollowLinkInsideManagedEntry()
    {
        var root = CreateTempRoot();
        var outside = CreateTempRoot();
        try
        {
            CreateCache(root, 100, 32);
            var service = new FileSystemCacheTrashService();
            var moved = service.MoveToTrash(root, 100);
            var outsideSentinel = Path.Combine(outside, "outside.txt");
            File.WriteAllText(outsideSentinel, "keep");
            var linkPath = Path.Combine(moved.TrashPath!, "outside-link");
            if (!TryCreateDirectoryLink(linkPath, outside))
            {
                return;
            }

            var result = service.Purge(root);

            Assert.Equal(0, result.DeletedEntryCount);
            Assert.Equal(1, result.FailedEntryCount);
            Assert.True(Directory.Exists(moved.TrashPath));
            Assert.True(File.Exists(Path.Combine(moved.TrashPath!, ".trash-info.json")));
            Assert.True(File.Exists(outsideSentinel));
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(outside);
        }
    }

    [Fact]
    public void Purge_ShouldBlockDirectoryReplacementAfterHandleValidation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempRoot();
        var outside = CreateTempRoot();
        try
        {
            CreateCache(root, 100, 32);
            var service = new FileSystemCacheTrashService();
            var moved = service.MoveToTrash(root, 100);
            Assert.True(moved.Succeeded);
            var outsideSentinel = Path.Combine(outside, "outside.txt");
            File.WriteAllText(outsideSentinel, "keep");
            Exception? replacementError = null;
            var hookCalled = false;
            service.BeforeTrashDirectoryEnumerationForTesting = path =>
            {
                if (!string.Equals(
                        Path.GetFileName(path),
                        "c_1",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                hookCalled = true;
                try
                {
                    var displacedPath = $"{path}.displaced";
                    Directory.Move(path, displacedPath);
                    Directory.CreateSymbolicLink(path, outside);
                }
                catch (Exception ex)
                {
                    replacementError = ex;
                }
            };

            var result = service.Purge(root);

            Assert.True(hookCalled);
            Assert.NotNull(replacementError);
            Assert.Equal(1, result.DeletedEntryCount);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.False(Directory.Exists(moved.TrashPath));
            Assert.True(File.Exists(outsideSentinel));
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(outside);
        }
    }

    [Fact]
    public void Purge_ShouldRejectTrashRootLink()
    {
        var root = CreateTempRoot();
        var outside = CreateTempRoot();
        try
        {
            var sentinel = Path.Combine(outside, "outside.txt");
            File.WriteAllText(sentinel, "keep");
            var trashRoot = CacheStorageLayout.GetTrashDirectory(root);
            if (!TryCreateDirectoryLink(trashRoot, outside))
            {
                return;
            }

            var service = new FileSystemCacheTrashService();
            Assert.Throws<InvalidOperationException>(() => service.Purge(root));
            Assert.True(File.Exists(sentinel));
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(outside);
        }
    }

    [Fact]
    public void Purge_WhenMetadataIsMissingButValidMarkerExists_ShouldResume()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashPath = CreateInterruptedPendingEntry(root, service);
            var journalPath = GetSinglePurgeJournalPath(root, service);
            File.Delete(journalPath);
            File.Delete(Path.Combine(trashPath, ".trash-info.json"));
            var beforeRetryBytes = MeasureFileBytes(service.GetTrashDirectory(root));

            var statistics = service.GetStatistics(root);

            Assert.Equal(1, statistics.PendingPurgeEntryCount);
            Assert.Equal(0, statistics.FailedEntryCount);
            Assert.Equal(0, statistics.SkippedEntryCount);
            Assert.Throws<InvalidOperationException>(() =>
                service.Restore(root, 100, trashPath));

            var retryResult = service.Purge(root);

            Assert.Equal(1, retryResult.DeletedEntryCount);
            Assert.Equal(beforeRetryBytes, retryResult.FreedBytes);
            Assert.Equal(0, retryResult.FailedEntryCount);
            Assert.Equal(0, retryResult.PendingPurgeEntryCount);
            Assert.False(Directory.Exists(trashPath));
            Assert.False(File.Exists(journalPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_WithValidMarkerAndMismatchedMetadata_ShouldRemainPending()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashPath = CreateInterruptedPendingEntry(root, service);
            File.WriteAllText(Path.Combine(trashPath, ".trash-info.json"), "{}");

            var result = service.Purge(root);
            var statistics = service.GetStatistics(root);

            Assert.Equal(0, result.DeletedEntryCount);
            Assert.Equal(1, result.FailedEntryCount);
            Assert.Equal(0, result.PartiallyDeletedEntryCount);
            Assert.Equal(1, result.PendingPurgeEntryCount);
            Assert.Equal(1, statistics.FailedEntryCount);
            Assert.Equal(1, statistics.PendingPurgeEntryCount);
            Assert.True(Directory.Exists(trashPath));
            Assert.True(File.Exists(Path.Combine(trashPath, "late-file.bin")));
            Assert.True(File.Exists(GetSinglePurgeJournalPath(root, service)));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_ShouldRejectTamperedPurgeMarker()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashPath = CreateInterruptedPendingEntry(root, service);
            File.Delete(GetSinglePurgeJournalPath(root, service));
            var markerPath = Path.Combine(trashPath, ".purge-in-progress.json");
            using var markerDocument = JsonDocument.Parse(File.ReadAllText(markerPath));
            var marker = markerDocument.RootElement;
            File.WriteAllText(
                markerPath,
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = 99,
                    Avid = marker.GetProperty("Avid").GetInt64(),
                    EntryId = marker.GetProperty("EntryId").GetGuid(),
                    StartedAtUtc = marker.GetProperty("StartedAtUtc").GetDateTimeOffset(),
                    MetadataJson = marker.GetProperty("MetadataJson").GetString()
                }));

            var result = service.Purge(root);
            var statistics = service.GetStatistics(root);

            Assert.Equal(0, result.DeletedEntryCount);
            Assert.Equal(1, result.FailedEntryCount);
            Assert.Equal(0, result.PendingPurgeEntryCount);
            Assert.Equal(1, statistics.FailedEntryCount);
            Assert.Equal(0, statistics.PendingPurgeEntryCount);
            Assert.Throws<InvalidDataException>(() =>
                service.Restore(root, 100, trashPath));
            Assert.True(Directory.Exists(trashPath));
            Assert.True(File.Exists(Path.Combine(trashPath, "late-file.bin")));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_WhenOnlyValidJournalRemains_ShouldResume()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashPath = CreateInterruptedPendingEntry(root, service);
            var journalPath = GetSinglePurgeJournalPath(root, service);
            File.Delete(Path.Combine(trashPath, ".trash-info.json"));
            File.Delete(Path.Combine(trashPath, ".purge-in-progress.json"));
            var beforeRetryBytes = MeasureFileBytes(service.GetTrashDirectory(root));

            var statistics = service.GetStatistics(root);

            Assert.Equal(1, statistics.PendingPurgeEntryCount);
            Assert.Equal(0, statistics.FailedEntryCount);
            Assert.Equal(0, statistics.SkippedEntryCount);
            Assert.Throws<InvalidOperationException>(() =>
                service.Restore(root, 100, trashPath));

            var retryResult = service.Purge(root);

            Assert.Equal(1, retryResult.DeletedEntryCount);
            Assert.Equal(beforeRetryBytes, retryResult.FreedBytes);
            Assert.Equal(0, retryResult.FailedEntryCount);
            Assert.Equal(0, retryResult.SkippedEntryCount);
            Assert.Equal(0, retryResult.PendingPurgeEntryCount);
            Assert.False(Directory.Exists(trashPath));
            Assert.False(File.Exists(journalPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_ShouldRejectJournalWithMissingRequiredFields()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashPath = CreateInterruptedPendingEntry(root, service);
            var journalPath = GetSinglePurgeJournalPath(root, service);
            File.Delete(Path.Combine(trashPath, ".trash-info.json"));
            File.Delete(Path.Combine(trashPath, ".purge-in-progress.json"));
            File.WriteAllText(journalPath, "{}");

            var result = service.Purge(root);
            var statistics = service.GetStatistics(root);

            Assert.Equal(0, result.DeletedEntryCount);
            Assert.Equal(1, result.FailedEntryCount);
            Assert.Equal(0, result.PendingPurgeEntryCount);
            Assert.Equal(1, statistics.FailedEntryCount);
            Assert.Equal(0, statistics.PendingPurgeEntryCount);
            Assert.Throws<InvalidDataException>(() =>
                service.Restore(root, 100, trashPath));
            Assert.True(File.Exists(journalPath));
            Assert.True(File.Exists(Path.Combine(trashPath, "late-file.bin")));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_ShouldDeleteOrphanJournalAndReportItsNetBytes()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashPath = CreateInterruptedPendingEntry(root, service);
            var journalPath = GetSinglePurgeJournalPath(root, service);
            var journalBytes = new FileInfo(journalPath).Length;
            Directory.Delete(trashPath, recursive: true);

            var result = service.Purge(root);

            Assert.Equal(0, result.DeletedEntryCount);
            Assert.Equal(journalBytes, result.FreedBytes);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.Equal(0, result.SkippedEntryCount);
            Assert.Equal(0, result.PendingPurgeEntryCount);
            Assert.False(File.Exists(journalPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_WhenJournalBoundDirectoryWasReplaced_ShouldPreserveReplacement()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashPath = CreateInterruptedPendingEntry(root, service);
            var journalPath = GetSinglePurgeJournalPath(root, service);
            var displacedPath = Path.Combine(root, "displaced-trash-entry");
            Directory.Move(trashPath, displacedPath);
            Directory.CreateDirectory(trashPath);
            var sentinel = Path.Combine(trashPath, "replacement.bin");
            File.WriteAllText(sentinel, "replacement");

            var result = service.Purge(root);
            var statistics = service.GetStatistics(root);

            Assert.Equal(0, result.DeletedEntryCount);
            Assert.Equal(0, result.FreedBytes);
            Assert.Equal(1, result.FailedEntryCount);
            Assert.Equal(0, result.PartiallyDeletedEntryCount);
            Assert.Equal(1, result.PendingPurgeEntryCount);
            Assert.Equal(1, statistics.FailedEntryCount);
            Assert.Equal(1, statistics.PendingPurgeEntryCount);
            Assert.Throws<InvalidOperationException>(() =>
                service.Restore(root, 100, trashPath));
            Assert.Equal("replacement", File.ReadAllText(sentinel));
            Assert.True(Directory.Exists(displacedPath));
            Assert.True(File.Exists(journalPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_WhenEntryWasDeletedButJournalDeleteFails_ShouldFinalizeOnRetry()
    {
        var root = CreateTempRoot();
        try
        {
            CreateCache(root, 100, 32);
            var service = new FileSystemCacheTrashService();
            var moved = service.MoveToTrash(root, 100);
            Assert.True(moved.Succeeded);
            var trashRoot = service.GetTrashDirectory(root);
            var beforePurgeBytes = MeasureFileBytes(trashRoot);
            service.BeforePurgeJournalDeleteForTesting = _ =>
                throw new IOException("Simulated journal deletion failure.");

            var firstResult = service.Purge(root);
            var afterFailedPurgeBytes = MeasureFileBytes(trashRoot);
            var journalPath = GetSinglePurgeJournalPath(root, service);
            var journalBytes = new FileInfo(journalPath).Length;

            Assert.Equal(0, firstResult.DeletedEntryCount);
            Assert.Equal(
                Math.Max(0, beforePurgeBytes - afterFailedPurgeBytes),
                firstResult.FreedBytes);
            Assert.Equal(1, firstResult.FailedEntryCount);
            Assert.Equal(1, firstResult.PartiallyDeletedEntryCount);
            Assert.Equal(1, firstResult.PendingPurgeEntryCount);
            Assert.False(Directory.Exists(moved.TrashPath));

            service.BeforePurgeJournalDeleteForTesting = null;
            var retryResult = service.Purge(root);

            Assert.Equal(0, retryResult.DeletedEntryCount);
            Assert.Equal(journalBytes, retryResult.FreedBytes);
            Assert.Equal(0, retryResult.FailedEntryCount);
            Assert.Equal(0, retryResult.SkippedEntryCount);
            Assert.Equal(0, retryResult.PendingPurgeEntryCount);
            Assert.False(File.Exists(journalPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_ShouldFinalizeEmptyVersionedEntryWithoutStateFiles()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashPath = Path.Combine(
                service.GetTrashDirectory(root),
                $"v1_100_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(trashPath);

            var result = service.Purge(root);

            Assert.Equal(1, result.DeletedEntryCount);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.Equal(0, result.SkippedEntryCount);
            Assert.Equal(0, result.PendingPurgeEntryCount);
            Assert.False(Directory.Exists(trashPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Purge_ShouldPreserveNonEmptyVersionedEntryWithoutStateFiles()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashPath = Path.Combine(
                service.GetTrashDirectory(root),
                $"v1_100_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(trashPath);
            var sentinel = Path.Combine(trashPath, "keep.bin");
            File.WriteAllText(sentinel, "keep");

            var result = service.Purge(root);

            Assert.Equal(0, result.DeletedEntryCount);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.Equal(1, result.SkippedEntryCount);
            Assert.True(Directory.Exists(trashPath));
            Assert.Equal("keep", File.ReadAllText(sentinel));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    private static string CreateInterruptedPendingEntry(
        string root,
        FileSystemCacheTrashService service)
    {
        CreateCache(root, 100, 32);
        var moved = service.MoveToTrash(root, 100);
        Assert.True(moved.Succeeded);
        service.BeforeTrashEntryFinalDeleteForTesting = path =>
            File.WriteAllText(Path.Combine(path, "late-file.bin"), "late");

        var firstResult = service.Purge(root);

        service.BeforeTrashEntryFinalDeleteForTesting = null;
        Assert.Equal(0, firstResult.DeletedEntryCount);
        Assert.Equal(1, firstResult.FailedEntryCount);
        Assert.Equal(1, firstResult.PendingPurgeEntryCount);
        Assert.Equal(0, firstResult.SkippedEntryCount);
        Assert.True(File.Exists(Path.Combine(moved.TrashPath!, ".trash-info.json")));
        Assert.True(File.Exists(Path.Combine(moved.TrashPath!, ".purge-in-progress.json")));
        Assert.True(File.Exists(GetSinglePurgeJournalPath(root, service)));
        return moved.TrashPath!;
    }
    private static string GetSinglePurgeJournalPath(
        string root,
        FileSystemCacheTrashService service)
    {
        return Assert.Single(Directory.GetFiles(
            service.GetTrashDirectory(root),
            ".purge-journal-*.json",
            SearchOption.TopDirectoryOnly));
    }

    private static long MeasureFileBytes(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return 0;
        }

        var totalBytes = 0L;
        foreach (var filePath in Directory.EnumerateFiles(
                     directoryPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            totalBytes += new FileInfo(filePath).Length;
        }

        return totalBytes;
    }


    private static void AssertZeroResult(
        BiliBiliLocalCacheManager.Core.Application.Models.CacheTrashPurgeResult result)
    {
        Assert.Equal(0, result.DeletedEntryCount);
        Assert.Equal(0, result.FreedBytes);
        Assert.Equal(0, result.FailedEntryCount);
        Assert.Equal(0, result.SkippedEntryCount);
        Assert.Null(result.FirstErrorMessage);
        Assert.Equal(0, result.PartiallyDeletedEntryCount);
        Assert.Equal(0, result.PendingPurgeEntryCount);
    }

    private static string CreateManagedEntryName(long avid)
    {
        return $"{avid}_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
    }

    private static void CreateCache(string root, long avid, int byteCount)
    {
        var directory = Path.Combine(root, avid.ToString(), "c_1");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "video.bin"), new byte[byteCount]);
    }

    private static string CreateLegacyTrashEntry(
        string root,
        FileSystemCacheTrashService service,
        long avid,
        string contents)
    {
        var deletedAt = DateTimeOffset.UtcNow;
        var trashPath = Path.Combine(
            service.GetTrashDirectory(root),
            $"{avid}_{deletedAt.UtcDateTime:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(trashPath);
        File.WriteAllText(Path.Combine(trashPath, "payload.bin"), contents);
        File.WriteAllText(
            Path.Combine(trashPath, ".trash-info.json"),
            JsonSerializer.Serialize(new
            {
                Avid = avid,
                OriginalPath = Path.Combine(root, avid.ToString()),
                DeletedAt = deletedAt
            }));
        return trashPath;
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or
                IOException or
                PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or
                IOException or
                PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"bili_trash_purge_test_{Guid.NewGuid():N}");
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
            // Ignore test cleanup failures.
        }
    }
}
