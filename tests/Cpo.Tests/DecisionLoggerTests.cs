using System.Text.Json;
using Cpo.Core.Engine;
using Cpo.Core.Rules;
using Cpo.Core.Telemetry;
using Xunit;

namespace Cpo.Tests;

public class DecisionLoggerTests
{
    private static PolicyProposal Proposal() => new()
    {
        TsMs = 12345,
        Trigger = "rule:r1",
        TargetPid = 42,
        TargetName = "msbuild.exe",
        Action = ProposalActionKind.SetPriority,
        PriorityClass = 0x4000,
        DurationMs = 30_000,
        Reason = "规则 r1: 建议优先级 BelowNormal",
        RuleId = "r1",
    };

    [Fact]
    public void ToDecisionEvent_MapsAllFields()
    {
        var evt = DecisionLogger.ToDecisionEvent(Proposal(), DecisionMode.Supervised);

        Assert.Equal(TelemetryEventTypes.PolicyDecision, evt.Type);
        Assert.Equal(12345, evt.TsMs);
        Assert.Equal("rule:r1", evt.Trigger);
        Assert.Equal(42, evt.TargetPid);
        Assert.Equal("msbuild.exe", evt.TargetName);
        Assert.Equal(DecisionMode.Supervised, evt.Mode);

        var conclusion = JsonDocument.Parse(evt.ConclusionJson);
        Assert.Equal("SetPriority", conclusion.RootElement.GetProperty("action").GetString());
        Assert.Equal(16384, conclusion.RootElement.GetProperty("priorityClass").GetInt32());
        Assert.True(conclusion.RootElement.GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public void ToActionEvent_Succeeded_IncludesPreviousState()
    {
        var original = new ProcessControlState(42, "msbuild.exe", 0x20, 0xFF);
        var exec = new ExecutionEvent(
            Proposal(), true, null, original, 12345);

        var evt = DecisionLogger.ToActionEvent(exec);

        Assert.Equal(TelemetryEventTypes.PolicyAction, evt.Type);
        Assert.Equal(ActionKind.SetPriority, evt.Kind);
        Assert.Equal(ActionResult.Succeeded, evt.Result);
        Assert.Null(evt.Error);

        var previous = JsonDocument.Parse(evt.PreviousJson!);
        Assert.Equal(32, previous.RootElement.GetProperty("priorityClass").GetInt32());
        Assert.Equal(255UL, previous.RootElement.GetProperty("affinityMask").GetUInt64());
    }

    [Fact]
    public void ToActionEvent_Failed_IncludesError()
    {
        var exec = new ExecutionEvent(Proposal(), false, "access denied", null, 12345);
        var evt = DecisionLogger.ToActionEvent(exec);

        Assert.Equal(ActionResult.Failed, evt.Result);
        Assert.Equal("access denied", evt.Error);
        Assert.Null(evt.PreviousJson);
    }

    [Fact]
    public void ToActionEvent_Restore_KindIsRestore()
    {
        var original = new ProcessControlState(42, "msbuild.exe", 0x20, 0xFF);
        var restoreProposal = Proposal() with
        {
            Action = ProposalActionKind.Restore,
            PriorityClass = 0x20,
            Trigger = "restore",
        };
        var exec = new ExecutionEvent(restoreProposal, true, null, original, 20000);

        var evt = DecisionLogger.ToActionEvent(exec);

        Assert.Equal(ActionKind.Restore, evt.Kind);
        Assert.Equal(20000, evt.TsMs);
    }
}

public class RuleStoreTests
{
    [Fact]
    public void Add_Remove_ChangeLog_TracksEvents()
    {
        var store = new RuleStore();
        var rule = new PolicyRule
        {
            Id = "r1", ProcessPattern = "msbuild.exe",
            Action = RuleActionKind.SetPriority, PriorityClass = 0x4000,
        };

        store.Add(rule);
        Assert.Single(store.Rules);

        store.Remove("r1");
        Assert.Empty(store.Rules);

        var log = store.ChangeLog;
        Assert.Equal(2, log.Count);
        Assert.Equal(RuleChangeKind.Added, log[0].ChangeKind);
        Assert.Equal(RuleChangeKind.Removed, log[1].ChangeKind);
        Assert.Equal(TelemetryEventTypes.RuleChanged, log[0].Type);
        Assert.Equal("r1", log[0].RuleId);
    }

    [Fact]
    public void Add_SameId_ReplacesRule()
    {
        var store = new RuleStore();
        store.Add(new PolicyRule { Id = "r1", ProcessPattern = "a.exe", Action = RuleActionKind.SetPriority, PriorityClass = 0x4000 });
        store.Add(new PolicyRule { Id = "r1", ProcessPattern = "b.exe", Action = RuleActionKind.SetPriority, PriorityClass = 0x40 });

        Assert.Single(store.Rules);
        Assert.Equal("b.exe", store.Rules[0].ProcessPattern);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cpo-rules-{Guid.NewGuid():N}.json");
        try
        {
            var store = new RuleStore();
            store.Add(new PolicyRule { Id = "r1", ProcessPattern = "msbuild.exe", Action = RuleActionKind.SetBoth, PriorityClass = 0x4000, AffinityMask = 0b11 });
            store.SaveToFile(path);

            var loaded = new RuleStore();
            loaded.LoadFromFile(path);

            var rule = Assert.Single(loaded.Rules);
            Assert.Equal("r1", rule.Id);
            Assert.Equal("msbuild.exe", rule.ProcessPattern);
            Assert.Equal(RuleActionKind.SetBoth, rule.Action);
            Assert.Equal(0x4000, rule.PriorityClass);
            Assert.Equal(0b11UL, rule.AffinityMask);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromFile_MissingFile_EmptyRules()
    {
        var store = new RuleStore();
        store.LoadFromFile("C:\\nonexistent\\rules.json");
        Assert.Empty(store.Rules);
    }
}
