using Cpo.Core.Engine;
using Cpo.Core.Rules;
using Xunit;

namespace Cpo.Tests;

/// <summary>
/// 启发式 v1（响应性保护）测试——会话⑲定案的核心约束：
/// 目标是保持 OS/前台响应性，降 CPU 是手段不是硬指标。
/// 触发 = 系统饱和 + 挤占者 + 非关键 三条件齐备；系统有余量绝不干预。
/// </summary>
public class HeuristicTests
{
    private static readonly HeuristicConfig DefaultConfig = new();

    private static EngineInput Input(
        double systemCpu,
        int? foregroundPid,
        params (int Pid, string Name, double Cpu)[] processes) => new()
    {
        Processes = processes.Select(p => new ProcessState(p.Pid, p.Name, p.Cpu, 0)).ToArray(),
        SystemCpuPercent = systemCpu,
        ForegroundPid = foregroundPid,
        Rules = Array.Empty<PolicyRule>(),
        CoreCount = 8,
    };

    /// <summary>标准饱和场景：系统 95%，后台进程吃 90%，有前台进程（前台本身低占用）。</summary>
    private static EngineInput SaturatedScenario() =>
        Input(95, foregroundPid: 100, (100, "editor.exe", 10), (200, "compiler.exe", 90));

    [Fact]
    public void SystemNotSaturated_NoHeuristicProposal()
    {
        // 系统 60% 还有余量：进程吃满 CPU 是合理的，绝不干预（会话⑲核心约束）
        var input = Input(60, foregroundPid: 100, (100, "editor.exe", 10), (200, "compiler.exe", 95));

        var proposals = PolicyEngine.Evaluate(input, DefaultConfig);

        Assert.Empty(proposals);
    }

    [Fact]
    public void SaturatedAndHogger_ProposesBelowNormal()
    {
        var proposals = PolicyEngine.Evaluate(SaturatedScenario(), DefaultConfig);

        var proposal = Assert.Single(proposals);
        Assert.Equal(200, proposal.TargetPid);
        Assert.Equal("compiler.exe", proposal.TargetName);
        Assert.Equal(ProposalActionKind.SetPriority, proposal.Action);
        Assert.Equal(0x4000, proposal.PriorityClass);              // BelowNormal，保守强度
        Assert.Equal(30_000, proposal.DurationMs);                 // 30s 后自动恢复
        Assert.Equal("heuristic.saturation", proposal.Trigger);
        Assert.Null(proposal.RuleId);
        Assert.Contains("95%", proposal.Reason);                   // 理由含系统 CPU（可解释）
        Assert.Contains("90%", proposal.Reason);                   // 理由含进程 CPU
    }

    [Fact]
    public void NoForegroundInfo_NoHeuristicProposal()
    {
        // 无前台信息 → 保守模式：不主动降后台进程（SPEC §6 定案），规则仍可用但启发式不触发
        var input = Input(95, foregroundPid: null, (200, "compiler.exe", 95));

        var proposals = PolicyEngine.Evaluate(input, DefaultConfig);

        Assert.Empty(proposals);
    }

    [Fact]
    public void SkipsForegroundProcess()
    {
        // 前台进程即使挤占也不降（保护用户正在用的程序）
        var input = Input(95, foregroundPid: 100, (100, "editor.exe", 95), (200, "compiler.exe", 90));

        var proposals = PolicyEngine.Evaluate(input, DefaultConfig);

        var proposal = Assert.Single(proposals);
        Assert.Equal(200, proposal.TargetPid);   // 只降后台挤占者
    }

    [Fact]
    public void SkipsSystemCriticalProcess()
    {
        var input = Input(95, foregroundPid: 100, (100, "editor.exe", 10), (4, "System", 90));

        var proposals = PolicyEngine.Evaluate(input, DefaultConfig);

        Assert.Empty(proposals);
    }

    [Fact]
    public void SkipsEngineItself()
    {
        // 饱和时采样/评估进程自身 CPU 也高（实机观察）：引擎绝不能把自己降了
        var input = Input(95, foregroundPid: 100,
            (100, "editor.exe", 10), (200, "Cpo.Service", 98), (201, "Cpo.App", 90));

        var proposals = PolicyEngine.Evaluate(input, DefaultConfig);

        Assert.Empty(proposals);
    }

    [Fact]
    public void SkipsLowCpuProcess()
    {
        // 饱和但进程不是挤占者（20% 单核）→ 不干预
        var input = Input(95, foregroundPid: 100, (100, "editor.exe", 10), (200, "compiler.exe", 20));

        var proposals = PolicyEngine.Evaluate(input, DefaultConfig);

        Assert.Empty(proposals);
    }

    [Fact]
    public void RuleTakesPriorityOverHeuristic()
    {
        // 规则命中的进程走规则建议（含规则参数），不再叠加启发式建议
        var rules = new[]
        {
            new PolicyRule
            {
                Id = "r1", ProcessPattern = "compiler.exe",
                Action = RuleActionKind.SetPriority, PriorityClass = 0x40,
            },
        };
        var input = SaturatedScenario() with { Rules = rules };

        var proposals = PolicyEngine.Evaluate(input, DefaultConfig);

        var proposal = Assert.Single(proposals);
        Assert.Equal("r1", proposal.RuleId);
        Assert.Equal(0x40, proposal.PriorityClass);   // 规则参数，非启发式 BelowNormal
    }

    [Fact]
    public void NullConfig_HeuristicDisabled()
    {
        // 不传启发式配置 = 纯规则模式（向后兼容：ReplayRunner/PolicyRunner 旧调用不受影响）
        var proposals = PolicyEngine.Evaluate(SaturatedScenario(), heuristic: null);

        Assert.Empty(proposals);
    }

    [Fact]
    public void CustomConfig_ThresholdsApply()
    {
        // 参数化验证：更饱和才触发 / 更长干预时长 / 更强动作
        var config = new HeuristicConfig
        {
            SystemSaturationPercent = 98,
            ProcessCpuPercent = 80,
            DurationMs = 60_000,
            PriorityClass = 0x40,   // Idle
        };

        // 系统 95% < 98% → 不触发
        Assert.Empty(PolicyEngine.Evaluate(SaturatedScenario(), config));

        var input = Input(99, foregroundPid: 100, (100, "editor.exe", 10), (200, "compiler.exe", 85));
        var proposal = Assert.Single(PolicyEngine.Evaluate(input, config));
        Assert.Equal(60_000, proposal.DurationMs);
        Assert.Equal(0x40, proposal.PriorityClass);
    }
}
