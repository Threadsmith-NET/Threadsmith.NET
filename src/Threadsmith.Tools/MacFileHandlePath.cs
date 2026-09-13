namespace Threadsmith.Tools;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>Resolves an opened file using Darwin's fixed-signature descriptor inspection API.</summary>
[SupportedOSPlatform("macos")]
internal static class MacFileHandlePath
{
    // Darwin sys/proc_info.h: PROC_PIDFDVNODEPATHINFO returns vnode_fdinfowithpath.
    // Both supported 64-bit ABIs place vip_path after proc_fileinfo (24 bytes)
    // and vnode_info (152 bytes), followed by MAXPATHLEN (1024 bytes).
    // https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/proc_info.h
    private const int VnodePathInfo = 2;
    private const int PathOffset = 176;
    private const int PathCapacity = 1024;
    private const int InfoSize = PathOffset + PathCapacity;

    /// <summary>Returns the path of the held descriptor, or null when the OS cannot resolve it.</summary>
    internal static string? GetPath(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var retained = false;
        try
        {
            handle.DangerousAddRef(ref retained);
            var buffer = new byte[InfoSize];
            var length = GetDescriptorInfo(
                Environment.ProcessId,
                handle.DangerousGetHandle().ToInt32(),
                VnodePathInfo,
                buffer,
                buffer.Length);
            if (length != InfoSize)
            {
                return null;
            }

            var terminator = Array.IndexOf(buffer, (byte)0, PathOffset, PathCapacity);
            return terminator > PathOffset
                ? Encoding.UTF8.GetString(buffer, PathOffset, terminator - PathOffset)
                : null;
        }
        finally
        {
            if (retained)
            {
                handle.DangerousRelease();
            }
        }
    }

    // fcntl is variadic: a fixed three-argument P/Invoke corrupts the stack on Apple ARM64.
    // proc_pidfdinfo has the same fixed calling convention on macOS ARM64 and x64.
    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pidfdinfo", SetLastError = true)]
    private static extern int GetDescriptorInfo(
        int processId,
        int fileDescriptor,
        int flavor,
        [Out] byte[] buffer,
        int bufferSize);
}
