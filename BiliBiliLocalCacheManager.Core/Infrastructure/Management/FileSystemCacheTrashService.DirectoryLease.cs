using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

public sealed partial class FileSystemCacheTrashService
{
    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint OpenExisting = 3;
    private const int FileStandardInfoClass = 1;
    private const int FileDispositionInfoExClass = 21;
    private const int FileRenameInfoClass = 3;
    private const int FileAttributeTagInfoClass = 9;
    private const int FileIdInfoClass = 18;
    private const uint FileDispositionDelete = 0x00000001;
    private const uint FileDispositionPosixSemantics = 0x00000002;
    private const uint FileDispositionIgnoreReadOnlyAttribute = 0x00000010;

    private static SafeFileHandle OpenPhysicalDirectoryLease(
        string directoryPath,
        string description,
        bool allowDelete)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Race-safe permanent trash deletion is currently supported only on Windows.");
        }

        var desiredAccess = FileReadAttributes | (allowDelete ? DeleteAccess : 0);
        var handle = OpenWindowsHandle(
            directoryPath,
            desiredAccess,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            description);

        try
        {
            var attributes = GetHandleAttributes(handle, description);
            if (!attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    $"{description} must be a locked physical directory, not a symbolic link or directory junction.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenPhysicalFileLease(
        string filePath,
        string description)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Race-safe trash file handling is currently supported only on Windows.");
        }

        var handle = OpenWindowsHandle(
            filePath,
            DeleteAccess | FileReadAttributes,
            FileFlagOpenReparsePoint,
            description);
        try
        {
            var attributes = GetHandleAttributes(handle, description);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    $"{description} must be a locked physical file, not a directory or reparse point.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static PhysicalDirectoryIdentity GetPhysicalDirectoryIdentity(
        SafeFileHandle handle,
        string description)
    {
        if (!GetFileIdInformation(
                handle,
                FileIdInfoClass,
                out var information,
                (uint)Marshal.SizeOf<FileIdInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"{description} identity could not be inspected safely: {new Win32Exception(error).Message}");
        }
        if (information.FileIdLow == 0 && information.FileIdHigh == 0)
        {
            throw new PlatformNotSupportedException(
                $"{description} is on a file system that does not expose a stable 128-bit file identity.");
        }


        return new PhysicalDirectoryIdentity(
            information.VolumeSerialNumber,
            information.FileIdLow,
            information.FileIdHigh);
    }

    private static long DeletePhysicalFileByHandle(string filePath, string description)
    {
        using var handle = OpenPhysicalFileLease(filePath, description);
        var length = GetPhysicalFileLength(handle, description);
        MarkHandleForDeletion(handle, description);
        return length;
    }

    private static long GetPhysicalFileLength(SafeFileHandle handle, string description)
    {
        if (!GetFileStandardInformation(
                handle,
                FileStandardInfoClass,
                out var standardInformation,
                (uint)Marshal.SizeOf<FileStandardInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"{description} size could not be inspected safely: {new Win32Exception(error).Message}");
        }

        return Math.Max(0, standardInformation.EndOfFile);
    }

    private static void MarkHandleForDeletion(SafeFileHandle handle, string description)
    {
        var disposition = new FileDispositionInformationEx
        {
            Flags = FileDispositionDelete |
                    FileDispositionPosixSemantics |
                    FileDispositionIgnoreReadOnlyAttribute
        };
        if (!SetFileDispositionInformation(
                handle,
                FileDispositionInfoExClass,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformationEx>()))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"{description} could not be deleted safely: {new Win32Exception(error).Message}");
        }
    }

    private static void RenamePhysicalDirectoryByHandle(
        SafeFileHandle handle,
        string destinationPath,
        string description)
    {
        var normalizedDestinationPath = Path.GetFullPath(destinationPath);
        var fileNameBytes = Encoding.Unicode.GetBytes(normalizedDestinationPath);
        var rootDirectoryOffset = IntPtr.Size;
        var fileNameLengthOffset = rootDirectoryOffset + IntPtr.Size;
        var fileNameOffset = fileNameLengthOffset + sizeof(uint);
        var headerSize = IntPtr.Size == sizeof(long) ? 24 : 16;
        var bufferSize = checked(headerSize + fileNameBytes.Length + sizeof(char));
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
            Marshal.WriteInt32(buffer, 0, 0);
            Marshal.WriteIntPtr(buffer, rootDirectoryOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, fileNameLengthOffset, fileNameBytes.Length);
            Marshal.Copy(fileNameBytes, 0, IntPtr.Add(buffer, fileNameOffset), fileNameBytes.Length);

            if (!SetFileRenameInformation(
                    handle,
                    FileRenameInfoClass,
                    buffer,
                    (uint)bufferSize))
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException(
                    $"{description} could not be restored safely: {new Win32Exception(error).Message}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private string? TryDeleteRestoredMetadata(string directoryPath)
    {
        try
        {
            using var metadataLease = OpenPhysicalFileLease(
                Path.Combine(directoryPath, MetadataFileName),
                "Restored trash metadata");
            BeforeRestoredMetadataDeleteForTesting?.Invoke(directoryPath);
            MarkHandleForDeletion(metadataLease, "Restored trash metadata");
            return null;
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                InvalidOperationException or
                System.Security.SecurityException)
        {
            return $"缓存内容已恢复，但保留元数据未能删除：{ex.Message}";
        }
    }

    private static SafeFileHandle OpenWindowsHandle(
        string path,
        uint desiredAccess,
        uint flags,
        string description,
        FileShare shareMode = FileShare.Read | FileShare.Write)
    {
        var handle = CreateFileW(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException(
            $"{description} could not be opened safely: {new Win32Exception(error).Message}");
    }

    private static FileAttributes GetHandleAttributes(SafeFileHandle handle, string description)
    {
        if (GetFileAttributeTagInformation(
                handle,
                FileAttributeTagInfoClass,
                out var information,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            return (FileAttributes)information.FileAttributes;
        }

        var error = Marshal.GetLastWin32Error();
        throw new IOException(
            $"{description} could not be inspected safely: {new Win32Exception(error).Message}");
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileAttributeTagInformation(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileIdInformation(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileIdInformation fileInformation,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileStandardInformation(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileStandardInformation fileInformation,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "SetFileInformationByHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileDispositionInformation(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        ref FileDispositionInformationEx fileInformation,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "SetFileInformationByHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileRenameInformation(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;

        [MarshalAs(UnmanagedType.U1)]
        public bool DeletePending;

        [MarshalAs(UnmanagedType.U1)]
        public bool Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformationEx
    {
        public uint Flags;
    }

    private sealed record PhysicalDirectoryIdentity(
        ulong VolumeSerialNumber,
        ulong FileIdLow,
        ulong FileIdHigh);
}
