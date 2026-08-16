namespace Cpo.Core.Engine;

/// <summary>进程当前控制状态（原值记录用，SPEC：每次干预记录原优先级类/原亲和性掩码）。</summary>
public sealed record ProcessControlState(
    int Pid,
    string Name,
    int PriorityClass,
    ulong AffinityMask);

/// <summary>干预执行结果。</summary>
public sealed record InterventionResult(bool Succeeded, string? Error);

/// <summary>
/// 进程控制抽象（执行路径依赖的最小接口）。
/// interop 层提供真实实现（SetPriorityClass / SetProcessAffinityMask）；
/// 测试与回放提供 Fake 实现。core 不依赖任何 Win32 API。
/// </summary>
public interface IProcessController
{
    /// <summary>读取进程当前优先级类与亲和性掩码（原值记录）。进程不存在返回 null。</summary>
    ProcessControlState? GetState(int pid);

    /// <summary>设置进程优先级类（Windows 优先级类常量）。</summary>
    InterventionResult SetPriorityClass(int pid, int priorityClass);

    /// <summary>设置进程 CPU 亲和性掩码。</summary>
    InterventionResult SetAffinityMask(int pid, ulong mask);
}
