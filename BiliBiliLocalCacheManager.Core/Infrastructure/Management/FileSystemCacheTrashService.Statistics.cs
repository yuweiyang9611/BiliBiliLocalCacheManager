using BiliBiliLocalCacheManager.Core.Application.Models;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

public sealed partial class FileSystemCacheTrashService
{
    public CacheTrashStatistics GetStatistics(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var root = Path.GetFullPath(rootDirectory);
        using var transaction = EnterMutationTransaction(
            root,
            CacheTrashMutationOperation.Statistics);
        ValidateRoot(root);
        var trashRoot = CacheStorageLayout.GetTrashDirectory(root);
        EnsureDirectChild(root, trashRoot);
        if (!Directory.Exists(trashRoot))
        {
            return new CacheTrashStatistics(trashRoot, 0, 0, 0, 0, 0, null);
        }

        using var trashRootLease = OpenPhysicalDirectoryLease(
            trashRoot,
            "The application trash directory",
            allowDelete: false);

        var managedEntryCount = 0;
        var fileCount = 0;
        var failedEntryCount = 0;
        var skippedEntryCount = 0;
        var totalBytes = 0L;
        var untrustedLegacyEntryCount = 0;
        var untrustedLegacyFileCount = 0;
        var untrustedLegacyBytes = 0L;
        var pendingPurgeEntryCount = 0;
        string? firstError = null;

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     trashRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            if (TryParsePurgeJournalFileName(name, out _))
            {
                continue;
            }

            if (!TryParseManagedTrashEntryName(name, out var entryIdentity))
            {
                skippedEntryCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                    skippedEntryCount,
                    1);
                continue;
            }

            try
            {
                EnsureDirectChild(trashRoot, path);
                var attributes = File.GetAttributes(path);
                if (!attributes.HasFlag(FileAttributes.Directory) ||
                    attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    skippedEntryCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                        skippedEntryCount,
                        1);
                    continue;
                }
                using var entryLease = OpenPhysicalDirectoryLease(
                    path,
                    "The managed trash entry",
                    allowDelete: false);


                var originalPath = Path.Combine(
                    root,
                    entryIdentity.Avid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var journalState = ReadPurgeJournalState(trashRoot, path);
                if (journalState is not null)
                {
                    pendingPurgeEntryCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                        pendingPurgeEntryCount,
                        1);
                    EnsurePurgeJournalMatchesPhysicalEntry(
                        journalState.Journal,
                        entryLease,
                        "The managed trash entry");
                    EnsurePurgeJournalMatchesInternalState(path, journalState.Journal);
                }
                else
                {
                    if (HasPurgeMarker(path))
                    {
                        pendingPurgeEntryCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                            pendingPurgeEntryCount,
                            1);
                    }

                    EnsureTrashIdentity(
                        path,
                        entryIdentity.Avid,
                        originalPath,
                        allowPendingPurge: true);
                }

                managedEntryCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                    managedEntryCount,
                    1);
                var inspection = InspectDirectoryTree(path, cancellationToken);
                fileCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                    fileCount,
                    inspection.FileCount);
                totalBytes = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                    totalBytes,
                    inspection.TotalBytes);
            }
            catch (UntrustedTrashEntryException) when (entryIdentity.SchemaVersion != 0)
            {
                skippedEntryCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                    skippedEntryCount,
                    1);
            }
            catch (UntrustedTrashEntryException)
            {
                skippedEntryCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                    skippedEntryCount,
                    1);
                try
                {
                    var inspection = InspectDirectoryTree(path, cancellationToken);
                    untrustedLegacyEntryCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                        untrustedLegacyEntryCount,
                        1);
                    untrustedLegacyFileCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                        untrustedLegacyFileCount,
                        inspection.FileCount);
                    untrustedLegacyBytes = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                        untrustedLegacyBytes,
                        inspection.TotalBytes);
                }
                catch (Exception ex) when (IsPurgeFailure(ex))
                {
                    failedEntryCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                        failedEntryCount,
                        1);
                    firstError ??= ex.Message;
                }
            }
            catch (Exception ex) when (IsPurgeFailure(ex))
            {
                failedEntryCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                    failedEntryCount,
                    1);
                firstError ??= ex.Message;
            }
        }

        return new CacheTrashStatistics(
            trashRoot,
            managedEntryCount,
            fileCount,
            totalBytes,
            failedEntryCount,
            skippedEntryCount,
            firstError,
            untrustedLegacyEntryCount,
            untrustedLegacyFileCount,
            untrustedLegacyBytes,
            pendingPurgeEntryCount);
    }
}
