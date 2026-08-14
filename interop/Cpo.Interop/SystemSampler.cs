using System.Runtime.InteropServices;

namespace Cpo.Interop;

/// <summary>
/// 系统级采样 P/Invoke 隔离层：CPU 累计时间（GetSystemTimes）+ 物理内存（GlobalMemoryStatusEx）+ 核心数。
/// </summary>
public static partial class SystemSampler
{
    /// <summary>采集系统级快照。</summary>
    public static SystemSnapshot Snapshot()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new Win32ExceptionMarshal();
        }

        var memory = new Native.MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<Native.MemoryStatusEx>() };
        if (!Native.GlobalMemoryStatusEx(ref memory))
        {
            throw new Win32ExceptionMarshal();
        }

        // GetSystemTimes 返回 FILETIME（100ns 单位）；kernel 含 idle，需扣除
        var total100ns = ToInt64(kernel) + ToInt64(user);
        var idle100ns = ToInt64(idle);
        var coreCount = Environment.ProcessorCount;

        return new SystemSnapshot(
            TotalCpuMs: total100ns / 10_000,
            IdleCpuMs: idle100ns / 10_000,
            AvailableBytes: (long)memory.ullAvailPhys,
            TotalBytes: (long)memory.ullTotalPhys,
            CoreCount: coreCount);
    }

    private static long ToInt64(System.Runtime.InteropServices.ComTypes.FILETIME ft) =>
        ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    private sealed class Win32ExceptionMarshal : Exception
    {
        public Win32ExceptionMarshal()
            : base($"系统采样失败，Win32 错误 {Marshal.GetLastWin32Error()}") { }
    }

    internal static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct MemoryStatusEx
        {
            internal uint dwLength;
            internal uint dwMemoryLoad;
            internal ulong ullTotalPhys;
            internal ulong ullAvailPhys;
            internal ulong ullTotalPageFile;
            internal ulong ullAvailPageFile;
            internal ulong ullTotalVirtual;
            internal ulong ullAvailVirtual;
            internal ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", EntryPoint = "GetSystemTimes", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(
            out System.Runtime.InteropServices.ComTypes.FILETIME lpIdleTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME lpKernelTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME lpUserTime);

        [DllImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
    }
}
