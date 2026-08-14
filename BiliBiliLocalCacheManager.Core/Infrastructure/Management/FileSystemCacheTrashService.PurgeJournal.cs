using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

public sealed partial class FileSystemCacheTrashService
{
    private const string PurgeJournalFilePrefix = ".purge-journal-";
    private const string PurgeJournalFileSuffix = ".json";
    private const int MaximumPurgeJournalByteCount = 64 * 1024;

    private static string GetPurgeJournalFileName(Guid entryId) =>
        $"{PurgeJournalFilePrefix}{entryId:N}{PurgeJournalFileSuffix}";

    private static string GetPurgeJournalPath(string trashRoot, Guid entryId) =>
        Path.Combine(trashRoot, GetPurgeJournalFileName(entryId));

    private static bool TryParsePurgeJournalFileName(string fileName, out Guid entryId)
    {
        if (!fileName.StartsWith(PurgeJournalFilePrefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(PurgeJournalFileSuffix, StringComparison.OrdinalIgnoreCase))
        {
            entryId = default;
            return false;
        }

        var tokenLength = fileName.Length -
                          PurgeJournalFilePrefix.Length -
                          PurgeJournalFileSuffix.Length;
        if (tokenLength <= 0 ||
            !Guid.TryParseExact(
                fileName.AsSpan(PurgeJournalFilePrefix.Length, tokenLength),
                "N",
                out entryId))
        {
            entryId = default;
            return false;
        }

        return true;
    }

    private static PurgeJournalState? ReadPurgeJournalState(
        string trashRoot,
        string directoryPath)
    {
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath));
        if (!TryParseManagedTrashEntryName(directoryName, out var entryIdentity))
        {
            throw new InvalidDataException("永久清理日志对应的回收站目录身份无效。");
        }

        var journalPath = GetPurgeJournalPath(trashRoot, entryIdentity.EntryId);
        if (!TryGetExistingPathAttributes(journalPath, out _))
        {
            return null;
        }

