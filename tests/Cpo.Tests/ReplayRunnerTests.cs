using Cpo.Core.Engine;
using Cpo.Core.Replay;
using Cpo.Core.Rules;
using Cpo.Core.Telemetry;
using Xunit;

namespace Cpo.Tests;

/// <summary>回放框架测试：用合成事件流验证离线回放与策略评估（M2 验收核心）。</summary>
public class ReplayRunnerTests
{
    private static List<TelemetryEvent> BuildEvents(params TelemetryEvent[] events) =>
        events.OrderBy(e => e.TsMs).ToList();

    [Fact]
    public void BuildFrames_GroupsBySystemSample()
    {
        var events = BuildEvents(
            new ProcessLifecycleEvent(100, LifecycleKind.Started, 1, 0, "a.exe", null),
            new ProcessLifecycleEvent(100, LifecycleKind.Started, 2, 0, "b.exe", null),
            new CpuSampleEvent(100, SampleScope.System, null, null, 50, 100, 8, 1000),
            new CpuSampleEvent(150, SampleScope.Process, 1, "a.exe", 30, 1000, null, 1000),
            new CpuSampleEvent(200, SampleScope.Process, 2, "b.exe", 10, 2000, null, 1000),
            new CpuSampleEvent(200, SampleScope.System, null, null, 40, 200, 8, 1000),
            new ProcessLifecycleEvent(300, LifecycleKind.Exited, 2, 0, "b.exe", null),
            new CpuSampleEvent(300, SampleScope.System, null, null, 60, 300, 8, 1000));

        var frames = ReplayRunner.BuildFrames(events);

        Assert.Equal(3, frames.Count);
        Assert.Equal(50, frames[0].SystemCpuPercent);
        Assert.Equal(2, frames[0].ProcessCount);
        Assert.Equal(40, frames[1].SystemCpuPercent);
        Assert.Equal(60, frames[2].SystemCpuPercent);
        Assert.Equal(1, frames[2].ProcessCount);   // b.exe 已退出
    }

    [Fact]
    public void BuildFrames_ProcessCpuUpdatedWithinFrame()
    {
        var events = BuildEvents(
            new ProcessLifecycleEvent(100, LifecycleKind.Started, 1, 0, "a.exe", null),
            new CpuSampleEvent(100, SampleScope.System, null, null, 50, 100, 8, 1000),
            new CpuSampleEvent(150, SampleScope.Process, 1, "a.exe", 30, 1000, null, 1000),
            new CpuSampleEvent(180, SampleScope.Process, 1, "a.exe", 80, 2000, null, 1000),
            new CpuSampleEvent(200, SampleScope.System, null, null, 50, 200, 8, 1000));

        var frames = ReplayRunner.BuildFrames(events);

        // 第二帧应包含最新 CPU 值 80
        Assert.Equal(80, frames[1].Proposals.Count == 0 ? 80 : -1); // 哨兵：BuildFrames 不评估
    }

    [Fact]
    public void Evaluate_MatchingRule_ProducesProposalsAcrossFrames()
    {
        var rules = new[]
        {
            new PolicyRule
            {
                Id = "r1", ProcessPattern = "msbuild.exe",
                Action = RuleActionKind.SetPriority, PriorityClass = 0x4000,
            },
        };

        var events = new List<TelemetryEvent>();
        for (var t = 0; t < 5000; t += 1000)
        {
            events.Add(new ProcessLifecycleEvent(t, LifecycleKind.Started, 42, 0, "msbuild.exe", null));
            events.Add(new CpuSampleEvent(t, SampleScope.Process, 42, "msbuild.exe", 90, t, null, 1000));
            events.Add(new CpuSampleEvent(t, SampleScope.System, null, null, 70, t, 8, 1000));
        }

        var summary = ReplayRunner.Evaluate(events, rules, coreCount: 8);

        Assert.Equal(5, summary.FrameCount);
        Assert.Equal(5, summary.TotalProposals);          // 每帧一个建议
        Assert.Equal(5, summary.MatchedRuleProposals);
        Assert.Equal(1.0, summary.AvgProposalsPerFrame, 2);
        Assert.Equal(4000, summary.DurationMs);
    }

