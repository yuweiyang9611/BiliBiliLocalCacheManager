using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BiliBiliLocalCacheManager.Desktop.Host.Services;

internal sealed partial class ExportDirectoryGuard
{
    // Linux UAPI constants used by the supported linux-x64 Host.
    private const int LinuxDirectoryFlags = 0x200000 | 0x10000 | 0x20000 | 0x80000; // O_PATH | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC
    private const uint InCreate = 0x100, InDelete = 0x200, InMovedFrom = 0x40, InMovedTo = 0x80;
    private const uint InDeleteSelf = 0x400, InMoveSelf = 0x800, InUnmount = 0x2000;
    private const uint InOverflow = 0x4000, InIgnored = 0x8000, InDirectory = 0x40000000;

    private static SafeFileHandle OpenLinuxDirectory(string path) =>
        LinuxHandle(LinuxOpen(path, LinuxDirectoryFlags), "open export directory");

    private static SafeFileHandle CreateLinuxStagingDirectory(SafeFileHandle parent, string parentPath, string name)
    {
        using var monitor = LinuxHandle(InotifyInit(0x800 | 0x80000), "monitor export destination"); // IN_NONBLOCK | IN_CLOEXEC
        // The watch and mkdirat must refer to the same directory even if its pathname changes.
        int watch;
        var addedRef = false;
        try
        {
            parent.DangerousAddRef(ref addedRef);
            watch = InotifyAddWatch(monitor, $"/proc/self/fd/{parent.DangerousGetHandle()}",
                InCreate | InDelete | InMovedFrom | InMovedTo | InDeleteSelf | InMoveSelf);
            if (watch < 0) throw LinuxIoError("watch export destination");
        }
        finally
        {
            if (addedRef) parent.DangerousRelease();
        }
        if (LinuxMkdirAt(parent, name, 0x1ff) != 0) throw LinuxIoError("create export staging directory"); // 0777 subject to umask/default ACL

        SafeFileHandle? created = null;
        try
        {
            created = LinuxHandle(LinuxOpenAt(parent, name, LinuxDirectoryFlags), "open new export staging directory");
            // A same-name rename is a no-op. It takes the parent inode lock, so a concurrent
            // namespace change visible to openat has queued its inotify events before we read.
            if (LinuxRenameAt(parent, name, parent, name) != 0) throw LinuxIoError("verify new staging name");
            // Cover a mount at the new name, which does not generate a directory rename event.
            if (LinuxMountId(created) != LinuxMountId(parent)) throw new IOException("The staging name crosses a mount.");
            if (InotifyRemoveWatch(monitor, watch) != 0) throw LinuxIoError("finish watching export destination");
            // Removing the only watch leaves a finite queue ending in IN_IGNORED.
            // Read complete records in fixed-size chunks, including changes after the CREATE.
            var events = new byte[4096];
            var expected = Encoding.UTF8.GetBytes(name);
            var sawCreation = false;
            while (true)
            {
                var count = (int)LinuxRead(monitor, events, (nuint)events.Length);
                if (count < 0 && Marshal.GetLastPInvokeError() == 4) continue; // EINTR
                if (count < 0) throw LinuxIoError("read export directory notifications");
                if (count == 0) throw new IOException("Incomplete export directory notifications.");
                if (ConsumeLinuxCreationEvents(events.AsSpan(0, count), watch, expected, ref sawCreation)) break;
            }
            return created;
        }
        catch (Exception ex)
        {
            created?.Dispose();
            throw new IOException($"无法确认新建暂存目录的身份，请核对：{FormatStagingLocation(Path.Combine(parentPath, name))}。{ex.Message}", ex);
        }
    }

    private static ulong LinuxMountId(SafeFileHandle handle)
    {
        // fdinfo reports the mount of the held object, also on kernels predating STATX_MNT_ID.
        var addedRef = false;
        try
        {
            handle.DangerousAddRef(ref addedRef);
            foreach (var line in File.ReadLines($"/proc/self/fdinfo/{handle.DangerousGetHandle()}"))
            {
                if (line.StartsWith("mnt_id:", StringComparison.Ordinal) &&
                    ulong.TryParse(line.AsSpan("mnt_id:".Length).Trim(), out var mountId))
                    return mountId;
            }
        }
        finally
        {
            if (addedRef) handle.DangerousRelease();
        }
        throw new IOException("Cannot determine the export directory mount.");
    }

    internal static bool ConsumeLinuxCreationEvents(ReadOnlySpan<byte> events, int watch, ReadOnlySpan<byte> expectedName, ref bool sawCreation)
    {
        while (!events.IsEmpty)
        {
            // inotify_event is four 32-bit fields followed by the NUL-padded entry name.
            if (events.Length < 16) throw new IOException("Incomplete export directory event.");
            var descriptor = BitConverter.ToInt32(events);
            var mask = BitConverter.ToUInt32(events[4..]);
            var length = BitConverter.ToUInt32(events[12..]);
            if (descriptor != watch || (mask & (InDeleteSelf | InMoveSelf | InUnmount | InOverflow)) != 0 ||
                length > events.Length - 16)
                throw new IOException("Export directory notifications were lost.");
            var nameField = events.Slice(16, (int)length);
            if (!nameField.IsEmpty && nameField[^1] != 0) throw new IOException("Invalid export directory event name.");
            var entry = nameField.TrimEnd((byte)0);
            events = events[(16 + (int)length)..];
            if ((mask & InIgnored) != 0)
            {
                if (mask == InIgnored && length == 0 && events.IsEmpty && sawCreation) return true;
                throw new IOException("Incomplete export directory notifications.");
            }
            // Only the exact spelling confirms creation. Detect ASCII case variants on case-insensitive mounts.
            if (entry.SequenceEqual(expectedName) || Ascii.EqualsIgnoreCase(entry, expectedName))
            {
                if (mask != (InCreate | InDirectory) || sawCreation || !entry.SequenceEqual(expectedName))
                    throw new IOException("The staging directory changed during creation.");
                sawCreation = true;
            }
        }
        return false;
    }

    private static IOException LinuxIoError(string operation) =>
        new($"Cannot {operation} (errno {Marshal.GetLastPInvokeError()}).");

    private static SafeFileHandle LinuxHandle(int descriptor, string operation) =>
        descriptor < 0 ? throw LinuxIoError(operation) : new SafeFileHandle(descriptor, ownsHandle: true);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int LinuxOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int LinuxOpenAt(SafeFileHandle parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int LinuxMkdirAt(SafeFileHandle parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint mode);
    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
    private static extern int LinuxRenameAt(SafeFileHandle parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        SafeFileHandle destination, [MarshalAs(UnmanagedType.LPUTF8Str)] string destinationName);
    [DllImport("libc", EntryPoint = "inotify_init1", SetLastError = true)]
    private static extern int InotifyInit(int flags);
    [DllImport("libc", EntryPoint = "inotify_add_watch", SetLastError = true)]
    private static extern int InotifyAddWatch(SafeFileHandle monitor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mask);
    [DllImport("libc", EntryPoint = "inotify_rm_watch", SetLastError = true)]
    private static extern int InotifyRemoveWatch(SafeFileHandle monitor, int watch);
    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint LinuxRead(SafeFileHandle monitor, [Out] byte[] buffer, nuint length);
}
