using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BiliBiliLocalCacheManager.Desktop.Host.Services;

internal sealed partial class ExportDirectoryGuard
{
    private static SafeFileHandle CreateWindowsStagingDirectory(SafeFileHandle parent, string name)
    {
        const uint fileReadAttributes = 0x80;
        const uint synchronize = 0x100000;
        const uint fileCreate = 2;
        const uint fileDirectoryFile = 0x01;
        const uint fileSynchronousIoNonalert = 0x20;
        const uint objCaseInsensitive = 0x40;

        // Use the guarded parent and one generated leaf name. FILE_CREATE returns a handle
        // to the new directory in the same operation and fails if the name already exists.
        var length = checked((ushort)(name.Length * sizeof(char)));
        var descriptorSize = Marshal.SizeOf<NativeUnicodeString>();
        var descriptor = Marshal.AllocHGlobal(descriptorSize + length);
        var parentRetained = false;
        try
        {
            var buffer = descriptor + descriptorSize;
            Marshal.Copy(name.ToCharArray(), 0, buffer, name.Length);
            Marshal.StructureToPtr(new NativeUnicodeString { Length = length, MaximumLength = length, Buffer = buffer },
                descriptor, false);
            parent.DangerousAddRef(ref parentRetained);
            var attributes = new NativeObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<NativeObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = descriptor,
                Attributes = objCaseInsensitive
            };
            var status = NtCreateFile(out var handle, fileReadAttributes | synchronize, in attributes, out _,
                IntPtr.Zero, 0, FileShare.Read | FileShare.Write | FileShare.Delete, fileCreate,
                fileDirectoryFile | fileSynchronousIoNonalert, IntPtr.Zero, 0);
            if (status >= 0) return handle;
            handle.Dispose();
            throw new IOException($"Cannot create the export staging directory (NTSTATUS 0x{status:X8}).");
        }
        finally
        {
            if (parentRetained) parent.DangerousRelease();
            Marshal.FreeHGlobal(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeUnicodeString
    {
        public ushort Length, MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory, ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor, SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIoStatusBlock
    {
        public IntPtr Status, Information;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, in NativeObjectAttributes attributes,
        out NativeIoStatusBlock ioStatus, IntPtr allocationSize, uint fileAttributes, FileShare share,
        uint disposition, uint options, IntPtr eaBuffer, uint eaLength);
}
