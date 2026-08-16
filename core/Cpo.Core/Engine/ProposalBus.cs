namespace Cpo.Core.Engine;

/// <summary>
/// 建议总线（ProposalBus）——确定性屏障的建议侧。
/// 引擎产出的所有建议都经此通道；未来 AI 层也只允许写入此通道（SPEC §6 确定性屏障）。
/// 执行路径不读取这里；采纳/拒绝由上层（监督模式的用户、自动模式的执行器）决定。
/// </summary>
public sealed class ProposalBus
{
    private readonly object _gate = new();
    private readonly List<PolicyProposal> _pending = new();

    /// <summary>提交一批建议（引擎评估结果）。</summary>
    public void Publish(IEnumerable<PolicyProposal> proposals)
    {
        lock (_gate)
        {
            _pending.Clear();
            _pending.AddRange(proposals);
        }
    }

    /// <summary>取走当前待处理建议（消费后清空）。</summary>
    public IReadOnlyList<PolicyProposal> Drain()
    {
        lock (_gate)
        {
            var result = _pending.ToArray();
            _pending.Clear();
            return result;
        }
    }

    /// <summary>当前待处理建议数。</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }
}
