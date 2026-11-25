using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using Microsoft.Win32.SafeHandles;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

public sealed partial class FileSystemCacheTrashService
{
    private const int MaximumEntryMetadataBytes = 4 * 1024 * 1024;

    internal static (byte[] Bytes, string? DirectoryIdentity, string? Error) ReadEntryForScan(
        string root, string parent, string directory, string entryPath, bool captureIdentity, CancellationToken token)
    {
        SafeFileHandle? parentLease = null;
        SafeFileHandle? directoryLease = null;
        FileStream? entry = null;
        try
        {
            string? identity = null;
            string? error = null;
            if (captureIdentity)
            {
                try
                {
                    EnsurePhysicalDirectory(root, "The cache root directory");
                    EnsureDirectChild(root, parent);
                    EnsureDirectChild(parent, directory);
                    if (!OperatingSystem.IsWindows())
                    {
                        EnsurePhysicalDirectory(parent, "The parent video directory");
                        EnsurePhysicalDirectory(directory, "The selected part directory");
                    }
                    parentLease = LeasePartDirectory(parent);
                    directoryLease = LeasePartDirectory(directory);
                    identity = GetPartDirectoryIdentity(directory, directoryLease);
                    entry = OpenPartMetadata(entryPath);
                }
                catch (FileNotFoundException) { throw; }
                catch (Exception ex) when (IsPurgeFailure(ex))
                {
                    // A readable entry can still be displayed if its deletion identity cannot be bound.
                    identity = null;
                    error = ex.Message;
                    if (!IsPhysicalScanDirectory(root) || !IsPhysicalScanDirectory(parent) || !IsPhysicalScanDirectory(directory))
                        throw new InvalidDataException("The metadata is not inside physical cache directories.", ex);
                }
            }
            entry ??= OpenPartMetadata(entryPath, requireDeleteAccess: false);
            var bytes = ReadEntryBytes(entry, token);
            if (identity is not null && directoryLease is null)
            {
                // Unix permits renames during the read. Never bind bytes from a replaced directory.
                try
                {
                    if (identity != GetPartDirectoryIdentity(directory, null))
                        throw new InvalidDataException("The selected part directory changed while indexing.");
                }
                catch (Exception ex) when (IsPurgeFailure(ex))
                {
                    identity = null;
                    error = ex.Message;
                }
            }
            return (bytes, identity, error);
        }
        finally
        {
            entry?.Dispose();
            directoryLease?.Dispose();
            parentLease?.Dispose();
        }
    }

    private static bool IsPhysicalScanDirectory(string path) =>
        (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;

    internal static CacheScanPartIdentity BindScannedPart(BiliSegment segment, byte[] bytes, string directoryIdentity)
    {
        try
        {
            if (Path.GetFileName(Path.GetDirectoryName(segment.SegmentDirectory)) != segment.Avid.ToString(CultureInfo.InvariantCulture) ||
                !string.Equals(Path.GetFullPath(segment.EntryJsonPath), Path.Combine(segment.SegmentDirectory, "entry.json"),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("The indexed metadata is outside its canonical part location.");
            var hash = HashPartEntry(bytes, segment.Avid, segment.PageIndex, segment.Cid);
            return new(new(segment.Avid, segment.PageIndex, segment.SegmentDirectory, hash, directoryIdentity), null);
        }
        catch (Exception ex) when (IsPurgeFailure(ex) || ex is JsonException)
        {
            return new(null, ex.Message);
        }
    }

    private static byte[] ReadEntryBytes(FileStream stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!stream.CanSeek) throw new InvalidDataException("Part metadata must be a regular file.");
        var length = stream.Length;
        if (length > MaximumEntryMetadataBytes)
            throw new InvalidDataException($"entry.json exceeds the {MaximumEntryMetadataBytes}-byte metadata limit.");
        stream.Position = 0;
        // A FileStream constructed from a Unix fd can track Position independently of the native read offset.
        // Rewind the fd as well when moving a part: both checks must read the actual bytes through the same lease.
        if (OperatingSystem.IsLinux() && SeekLinuxFile(stream.SafeFileHandle, 0, 0) < 0)
            throw new IOException($"The part metadata could not be rewound: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        var bytes = new byte[(int)length];
        for (var offset = 0; offset < bytes.Length;)
        {
            token.ThrowIfCancellationRequested();
            var count = stream.Read(bytes.AsSpan(offset, Math.Min(bytes.Length - offset, 64 * 1024)));
            if (count == 0) throw new EndOfStreamException("Part metadata changed while reading.");
            offset += count;
        }
        if (stream.ReadByte() != -1) throw new InvalidDataException("Part metadata changed while reading.");
        return bytes;
    }

    private static FileStream OpenPartMetadata(string path, bool requireDeleteAccess = true)
    {
        if (!OperatingSystem.IsLinux())
        {
            try { return OpenStateFileLease(path, "The part metadata", requireDeleteAccess); }
            catch (IOException)
            {
                // The native lease reports IOException for all open failures. Preserve the scan's missing-file behavior.
                _ = File.GetAttributes(path);
                throw;
            }
        }
        // Linux open(2): enforce O_NOFOLLOW at open and avoid blocking on a FIFO before it can be rejected.
        const int closeOnExec = 0x80000, nonBlocking = 0x800;
        var noFollow = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64 or Architecture.Ppc64le
            ? 0x8000 : 0x20000;
        var fd = OpenLinuxFile(path, closeOnExec | noFollow | nonBlocking);
        if (fd == -1)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == 2) throw new FileNotFoundException("Part metadata not found.", path);
            throw new IOException($"The part metadata could not be opened safely: {new Win32Exception(error).Message}");
        }
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        try
        {
            // Match FileStream(FileShare.Read) on Linux: respect an exclusive lock, including during move revalidation.
            const int sharedNonBlocking = 1 | 4;
            if (LockLinuxFile(handle, sharedNonBlocking) != 0 && Marshal.GetLastPInvokeError() == 11)
                throw new IOException("The part metadata is exclusively locked.");
            return new FileStream(handle, FileAccess.Read, bufferSize: 1);
        }
        catch { handle.Dispose(); throw; }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenLinuxFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int LockLinuxFile(SafeFileHandle handle, int operation);

    [DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
    private static extern nint SeekLinuxFile(SafeFileHandle handle, nint offset, int origin);
}
