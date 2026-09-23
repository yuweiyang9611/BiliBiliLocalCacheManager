using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using Microsoft.Win32.SafeHandles;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

public sealed partial class FileSystemCacheTrashService
{
    private const int PartMetadataSchemaVersion = 2;

    public CacheTrashPartTarget CapturePartTarget(string rootDirectory, BiliSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segment.Avid);
        ArgumentOutOfRangeException.ThrowIfNegative(segment.PageIndex);
        var root = Path.GetFullPath(rootDirectory);
        ValidateRoot(root);
        var parent = Path.Combine(root, segment.Avid.ToString(CultureInfo.InvariantCulture));
        var path = Path.GetFullPath(segment.SegmentDirectory);
        EnsureDirectChild(root, parent);
        EnsureDirectChild(parent, path);
        EnsurePhysicalDirectory(parent, "The parent video directory");
        EnsurePhysicalDirectory(path, "The selected part directory");
        using var parentLease = LeasePartDirectory(parent);
        using var lease = LeasePartDirectory(path);
        if (!string.Equals(Path.GetFullPath(segment.EntryJsonPath), Path.Combine(path, "entry.json"),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The indexed entry is not in the selected part directory.");
        using var entry = OpenStateFileLease(segment.EntryJsonPath, "The indexed part metadata");
        var hash = HashPartEntry(entry, segment.Avid, segment.PageIndex, segment.Cid);
        return new CacheTrashPartTarget(segment.Avid, segment.PageIndex, path, hash,
            GetPartDirectoryIdentity(path, lease));
    }

    public CacheTrashOperationResult MovePartToTrash(string rootDirectory, CacheTrashPartTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var root = Path.GetFullPath(rootDirectory);
        using var transaction = EnterMutationTransaction(root, CacheTrashMutationOperation.Move);
        ValidateRoot(root);
        var path = Path.GetFullPath(target.SourcePath);
        var parent = Path.Combine(root, target.Avid.ToString(CultureInfo.InvariantCulture));
        string? trashPath = null;
        try
        {
            EnsureDirectChild(root, parent);
            EnsureDirectChild(parent, path);
            EnsurePhysicalDirectory(parent, "The parent video directory");
            EnsurePhysicalDirectory(path, "The selected part directory");
            using var rootLease = LeasePartDirectory(root);
            using var parentLease = LeasePartDirectory(parent);
            using var lease = LeasePartDirectory(path, allowDelete: true);
            EnsurePartTargetIdentity(path, lease, target);
            // Keep entry contents stable through validation; Windows requires closing child files before a directory rename.
            using var entryLease = OpenStateFileLease(Path.Combine(path, "entry.json"), "The selected part metadata");
            if (HashPartEntry(entryLease, target.Avid, target.PageIndex) != target.EntryHash)
                throw new InvalidDataException("The selected part changed after indexing; scan again.");

            var trashRoot = CacheStorageLayout.GetTrashDirectory(root);
            EnsureDirectChild(root, trashRoot);
            Directory.CreateDirectory(trashRoot);
            EnsurePhysicalDirectory(trashRoot, "The application trash directory");
            using var trashRootLease = LeasePartDirectory(trashRoot);
            EnsureReservedPurgeMarkerAbsent(path);
            var relativePath = $"{target.Avid.ToString(CultureInfo.InvariantCulture)}/{Path.GetFileName(path)}";
            var metadataPath = Path.Combine(path, MetadataFileName);
            PartTrashMetadata metadata;
            if (TryGetExistingPathAttributes(metadataPath, out _))
            {
                metadata = JsonSerializer.Deserialize<PartTrashMetadata>(ReadMetadataJson(metadataPath))
                    ?? throw new InvalidDataException("Reserved part metadata is invalid.");
                if (metadata.SchemaVersion != PartMetadataSchemaVersion || metadata.Avid != target.Avid ||
                    metadata.PageIndex != target.PageIndex || metadata.OriginalRelativePath != relativePath ||
                    metadata.EntryId == Guid.Empty || metadata.DeletedAtUtc == default)
                    throw new InvalidDataException("Reserved part metadata does not match the selected part.");
            }
            else
            {
                metadata = new(PartMetadataSchemaVersion, target.Avid, target.PageIndex, relativePath,
                    DateTimeOffset.UtcNow, Guid.NewGuid());
                BeforeTrashMetadataWriteForTesting?.Invoke(path);
                WriteStateFileAtomically(path, MetadataFileName,
                    stream => JsonSerializer.Serialize(stream, metadata, MetadataSerializerOptions));
            }
            trashPath = Path.Combine(trashRoot, $"v2_{target.Avid}_{metadata.DeletedAtUtc.UtcDateTime.ToString(TrashEntryTimestampFormat, CultureInfo.InvariantCulture)}_{metadata.EntryId:N}_{PartRestoreIdentityHash(metadata)}");
            EnsureDirectChild(trashRoot, trashPath);
            if (File.Exists(trashPath) || Directory.Exists(trashPath)) throw new IOException("The trash entry already exists.");
            BeforeMoveRenameForTesting?.Invoke(path);
            EnsurePhysicalDirectory(parent, "The parent video directory");
            EnsurePhysicalDirectory(trashRoot, "The application trash directory");
            EnsurePartTargetIdentity(path, lease, target);
            if (ReadPartMetadata(path) != metadata) throw new InvalidDataException("Part metadata changed before moving.");
            if (HashPartEntry(entryLease, target.Avid, target.PageIndex) != target.EntryHash)
                throw new InvalidDataException("Part metadata changed before moving.");
            entryLease.Dispose();
            if (OperatingSystem.IsWindows()) RenamePhysicalDirectoryByHandle(lease!, trashPath, "The selected part directory");
            else Directory.Move(path, trashPath);
            return new(target.Avid, true, true, path, trashPath, null);
        }
        catch (Exception exception) when (IsPurgeFailure(exception) || exception is JsonException)
        {
            return new(target.Avid, Directory.Exists(path), false, path, trashPath, exception.Message);
        }
    }

    private static SafeFileHandle? LeasePartDirectory(string path, bool allowDelete = false)
        => OperatingSystem.IsWindows() ? OpenPhysicalDirectoryLease(path, "The selected cache directory", allowDelete) : null;

    private static string GetPartDirectoryIdentity(string path, SafeFileHandle? lease)
        => lease is null ? ReadUnixPartDirectoryIdentity(path)
            : GetPhysicalDirectoryIdentity(lease, "The selected part directory").ToString();

    private static void EnsurePartTargetIdentity(string path, SafeFileHandle? lease, CacheTrashPartTarget target)
    {
        EnsurePhysicalDirectory(path, "The selected part directory");
        if (GetPartDirectoryIdentity(path, lease) != target.DirectoryIdentity)
            throw new InvalidDataException("The selected part directory was replaced after indexing; scan again.");
    }

    private static string HashPartEntry(FileStream stream, long avid, int pageIndex, long? cid = null)
    {
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Part metadata is too large.");
        stream.Position = 0;
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (!root.TryGetProperty("avid", out var avidValue) || !avidValue.TryGetInt64(out var actualAvid) || actualAvid != avid ||
            !root.TryGetProperty("page_data", out var page) ||
            !page.TryGetProperty("page", out var number) || !number.TryGetInt32(out var actualPage) || actualPage != pageIndex ||
            (cid is { } expectedCid && (!page.TryGetProperty("cid", out var cidValue) || !cidValue.TryGetInt64(out var actualCid) || actualCid != expectedCid)))
            throw new InvalidDataException("Part metadata no longer matches the indexed selection.");
        stream.Position = 0;
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static PartTrashMetadata? ReadPartMetadata(string directory)
    {
        using var document = JsonDocument.Parse(ReadMetadataJson(Path.Combine(directory, MetadataFileName)));
        return document.RootElement.TryGetProperty("SchemaVersion", out var version) && version.TryGetInt32(out var schema) && schema == PartMetadataSchemaVersion
            ? document.RootElement.Deserialize<PartTrashMetadata>() : null;
    }

    private static void ValidatePartMetadata(JsonElement element, TrashEntryNameIdentity identity)
    {
        if (!element.TryGetProperty("PageIndex", out var pageIndex) || pageIndex.ValueKind != JsonValueKind.Number ||
            !pageIndex.TryGetInt32(out _))
            throw new InvalidDataException("Part metadata is missing a valid page index.");
        PartTrashMetadata metadata;
        try { metadata = element.Deserialize<PartTrashMetadata>() ?? throw new InvalidDataException("Part metadata is empty."); }
        catch (JsonException exception) { throw new InvalidDataException("Part metadata has invalid field types.", exception); }
        if (metadata.SchemaVersion != PartMetadataSchemaVersion || metadata.Avid != identity.Avid || metadata.PageIndex < 0 ||
            metadata.EntryId != identity.EntryId || metadata.EntryId == Guid.Empty || metadata.DeletedAtUtc == default ||
            metadata.DeletedAtUtc.UtcDateTime.ToString(TrashEntryTimestampFormat, CultureInfo.InvariantCulture) != identity.TimestampToken ||
            PartRestoreIdentityHash(metadata) != identity.PartIdentityHash)
            throw new InvalidDataException("Part metadata does not match its trash identity.");
        ValidatePartRelativePath(metadata);
    }

    private static string PartRestoreIdentityHash(PartTrashMetadata metadata)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            metadata.Avid, metadata.PageIndex, metadata.OriginalRelativePath
        })));

    private static void ValidatePartRelativePath(PartTrashMetadata metadata)
    {
        var pieces = metadata.OriginalRelativePath?.Split('/');
        if (pieces is not { Length: 2 } || pieces[0] != metadata.Avid.ToString(CultureInfo.InvariantCulture) ||
            string.IsNullOrWhiteSpace(pieces[1]) || pieces[1] is "." or ".." || pieces[1].IndexOfAny(['\\', ':', '\0']) >= 0 ||
            pieces[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || pieces[1].EndsWith('.') || pieces[1].EndsWith(' '))
            throw new InvalidDataException("Part metadata contains an unsafe original path.");
    }

    private static string GetPartRestorePath(string root, PartTrashMetadata metadata)
    {
        ValidatePartRelativePath(metadata);
        var parent = Path.Combine(root, metadata.Avid.ToString(CultureInfo.InvariantCulture));
        var path = Path.Combine(parent, metadata.OriginalRelativePath.Split('/')[1]);
        EnsureDirectChild(root, parent);
        EnsureDirectChild(parent, path);
        return path;
    }

    private CacheTrashOperationResult RestorePart(string root, string trashRoot, string trashPath, PartTrashMetadata metadata)
    {
        var destination = GetPartRestorePath(root, metadata);
        try
        {
            using var rootLease = LeasePartDirectory(root);
            using var trashRootLease = LeasePartDirectory(trashRoot);
            using var entryLease = LeasePartDirectory(trashPath, allowDelete: true);
            var entryIdentity = GetPartDirectoryIdentity(trashPath, entryLease);
            var parent = Path.GetDirectoryName(destination)!;
            if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
            EnsurePhysicalDirectory(parent, "The parent video directory");
            using var parentLease = LeasePartDirectory(parent);
            if (Directory.Exists(destination) || File.Exists(destination))
                return new(metadata.Avid, true, false, destination, trashPath, "该分 P 的原始位置已存在内容，无法覆盖恢复。");
            EnsureRestoreIsNotPendingPurge(trashRoot, trashPath);
            EnsureTrashIdentity(trashPath, metadata.Avid, destination);
            BeforeRestoreRenameForTesting?.Invoke(trashPath);
            ValidateRoot(root);
            EnsurePhysicalDirectory(trashRoot, "The application trash directory");
            EnsurePhysicalDirectory(parent, "The parent video directory");
            EnsurePhysicalDirectory(trashPath, "The managed part entry");
            if (GetPartDirectoryIdentity(trashPath, entryLease) != entryIdentity)
                throw new InvalidDataException("The managed part entry changed before restoration.");
            if (ReadPartMetadata(trashPath) != metadata) throw new InvalidDataException("Part metadata changed before restoration.");
            if (OperatingSystem.IsWindows()) RenamePhysicalDirectoryByHandle(entryLease!, destination, "The managed part entry");
            else Directory.Move(trashPath, destination);
            var warning = OperatingSystem.IsWindows() ? TryDeleteRestoredMetadata(destination) : TryDeleteMetadata(destination);
            return new(metadata.Avid, true, true, destination, trashPath, warning);
        }
        catch (Exception exception) when (IsPurgeFailure(exception))
        {
            return new(metadata.Avid, true, false, destination, trashPath, exception.Message);
        }
    }

    private sealed record PartTrashMetadata(int SchemaVersion, long Avid, int PageIndex,
        string OriginalRelativePath, DateTimeOffset DeletedAtUtc, Guid EntryId);
}
