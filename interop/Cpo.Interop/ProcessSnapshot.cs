namespace Cpo.Interop;

/// <summary>
/// 进程快照（P/Invoke 采集原始值，不含百分比计算——百分比由调用方基于两次快照差值计算）。
/// </summary>
public sealed record ProcessSnapshot(
    int Pid,
    int ParentPid,
    string Name,
    string? Path,
    long TotalCpuMs,      // kernel + user CPU 时间（毫秒）
    long WorkingSetBytes,
    long PrivateBytes);

/// <summary>系统快照（P/Invoke 采集原始值）。</summary>
public sealed record SystemSnapshot(
    long TotalCpuMs,      // busy CPU 时间（kernel + user − idle，毫秒；kernel 含 idle 必须扣除，会话⑳f）
    long IdleCpuMs,       // 空闲 CPU 时间（毫秒）
    long AvailableBytes,  // 可用物理内存
    long TotalBytes,      // 总物理内存
    int CoreCount);       // 逻辑核心数

/// <summary>快照采集失败（特定进程已退出等），调用方可跳过该进程。</summary>
public sealed class SnapshotException : Exception
{
    public SnapshotException(string message, Exception inner) : base(message, inner) { }
}
