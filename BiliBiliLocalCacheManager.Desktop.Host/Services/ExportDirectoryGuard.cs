using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BiliBiliLocalCacheManager.Desktop.Host.Services;

internal sealed class ExportDirectoryGuard : IDisposable
{
    private readonly string _path;
    private readonly string _identity;
    private readonly SafeFileHandle? _lease;

    public ExportDirectoryGuard(string path, bool allowRename)
    {
        _path = Path.GetFullPath(path);
        ValidatePath();
        if (OperatingSystem.IsWindows()) _lease = OpenDirectory(_path, allowRename);
        try { _identity = ReadIdentity(_path, _lease); }
        catch { _lease?.Dispose(); throw; }
    }

    public bool IsCurrent
    {
        get
        {
            try { Validate(); return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    public void Validate()
    {
        ValidatePath();
        using var current = OperatingSystem.IsWindows() ? OpenDirectory(_path, true) : null;
        if (ReadIdentity(_path, current) != _identity)
            throw new IOException("The export directory was replaced during preparation.");
    }

    private void ValidatePath()
    {
        for (DirectoryInfo? directory = new(_path); directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0 || directory.LinkTarget is not null)
                throw new IOException("Export directories must remain physical directories without symbolic links or junctions.");
        }
    }

    private static SafeFileHandle OpenDirectory(string path, bool allowRename)
    {
        var share = FileShare.Read | FileShare.Write | (allowRename ? FileShare.Delete : 0);
        var handle = CreateFile(path, 0x80, share, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid || !GetAttributes(handle, 9, out var attributes, 8) ||
                ((FileAttributes)attributes.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException($"Cannot lock a physical export directory (error {Marshal.GetLastPInvokeError()}).");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static string ReadIdentity(string path, SafeFileHandle? handle)
    {
        if (handle is not null)
        {
            if (!GetIdentity(handle, 18, out var identity, 24))
                throw new IOException($"Cannot identify export directory (error {Marshal.GetLastPInvokeError()}).");
            return $"{identity.Volume}:{identity.Low}:{identity.High}";
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Export directory identity requires Windows or Linux.");
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            // Match the fixed Linux statx UAPI layout used by cache part identity checks.
            if (Statx(-100, path, 0x100, 0x901, buffer) != 0)
                throw new IOException($"Cannot identify export directory (errno {Marshal.GetLastPInvokeError()}).");
            var mask = Marshal.ReadInt32(buffer);
            if ((mask & 0x101) != 0x101 || (Marshal.ReadInt16(buffer, 28) & 0xf000) != 0x4000)
                throw new IOException("Export directory has no physical identity.");
            return $"{Marshal.ReadInt32(buffer, 136)}:{Marshal.ReadInt32(buffer, 140)}:{Marshal.ReadInt64(buffer, 32)}:" +
                ((mask & 0x800) != 0 ? $"{Marshal.ReadInt64(buffer, 80)}:{Marshal.ReadInt32(buffer, 88)}" : "0:0");
        }
        catch (EntryPointNotFoundException exception) { throw new IOException("Cannot safely identify export directories on this system.", exception); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose() => _lease?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct DirectoryId { public ulong Volume; public ulong Low; public ulong High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeInfo { public uint Attributes; public uint Tag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentity(SafeFileHandle handle, int infoClass, out DirectoryId information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAttributes(SafeFileHandle handle, int infoClass, out AttributeInfo information, uint size);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, IntPtr buffer);
}
