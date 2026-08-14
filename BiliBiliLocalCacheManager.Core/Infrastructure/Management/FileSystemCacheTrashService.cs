using System.Globalization;
using System.Text;
using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

public sealed partial class FileSystemCacheTrashService : ICacheTrashService
{
    private const string MetadataFileName = ".trash-info.json";
    private const string PurgeMarkerFileName = ".purge-in-progress.json";
    private const int CurrentMetadataSchemaVersion = 1;
    private const int MaximumMetadataByteCount = 16 * 1024;
    private const int MaximumPurgeMarkerByteCount = 64 * 1024;
    private const string TrashEntryTimestampFormat = "yyyyMMddHHmmssfff";
    private static readonly JsonSerializerOptions MetadataSerializerOptions = new()
    {
        WriteIndented = true
    };
    private static readonly TimeSpan LegacyTimestampTolerance = TimeSpan.FromMinutes(5);

    internal Action<string>? BeforeTrashMetadataWriteForTesting { get; set; }
    internal Action<string>? BeforeRestoredMetadataDeleteForTesting { get; set; }
    internal Action<string>? BeforeTrashEntryFinalDeleteForTesting { get; set; }
    internal Action<string>? BeforePurgeJournalDeleteForTesting { get; set; }
    internal Action<string>? BeforeTrashDirectoryEnumerationForTesting { get; set; }
    internal Action<string>? BeforeRestoreRenameForTesting { get; set; }
    internal Action<string>? BeforeMoveRenameForTesting { get; set; }

    public string GetTrashDirectory(string rootDirectory)
    {
        ValidateRoot(rootDirectory);
        return CacheStorageLayout.GetTrashDirectory(rootDirectory);
    }

    public CacheTrashOperationResult MoveToTrash(string rootDirectory, long avid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(avid);
        var root = Path.GetFullPath(rootDirectory);
        using var transaction = EnterMutationTransaction(root, CacheTrashMutationOperation.Move);
        ValidateRoot(root);
        var originalPath = Path.Combine(root, avid.ToString(CultureInfo.InvariantCulture));
        if (!Directory.Exists(originalPath))
        {
            return new CacheTrashOperationResult(avid, false, false, originalPath, null, null);
        }

        var trashRoot = CacheStorageLayout.GetTrashDirectory(root);
        string? trashPath = null;

        try
        {
            EnsureDirectChild(root, originalPath);
            EnsureDirectChild(root, trashRoot);
            using var rootLease = OperatingSystem.IsWindows()
                ? OpenPhysicalDirectoryLease(
                    root,
                    "The cache root directory",
                    allowDelete: true)
                : null;

            EnsurePhysicalDirectory(originalPath, "The original avid cache directory");
            using var originalLease = OperatingSystem.IsWindows()
                ? OpenPhysicalDirectoryLease(
                    originalPath,
                    "The original avid cache directory",
                    allowDelete: true)
                : null;

            if (Directory.Exists(trashRoot))
            {
                EnsurePhysicalDirectory(trashRoot, "The application trash directory");
            }
            else
            {
                Directory.CreateDirectory(trashRoot);
                EnsurePhysicalDirectory(trashRoot, "The application trash directory");
            }

            using var trashRootLease = OperatingSystem.IsWindows()
                ? OpenPhysicalDirectoryLease(
                    trashRoot,
                    "The application trash directory",
                    allowDelete: true)
                : null;

            EnsureReservedPurgeMarkerAbsent(originalPath);
            var metadata = ReadStagedMetadataForMove(originalPath, avid);
            if (metadata is null)
            {
                metadata = new TrashMetadata(
                    CurrentMetadataSchemaVersion,
                    avid,
                    avid.ToString(CultureInfo.InvariantCulture),
                    DateTimeOffset.UtcNow,
                    Guid.NewGuid());
                WriteMetadataAtomically(originalPath, metadata);
            }

            trashPath = Path.Combine(
                trashRoot,
                $"v{CurrentMetadataSchemaVersion}_{avid}_" +
                $"{metadata.DeletedAtUtc.UtcDateTime.ToString(TrashEntryTimestampFormat, CultureInfo.InvariantCulture)}_" +
                $"{metadata.EntryId:N}");
            EnsureDirectChild(trashRoot, trashPath);
            if (Directory.Exists(trashPath) || File.Exists(trashPath))
            {
                throw new IOException($"The target trash entry already exists: {trashPath}");
            }

            // Revalidate both paths immediately before moving. This also prevents an existing
            // trash-root junction from redirecting the cache into an external directory.
            EnsurePhysicalDirectory(originalPath, "The original avid cache directory");
            EnsurePhysicalDirectory(trashRoot, "The application trash directory");
            EnsureReservedPurgeMarkerAbsent(originalPath);
            if (ReadStagedMetadataForMove(originalPath, avid) != metadata)
            {
                throw new InvalidDataException(
                    "回收站元数据在移动前发生变化，已拒绝操作。");
            }

            BeforeMoveRenameForTesting?.Invoke(originalPath);
            if (OperatingSystem.IsWindows())
            {
                RenamePhysicalDirectoryByHandle(
                    originalLease!,
                    trashPath,
                    "The original avid cache directory");
            }
            else
            {
                Directory.Move(originalPath, trashPath);
            }

            return new CacheTrashOperationResult(avid, true, true, originalPath, trashPath, null);
        }
        catch (Exception ex)
        {
            return new CacheTrashOperationResult(
                avid,
                true,
                false,
                originalPath,
                trashPath,
                ex.Message);
        }
    }

