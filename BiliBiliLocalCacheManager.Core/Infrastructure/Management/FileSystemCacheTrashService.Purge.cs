using System.Globalization;
using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Models;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

public sealed partial class FileSystemCacheTrashService
{
    public CacheTrashPurgeResult Purge(
        string rootDirectory,
        bool includeUntrustedLegacyEntries = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var root = Path.GetFullPath(rootDirectory);
        using var transaction = EnterMutationTransaction(root, CacheTrashMutationOperation.Purge);
        ValidateRoot(root);
        var trashRoot = CacheStorageLayout.GetTrashDirectory(root);
        EnsureDirectChild(root, trashRoot);
        if (!Directory.Exists(trashRoot))
        {
            return new CacheTrashPurgeResult(0, 0, 0, 0, null);
        }

        if (!OperatingSystem.IsWindows())
        {
            return new CacheTrashPurgeResult(
                0,
                0,
                1,
                0,
                "当前 Linux 版本已禁用永久清空回收站：尚未实现与 Windows 物理目录句柄同等级的防符号链接竞态保护。请先还原需要保留的条目，再使用发行版文件管理器自行处理回收站目录。");
        }

        using var trashRootLease = OpenPhysicalDirectoryLease(
            trashRoot,
            "The application trash directory",
            allowDelete: true);

        var deletedCount = 0;
        var failedCount = 0;
        var skippedCount = 0;
        var partiallyDeletedCount = 0;
        var pendingPurgeCount = 0;
        var freedBytes = 0L;
        string? firstError = null;
        var protectedJournalEntryIds = new HashSet<Guid>();
        var nonRestorableTrashPaths = new List<string>();

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     trashRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            if (TryParsePurgeJournalFileName(name, out _))
            {
                continue;
            }

            if (!TryParseManagedTrashEntryName(name, out var entryIdentity))
            {
                skippedCount++;
                continue;
            }

            var progress = new PurgeDeletionProgress();
            var originalPath = Path.Combine(
                root,
                entryIdentity.Avid.ToString(CultureInfo.InvariantCulture));
            try
            {
                EnsureDirectChild(trashRoot, path);
                var attributes = File.GetAttributes(path);
                if (!attributes.HasFlag(FileAttributes.Directory) ||
                    attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    skippedCount++;
                    continue;
                }
                protectedJournalEntryIds.Add(entryIdentity.EntryId);
                var journalState = ReadPurgeJournalState(trashRoot, path);
                if (journalState is not null)
                {
                    progress.MarkPurgeStarted();
                    DeleteManagedTrashEntry(
                        trashRoot,
                        path,
                        entryIdentity.Avid,
                        originalPath,
                        progress);
                    deletedCount++;
                    nonRestorableTrashPaths.Add(Path.GetFullPath(path));
                    freedBytes = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                        freedBytes,
                        progress.FreedBytes);
                    continue;
                }

                if (TryFinalizeEmptyVersionedTrashEntry(
                        trashRoot,
                        path,
                        entryIdentity,
                        progress))
                {
                    progress.SetInitialBytesIfUnset(0);
                    progress.CompleteDeletion();
                    deletedCount++;
                    nonRestorableTrashPaths.Add(Path.GetFullPath(path));
                    continue;
                }

                if (HasPurgeMarker(path))
                {
                    progress.MarkPurgeStarted();
                }

                EnsureTrashIdentity(
                    path,
                    entryIdentity.Avid,
                    originalPath,
                    allowPendingPurge: true);
                EnsurePhysicalDirectory(path, "The managed trash entry");
                EnsureTrashIdentity(
                    path,
                    entryIdentity.Avid,
                    originalPath,
                    allowPendingPurge: true);
                DeleteManagedTrashEntry(
                    trashRoot,
                    path,
                    entryIdentity.Avid,
                    originalPath,
                    progress);
                deletedCount++;
                nonRestorableTrashPaths.Add(Path.GetFullPath(path));
                freedBytes = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                    freedBytes,
                    progress.FreedBytes);
            }
            catch (UntrustedTrashEntryException) when (
                !includeUntrustedLegacyEntries || entryIdentity.SchemaVersion != 0)
            {
                skippedCount++;
            }
            catch (UntrustedTrashEntryException)
            {
                try
                {
                    using (var legacyEntryLease = OpenPhysicalDirectoryLease(
                               path,
                               "The untrusted legacy trash entry",
                               allowDelete: true))
                    {
                        var inspection = InspectDirectoryTree(path, CancellationToken.None);
                        progress.SetInitialBytesIfUnset(inspection.TotalBytes);
                        var deletedAt = new DateTimeOffset(
                            DateTime.SpecifyKind(entryIdentity.DeletedAtUtc, DateTimeKind.Utc));
                        var legacyMetadataJson = JsonSerializer.Serialize(
                            new LegacyTrashMetadata(
                                entryIdentity.Avid,
                                originalPath,
                                deletedAt),
                            MetadataSerializerOptions);
                        WriteRawMetadataAtomically(path, legacyMetadataJson);
                        EnsureTrashIdentity(
                            path,
                            entryIdentity.Avid,
                            originalPath,
                            allowPendingPurge: true);
                    }
                    DeleteManagedTrashEntry(
                        trashRoot,
                        path,
                        entryIdentity.Avid,
                        originalPath,
                        progress);
                    deletedCount++;
                    nonRestorableTrashPaths.Add(Path.GetFullPath(path));
                    freedBytes = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                        freedBytes,
                        progress.FreedBytes);
                }
                catch (Exception ex) when (IsPurgeFailure(ex))
                {
                    failedCount++;
                    ReconcilePurgeProgress(
                        trashRoot,
                        path,
                        entryIdentity.EntryId,
                        progress);
                    freedBytes = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                        freedBytes,
                        progress.FreedBytes);
                    if (progress.MutationCount > 0)
                    {
                        partiallyDeletedCount++;
                    }

                    if (progress.PurgeStarted)
                    {
                        pendingPurgeCount++;
                        nonRestorableTrashPaths.Add(Path.GetFullPath(path));
                    }

                    firstError ??= ex.Message;
                }
            }
            catch (Exception ex) when (IsPurgeFailure(ex))
            {
                failedCount++;
                ReconcilePurgeProgress(
                    trashRoot,
                    path,
                    entryIdentity.EntryId,
                    progress);
                freedBytes = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                    freedBytes,
                    progress.FreedBytes);
                if (progress.MutationCount > 0)
                {
                    partiallyDeletedCount++;
                }

                if (progress.PurgeStarted)
                {
                    pendingPurgeCount++;
                    nonRestorableTrashPaths.Add(Path.GetFullPath(path));
                }

                firstError ??= ex.Message;
            }
        }
        var journalCleanup = CleanupOrphanPurgeJournals(
            trashRoot,
            protectedJournalEntryIds);
        failedCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(
            failedCount,
            journalCleanup.FailedCount);
        freedBytes = FileSystemCacheStorageStatisticsService.SaturatingAdd(
            freedBytes,
            journalCleanup.FreedBytes);
        firstError ??= journalCleanup.FirstError;

        return new CacheTrashPurgeResult(
            deletedCount,
            freedBytes,
            failedCount,
            skippedCount,
            firstError,
            partiallyDeletedCount,
            pendingPurgeCount,
            nonRestorableTrashPaths.ToArray());
    }

    private static bool TryParseManagedTrashEntryName(
        string name,
        out TrashEntryNameIdentity identity)
    {
        var parts = name.Split('_');
        var schemaVersion = 0;
        var valueOffset = 0;
        if (parts.Length == 4 &&
            parts[0].Length > 1 &&
            parts[0][0] == 'v' &&
            int.TryParse(
                parts[0].AsSpan(1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedVersion) &&
            parsedVersion > 0)
        {
            schemaVersion = parsedVersion;
            valueOffset = 1;
        }
        else if (parts.Length != 3)
        {
            identity = default!;
            return false;
        }

        if (!long.TryParse(
                parts[valueOffset],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var avid) ||
            avid <= 0 ||
            !DateTime.TryParseExact(
                parts[valueOffset + 1],
                TrashEntryTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var deletedAtUtc) ||
            !Guid.TryParseExact(parts[valueOffset + 2], "N", out var entryId))
        {
            identity = default!;
            return false;
        }

        identity = new TrashEntryNameIdentity(
            schemaVersion,
            avid,
            parts[valueOffset + 1],
            deletedAtUtc,
            entryId);
        return true;
    }

    private static DirectoryTreeStatistics InspectDirectoryTree(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(directoryPath);
        if (!attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                "A managed trash entry contains a symbolic link or directory junction.");
        }

        var fileCount = 0;
        var totalBytes = 0L;
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     directoryPath,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    "A managed trash entry contains a symbolic link or directory junction.");
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                var childStatistics = InspectDirectoryTree(path, cancellationToken);
                fileCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                    fileCount,
                    childStatistics.FileCount);
                totalBytes = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                    totalBytes,
                    childStatistics.TotalBytes);
                continue;
            }

            fileCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(fileCount, 1);
            var length = new FileInfo(path).Length;
            totalBytes = FileSystemCacheStorageStatisticsService.SaturatingAdd(totalBytes, length);
        }

        return new DirectoryTreeStatistics(fileCount, totalBytes);
    }

    private static bool TryFinalizeEmptyVersionedTrashEntry(
        string trashRoot,
        string directoryPath,
        TrashEntryNameIdentity entryIdentity,
        PurgeDeletionProgress progress)
    {
        if (entryIdentity.SchemaVersion != CurrentMetadataSchemaVersion)
        {
            return false;
        }

        using var directoryLease = OpenPhysicalDirectoryLease(
            directoryPath,
            "An interrupted managed trash entry",
            allowDelete: true);
        foreach (var _ in Directory.EnumerateFileSystemEntries(
                     directoryPath,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            return false;
        }
        if (ReadPurgeJournalState(trashRoot, directoryPath) is not null)
        {
            return false;
        }


        MarkHandleForDeletion(directoryLease, "An interrupted managed trash entry");
        progress.MarkMutation();
        return true;
    }

    private void DeleteManagedTrashEntry(
        string trashRoot,
        string directoryPath,
        long avid,
        string originalPath,
        PurgeDeletionProgress progress)
    {
        using var directoryLease = OpenPhysicalDirectoryLease(
            directoryPath,
            "The managed trash entry",
            allowDelete: true);
        var physicalIdentity = GetPhysicalDirectoryIdentity(
            directoryLease,
            "The managed trash entry");
        var journalState = ReadPurgeJournalState(trashRoot, directoryPath);
        if (journalState is not null)
        {
            EnsurePurgeJournalMatchesPhysicalEntry(
                journalState.Journal,
                directoryLease,
                "The managed trash entry");
            progress.MarkPurgeStarted();
            var inspection = InspectDirectoryTree(directoryPath, CancellationToken.None);
            progress.SetInitialBytesIfUnset(
                FileSystemCacheStorageStatisticsService.SaturatingAdd(
                    inspection.TotalBytes,
                    journalState.InitialLength));
            RestoreInternalPurgeState(directoryPath, journalState.Journal);
        }

        EnsureTrashIdentity(
            directoryPath,
            avid,
            originalPath,
            allowPendingPurge: true);
        if (journalState is null)
        {
            var inspection = InspectDirectoryTree(directoryPath, CancellationToken.None);
            progress.SetInitialBytesIfUnset(inspection.TotalBytes);
        }

        var marker = EnsurePurgeMarker(directoryPath);
        progress.MarkPurgeStarted();
        var persistedJournalState = EnsurePurgeJournal(
            trashRoot,
            directoryPath,
            marker,
            physicalIdentity);
        EnsurePurgeMetadataFile(directoryPath);

        var metadataPath = Path.GetFullPath(Path.Combine(directoryPath, MetadataFileName));
        var markerPath = Path.GetFullPath(Path.Combine(directoryPath, PurgeMarkerFileName));
        var journalPath = GetPurgeJournalPath(trashRoot, marker.EntryId);
        var metadataJson = marker.MetadataJson;
        var markerJson = JsonSerializer.Serialize(marker, MetadataSerializerOptions);

        using var journalLease = OpenStateFileLease(
            journalPath,
            "Managed trash purge journal");
        var lockedJournalState = ReadPurgeJournalFile(
            journalLease,
            journalPath,
            marker.EntryId);
        EnsurePurgeJournalDirectoryName(
            lockedJournalState.Journal,
            Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath)));
        EnsurePurgeJournalMatchesPhysicalEntry(
            lockedJournalState.Journal,
            directoryLease,
            "The managed trash entry");
        if (lockedJournalState.Journal != persistedJournalState.Journal)
        {
            throw new InvalidDataException(
                "永久清理日志在删除开始前发生变化，已拒绝删除任何内容。");
        }

        try
        {
            using (var metadataLease = OpenStateFileLease(
                       metadataPath,
                       "Managed trash metadata"))
            using (var markerLease = OpenStateFileLease(
                       markerPath,
                       "Managed purge state"))
            {
                var lockedMetadataJson = ReadLockedStateFile(
                    metadataLease,
                    MaximumMetadataByteCount,
                    "Managed trash metadata");
                var lockedMarkerJson = ReadLockedStateFile(
                    markerLease,
                    MaximumPurgeMarkerByteCount,
                    "Managed purge state");
                if (!string.Equals(
                        lockedMetadataJson,
                        metadataJson,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        lockedMarkerJson,
                        markerJson,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "回收站条目的锁定状态文件与永久清理日志不一致，已拒绝删除。");
                }

                foreach (var path in Directory.EnumerateFileSystemEntries(
                             directoryPath,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    var normalizedPath = Path.GetFullPath(path);
                    if (string.Equals(
                            normalizedPath,
                            metadataPath,
                            OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase
                                : StringComparison.Ordinal) ||
                        string.Equals(
                            normalizedPath,
                            markerPath,
                            OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase
                                : StringComparison.Ordinal))
                    {
                        continue;
                    }

                    DeleteFileSystemEntryByHandle(path, progress);
                }

                MarkHandleForDeletion(
                    metadataLease.SafeFileHandle,
                    "Managed trash metadata");
                progress.MarkMutation();
                MarkHandleForDeletion(
                    markerLease.SafeFileHandle,
                    "Managed purge state");
                progress.MarkMutation();
            }

            BeforeTrashEntryFinalDeleteForTesting?.Invoke(directoryPath);
            MarkHandleForDeletion(directoryLease, "The managed trash entry");
            progress.MarkMutation();
        }
        catch (Exception ex) when (IsPurgeFailure(ex))
        {
            try
            {
                RestoreReservedStateFile(
                    directoryPath,
                    PurgeMarkerFileName,
                    markerJson);
                RestoreReservedStateFile(
                    directoryPath,
                    MetadataFileName,
                    metadataJson);
            }
            catch (Exception recoveryException) when (IsPurgeFailure(recoveryException))
            {
                throw new IOException(
                    "The trash entry could not be deleted and its durable purge state could not be restored.",
                    new AggregateException(ex, recoveryException));
            }

            throw new IOException(
                "The trash entry could not be deleted; its durable purge state was restored for retry.",
                ex);
        }

        directoryLease.Dispose();
        BeforePurgeJournalDeleteForTesting?.Invoke(journalPath);
        MarkHandleForDeletion(
            journalLease.SafeFileHandle,
            "Managed trash purge journal");
        journalLease.Dispose();
        progress.CompleteDeletion();
    }

    private static FileStream OpenStateFileLease(string path, string description)
    {
        if (!OperatingSystem.IsWindows())
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    $"{description} must be a physical file.");
            }

            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
        }

        var handle = OpenWindowsHandle(
            path,
            GenericRead | DeleteAccess | FileReadAttributes,
            FileFlagOpenReparsePoint,
            description,
            FileShare.Read);
        try
        {
            var attributes = GetHandleAttributes(handle, description);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    $"{description} must be a physical file.");
            }

            return new FileStream(
                handle,
                FileAccess.Read,
                bufferSize: 4096,
                isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string ReadLockedStateFile(
        FileStream stream,
        int maximumByteCount,
        string description)
    {
        if (stream.Length <= 0 || stream.Length > maximumByteCount)
        {
            throw new InvalidDataException($"{description} has an invalid size.");
        }

        stream.Position = 0;
        using var reader = new StreamReader(
            stream,
            encoding: null,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        return reader.ReadToEnd();
    }

    private void DeleteFileSystemEntryByHandle(
        string path,
        PurgeDeletionProgress progress)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                "Refusing to follow a symbolic link while purging the application trash.");
        }

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            DeleteDirectoryTree(path, progress);
            return;
        }

        var freedBytes = DeletePhysicalFileByHandle(path, "A managed trash payload file");
        progress.AddDeletion(freedBytes);
    }

    private void DeleteDirectoryTree(
        string directoryPath,
        PurgeDeletionProgress progress)
    {
        using var directoryLease = OpenPhysicalDirectoryLease(
            directoryPath,
            "A managed trash payload directory",
            allowDelete: true);
        BeforeTrashDirectoryEnumerationForTesting?.Invoke(directoryPath);
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     directoryPath,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            DeleteFileSystemEntryByHandle(path, progress);
        }

        MarkHandleForDeletion(directoryLease, "A managed trash payload directory");
        progress.MarkMutation();
    }

    private static void RestoreReservedStateFile(
        string directoryPath,
        string fileName,
        string expectedContents)
    {
        var path = Path.Combine(directoryPath, fileName);
        if (TryGetExistingPathAttributes(path, out var attributes))
        {
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    $"The reserved trash state path is not a physical file: {path}");
            }

            var maximumByteCount = string.Equals(
                fileName,
                PurgeMarkerFileName,
                StringComparison.Ordinal)
                ? MaximumPurgeMarkerByteCount
                : MaximumMetadataByteCount;
            var actualContents = ReadPhysicalStateFile(
                path,
                maximumByteCount,
                "Reserved trash state");
            if (string.Equals(actualContents, expectedContents, StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidDataException(
                $"The reserved trash state changed while recovering: {path}");
        }

        WriteRawFileAtomically(directoryPath, fileName, expectedContents);
    }

    private static void EnsureDirectChild(string parentPath, string childPath)
    {
        var relative = Path.GetRelativePath(
            Path.GetFullPath(parentPath),
            Path.GetFullPath(childPath));
        if (string.IsNullOrWhiteSpace(relative) ||
            relative == "." ||
            relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative) ||
            relative.Contains(Path.DirectorySeparatorChar) ||
            relative.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException(
                "The application trash path is not a direct child of its managed parent.");
        }
    }

    private static bool IsPurgeFailure(Exception ex)
    {
        return ex is IOException or
            InvalidDataException or
            UnauthorizedAccessException or
            PlatformNotSupportedException or
            InvalidOperationException or
            System.Security.SecurityException;
    }
    private static void ReconcilePurgeProgress(
        string trashRoot,
        string directoryPath,
        Guid entryId,
        PurgeDeletionProgress progress)
    {
        try
        {
            progress.ReconcileRemainingBytes(
                MeasureRemainingPurgeBytes(trashRoot, directoryPath, entryId));
        }
        catch (Exception ex) when (IsPurgeFailure(ex))
        {
            progress.ReconcileUnknown();
        }
    }


    private sealed class PurgeDeletionProgress
    {
        public bool PurgeStarted { get; private set; }

        public long FreedBytes { get; private set; }

        public long InitialBytes { get; private set; }

        public bool HasInitialBytes { get; private set; }

        public int MutationCount { get; private set; }

        public void MarkPurgeStarted()
        {
            PurgeStarted = true;
        }

        public void AddDeletion(long freedBytes)
        {
            _ = freedBytes;
            MarkMutation();
        }

        public void SetInitialBytesIfUnset(long initialBytes)
        {
            if (HasInitialBytes)
            {
                return;
            }

            InitialBytes = Math.Max(0, initialBytes);
            HasInitialBytes = true;
        }

        public void CompleteDeletion()
        {
            FreedBytes = HasInitialBytes ? InitialBytes : 0;
        }

        public void ReconcileRemainingBytes(long remainingBytes)
        {
            FreedBytes = HasInitialBytes
                ? Math.Max(0, InitialBytes - Math.Max(0, remainingBytes))
                : 0;
        }

        public void ReconcileUnknown()
        {
            FreedBytes = 0;
        }

        public void MarkMutation()
        {
            MutationCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                MutationCount,
                1);
        }
    }

    private sealed record DirectoryTreeStatistics(int FileCount, long TotalBytes);

    private sealed record TrashEntryNameIdentity(
        int SchemaVersion,
        long Avid,
        string TimestampToken,
        DateTime DeletedAtUtc,
        Guid EntryId);
}
