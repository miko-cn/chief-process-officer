using Cpo.Core.Engine;

namespace Cpo.Core.Storage;

/// <summary>
/// 生效干预的持久化存储（会话⑳h 定案）。
/// 背景：ExecutionPath 的生效干预队列（_active）是纯内存的——service 被强杀/崩溃/断电时
/// 队列随进程消失，已降级的进程永远没人恢复（残留到进程自己退出）。
/// 持久化后：Execute 成功落盘、恢复即删除；service 启动时扫描残留并恢复（RestoreOrphanedAsync）。
/// 恢复动作天然无害（升回原值，即使 pid 被复用误匹配也不会造成伤害）。
/// </summary>
public interface IInterventionStore
{
    /// <summary>落盘一条生效干预（同 pid 覆盖，幂等）。</summary>
    Task SaveAsync(ActiveIntervention intervention, CancellationToken ct = default);

    /// <summary>删除一条生效干预（恢复/进程消失时调用）。</summary>
    Task DeleteAsync(int pid, CancellationToken ct = default);

    /// <summary>加载全部生效干预（service 启动恢复用）。</summary>
    Task<IReadOnlyList<ActiveIntervention>> LoadAllAsync(CancellationToken ct = default);
}
