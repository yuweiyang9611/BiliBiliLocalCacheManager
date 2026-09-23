using System.Runtime.InteropServices;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

public sealed partial class FileSystemCacheTrashService
{
    private static string ReadUnixPartDirectoryIdentity(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Part identity requires Windows or Linux.");
        // statx has a fixed 256-byte Linux UAPI layout, unlike the architecture-dependent stat structure.
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            const uint typeInodeAndBirthTime = 0x00000901;
            if (Statx(-100, path, 0x100, typeInodeAndBirthTime, buffer) != 0)
                throw new IOException($"Could not read the physical part identity (errno {Marshal.GetLastPInvokeError()}).");
            var mask = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, 28));
            if ((mask & 0x101) != 0x101 || (mode & 0xf000) != 0x4000)
                throw new InvalidDataException("The selected part does not expose a physical directory identity.");
            var inode = unchecked((ulong)Marshal.ReadInt64(buffer, 32));
            var major = unchecked((uint)Marshal.ReadInt32(buffer, 136));
            var minor = unchecked((uint)Marshal.ReadInt32(buffer, 140));
            var birthSeconds = (mask & 0x800) == 0 ? 0 : Marshal.ReadInt64(buffer, 80);
            var birthNanoseconds = (mask & 0x800) == 0 ? 0 : Marshal.ReadInt32(buffer, 88);
            return $"{major}:{minor}:{inode}:{birthSeconds}:{birthNanoseconds}";
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new PlatformNotSupportedException("This system cannot safely identify selected part directories.", exception);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directoryFd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, IntPtr buffer);
}
