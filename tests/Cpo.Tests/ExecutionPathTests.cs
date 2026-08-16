using Cpo.Core.Engine;
using Xunit;

namespace Cpo.Tests;

public class ExecutionPathTests
{
    private sealed class FakeController : IProcessController
    {
        public Dictionary<int, ProcessControlState> Processes { get; } = new();
        public List<string> Calls { get; } = new();

        public FakeController(params (int Pid, string Name, int Priority, ulong Mask)[] processes)
        {
            foreach (var (pid, name, prio, mask) in processes)
            {
                Processes[pid] = new ProcessControlState(pid, name, prio, mask);
            }
        }

        public ProcessControlState? GetState(int pid) => Processes.TryGetValue(pid, out var s) ? s : null;

        public InterventionResult SetPriorityClass(int pid, int priorityClass)
        {
            Calls.Add($"prio:{pid}={priorityClass}");
            if (!Processes.TryGetValue(pid, out var s))
            {
                return new InterventionResult(false, "not found");
            }

            Processes[pid] = s with { PriorityClass = priorityClass };
            return new InterventionResult(true, null);
        }

        public InterventionResult SetAffinityMask(int pid, ulong mask)
        {
            Calls.Add($"aff:{pid}={mask}");
            if (!Processes.TryGetValue(pid, out var s))
            {
                return new InterventionResult(false, "not found");
            }

            Processes[pid] = s with { AffinityMask = mask };
            return new InterventionResult(true, null);
        }

        public IReadOnlySet<int> GetDescendantPids(int rootPid) => new HashSet<int> { rootPid };
    }

    private static PolicyProposal Proposal(int pid, string name, ProposalActionKind action,
        int? priority = null, ulong? mask = null, long? duration = null, long ts = 1000) => new()
    {
        TsMs = ts,
        Trigger = "rule:r1",
        TargetPid = pid,
        TargetName = name,
        Action = action,
        PriorityClass = priority,
        AffinityMask = mask,
        DurationMs = duration,
        Reason = "test",
        RuleId = "r1",
    };

    [Fact]
    public void Execute_SetPriority_AppliesAndRecordsOriginal()
    {
        var controller = new FakeController((42, "msbuild.exe", 0x20, 0xFF));
        var path = new ExecutionPath(controller);

        var result = path.Execute(Proposal(42, "msbuild.exe", ProposalActionKind.SetPriority, priority: 0x4000));

        Assert.True(result.Succeeded);
        Assert.Equal(0x4000, controller.Processes[42].PriorityClass);
        Assert.Equal(0x20, result.OriginalState!.PriorityClass);   // 原值已记录
        var intervention = Assert.Single(path.ActiveInterventions.Values);
        Assert.Equal(0x20, intervention.OriginalState.PriorityClass);
    }

    [Fact]
    public void Execute_SameProposalTwice_IsIdempotent()
    {
        var controller = new FakeController((42, "msbuild.exe", 0x20, 0xFF));
        var path = new ExecutionPath(controller);

        path.Execute(Proposal(42, "msbuild.exe", ProposalActionKind.SetPriority, priority: 0x4000));
        var second = path.Execute(Proposal(42, "msbuild.exe", ProposalActionKind.SetPriority, priority: 0x4000));

        Assert.True(second.Succeeded);
        Assert.Contains("already active", second.Error);
        Assert.Single(path.ExecutionLog);   // 第二次未产生新执行
        Assert.Single(controller.Calls);    // 底层只调了一次
    }

    [Fact]
    public void Execute_DifferentParameters_RestoresThenApplies()
    {
        var controller = new FakeController((42, "msbuild.exe", 0x20, 0xFF));
        var path = new ExecutionPath(controller);

        path.Execute(Proposal(42, "msbuild.exe", ProposalActionKind.SetPriority, priority: 0x4000));
        path.Execute(Proposal(42, "msbuild.exe", ProposalActionKind.SetPriority, priority: 0x40));

        // 先恢复原值 0x20，再应用 0x40
        Assert.Equal(0x40, controller.Processes[42].PriorityClass);
        Assert.Equal(new[] { "prio:42=16384", "prio:42=32", "prio:42=64" }, controller.Calls);
        Assert.Contains(path.ExecutionLog, e => e.Proposal.Action == ProposalActionKind.Restore);
    }

    [Fact]
    public void Restore_ReturnsToOriginalValues()
    {
        var controller = new FakeController((42, "msbuild.exe", 0x20, 0xFF));
        var path = new ExecutionPath(controller);

        path.Execute(Proposal(42, "msbuild.exe", ProposalActionKind.SetPriority, priority: 0x4000));
        var restore = path.Restore(42);

        Assert.NotNull(restore);
        Assert.True(restore.Succeeded);
        Assert.Equal(ProposalActionKind.Restore, restore.Proposal.Action);
        Assert.Equal(0x20, controller.Processes[42].PriorityClass);
        Assert.Empty(path.ActiveInterventions);
        // 恢复动作同样入日志（SPEC：恢复动作写入决策日志）
        Assert.Contains(path.ExecutionLog, e => e.Proposal.Trigger == "restore");
    }