        var state = ReadPurgeJournalFile(journalPath, entryIdentity.EntryId);
        EnsurePurgeJournalDirectoryName(state.Journal, directoryName);
        return state;
    }

    private static PurgeJournalState ReadPurgeJournalFile(
        string journalPath,
        Guid expectedEntryId)
    {
        using var journalLease = OpenStateFileLease(
            journalPath,
            "Managed trash purge journal");
        return ReadPurgeJournalFile(journalLease, journalPath, expectedEntryId);
    }

    private static PurgeJournalState ReadPurgeJournalFile(
        FileStream journalLease,
        string journalPath,
        Guid expectedEntryId)
    {
        var journalJson = ReadLockedStateFile(
            journalLease,
            MaximumPurgeJournalByteCount,
            "Managed trash purge journal");
        PurgeJournal journal;
        try
        {
            journal = JsonSerializer.Deserialize<PurgeJournal>(
                          journalJson,
                          MetadataSerializerOptions) ??
                      throw new InvalidDataException("永久清理日志为空，已拒绝操作。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("永久清理日志损坏，已拒绝操作。", ex);
        }

        if (string.IsNullOrWhiteSpace(journal.EntryDirectoryName) ||
            !TryParseManagedTrashEntryName(
                journal.EntryDirectoryName,
                out var entryIdentity))
        {
            throw new InvalidDataException("永久清理日志中的目录身份无效。");
        }

        var metadataByteCount = string.IsNullOrWhiteSpace(journal.MetadataJson)
            ? 0
            : Encoding.UTF8.GetByteCount(journal.MetadataJson);
        if (journal.SchemaVersion != CurrentMetadataSchemaVersion ||
            journal.Avid != entryIdentity.Avid ||
            journal.EntryId == Guid.Empty ||
            journal.EntryId != expectedEntryId ||
            journal.EntryId != entryIdentity.EntryId ||
            journal.StartedAtUtc == default ||
            metadataByteCount <= 0 ||
            metadataByteCount > MaximumMetadataByteCount)
        {
            throw new InvalidDataException(
                "永久清理日志与回收站目录身份不匹配或内容无效，已拒绝操作。");
        }

        ValidateTrashMetadataJson(
            journal.MetadataJson,
            entryIdentity,
            entryIdentity.Avid);
        return new PurgeJournalState(
            journal,
            journalPath,
            journalLease.Length);
    }

    private static PurgeJournalState EnsurePurgeJournal(
        string trashRoot,
        string directoryPath,
        PurgeMarker marker,
        PhysicalDirectoryIdentity physicalIdentity)
    {
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath));
        if (!TryParseManagedTrashEntryName(directoryName, out var entryIdentity) ||
            marker.Avid != entryIdentity.Avid ||
            marker.EntryId != entryIdentity.EntryId)
        {
            throw new InvalidDataException("无法为身份不匹配的回收站目录创建永久清理日志。");
        }

        var expectedJournal = new PurgeJournal(
            CurrentMetadataSchemaVersion,
            marker.Avid,
            marker.EntryId,
            directoryName,
            marker.StartedAtUtc,
            marker.MetadataJson,
            physicalIdentity.VolumeSerialNumber,
            physicalIdentity.FileIdLow,
            physicalIdentity.FileIdHigh);
        var existingState = ReadPurgeJournalState(trashRoot, directoryPath);
        if (existingState is not null)
        {
            if (existingState.Journal != expectedJournal)
            {
                throw new InvalidDataException(
                    "已有永久清理日志与当前清理状态不一致，已拒绝操作。");
            }

            return existingState;
        }

        var journalJson = JsonSerializer.Serialize(
            expectedJournal,
            MetadataSerializerOptions);
        if (Encoding.UTF8.GetByteCount(journalJson) > MaximumPurgeJournalByteCount)
        {
            throw new InvalidDataException("永久清理日志过大，已拒绝开始删除。");
        }

        WriteRawFileAtomically(
            trashRoot,
            GetPurgeJournalFileName(entryIdentity.EntryId),
            journalJson);
        var persistedState = ReadPurgeJournalState(trashRoot, directoryPath);
        if (persistedState is null || persistedState.Journal != expectedJournal)
        {
            throw new IOException("永久清理日志未能可靠写入，已拒绝删除任何内容。");
        }

        return persistedState;
    }

    private static void EnsurePurgeJournalDirectoryName(
        PurgeJournal journal,
        string expectedDirectoryName)
    {
        if (!string.Equals(
                journal.EntryDirectoryName,
                expectedDirectoryName,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidDataException("永久清理日志与回收站目录名称不匹配。");
        }
    }

    private static bool PurgeJournalMatchesPhysicalEntry(
        PurgeJournal journal,
        SafeFileHandle directoryLease,
        string description)
    {
        var actualIdentity = GetPhysicalDirectoryIdentity(directoryLease, description);
        return journal.VolumeSerialNumber == actualIdentity.VolumeSerialNumber &&
               journal.FileIdLow == actualIdentity.FileIdLow &&
               journal.FileIdHigh == actualIdentity.FileIdHigh;
    }

    private static void EnsurePurgeJournalMatchesPhysicalEntry(
        PurgeJournal journal,
        SafeFileHandle directoryLease,
        string description)
    {
        if (!PurgeJournalMatchesPhysicalEntry(journal, directoryLease, description))
        {
            throw new InvalidDataException(
                "永久清理日志绑定的物理目录已被替换，已拒绝恢复状态或删除内容。");
        }
    }

    private static void EnsurePurgeJournalMatchesInternalState(
        string directoryPath,
        PurgeJournal journal)
    {
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath));
        if (!TryParseManagedTrashEntryName(directoryName, out var entryIdentity) ||
            journal.Avid != entryIdentity.Avid ||
            journal.EntryId != entryIdentity.EntryId ||
            !string.Equals(
                journal.EntryDirectoryName,
                directoryName,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidDataException("永久清理日志与回收站目录身份不匹配。");
        }

        var markerPath = Path.Combine(directoryPath, PurgeMarkerFileName);
        if (TryGetExistingPathAttributes(markerPath, out _))
        {
            var marker = ReadPurgeMarker(directoryPath, entryIdentity) ??
                         throw new InvalidDataException("永久清理状态缺失。");
            if (marker.SchemaVersion != journal.SchemaVersion ||
                marker.Avid != journal.Avid ||
                marker.EntryId != journal.EntryId ||
                marker.StartedAtUtc != journal.StartedAtUtc ||
                !string.Equals(
                    marker.MetadataJson,
                    journal.MetadataJson,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "条目内永久清理状态与父级日志不一致，已拒绝操作。");
            }
        }

        var metadataPath = Path.Combine(directoryPath, MetadataFileName);
        if (TryGetExistingPathAttributes(metadataPath, out _))
        {
            var metadataJson = ReadMetadataJson(metadataPath);
            if (!string.Equals(
                    metadataJson,
                    journal.MetadataJson,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "条目元数据与父级永久清理日志不一致，已拒绝操作。");
            }
        }
    }

    private static void RestoreInternalPurgeState(
        string directoryPath,
        PurgeJournal journal)
    {
        EnsurePurgeJournalMatchesInternalState(directoryPath, journal);

        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath));
        if (!TryParseManagedTrashEntryName(directoryName, out var entryIdentity))
        {
            throw new InvalidDataException("永久清理日志对应的回收站目录身份无效。");
        }
        var markerPath = Path.Combine(directoryPath, PurgeMarkerFileName);
        if (!TryGetExistingPathAttributes(markerPath, out _))
        {
            var marker = new PurgeMarker(
                journal.SchemaVersion,
                journal.Avid,
                journal.EntryId,
                journal.StartedAtUtc,
                journal.MetadataJson);
            var markerJson = JsonSerializer.Serialize(marker, MetadataSerializerOptions);
            WriteRawFileAtomically(directoryPath, PurgeMarkerFileName, markerJson);
            var restoredMarker = ReadPurgeMarker(directoryPath, entryIdentity);
            if (restoredMarker != marker)
            {
                throw new IOException("未能从父级日志可靠恢复条目清理状态。");
            }
        }

        var metadataPath = Path.Combine(directoryPath, MetadataFileName);
        if (!TryGetExistingPathAttributes(metadataPath, out _))
        {
            WriteRawMetadataAtomically(directoryPath, journal.MetadataJson);
            var restoredMetadataJson = ReadMetadataJson(metadataPath);
            if (!string.Equals(
                    restoredMetadataJson,
                    journal.MetadataJson,
                    StringComparison.Ordinal))
            {
                throw new IOException("未能从父级日志可靠恢复条目元数据。");
            }
        }
    }

    private static long DeletePurgeJournal(
        string trashRoot,
        string directoryPath)
    {
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath));
        if (!TryParseManagedTrashEntryName(directoryName, out var entryIdentity))
        {
            throw new InvalidDataException("永久清理日志对应的回收站目录身份无效。");
        }

        var journalPath = GetPurgeJournalPath(trashRoot, entryIdentity.EntryId);
        using var journalLease = OpenStateFileLease(
            journalPath,
            "Managed trash purge journal");
        var state = ReadPurgeJournalFile(
            journalLease,
            journalPath,
            entryIdentity.EntryId);
        EnsurePurgeJournalDirectoryName(state.Journal, directoryName);
        MarkHandleForDeletion(
            journalLease.SafeFileHandle,
            "Managed trash purge journal");
        return state.InitialLength;
    }

    private static long MeasureRemainingPurgeBytes(
        string trashRoot,
        string directoryPath,
        Guid entryId)
    {
        var remainingBytes = 0L;
        if (TryGetExistingPathAttributes(directoryPath, out var directoryAttributes))
        {
            if (!directoryAttributes.HasFlag(FileAttributes.Directory) ||
                directoryAttributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("待清理条目不再是物理目录。");
            }

            remainingBytes = InspectDirectoryTree(
                directoryPath,
                CancellationToken.None).TotalBytes;
        }

        var journalPath = GetPurgeJournalPath(trashRoot, entryId);
        if (TryGetExistingPathAttributes(journalPath, out _))
        {
            using var journalLease = OpenStateFileLease(
                journalPath,
                "Managed trash purge journal");
            remainingBytes = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                remainingBytes,
                journalLease.Length);
        }

        return remainingBytes;
    }

    private static PurgeJournalCleanupResult CleanupOrphanPurgeJournals(
        string trashRoot,
        IReadOnlySet<Guid> protectedEntryIds)
    {
        var failedCount = 0;
        var freedBytes = 0L;
        string? firstError = null;
        foreach (var journalPath in Directory.EnumerateFileSystemEntries(
                     trashRoot,
                     $"{PurgeJournalFilePrefix}*{PurgeJournalFileSuffix}",
                     SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(journalPath);
            if (!TryParsePurgeJournalFileName(fileName, out var entryId) ||
                protectedEntryIds.Contains(entryId))
            {
                continue;
            }

            try
            {
                using var journalLease = OpenStateFileLease(
                    journalPath,
                    "Orphaned managed trash purge journal");
                var state = ReadPurgeJournalFile(
                    journalLease,
                    journalPath,
                    entryId);
                var entryPath = Path.Combine(
                    trashRoot,
                    state.Journal.EntryDirectoryName);
                EnsureDirectChild(trashRoot, entryPath);
                if (TryGetExistingPathAttributes(entryPath, out _))
                {
                    using var entryLease = OpenPhysicalDirectoryLease(
                        entryPath,
                        "The journal-bound managed trash entry",
                        allowDelete: false);
                    if (PurgeJournalMatchesPhysicalEntry(
                            state.Journal,
                            entryLease,
                            "The journal-bound managed trash entry"))
                    {
                        continue;
                    }
                }

                MarkHandleForDeletion(
                    journalLease.SafeFileHandle,
                    "Orphaned managed trash purge journal");
                freedBytes = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                    freedBytes,
                    state.InitialLength);
            }
            catch (Exception ex) when (IsPurgeFailure(ex))
            {
                failedCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                    failedCount,
                    1);
                firstError ??= ex.Message;
            }
        }

        return new PurgeJournalCleanupResult(
            failedCount,
            freedBytes,
            firstError);
    }

    private sealed record PurgeJournal(
        int SchemaVersion,
        long Avid,
        Guid EntryId,
        string EntryDirectoryName,
        DateTimeOffset StartedAtUtc,
        string MetadataJson,
        ulong VolumeSerialNumber,
        ulong FileIdLow,
        ulong FileIdHigh);

    private sealed record PurgeJournalState(
        PurgeJournal Journal,
        string Path,
        long InitialLength);

    private sealed record PurgeJournalCleanupResult(
        int FailedCount,
        long FreedBytes,
        string? FirstError);
}
