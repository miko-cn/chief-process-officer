using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Cpo.Core.Engine;

namespace Cpo.Interop;

/// <summary>
/// 进程控制 P/Invoke 隔离层（SPEC §6 降优手段）：SetPriorityClass / SetProcessAffinityMask。
/// 只调用文档化 Win32 API，与 Process Lasso / BES 同款行为，无注入无 hook（SPEC §11 杀软误报对策）。
/// 优先级类常量：0x40 Idle / 0x4000 BelowNormal / 0x20 Normal / 0x8000 AboveNormal / 0x80 High / 0x100 Realtime。
/// </summary>
public static partial class ProcessController
{
    public const int PriorityIdle = 0x40;
    public const int PriorityBelowNormal = 0x4000;
    public const int PriorityNormal = 0x20;
    public const int PriorityAboveNormal = 0x8000;
    public const int PriorityHigh = 0x80;
    public const int PriorityRealtime = 0x100;

    /// <summary>读取进程当前优先级类与亲和性掩码（原值记录）。进程不存在/权限不足返回 null。</summary>
    public static ProcessControlState? GetState(int pid)
    {
        try
        {
            using var process = OpenProcess(pid, ProcessAccessFlags.QueryLimitedInformation);
            if (process.IsInvalid)
            {
                return null;
            }

            var priority = Native.GetPriorityClass(process);
            if (priority == 0)
            {
                return null;
            }

            if (!Native.GetProcessAffinityMask(process, out var mask, out _))
            {
                return null;
            }

            var name = GetName(pid);
            return new ProcessControlState(pid, name, (int)priority, mask);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>设置进程优先级类。返回成功与否。</summary>
    public static InterventionResult SetPriorityClass(int pid, int priorityClass)
    {
        try
        {
            using var process = OpenProcess(pid, ProcessAccessFlags.SetInformation | ProcessAccessFlags.QueryLimitedInformation);
            if (process.IsInvalid)
            {
                return new InterventionResult(false, $"无法打开进程 {pid}（可能已退出或权限不足）");
            }

            if (!Native.SetPriorityClass(process, (uint)priorityClass))
            {
                return new InterventionResult(false, $"SetPriorityClass 失败: Win32 错误 {Marshal.GetLastWin32Error()}");
            }

            return new InterventionResult(true, null);
        }
        catch (Win32Exception ex)
        {
            return new InterventionResult(false, $"SetPriorityClass 异常: {ex.Message}");
        }
    }

    /// <summary>设置进程 CPU 亲和性掩码。返回成功与否。</summary>
    public static InterventionResult SetAffinityMask(int pid, ulong mask)
    {
        try
        {
            using var process = OpenProcess(pid, ProcessAccessFlags.SetInformation | ProcessAccessFlags.QueryLimitedInformation);
            if (process.IsInvalid)
            {
                return new InterventionResult(false, $"无法打开进程 {pid}（可能已退出或权限不足）");
            }

            if (!Native.SetProcessAffinityMask(process, (nuint)mask))
            {
                return new InterventionResult(false, $"SetProcessAffinityMask 失败: Win32 错误 {Marshal.GetLastWin32Error()}");
            }

            return new InterventionResult(true, null);
        }
        catch (Win32Exception ex)
        {
            return new InterventionResult(false, $"SetProcessAffinityMask 异常: {ex.Message}");
        }
    }

    /// <summary>供上层组合的完整控制器（适配 IProcessController 接口）。</summary>
    public static IProcessController CreateController() => new Win32ProcessController();

    /// <summary>
    /// 进程树枚举（Toolhelp32 快照）：返回 rootPid 自身与全部后代 pid。
    /// 启发式"前台进程树保护"的数据源（用户当前活动的整个进程树绝不干预）。
    /// 失败/进程不存在返回空集合（保守：空集合 = 不保护额外进程，不误伤保护能力）。
    /// </summary>
    public static IReadOnlySet<int> GetDescendantPids(int rootPid)
    {
        try
        {
            // 快照全部进程的 (pid, ppid)，构建父子映射后 BFS
            var snapshot = Native.CreateToolhelp32Snapshot(SnapshotFlags.Process, 0);
            if (snapshot.IsInvalid)
            {
                return new HashSet<int>();
            }

            try
            {
                var children = new Dictionary<int, List<int>>();
                var entry = new Native.ProcessEntry32 { Size = (uint)Marshal.SizeOf<Native.ProcessEntry32>() };
                if (Native.Process32First(snapshot, ref entry))
                {
                    do
                    {
                        var pid = (int)entry.ProcessId;
                        var ppid = (int)entry.ParentProcessId;
                        if (ppid > 0)
                        {
                            if (!children.TryGetValue(ppid, out var list))
                            {
                                list = new List<int>();
                                children[ppid] = list;
                            }

                            list.Add(pid);
                        }
                    }
                    while (Native.Process32Next(snapshot, ref entry));
                }

                // BFS 收集 root 自身 + 全部后代
                var result = new HashSet<int> { rootPid };
                var queue = new Queue<int>();
                queue.Enqueue(rootPid);
                while (queue.Count > 0)
                {
                    var pid = queue.Dequeue();
                    if (children.TryGetValue(pid, out var kids))
                    {
                        foreach (var kid in kids)
                        {
                            if (result.Add(kid))
                            {
                                queue.Enqueue(kid);
                            }
                        }
                    }
                }

                return result;
            }
            finally
            {
                Native.CloseHandle(snapshot);
            }
        }
        catch (Exception)
        {
            return new HashSet<int>();
        }
    }

    private sealed class Win32ProcessController : IProcessController
    {
        public ProcessControlState? GetState(int pid) => ProcessController.GetState(pid);

        public InterventionResult SetPriorityClass(int pid, int priorityClass) =>
            ProcessController.SetPriorityClass(pid, priorityClass);

        public InterventionResult SetAffinityMask(int pid, ulong mask) =>
            ProcessController.SetAffinityMask(pid, mask);

        public IReadOnlySet<int> GetDescendantPids(int rootPid) => ProcessController.GetDescendantPids(rootPid);
    }

    private static Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(int pid, ProcessAccessFlags access)
    {
        var handle = Native.OpenProcess((uint)access, false, (uint)pid);
        if (handle.IsInvalid)
        {
            // 区分"进程不存在"（ERROR_INVALID_PARAMETER）与"权限不足"（ERROR_ACCESS_DENIED），
            // 统一按不可操作处理（调用方容错）
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return handle;
    }

    private static string GetName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception)
        {
            return $"pid-{pid}";
        }
    }

    /// <summary>取 named pipe 服务端句柄对应的对端（客户端）进程 PID（门卫管道校验用）。失败返回 null。</summary>
    public static int? GetClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipeHandle)
    {
        if (Native.GetNamedPipeClientProcessId(pipeHandle, out var pid))
        {
            return (int)pid;
        }

        return null;
    }

