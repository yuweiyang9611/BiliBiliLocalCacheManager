using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using Microsoft.Win32.SafeHandles;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

public sealed partial class FileSystemCacheTrashService
{
    private const int MaximumEntryMetadataBytes = 4 * 1024 * 1024;

    // Reuse the Windows AV directory handle across its parts. Concurrent changes to the
    // directory tree still need to be detected; individual part leases remain short lived.
    internal static SafeFileHandle? LeaseScanParent(string root, string parent, bool captureIdentity, out string? error)
    {
        error = null;
        if (!captureIdentity || !OperatingSystem.IsWindows()) return null;
        try
        {
            EnsureDirectChild(root, parent);
            var lease = LeasePartDirectory(parent);
            try
            {
                EnsurePhysicalDirectory(root, "The cache root directory");
                return lease;
            }
            catch { lease?.Dispose(); throw; }
        }
        catch (Exception ex) when (IsPurgeFailure(ex))
        {
            error = ex.Message;
            return null;
        }
    }

    internal sealed class ScannedPartMetadata(ArraySegment<byte> bytes, SafeFileHandle? directoryLease, string? unixIdentity, string? error)
        : IDisposable
    {
        public string ReadText()
        {
            // The common UTF-8 object needs only its decoded string. Preserve StreamReader's BOM detection otherwise.
            if (bytes.Count > 0 && bytes[0] == (byte)'{') return Encoding.UTF8.GetString(bytes.AsSpan());
            using var reader = new StreamReader(new MemoryStream(bytes.Array!, bytes.Offset, bytes.Count, writable: false));
            return reader.ReadToEnd();
        }

        public CacheScanPartIdentity Bind(BiliSegment segment)
        {
            if (error is not null) return new(null, error);
            try
            {
                if (Path.GetFileName(Path.GetDirectoryName(segment.SegmentDirectory)) != segment.Avid.ToString(CultureInfo.InvariantCulture) ||
                    !string.Equals(Path.GetFullPath(segment.EntryJsonPath), Path.Combine(segment.SegmentDirectory, "entry.json"),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidDataException("The indexed metadata is outside its canonical part location.");
                var identity = GetPartDirectoryIdentity(segment.SegmentDirectory, directoryLease);
                if (directoryLease is null && unixIdentity != identity)
                    throw new InvalidDataException("The selected part directory changed while indexing.");
                var hash = HashPartEntry(bytes, segment.Avid, segment.PageIndex, segment.Cid);
                return new(new(segment.Avid, segment.PageIndex, segment.SegmentDirectory, hash, identity), null);
            }
            catch (Exception ex) when (IsPurgeFailure(ex) || ex is JsonException)
            {
                return new(null, ex.Message);
            }
        }

        public void Dispose() => directoryLease?.Dispose();
    }

    internal static ScannedPartMetadata ReadEntryForScan(
        string root, string parent, string directory, string entryPath, bool captureIdentity, string? parentError,
        byte[] smallEntryBuffer, CancellationToken token)
    {
        SafeFileHandle? directoryLease = null;
        FileStream? entry = null;
        try
        {
            string? unixIdentity = null;
            var error = parentError;
            if (captureIdentity && error is null)
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                        directoryLease = LeasePartDirectory(directory);
                    else
                    {
                        EnsurePhysicalDirectory(root, "The cache root directory");
                        EnsurePhysicalDirectory(parent, "The parent video directory");
                        unixIdentity = GetPartDirectoryIdentity(directory, null);
                    }
                    entry = OpenPartMetadata(entryPath, bufferSize: 1);
                }
                catch (FileNotFoundException) { throw; }
                catch (Exception ex) when (IsPurgeFailure(ex))
                {
                    // A readable entry can still be displayed if its deletion identity cannot be bound.
                    error = ex.Message;
                }
            }
            if (error is not null && (!IsPhysicalScanDirectory(root) || !IsPhysicalScanDirectory(parent) || !IsPhysicalScanDirectory(directory)))
                throw new InvalidDataException("The metadata is not inside physical cache directories.");
            entry ??= OpenPartMetadata(entryPath, requireDeleteAccess: false, bufferSize: 1);
            return new(ReadEntryBytes(entry, token, smallEntryBuffer), directoryLease, unixIdentity, error);
        }
        catch
        {
            directoryLease?.Dispose();
            throw;
        }
        finally { entry?.Dispose(); }
    }

    private static bool IsPhysicalScanDirectory(string path) =>
        (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;

    private static ArraySegment<byte> ReadEntryBytes(FileStream stream, CancellationToken token, byte[]? smallEntryBuffer = null)
    {
        token.ThrowIfCancellationRequested();
        if (!stream.CanSeek) throw new InvalidDataException("Part metadata must be a regular file.");
        var offset = 0;
        if (smallEntryBuffer is not null)
        {
            // A newly opened file starts at zero. Most entries fit in this scan-local buffer, so read to EOF
            // without querying length, allocating a per-entry I/O buffer, or seeking the handle.
            while (offset < smallEntryBuffer.Length)
            {
                var count = stream.Read(smallEntryBuffer.AsSpan(offset));
                if (count == 0) return new(smallEntryBuffer, 0, offset);
                offset += count;
            }
        }
        else
        {
            stream.Position = 0;
            // Revalidation rereads the same lease; an externally opened Unix fd also needs its native offset reset.
            if (OperatingSystem.IsLinux() && SeekLinuxFile(stream.SafeFileHandle, 0, 0) < 0)
                throw new IOException($"The part metadata could not be rewound: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }
        var length = stream.Length;
        if (length > MaximumEntryMetadataBytes)
            throw new InvalidDataException($"entry.json exceeds the {MaximumEntryMetadataBytes}-byte metadata limit.");
        if (length < offset) throw new EndOfStreamException("Part metadata changed while reading.");
        var bytes = new byte[(int)length];
        if (offset != 0) smallEntryBuffer!.AsSpan(0, offset).CopyTo(bytes);
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = stream.Read(bytes.AsSpan(offset, Math.Min(bytes.Length - offset, 64 * 1024)));
            if (count == 0) throw new EndOfStreamException("Part metadata changed while reading.");
            offset += count;
        }
        if (stream.ReadByte() != -1) throw new InvalidDataException("Part metadata changed while reading.");
        return bytes;
    }

    private static FileStream OpenPartMetadata(string path, bool requireDeleteAccess = true, int bufferSize = 4096)
    {
        if (!OperatingSystem.IsLinux())
        {
            try { return OpenStateFileLease(path, "The part metadata", requireDeleteAccess, bufferSize); }
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
