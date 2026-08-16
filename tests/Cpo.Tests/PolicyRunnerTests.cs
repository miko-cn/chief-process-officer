using Cpo.Core.Engine;
using Cpo.Core.Rules;
using Cpo.Core.Telemetry;
using Cpo.Service;
using Xunit;

namespace Cpo.Tests;

/// <summary>
/// PolicyRunner ProBalance 开关测试（会话⑫定案）：
/// 开关只控制"自动干预执行"；关闭时立即恢复全部生效干预；切换动作留痕 policy.intervention_toggled。
/// </summary>
public class PolicyRunnerTests
{
    private static PolicyRunner CreateRunner(FakeTelemetryStore store, FakeProcessController controller,
        out RuleStore rules, bool interventionEnabled = true)
    {
        rules = new RuleStore();
        rules.Add(new PolicyRule
        {
            Id = "r1",
            ProcessPattern = "powershell",
            Action = RuleActionKind.SetPriority,
            PriorityClass = 0x4000,
            Source = RuleChangeSource.User,
        });

        return new PolicyRunner(store, controller, rules, coreCount: 8)
        {
            Mode = DecisionMode.Automatic,
            InterventionEnabled = interventionEnabled,
        };
    }

    /// <summary>构造一个"窗口内命中规则"的样本集（system 90% + powershell 90%，当前时刻前 1s）。</summary>
    private static void SeedStormSample(FakeTelemetryStore store)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.Events.Add(new CpuSampleEvent(now - 1000, SampleScope.System, null, null, 90, 100, 8, 1000));
        store.Events.Add(new CpuSampleEvent(now - 1000, SampleScope.Process, 42, "powershell", 90, 100, null, 1000));
    }

    [Fact]
    public async Task Evaluate_InterventionDisabled_DoesNotExecute()
    {
        var store = new FakeTelemetryStore();
        SeedStormSample(store);
        var controller = new FakeProcessController((42, "powershell", 0x20, 0xFF));
        var runner = CreateRunner(store, controller, out _, interventionEnabled: false);
        await using (runner)
        {
            await runner.EvaluateAsync();

            // 开关关：只建议不执行
            Assert.Empty(controller.Calls);
            Assert.Contains(store.Events, e => e.Type == TelemetryEventTypes.PolicyDecision);
            Assert.DoesNotContain(store.Events, e => e.Type == TelemetryEventTypes.PolicyAction);
        }
    }

    [Fact]
    public async Task Evaluate_InterventionEnabled_Executes()
    {
        var store = new FakeTelemetryStore();
        SeedStormSample(store);
        var controller = new FakeProcessController((42, "powershell", 0x20, 0xFF));
        var runner = CreateRunner(store, controller, out _);
        await using (runner)
        {
            await runner.EvaluateAsync();

            Assert.Contains(controller.Calls, c => c == "prio:42=16384");
            Assert.Contains(store.Events, e => e.Type == TelemetryEventTypes.PolicyAction);
        }
    }

    [Fact]
    public async Task SetInterventionEnabled_False_RestoresActiveAndLogs()
    {
        var store = new FakeTelemetryStore();
        SeedStormSample(store);
        var controller = new FakeProcessController((42, "powershell", 0x20, 0xFF));
        var runner = CreateRunner(store, controller, out _);
        await using (runner)
        {
            await runner.EvaluateAsync();
            Assert.Contains(controller.Calls, c => c == "prio:42=16384");   // 干预已生效

            var toggle = await runner.SetInterventionEnabledAsync(false);

            Assert.False(toggle.Enabled);
            // 生效中的干预立即恢复原值 0x20=32
            Assert.Contains(controller.Calls, c => c == "prio:42=32");
            // 留痕：开关切换 + 恢复动作
            var toggled = store.Events.OfType<InterventionToggledEvent>().Single();
            Assert.False(toggled.Enabled);
            Assert.Equal("app", toggled.Source);
            Assert.Contains(store.Events, e => e.Type == TelemetryEventTypes.PolicyAction);
        }
    }

    [Fact]
    public async Task SetInterventionEnabled_True_LogsToggleEvent()
    {
        var store = new FakeTelemetryStore();
        var controller = new FakeProcessController();
        var runner = CreateRunner(store, controller, out _, interventionEnabled: false);
        await using (runner)
        {
            var toggle = await runner.SetInterventionEnabledAsync(true);

            Assert.True(toggle.Enabled);
            var toggled = store.Events.OfType<InterventionToggledEvent>().Single();
            Assert.True(toggled.Enabled);
            Assert.Equal("app", toggled.Source);
        }
    }

    // ─── 启发式（响应性保护）+ 前台输入集成 ───

    /// <summary>无显式规则的 runner：启发式是唯一决策来源（验证启发式真实链路）。</summary>
    private static PolicyRunner CreateHeuristicRunner(FakeTelemetryStore store, FakeProcessController controller)
    {
        var runner = new PolicyRunner(store, controller, new RuleStore(), coreCount: 8)
        {
            Mode = DecisionMode.Automatic,
            InterventionEnabled = true,
        };
        runner.Heuristic = new HeuristicConfig();   // 默认保守配置（系统饱和 90% / 进程 50% / 30s）
        return runner;
    }

    [Fact]
    public async Task Evaluate_Heuristic_NoForegroundInfo_DoesNotExecute()
    {
        var store = new FakeTelemetryStore();
        SeedStormSample(store);
        var controller = new FakeProcessController((42, "powershell", 0x20, 0xFF));
        var runner = CreateHeuristicRunner(store, controller);
        await using (runner)
        {
            await runner.EvaluateAsync();

            // 无前台信息 → 启发式保守模式：不干预（SPEC §6 定案）
            Assert.Empty(controller.Calls);
            Assert.DoesNotContain(store.Events, e => e.Type == TelemetryEventTypes.PolicyAction);
        }
    }

    [Fact]
    public async Task Evaluate_Heuristic_ForegroundSet_ExecutesBackgroundHogger()
    {
        var store = new FakeTelemetryStore();
        SeedStormSample(store);
        var controller = new FakeProcessController((42, "powershell", 0x20, 0xFF));
        var runner = CreateHeuristicRunner(store, controller);
        runner.ForegroundPid = 999;   // 前台是别的进程（GUI 上报）
        await using (runner)
        {
            await runner.EvaluateAsync();

            // 系统饱和 + 后台挤占者 → 启发式触发：降为 BelowNormal (0x4000=16384)
            Assert.Contains(controller.Calls, c => c == "prio:42=16384");
            var decision = store.Events.OfType<PolicyDecisionEvent>().Single();
            Assert.Contains("heuristic.saturation", decision.Trigger);
        }
    }

    [Fact]
    public async Task Evaluate_Heuristic_ForegroundIsHogger_Skips()
    {
        var store = new FakeTelemetryStore();
        SeedStormSample(store);
        var controller = new FakeProcessController((42, "powershell", 0x20, 0xFF));
        var runner = CreateHeuristicRunner(store, controller);
        runner.ForegroundPid = 42;    // 挤占者就是前台（用户正在用）→ 保护不降
        await using (runner)
        {
            await runner.EvaluateAsync();

            Assert.Empty(controller.Calls);
            Assert.DoesNotContain(store.Events, e => e.Type == TelemetryEventTypes.PolicyAction);
        }
    }

    [Fact]
    public async Task Evaluate_Heuristic_SkipsForegroundProcessTree()
    {
        var store = new FakeTelemetryStore();
        SeedStormSample(store);
        var controller = new FakeProcessController((42, "powershell", 0x20, 0xFF));
        // 前台 999 的进程树包含 42（用户正在用的程序的子进程）→ 绝不干预
        controller.ProcessTrees[999] = new HashSet<int> { 999, 42 };
        var runner = CreateHeuristicRunner(store, controller);
        runner.ForegroundPid = 999;
        await using (runner)
        {
            await runner.EvaluateAsync();

            Assert.Empty(controller.Calls);
            Assert.DoesNotContain(store.Events, e => e.Type == TelemetryEventTypes.PolicyAction);
        }
    }

    [Fact]
    public async Task Evaluate_RestoresWhenConditionClears()
    {
        var store = new FakeTelemetryStore();
        SeedStormSample(store);
        var controller = new FakeProcessController((42, "powershell", 0x20, 0xFF));
        var runner = CreateHeuristicRunner(store, controller);
        runner.ForegroundPid = 999;
        await using (runner)
        {
            // 风暴：执行降优
            await runner.EvaluateAsync();
            Assert.Contains(controller.Calls, c => c == "prio:42=16384");

            // 条件解除：进程 CPU 降到 10%（不再挤占）→ 下一轮立即恢复，不等 30s 超时
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            store.Events.RemoveAll(e => e is CpuSampleEvent { Scope: SampleScope.Process });
            store.Events.Add(new CpuSampleEvent(now - 1000, SampleScope.Process, 42, "powershell", 10, 500, null, 1000));

            await runner.EvaluateAsync();

            Assert.Contains(controller.Calls, c => c == "prio:42=32");   // 恢复原值 0x20=32
            Assert.Contains(store.Events, e => e.Type == TelemetryEventTypes.PolicyAction
                                               && e is PolicyActionEvent { Kind: ActionKind.Restore });
        }
    }

    [Fact]
    public async Task Evaluate_RuleIntervention_NotRestoredOnCpuDrop()
    {
        // 规则干预尊重用户显式语义：CPU 降了也不提前恢复（只有启发式干预条件解除恢复）
        var store = new FakeTelemetryStore();
        SeedStormSample(store);
        var controller = new FakeProcessController((42, "powershell", 0x20, 0xFF));
        var runner = CreateRunner(store, controller, out _);   // 含规则 r1（powershell）
        runner.ForegroundPid = 999;
        await using (runner)
        {
            await runner.EvaluateAsync();
            Assert.Contains(controller.Calls, c => c == "prio:42=16384");

            // CPU 降了
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            store.Events.RemoveAll(e => e is CpuSampleEvent { Scope: SampleScope.Process });
            store.Events.Add(new CpuSampleEvent(now - 1000, SampleScope.Process, 42, "powershell", 10, 500, null, 1000));

            await runner.EvaluateAsync();

            // 规则干预仍在生效（未被提前恢复）
            Assert.DoesNotContain(controller.Calls, c => c == "prio:42=32");
            Assert.DoesNotContain(store.Events, e => e is PolicyActionEvent { Kind: ActionKind.Restore });
        }
    }
}