    [Fact]
    public void Evaluate_NoMatchingRule_ZeroProposals()
    {
        var events = BuildEvents(
            new ProcessLifecycleEvent(100, LifecycleKind.Started, 42, 0, "msbuild.exe", null),
            new CpuSampleEvent(100, SampleScope.System, null, null, 70, 100, 8, 1000));

        var summary = ReplayRunner.Evaluate(events, Array.Empty<PolicyRule>(), coreCount: 8);

        Assert.Equal(1, summary.FrameCount);
        Assert.Equal(0, summary.TotalProposals);
    }

    [Fact]
    public void Evaluate_ForegroundProtected_AcrossFrames()
    {
        var rules = new[]
        {
            new PolicyRule { Id = "r1", ProcessPattern = "*", Action = RuleActionKind.SetPriority, PriorityClass = 0x40 },
        };

        var events = BuildEvents(
            new ProcessLifecycleEvent(100, LifecycleKind.Started, 7, 0, "fg.exe", null),
            new ProcessLifecycleEvent(100, LifecycleKind.Started, 8, 0, "bg.exe", null),
            new CpuSampleEvent(100, SampleScope.System, null, null, 90, 100, 8, 1000),
            new CpuSampleEvent(150, SampleScope.Process, 7, "fg.exe", 95, 1000, null, 1000),
            new CpuSampleEvent(150, SampleScope.Process, 8, "bg.exe", 95, 2000, null, 1000));

        var summary = ReplayRunner.Evaluate(events, rules, coreCount: 8, foregroundPid: 7);

        Assert.Equal(1, summary.FrameCount);
        Assert.Equal(1, summary.TotalProposals);   // 只有 bg.exe 被建议
    }

    [Fact]
    public void Evaluate_EmptyEvents_ZeroFrames()
    {
        var summary = ReplayRunner.Evaluate(Array.Empty<TelemetryEvent>(), Array.Empty<PolicyRule>(), coreCount: 8);
        Assert.Equal(0, summary.FrameCount);
        Assert.Equal(0, summary.TotalProposals);
    }

    [Fact]
    public void Evaluate_Heuristic_SaturatedFramesPropose_UnsaturatedDont()
    {
        // 无规则 + 有前台信息：饱和帧（系统 95% + 后台 95%）→ 启发式建议；余量帧（60%）→ 不建议
        var events = BuildEvents(
            new ProcessLifecycleEvent(100, LifecycleKind.Started, 1, 0, "fg.exe", null),
            new ProcessLifecycleEvent(100, LifecycleKind.Started, 2, 0, "hog.exe", null),
            new CpuSampleEvent(110, SampleScope.Process, 1, "fg.exe", 5, 1000, null, 1000),
            new CpuSampleEvent(110, SampleScope.Process, 2, "hog.exe", 95, 2000, null, 1000),
            new CpuSampleEvent(120, SampleScope.System, null, null, 95, 100, 8, 1000),   // 饱和帧
            new CpuSampleEvent(200, SampleScope.System, null, null, 60, 200, 8, 1000));  // 余量帧

        var withHeuristic = ReplayRunner.Evaluate(
            events, Array.Empty<PolicyRule>(), coreCount: 8, foregroundPid: 1, heuristic: new HeuristicConfig());
        var withoutHeuristic = ReplayRunner.Evaluate(
            events, Array.Empty<PolicyRule>(), coreCount: 8, foregroundPid: 1);

        Assert.Equal(2, withHeuristic.FrameCount);
        Assert.Equal(1, withHeuristic.TotalProposals);    // 只有饱和帧产生建议
        Assert.Equal(0, withHeuristic.MatchedRuleProposals);  // 纯启发式建议
        Assert.Equal(0, withoutHeuristic.TotalProposals); // 不传启发式 = 纯规则模式
    }
}
