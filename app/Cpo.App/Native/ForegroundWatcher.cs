using System.Runtime.InteropServices;

namespace Cpo.App.Native;

/// <summary>
/// 前台窗口变化监听（SPEC §6 前台检测归属定案）：
/// 前台检测必须在用户会话内做（service 跑 Session 0 拿不到桌面），由 GUI 侧
/// SetWinEventHook(EVENT_SYSTEM_FOREGROUND) 事件驱动检测 → 经 gRPC ReportForeground 上报 service。
///
/// 线程模型：SetWinEventHook 以 WINEVENT_OUTOFCONTEXT 注册时，回调排入**注册线程**的消息队列
/// ——在 UI 线程注册（WinUI 3 UI 线程有消息泵），回调即 UI 线程执行，无需再 marshal。
/// GUI 最小化/退托盘不影响（进程仍在用户会话，hook 照常触发）。
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;   // 回调排入注册线程消息队列

    private delegate void WinEventDelegate(
        nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventDelegate pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    /// <summary>前台变化回调（pid + 进程名，无 .exe 后缀）。UI 线程执行。</summary>
    public event Action<int, string>? ForegroundChanged;

    private readonly WinEventDelegate _delegate;
    private nint _hook;

    public ForegroundWatcher()
    {
        // 持有委托引用：P/Invoke 回调必须防止 GC 回收（否则回调时崩溃）
        _delegate = OnWinEvent;
    }

    /// <summary>注册 hook（必须在 UI 线程调用）并立即上报当前前台。</summary>
    public void Start()
    {
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, nint.Zero,
            _delegate, 0, 0, WineventOutOfContext);
        ReportCurrentForeground();
    }

    public void Stop()
    {
        if (_hook != nint.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = nint.Zero;
        }
    }

    public void Dispose() => Stop();

    private void OnWinEvent(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == nint.Zero)
        {
            return;
        }

        ReportWindow(hwnd);
    }

    private void ReportCurrentForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd != nint.Zero)
        {
            ReportWindow(hwnd);
        }
    }

    private void ReportWindow(nint hwnd)
    {
        if (GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0)
        {
            return;
        }

        ForegroundChanged?.Invoke((int)pid, GetProcessName(pid));
    }

    private static string GetProcessName(uint pid)
    {
        try
        {
            return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            return "?";
        }
    }
}
