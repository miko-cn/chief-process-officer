using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cpo.Interop;

/// <summary>
/// 进程采样 P/Invoke 隔离层：枚举进程 + 获取 CPU 时间 / 内存 / 名称 / 路径。
/// 只调用文档化 Win32 API（Toolhelp32 / GetProcessTimes / GetProcessMemoryInfo），
/// 与 Process Lasso / BES 同款行为，无注入无 hook（SPEC §11 杀软误报对策）。
/// </summary>
public static partial class ProcessSampler
{
    /// <summary>
    /// 采集全部可见进程快照。单进程失败（进程已退出/权限拒绝）跳过不影响整体。
    /// 父子关系用**一轮** Toolhelp32 快照查表（原实现对每个进程单独建快照全表扫描，
    /// O(N²)——几百进程时每轮采样数万次扫描，系统饱和时被饿得更惨，会话⑳d 修复）。
    /// </summary>
    public static IReadOnlyList<ProcessSnapshot> SnapshotAll()
    {
        var snapshots = new List<ProcessSnapshot>();
        var parents = SnapshotParentMap();
        var processes = Process.GetProcesses();

        foreach (var p in processes)
        {
            try
            {
                snapshots.Add(Capture(p, parents));
            }
            catch (Exception ex)
            {
                // 进程可能已退出或属其他会话（权限拒绝）——跳过，不中断整体采集
                _ = ex;
            }
            finally
            {
                p.Dispose();
            }
        }

        return snapshots;
    }

    /// <summary>一轮快照收集全部 (pid → ppid) 映射。失败返回空表（调用方容错）。</summary>
    private static Dictionary<int, int> SnapshotParentMap()
    {
        var map = new Dictionary<int, int>();
        try
        {
            var entry = new Native.ProcessEntry32
            {
                dwSize = (uint)Marshal.SizeOf<Native.ProcessEntry32>(),
            };

            using var snapshot = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
            if (snapshot.IsInvalid)
            {
                return map;
            }

            if (Native.Process32First(snapshot, ref entry))
            {
                do
                {
                    map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                }
                while (Native.Process32Next(snapshot, ref entry));
            }
        }
        catch (Exception)
        {
            // 快照失败：返回空表（父进程未知按 0）
        }

        return map;
    }

    private static ProcessSnapshot Capture(Process p, IReadOnlyDictionary<int, int> parents)
    {
        var totalCpuMs = 0L;
        try
        {
            totalCpuMs = (long)p.TotalProcessorTime.TotalMilliseconds;
        }
        catch (Exception)
        {
            // 进程已退出/权限不足：CPU 时间不可得，按 0 处理
        }

        var name = SafeName(p);

        string? path = null;
        try
        {
            path = QueryFullImageName(p);
        }
        catch (Exception)
        {
            // 路径拿不到不致命（schema 中 path 可空）
        }

        long ws = 0, priv = 0;
        try
        {
            if (Native.GetProcessMemoryInfo(p.Handle, out var pmc, (uint)Marshal.SizeOf<Native.ProcessMemoryCounters>()))
            {
                ws = (long)pmc.WorkingSetSize;
                priv = (long)pmc.PrivateUsage;
            }
        }
        catch (Exception)
        {
            // 系统进程（svchost 等）句柄打开可能被拒：内存取不到按 0，不丢弃整条快照
        }

        var parentPid = parents.TryGetValue(p.Id, out var ppid) ? ppid : 0;

        return new ProcessSnapshot(
            Pid: p.Id,
            ParentPid: parentPid,
            Name: name,
            Path: path,
            TotalCpuMs: totalCpuMs,
            WorkingSetBytes: ws,
            PrivateBytes: priv);
    }

    private static string SafeName(Process p)
    {
        try
        {
            return p.ProcessName;
        }
        catch
        {
            return $"pid-{p.Id}";
        }
    }

    private static string? QueryFullImageName(Process p)
    {
        if (p.Handle == IntPtr.Zero)
        {
            return null;
        }

        // QueryFullProcessImageNameW 标准调用模式：StringBuilder + 容量（含 null 终止符）
        var buffer = new System.Text.StringBuilder(4096);
        var size = (uint)buffer.Capacity;
        return Native.QueryFullProcessImageName(p.Handle, 0, buffer, ref size)
            ? buffer.ToString()
            : null;
    }

    /// <summary>供 XAML/外部引用的进程名查询（无句柄场景）。</summary>
    public static string? GetProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static partial class Native
    {
        internal const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessEntry32
        {
            internal uint dwSize;
            internal uint cntUsage;
            internal uint th32ProcessID;
            internal IntPtr th32DefaultHeapID;
            internal uint th32ModuleID;
            internal uint cntThreads;
            internal uint th32ParentProcessID;
            internal int pcPriClassBase;
            internal uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            internal string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessMemoryCounters
        {
            internal uint cb;
            internal uint PageFaultCount;
            internal nuint PeakWorkingSetSize;
            internal nuint WorkingSetSize;
            internal nuint QuotaPeakPagedPoolUsage;
            internal nuint QuotaPagedPoolUsage;
            internal nuint QuotaPeakNonPagedPoolUsage;
            internal nuint QuotaNonPagedPoolUsage;
            internal nuint PagefileUsage;
            internal nuint PeakPagefileUsage;
            internal nuint PrivateUsage;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateToolhelp32Snapshot", SetLastError = true)]
        internal static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32First(Microsoft.Win32.SafeHandles.SafeFileHandle hSnapshot, ref ProcessEntry32 lppe);

        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32Next(Microsoft.Win32.SafeHandles.SafeFileHandle hSnapshot, ref ProcessEntry32 lppe);

        [DllImport("psapi.dll", EntryPoint = "GetProcessMemoryInfo", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessMemoryInfo(
            IntPtr hProcess, out ProcessMemoryCounters ppsmemCounters, uint cb);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(
            IntPtr hProcess, uint dwFlags, System.Text.StringBuilder lpExeName, ref uint lpdwSize);
    }
}