    [Fact]
    public void Restore_NoActiveIntervention_ReturnsNull()
    {
        var path = new ExecutionPath(new FakeController());
        Assert.Null(path.Restore(999));
    }

    [Fact]
    public void ReapExpired_RestoresExpiredInterventions()
    {
        var controller = new FakeController((42, "msbuild.exe", 0x20, 0xFF));
        var path = new ExecutionPath(controller);

        path.Execute(Proposal(42, "msbuild.exe", ProposalActionKind.SetPriority,
            priority: 0x4000, duration: 1000, ts: 0));

        // 2000ms 后：1000ms 时长的干预应过期
        var restored = path.ReapExpired(2000);

        Assert.Equal(1, restored);
        Assert.Equal(0x20, controller.Processes[42].PriorityClass);
        Assert.Empty(path.ActiveInterventions);
    }

    [Fact]
    public void ReapExpired_InfiniteDuration_NeverExpires()
    {
        var controller = new FakeController((42, "msbuild.exe", 0x20, 0xFF));
        var path = new ExecutionPath(controller);

        path.Execute(Proposal(42, "msbuild.exe", ProposalActionKind.SetPriority,
            priority: 0x4000, duration: null, ts: 0));

        Assert.Equal(0, path.ReapExpired(10_000_000));
        Assert.Single(path.ActiveInterventions);
    }

    [Fact]
    public void ReapExpired_GoneProcess_RemovedSilently()
    {
        var controller = new FakeController((42, "msbuild.exe", 0x20, 0xFF));
        var path = new ExecutionPath(controller);

        path.Execute(Proposal(42, "msbuild.exe", ProposalActionKind.SetPriority,
            priority: 0x4000, duration: 100, ts: 0));

        // 进程消失
        controller.Processes.Remove(42);
        var restored = path.ReapExpired(200);

        Assert.Equal(0, restored);                  // 不产生恢复动作（进程已不在）
        Assert.Empty(path.ActiveInterventions);     // 但干预被移除
    }

    [Fact]
    public void RestoreAll_OnShutdown_RecoversEverything()
    {
        var controller = new FakeController(
            (1, "a.exe", 0x20, 0xFF),
            (2, "b.exe", 0x20, 0xFF));
        var path = new ExecutionPath(controller);

        path.Execute(Proposal(1, "a.exe", ProposalActionKind.SetPriority, priority: 0x4000));
        path.Execute(Proposal(2, "b.exe", ProposalActionKind.SetAffinity, mask: 0b00000001));

        var restored = path.RestoreAll();

        Assert.Equal(2, restored);
        Assert.Equal(0x20, controller.Processes[1].PriorityClass);
        Assert.Equal(0xFFUL, controller.Processes[2].AffinityMask);
        Assert.Empty(path.ActiveInterventions);
    }

    [Fact]
    public void Execute_ProcessNotFound_FailsGracefully()
    {
        var controller = new FakeController();
        var path = new ExecutionPath(controller);

        var result = path.Execute(Proposal(999, "ghost.exe", ProposalActionKind.SetPriority, priority: 0x4000));

        Assert.False(result.Succeeded);
        Assert.Contains("not found", result.Error);
        Assert.Empty(path.ActiveInterventions);
    }

    [Fact]
    public void Execute_AfterRestore_CooldownBlocksReapply()
    {
        var controller = new FakeController((42, "msbuild.exe", 0x20, 0xFF));
        var path = new ExecutionPath(controller);

        // t=0 应用，DurationMs=3000
        path.Execute(Proposal(42, "msbuild.exe", ProposalActionKind.SetPriority,
            priority: 0x4000, duration: 3000, ts: 0));

        // t=3000 超时恢复
        Assert.Equal(1, path.ReapExpired(3000));
        Assert.Equal(0x20, controller.Processes[42].PriorityClass);

        // t=4000（恢复后 1s，仍在冷却期）：不重新应用
        var duringCooldown = path.Execute(Proposal(42, "msbuild.exe", ProposalActionKind.SetPriority,
            priority: 0x4000, duration: 3000, ts: 4000));
        Assert.True(duringCooldown.Succeeded);
        Assert.Contains("cooldown", duringCooldown.Error);
        Assert.Equal(0x20, controller.Processes[42].PriorityClass);   // 保持原值

        // t=7000（超过冷却期）：重新应用
        var afterCooldown = path.Execute(Proposal(42, "msbuild.exe", ProposalActionKind.SetPriority,
            priority: 0x4000, duration: 3000, ts: 7000));
        Assert.True(afterCooldown.Succeeded);
        Assert.Equal(0x4000, controller.Processes[42].PriorityClass);
    }

    [Fact]
    public void Execute_SetBoth_AppliesPriorityThenAffinity()
    {
        var controller = new FakeController((42, "msbuild.exe", 0x20, 0xFF));
        var path = new ExecutionPath(controller);

        var result = path.Execute(Proposal(42, "msbuild.exe", ProposalActionKind.SetBoth,
            priority: 0x4000, mask: 0b00000011));

        Assert.True(result.Succeeded);
        Assert.Equal(0x4000, controller.Processes[42].PriorityClass);
        Assert.Equal(0b00000011UL, controller.Processes[42].AffinityMask);
    }
}
