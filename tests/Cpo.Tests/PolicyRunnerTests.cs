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
}
