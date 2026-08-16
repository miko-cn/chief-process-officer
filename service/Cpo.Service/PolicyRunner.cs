using Cpo.Core.Engine;
using Cpo.Core.Storage;
using Cpo.Core.Telemetry;

namespace Cpo.Service;

/// <summary>
/// 策略运行器：每个采样周期把遥测转为引擎输入 → 引擎评估 → 
/// 监督模式（默认）：建议只入日志，不执行；自动模式：建议经执行路径执行。
/// 确定性屏障：引擎产出建议走 ProposalBus，执行只经 ExecutionPath（SPEC §6）。
///
/// 输入构建：固定滑动窗口（最近 <see cref="LookbackMs"/> 毫秒）+ 每进程取最新 CPU 样本。
/// 不用增量窗口——采样落库有滞后（进程枚举耗时），增量窗口会永远查不到刚采的数据。
/// </summary>
public sealed class PolicyRunner : IAsyncDisposable
{
    /// <summary>评估输入回看窗口：覆盖采样/落库滞后（进程枚举耗时数百 ms，取 3 倍采样间隔）。</summary>
    private const long LookbackMs = 5_000;

    private readonly ITelemetryStore _store;
    private readonly IProcessController _controller;
    private readonly RuleStore _rules;
    private readonly ExecutionPath _execution;
    private readonly ProposalBus _bus = new();
    private readonly int _coreCount;

    /// <summary>引擎模式（M1 默认监督；按类别升级自动模式 M3 实现）。</summary>
    public DecisionMode Mode { get; set; } = DecisionMode.Supervised;

    /// <summary>最近一次建议（供 UI/日志展示）。</summary>
    public IReadOnlyList<PolicyProposal> LastProposals { get; private set; } = Array.Empty<PolicyProposal>();

    public PolicyRunner(ITelemetryStore store, IProcessController controller, RuleStore rules, int coreCount)
    {
        _store = store;
        _controller = controller;
        _rules = rules;
        _execution = new ExecutionPath(controller);
        _coreCount = coreCount;
    }

    public ExecutionPath Execution => _execution;

    /// <summary>
    /// 处理一次采样：从遥测流查询最近窗口样本 → 引擎评估 → 按模式处理。
    /// </summary>
    public async Task EvaluateAsync(CancellationToken ct = default)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 滑动窗口查询：每进程最新 CPU 样本重建引擎输入
        var latestByPid = new Dictionary<int, ProcessState>();
        var latestTsByPid = new Dictionary<int, long>();
        double systemCpu = 0;

        await foreach (var evt in _store.QueryAsync(
            new EventQuery { FromMs = nowMs - LookbackMs, Limit = 100_000 }, ct))
        {
            switch (evt)
            {
                case CpuSampleEvent cpu when cpu.Scope == SampleScope.System:
                    systemCpu = cpu.CpuPercent;
                    break;
                case CpuSampleEvent cpu when cpu.Scope == SampleScope.Process
                                              && cpu.Pid is int pid
                                              && cpu.Name is { } name:
                    // 窗口内同一进程可能有多条样本，取最新一条（ts 最大）
                    if (!latestTsByPid.TryGetValue(pid, out var lastTs) || cpu.TsMs > lastTs)
                    {
                        latestTsByPid[pid] = cpu.TsMs;
                        latestByPid[pid] = new ProcessState(pid, name, cpu.CpuPercent, 0);
                    }

                    break;
            }
        }

        var input = new EngineInput
        {
            Processes = latestByPid.Values.ToArray(),
            SystemCpuPercent = systemCpu,
            ForegroundPid = null, // M2 前台检测接入前保守：无前台信息
            Rules = _rules.Rules,
            CoreCount = _coreCount,
        };

        var proposals = PolicyEngine.Evaluate(input);
        LastProposals = proposals;
        _bus.Publish(proposals);

        // 决策日志：建议全部落 policy.decision
        var events = new List<TelemetryEvent>(proposals.Count);
        foreach (var proposal in proposals)
        {
            events.Add(DecisionLogger.ToDecisionEvent(proposal, Mode));
        }

        // 自动模式：经执行路径执行（监督模式只建议不执行）
        if (Mode == DecisionMode.Automatic)
        {
            foreach (var proposal in proposals)
            {
                var result = _execution.Execute(proposal);
                events.Add(DecisionLogger.ToActionEvent(result));
            }
        }

        // 过期干预恢复（决策日志中恢复动作同样留痕）
        var restored = _execution.ReapExpired(nowMs);
        foreach (var evt in _execution.ExecutionLog.Skip(Math.Max(0, _execution.ExecutionLog.Count - restored)))
        {
            if (evt.Proposal.Trigger == "restore")
            {
                events.Add(DecisionLogger.ToActionEvent(evt));
            }
        }

        // rule.changed 日志（drain 一次性消费，避免重复落盘）
        foreach (var change in _rules.DrainChanges())
        {
            events.Add(change);
        }

        if (events.Count > 0)
        {
            await _store.AppendBatchAsync(events, ct);
        }
    }

    /// <summary>停止：恢复全部干预（SPEC：引擎退出时自动恢复原值）。</summary>
    public int Shutdown()
    {
        lock (_gate)
        {
            return _execution.RestoreAll();
        }
    }

    private readonly object _gate = new();

    public ValueTask DisposeAsync()
    {
        Shutdown();
        return ValueTask.CompletedTask;
    }
}
