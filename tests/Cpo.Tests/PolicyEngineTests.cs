using Cpo.Core.Engine;
using Cpo.Core.Rules;
using Xunit;

namespace Cpo.Tests;

public class PolicyEngineTests
{
    private static EngineInput Input(
        params (int Pid, string Name, double Cpu)[] processes) => new()
    {
        Processes = processes.Select(p => new ProcessState(p.Pid, p.Name, p.Cpu, 0)).ToArray(),
        SystemCpuPercent = 50,
        ForegroundPid = null,
        Rules = Array.Empty<PolicyRule>(),
        CoreCount = 8,
    };

    private static PolicyRule Rule(string id, string pattern, RuleActionKind action, int? priority = null, ulong? mask = null) =>
        new()
        {
            Id = id,
            ProcessPattern = pattern,
            Action = action,
            PriorityClass = priority,
            AffinityMask = mask,
        };

    [Fact]
    public void Evaluate_NoRules_NoProposals()
    {
        var proposals = PolicyEngine.Evaluate(Input((1, "a.exe", 10)));
        Assert.Empty(proposals);
    }

    [Fact]
    public void Evaluate_MatchingRule_ProducesProposal()
    {
        var rules = new[] { Rule("r1", "msbuild.exe", RuleActionKind.SetPriority, priority: 0x4000) };
        var input = Input((100, "msbuild.exe", 60), (200, "notepad.exe", 5)) with { Rules = rules };

        var proposals = PolicyEngine.Evaluate(input);

        var proposal = Assert.Single(proposals);
        Assert.Equal(100, proposal.TargetPid);
        Assert.Equal("msbuild.exe", proposal.TargetName);
        Assert.Equal(ProposalActionKind.SetPriority, proposal.Action);
        Assert.Equal(0x4000, proposal.PriorityClass);
        Assert.Equal("r1", proposal.RuleId);
        Assert.Contains("msbuild.exe", proposal.Reason);
    }

    [Fact]
    public void Evaluate_FirstRuleWins()
    {
        var rules = new[]
        {
            Rule("specific", "msbuild.exe", RuleActionKind.SetPriority, priority: 0x4000),
            Rule("generic", "*.exe", RuleActionKind.SetPriority, priority: 0x40),
        };
        var input = Input((100, "msbuild.exe", 60)) with { Rules = rules };

        var proposal = Assert.Single(PolicyEngine.Evaluate(input));
        Assert.Equal("specific", proposal.RuleId);
        Assert.Equal(0x4000, proposal.PriorityClass);
    }

    [Fact]
    public void Evaluate_ForegroundProcess_IsProtected()
    {
        var rules = new[] { Rule("r1", "*", RuleActionKind.SetPriority, priority: 0x40) };
        var input = Input((100, "foreground.exe", 90), (200, "background.exe", 90)) with
        {
            Rules = rules,
            ForegroundPid = 100,
        };

        var proposals = PolicyEngine.Evaluate(input);

        var proposal = Assert.Single(proposals);
        Assert.Equal(200, proposal.TargetPid);
        Assert.DoesNotContain(proposals, p => p.TargetPid == 100);
    }

    [Fact]
    public void Evaluate_AffinityRule_CarriesMask()
    {
        var rules = new[] { Rule("r2", "chrome*.exe", RuleActionKind.SetAffinity, mask: 0b00000011) };
        var input = Input((300, "chrome.exe", 40)) with { Rules = rules };

        var proposal = Assert.Single(PolicyEngine.Evaluate(input));
        Assert.Equal(ProposalActionKind.SetAffinity, proposal.Action);
        Assert.Equal(0b00000011UL, proposal.AffinityMask);
        Assert.Contains("0x3", proposal.Reason);
    }

    [Fact]
    public void Evaluate_UnmatchedProcess_NoProposal()
    {
        var rules = new[] { Rule("r1", "msbuild.exe", RuleActionKind.SetPriority, priority: 0x4000) };
        var input = Input((400, "notepad.exe", 99)) with { Rules = rules };

        Assert.Empty(PolicyEngine.Evaluate(input));
    }

    [Fact]
    public void Evaluate_ProposalHasDuration_WhenRuleSpecifies()
    {
        var rule = Rule("r3", "x.exe", RuleActionKind.SetPriority, priority: 0x40) with { DurationMs = 30_000 };
        var input = Input((1, "x.exe", 10)) with { Rules = new[] { rule } };

        var proposal = Assert.Single(PolicyEngine.Evaluate(input));
        Assert.Equal(30_000, proposal.DurationMs);
    }
}