    public CacheTrashOperationResult Restore(string rootDirectory, long avid, string trashPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(trashPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(avid);

        var root = Path.GetFullPath(rootDirectory);
        using var transaction = EnterMutationTransaction(root, CacheTrashMutationOperation.Restore);
        ValidateRoot(root);
        var trashRoot = CacheStorageLayout.GetTrashDirectory(root);
        var normalizedTrashPath = Path.GetFullPath(trashPath);
        EnsureDirectChild(root, trashRoot);
        EnsureInsideTrash(trashRoot, normalizedTrashPath);

        if (Directory.Exists(trashRoot))
        {
            EnsurePhysicalDirectory(trashRoot, "The application trash directory");
        }

        var originalPath = Path.Combine(root, avid.ToString(CultureInfo.InvariantCulture));
        if (!Directory.Exists(normalizedTrashPath))
        {
            return new CacheTrashOperationResult(avid, false, false, originalPath, normalizedTrashPath, null);
        }

        EnsureDirectChild(trashRoot, normalizedTrashPath);
        EnsurePhysicalDirectory(normalizedTrashPath, "The managed trash entry");
        EnsureRestoreIsNotPendingPurge(trashRoot, normalizedTrashPath);
        EnsureTrashIdentity(normalizedTrashPath, avid, originalPath);

        if (Directory.Exists(originalPath))
        {
            EnsurePhysicalDirectory(originalPath, "The original avid cache directory");
            return new CacheTrashOperationResult(
                avid,
                true,
                false,
                originalPath,
                normalizedTrashPath,
                "原缓存目录已经存在，无法覆盖恢复。");
        }

        try
        {
            string? metadataCleanupWarning;
            if (OperatingSystem.IsWindows())
            {
                using var rootLease = OpenPhysicalDirectoryLease(
                    root,
                    "The cache root directory",
                    allowDelete: true);
                using var trashRootLease = OpenPhysicalDirectoryLease(
                    trashRoot,
                    "The application trash directory",
                    allowDelete: true);
                using var entryLease = OpenPhysicalDirectoryLease(
                    normalizedTrashPath,
                    "The managed trash entry",
                    allowDelete: true);

                EnsureRestoreIsNotPendingPurge(trashRoot, normalizedTrashPath);
                EnsureTrashIdentity(normalizedTrashPath, avid, originalPath);
                if (Directory.Exists(originalPath) || File.Exists(originalPath))
                {
                    return new CacheTrashOperationResult(
                        avid,
                        true,
                        false,
                        originalPath,
                        normalizedTrashPath,
                        "原缓存路径已经存在，无法覆盖恢复。");
                }

                BeforeRestoreRenameForTesting?.Invoke(normalizedTrashPath);
                RenamePhysicalDirectoryByHandle(
                    entryLease,
                    originalPath,
                    "The managed trash entry");
                metadataCleanupWarning = TryDeleteRestoredMetadata(originalPath);
            }
            else
            {
                EnsurePhysicalDirectory(trashRoot, "The application trash directory");
                EnsurePhysicalDirectory(normalizedTrashPath, "The managed trash entry");
                EnsureRestoreIsNotPendingPurge(trashRoot, normalizedTrashPath);
                Directory.Move(normalizedTrashPath, originalPath);
                metadataCleanupWarning = TryDeleteMetadata(originalPath);
            }
            return new CacheTrashOperationResult(
                avid,
                true,
                true,
                originalPath,
                normalizedTrashPath,
                metadataCleanupWarning);
        }
        catch (Exception ex)
        {
            return new CacheTrashOperationResult(avid, true, false, originalPath, normalizedTrashPath, ex.Message);
        }
    }

    private static void ValidateRoot(string rootDirectory)
    {
        CacheRootSafety.ValidatePhysicalRoot(rootDirectory);
    }

    private static void EnsureRestoreIsNotPendingPurge(
        string trashRoot,
        string directoryPath)
    {
        if (ReadPurgeJournalState(trashRoot, directoryPath) is not null)
        {
            throw new InvalidOperationException(
                "该回收站条目已经开始永久清理，无法再恢复；请重试或完成清理。");
        }
    }

    private static void EnsureInsideTrash(string trashRoot, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(trashRoot), path);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("恢复路径不在应用回收站中，已拒绝操作。");
        }
    }

    private static void EnsurePhysicalDirectory(string path, string description)
    {
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                $"{description} must be a physical directory, not a symbolic link or directory junction.");
        }
    }

    private void WriteMetadataAtomically(string trashPath, TrashMetadata metadata)
    {
        BeforeTrashMetadataWriteForTesting?.Invoke(trashPath);
        var metadataPath = Path.Combine(trashPath, MetadataFileName);
        var temporaryPath = Path.Combine(
            trashPath,
            $"{MetadataFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            if (File.Exists(metadataPath) || Directory.Exists(metadataPath))
            {
                throw new IOException(
                    $"The reserved trash metadata path already exists: {metadataPath}");
            }

            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, metadata, MetadataSerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, metadataPath, overwrite: false);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Preserve the original error. An untrusted entry is never purged automatically.
            }
        }
    }

    private static void WriteRawMetadataAtomically(string directoryPath, string metadataJson) =>
        WriteRawFileAtomically(directoryPath, MetadataFileName, metadataJson);

    private static void WriteRawFileAtomically(
        string directoryPath,
        string fileName,
        string contents)
    {
        var targetPath = Path.Combine(directoryPath, fileName);
        var temporaryPath = Path.Combine(
            directoryPath,
            $"{fileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                throw new IOException(
                    $"The reserved trash state path already exists: {targetPath}");
            }

            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                using (var writer = new StreamWriter(
                           stream,
                           new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                           bufferSize: 1024,
                           leaveOpen: true))
                {
                    writer.Write(contents);
                    writer.Flush();
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, targetPath, overwrite: false);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Preserve the original purge failure.
            }
        }
    }

    private static void EnsureTrashIdentity(
        string trashPath,
        long avid,
        string originalPath,
        bool allowPendingPurge = false)
    {
        _ = originalPath;
        var trashDirectoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(trashPath));
        if (!TryParseManagedTrashEntryName(trashDirectoryName, out var entryIdentity) ||
            entryIdentity.Avid != avid)
        {
            throw new InvalidOperationException("回收站目录与待恢复的 avid 不匹配，已拒绝操作。");
        }

        var purgeMarker = ReadPurgeMarker(trashPath, entryIdentity);
        if (purgeMarker is not null && !allowPendingPurge)
        {
            throw new InvalidOperationException(
                "该回收站条目已进入永久清理流程，可能不完整，不能再恢复。请重试清空回收站。");
        }

        var metadataPath = Path.Combine(trashPath, MetadataFileName);
        string metadataJson;
        if (TryGetExistingPathAttributes(metadataPath, out _))
        {
            metadataJson = ReadMetadataJson(metadataPath);
            if (purgeMarker is not null &&
                !string.Equals(
                    metadataJson,
                    purgeMarker.MetadataJson,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "永久清理状态中的元数据备份与当前元数据不一致，已拒绝操作。");
            }
        }
        else if (allowPendingPurge && purgeMarker is not null)
        {
            metadataJson = purgeMarker.MetadataJson;
        }
        else
        {
            throw new UntrustedTrashEntryException(
                "回收站条目缺少元数据，无法证明它由本应用创建，已保留但拒绝自动处理。");
        }

        ValidateTrashMetadataJson(metadataJson, entryIdentity, avid);
    }

    private static void ValidateTrashMetadataJson(
        string metadataJson,
        TrashEntryNameIdentity entryIdentity,
        long avid)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(metadataJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("回收站元数据损坏，已拒绝操作。", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("回收站元数据不是有效对象，已拒绝操作。");
            }

            if (!document.RootElement.TryGetProperty("SchemaVersion", out var schemaElement))
            {
                ValidateLegacyMetadata(metadataJson, entryIdentity, avid);
                return;
            }

            if (!schemaElement.TryGetInt32(out var schemaVersion) ||
                schemaVersion != CurrentMetadataSchemaVersion)
            {
                throw new InvalidDataException(
                    $"不支持的回收站元数据版本：{schemaElement.GetRawText()}。");
            }

            if (entryIdentity.SchemaVersion != CurrentMetadataSchemaVersion)
            {
                throw new InvalidDataException("回收站目录版本与元数据版本不匹配，已拒绝操作。");
            }

            TrashMetadata metadata;
            try
            {
                metadata = JsonSerializer.Deserialize<TrashMetadata>(
                               metadataJson,
                               MetadataSerializerOptions) ??
                           throw new InvalidDataException("回收站元数据为空，已拒绝操作。");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("回收站元数据损坏，已拒绝操作。", ex);
            }

            var expectedRelativePath = avid.ToString(CultureInfo.InvariantCulture);
            var expectedTimestamp = metadata.DeletedAtUtc.UtcDateTime.ToString(
                TrashEntryTimestampFormat,
                CultureInfo.InvariantCulture);
            if (metadata.SchemaVersion != CurrentMetadataSchemaVersion ||
                metadata.Avid != avid ||
                !string.Equals(metadata.OriginalRelativePath, expectedRelativePath, StringComparison.Ordinal) ||
                metadata.EntryId == Guid.Empty ||
                metadata.EntryId != entryIdentity.EntryId ||
                metadata.DeletedAtUtc == default ||
                !string.Equals(expectedTimestamp, entryIdentity.TimestampToken, StringComparison.Ordinal))
            {
                throw new InvalidDataException("回收站元数据与目录身份不匹配，已拒绝操作。");
            }
        }
    }

    private static string ReadMetadataJson(string metadataPath) =>
        ReadPhysicalStateFile(
            metadataPath,
            MaximumMetadataByteCount,
            "回收站元数据");

    private static string ReadPhysicalStateFile(
        string path,
        int maximumByteCount,
        string description)
    {
        using var stream = OpenStateFileLease(path, description);
        return ReadLockedStateFile(stream, maximumByteCount, description);
    }

    private static bool HasPurgeMarker(string directoryPath)
    {
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath));
        if (!TryParseManagedTrashEntryName(directoryName, out var entryIdentity))
        {
            throw new InvalidDataException("永久清理状态所在目录的身份无效。");
        }

        return ReadPurgeMarker(directoryPath, entryIdentity) is not null;
    }

    private static PurgeMarker? ReadPurgeMarker(
        string directoryPath,
        TrashEntryNameIdentity entryIdentity)
    {
        var markerPath = Path.Combine(directoryPath, PurgeMarkerFileName);
        if (!TryGetExistingPathAttributes(markerPath, out var attributes))
        {
            return null;
        }

        if (attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "永久清理状态必须是普通文件，不能是目录、符号链接或联接点。");
        }

        var markerJson = ReadPhysicalStateFile(
            markerPath,
            MaximumPurgeMarkerByteCount,
            "永久清理状态");
        PurgeMarker marker;
        try
        {
            marker = JsonSerializer.Deserialize<PurgeMarker>(
                         markerJson,
                         MetadataSerializerOptions) ??
                     throw new InvalidDataException("永久清理状态为空，已拒绝操作。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("永久清理状态损坏，已拒绝操作。", ex);
        }

        var metadataByteCount = string.IsNullOrWhiteSpace(marker.MetadataJson)
            ? 0
            : Encoding.UTF8.GetByteCount(marker.MetadataJson);
        if (marker.SchemaVersion != CurrentMetadataSchemaVersion ||
            marker.Avid != entryIdentity.Avid ||
            marker.EntryId == Guid.Empty ||
            marker.EntryId != entryIdentity.EntryId ||
            marker.StartedAtUtc == default ||
            metadataByteCount <= 0 ||
            metadataByteCount > MaximumMetadataByteCount)
        {
            throw new InvalidDataException(
                "永久清理状态与回收站目录身份不匹配或内容无效，已拒绝操作。");
        }

        ValidateTrashMetadataJson(marker.MetadataJson, entryIdentity, entryIdentity.Avid);
        return marker;
    }

    private static PurgeMarker EnsurePurgeMarker(string directoryPath)
    {
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath));
        if (!TryParseManagedTrashEntryName(directoryName, out var entryIdentity))
        {
            throw new InvalidDataException("无法为身份无效的回收站目录创建永久清理状态。");
        }

        var existingMarker = ReadPurgeMarker(directoryPath, entryIdentity);
        var metadataPath = Path.Combine(directoryPath, MetadataFileName);
        if (existingMarker is not null)
        {
            if (TryGetExistingPathAttributes(metadataPath, out _))
            {
                var currentMetadataJson = ReadMetadataJson(metadataPath);
                if (!string.Equals(
                        currentMetadataJson,
                        existingMarker.MetadataJson,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "永久清理状态中的元数据备份与当前元数据不一致，已拒绝操作。");
                }
            }

            return existingMarker;
        }

        if (!TryGetExistingPathAttributes(metadataPath, out _))
        {
            throw new UntrustedTrashEntryException(
                "回收站条目缺少元数据，无法开始永久清理。");
        }

        var metadataJson = ReadMetadataJson(metadataPath);
        ValidateTrashMetadataJson(metadataJson, entryIdentity, entryIdentity.Avid);
        var marker = new PurgeMarker(
            CurrentMetadataSchemaVersion,
            entryIdentity.Avid,
            entryIdentity.EntryId,
            DateTimeOffset.UtcNow,
            metadataJson);
        var markerJson = JsonSerializer.Serialize(marker, MetadataSerializerOptions);
        if (Encoding.UTF8.GetByteCount(markerJson) > MaximumPurgeMarkerByteCount)
        {
            throw new InvalidDataException("永久清理状态过大，已拒绝开始删除。");
        }

        WriteRawFileAtomically(directoryPath, PurgeMarkerFileName, markerJson);
        var persistedMarker = ReadPurgeMarker(directoryPath, entryIdentity);
        if (persistedMarker is null || persistedMarker != marker)
        {
            throw new IOException("永久清理状态未能可靠写入，已拒绝删除任何内容。");
        }

        return persistedMarker;
    }

    private static void EnsurePurgeMetadataFile(string directoryPath)
    {
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath));
        if (!TryParseManagedTrashEntryName(directoryName, out var entryIdentity))
        {
            throw new InvalidDataException("永久清理状态所在目录的身份无效。");
        }

        var marker = ReadPurgeMarker(directoryPath, entryIdentity) ??
                     throw new InvalidDataException("永久清理状态缺失，无法恢复清理元数据。");
        var metadataPath = Path.Combine(directoryPath, MetadataFileName);
        if (TryGetExistingPathAttributes(metadataPath, out _))
        {
            var currentMetadataJson = ReadMetadataJson(metadataPath);
            if (!string.Equals(
                    currentMetadataJson,
                    marker.MetadataJson,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "永久清理状态中的元数据备份与当前元数据不一致，已拒绝操作。");
            }

            return;
        }

        WriteRawMetadataAtomically(directoryPath, marker.MetadataJson);
        var restoredMetadataJson = ReadMetadataJson(metadataPath);
        if (!string.Equals(
                restoredMetadataJson,
                marker.MetadataJson,
                StringComparison.Ordinal))
        {
            throw new IOException("永久清理元数据未能从耐久状态可靠恢复。");
        }
    }

    private static void EnsureReservedPurgeMarkerAbsent(string directoryPath)
    {
        var markerPath = Path.Combine(directoryPath, PurgeMarkerFileName);
        if (TryGetExistingPathAttributes(markerPath, out _))
        {
            throw new IOException(
                $"原缓存目录包含应用保留的永久清理状态路径，已拒绝移动：{markerPath}");
        }
    }

    private static bool TryGetExistingPathAttributes(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static TrashMetadata? ReadStagedMetadataForMove(string originalPath, long avid)
    {
        var metadataPath = Path.Combine(originalPath, MetadataFileName);
        if (!File.Exists(metadataPath))
        {
            if (Directory.Exists(metadataPath))
            {
                throw new InvalidDataException("保留的回收站元数据路径不是普通文件，已拒绝移动。");
            }

            return null;
        }

        var metadataJson = ReadMetadataJson(metadataPath);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(metadataJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("原缓存目录中存在损坏的保留元数据，已拒绝覆盖。", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("原缓存目录中的保留元数据不是有效对象。");
            }

            if (!document.RootElement.TryGetProperty("SchemaVersion", out var schemaElement))
            {
                LegacyTrashMetadata legacyMetadata;
                try
                {
                    legacyMetadata = JsonSerializer.Deserialize<LegacyTrashMetadata>(
                                         metadataJson,
                                         MetadataSerializerOptions) ??
                                     throw new InvalidDataException("旧版保留元数据为空。");
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("旧版保留元数据损坏，已拒绝覆盖。", ex);
                }

                string legacyLeaf;
                try
                {
                    legacyLeaf = Path.GetFileName(
                        Path.TrimEndingDirectorySeparator(
                            Path.GetFullPath(legacyMetadata.OriginalPath)));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
                {
                    throw new InvalidDataException("旧版保留元数据的原始路径无效。", ex);
                }

                if (legacyMetadata.Avid != avid ||
                    legacyMetadata.DeletedAt == default ||
                    !string.Equals(
                        legacyLeaf,
                        avid.ToString(CultureInfo.InvariantCulture),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("旧版保留元数据与当前缓存目录不匹配。");
                }

                DeleteMetadataFile(originalPath);
                return null;
            }

            if (!schemaElement.TryGetInt32(out var schemaVersion) ||
                schemaVersion != CurrentMetadataSchemaVersion)
            {
                throw new InvalidDataException(
                    $"原缓存目录包含不支持的保留元数据版本：{schemaElement.GetRawText()}。");
            }

            TrashMetadata metadata;
            try
            {
                metadata = JsonSerializer.Deserialize<TrashMetadata>(
                               metadataJson,
                               MetadataSerializerOptions) ??
                           throw new InvalidDataException("保留元数据为空。");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("保留元数据损坏，已拒绝移动。", ex);
            }

            if (metadata.SchemaVersion != CurrentMetadataSchemaVersion ||
                metadata.Avid != avid ||
                !string.Equals(
                    metadata.OriginalRelativePath,
                    avid.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal) ||
                metadata.DeletedAtUtc == default ||
                metadata.EntryId == Guid.Empty)
            {
                throw new InvalidDataException("保留元数据与当前缓存目录不匹配，已拒绝移动。");
            }

            return metadata;
        }
    }

    private static void ValidateLegacyMetadata(
        string metadataJson,
        TrashEntryNameIdentity entryIdentity,
        long avid)
    {
        if (entryIdentity.SchemaVersion != 0)
        {
            throw new InvalidDataException("新版回收站目录缺少版本化元数据，已拒绝操作。");
        }

        LegacyTrashMetadata metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<LegacyTrashMetadata>(
                           metadataJson,
                           MetadataSerializerOptions) ??
                       throw new InvalidDataException("旧版回收站元数据为空，已拒绝操作。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("旧版回收站元数据损坏，已拒绝操作。", ex);
        }

        string normalizedMetadataPath;
        try
        {
            normalizedMetadataPath = Path.GetFullPath(metadata.OriginalPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            throw new InvalidDataException("旧版回收站原始路径无效，已拒绝操作。", ex);
        }

        var metadataOriginalLeaf = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(normalizedMetadataPath));
        var directoryDeletedAt = new DateTimeOffset(
            DateTime.SpecifyKind(entryIdentity.DeletedAtUtc, DateTimeKind.Utc));
        if (metadata.Avid != avid ||
            string.IsNullOrWhiteSpace(metadata.OriginalPath) ||
            !string.Equals(
                metadataOriginalLeaf,
                avid.ToString(CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase) ||
            metadata.DeletedAt == default ||
            (metadata.DeletedAt.ToUniversalTime() - directoryDeletedAt).Duration() >
            LegacyTimestampTolerance)
        {
            throw new InvalidDataException("旧版回收站元数据与目录身份不匹配，已拒绝操作。");
        }
    }

    private static void DeleteMetadataFile(string directoryPath)
    {
        var path = Path.Combine(directoryPath, MetadataFileName);
        if (!File.Exists(path))
        {
            if (Directory.Exists(path))
            {
                throw new InvalidDataException("回收站元数据路径不是普通文件。");
            }

            return;
        }

        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("回收站元数据不是可安全删除的普通文件。");
        }

        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        File.Delete(path);
    }

    private string? TryDeleteMetadata(string directoryPath)
    {
        try
        {
            BeforeRestoredMetadataDeleteForTesting?.Invoke(directoryPath);
            DeleteMetadataFile(directoryPath);
            return null;
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                System.Security.SecurityException)
        {
            return $"缓存内容已恢复，但保留元数据未能删除：{ex.Message}";
        }
    }

    private sealed record TrashMetadata(
        int SchemaVersion,
        long Avid,
        string OriginalRelativePath,
        DateTimeOffset DeletedAtUtc,
        Guid EntryId);

    private sealed record LegacyTrashMetadata(
        long Avid,
        string OriginalPath,
        DateTimeOffset DeletedAt);

    private sealed record PurgeMarker(
        int SchemaVersion,
        long Avid,
        Guid EntryId,
        DateTimeOffset StartedAtUtc,
        string MetadataJson);

    private sealed class UntrustedTrashEntryException(string message) : IOException(message);
}
