using System.Globalization;
using BiliBiliLocalCacheManager.Core.Application.Models;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

public sealed partial class FileSystemCacheTrashService
{
    public IReadOnlyList<CacheTrashEntry> ListEntries(
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
            return Array.Empty<CacheTrashEntry>();
        }

        EnsurePhysicalDirectory(trashRoot, "The application trash directory");
        using var trashRootLease = OperatingSystem.IsWindows()
            ? OpenPhysicalDirectoryLease(
                trashRoot,
                "The application trash directory",
                allowDelete: false)
            : null;

        var entries = new List<CacheTrashEntry>();

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
                // 目录名不符合本应用格式，不属于受管条目，直接跳过。
                continue;
            }

            var originalPath = Path.Combine(
                root,
                entryIdentity.Avid.ToString(CultureInfo.InvariantCulture));
            var fullPath = Path.GetFullPath(path);
            var deletedAt = new DateTimeOffset(entryIdentity.DeletedAtUtc, TimeSpan.Zero);

            try
            {
                EnsureDirectChild(trashRoot, path);
                var attributes = File.GetAttributes(path);
                if (!attributes.HasFlag(FileAttributes.Directory) ||
                    attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                EnsurePhysicalDirectory(path, "The managed trash entry");
                using var entryLease = OperatingSystem.IsWindows()
                    ? OpenPhysicalDirectoryLease(
                        path,
                        "The managed trash entry",
                        allowDelete: false)
                    : null;

                var pendingPurge = ReadPurgeJournalState(trashRoot, path) is not null ||
                    HasPurgeMarker(path);
                if (!pendingPurge)
                {
                    EnsureTrashIdentity(
                        path,
                        entryIdentity.Avid,
                        originalPath,
                        allowPendingPurge: true);
                }

                var inspection = InspectDirectoryTree(path, cancellationToken);
                var blockedReason = pendingPurge
                    ? "已进入永久清理，无法还原。"
                    : Directory.Exists(originalPath)
                        ? "原始位置已存在同名目录。"
                        : null;

                entries.Add(new CacheTrashEntry(
                    entryIdentity.Avid,
                    fullPath,
                    originalPath,
                    deletedAt,
                    inspection.FileCount,
                    inspection.TotalBytes,
                    blockedReason is null,
                    blockedReason));
            }
            catch (UntrustedTrashEntryException ex)
            {
                entries.Add(new CacheTrashEntry(
                    entryIdentity.Avid,
                    fullPath,
                    originalPath,
                    deletedAt,
                    0,
                    0,
                    false,
                    $"身份元数据校验失败：{ex.Message}"));
            }
            catch (Exception ex) when (IsPurgeFailure(ex))
            {
                entries.Add(new CacheTrashEntry(
                    entryIdentity.Avid,
                    fullPath,
                    originalPath,
                    deletedAt,
                    0,
                    0,
                    false,
                    ex.Message));
            }
        }

        return entries
            .OrderByDescending(entry => entry.DeletedAtUtc)
            .ThenBy(entry => entry.Avid)
            .ToList();
    }
}
