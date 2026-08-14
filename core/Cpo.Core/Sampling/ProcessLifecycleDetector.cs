namespace Cpo.Core.Sampling;

/// <summary>生命周期检测输入：一次快照中的进程标识信息。</summary>
public readonly record struct ProcessIdentity(int Pid, int ParentPid, string Name);

/// <summary>生命周期比对结果（纯逻辑，可单测）。</summary>
public sealed record LifecycleDiff(
    IReadOnlyList<ProcessIdentity> Started,
    IReadOnlyList<ProcessIdentity> Exited);

/// <summary>
/// 进程生命周期检测（纯逻辑，可单测）：对比前后两次快照的 PID 集合，
/// 得出启动与退出的进程。M2 增强：换用 ETW 事件订阅后，本检测作为兜底保留。
/// </summary>
public static class ProcessLifecycleDetector
{
    /// <summary>
    /// 比对前后快照，返回启动/退出差异。结果按 PID 升序稳定排序。
    /// </summary>
    public static LifecycleDiff Diff(
        IReadOnlyDictionary<int, ProcessIdentity> previous,
        IReadOnlyDictionary<int, ProcessIdentity> current)
    {
        var started = current
            .Where(kv => !previous.ContainsKey(kv.Key))
            .Select(kv => kv.Value)
            .OrderBy(i => i.Pid)
            .ToArray();

        var exited = previous
            .Where(kv => !current.ContainsKey(kv.Key))
            .Select(kv => kv.Value)
            .OrderBy(i => i.Pid)
            .ToArray();

        return new LifecycleDiff(started, exited);
    }
}