    [Flags]
    private enum ProcessAccessFlags : uint
    {
        QueryLimitedInformation = 0x00001000,
        SetInformation = 0x00000200,
    }

    [Flags]
    internal enum SnapshotFlags : uint
    {
        Process = 0x00000002,
    }

    internal static partial class Native
    {
        [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
        internal static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(
            uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", EntryPoint = "GetPriorityClass", SetLastError = true)]
        internal static extern uint GetPriorityClass(Microsoft.Win32.SafeHandles.SafeProcessHandle hProcess);

        [DllImport("kernel32.dll", EntryPoint = "SetPriorityClass", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetPriorityClass(Microsoft.Win32.SafeHandles.SafeProcessHandle hProcess, uint dwPriorityClass);

        [DllImport("kernel32.dll", EntryPoint = "GetProcessAffinityMask", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessAffinityMask(
            Microsoft.Win32.SafeHandles.SafeProcessHandle hProcess, out nuint lpProcessAffinityMask, out nuint lpSystemAffinityMask);

        [DllImport("kernel32.dll", EntryPoint = "SetProcessAffinityMask", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessAffinityMask(
            Microsoft.Win32.SafeHandles.SafeProcessHandle hProcess, nuint dwProcessAffinityMask);

        [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeClientProcessId(
            Microsoft.Win32.SafeHandles.SafePipeHandle hNamedPipe, out uint lpdwClientProcessId);

        [DllImport("kernel32.dll", EntryPoint = "CreateToolhelp32Snapshot", SetLastError = true)]
        internal static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateToolhelp32Snapshot(
            SnapshotFlags dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32First(
            Microsoft.Win32.SafeHandles.SafeFileHandle hSnapshot, ref ProcessEntry32 lppe);

        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32Next(
            Microsoft.Win32.SafeHandles.SafeFileHandle hSnapshot, ref ProcessEntry32 lppe);

        [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(Microsoft.Win32.SafeHandles.SafeFileHandle hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public nuint DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int BasePriority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }
    }
}
