using System;
using System.Linq;
using Cpo.Core.Replay;
using Cpo.Core.Rules;
using Cpo.Core.Storage;
using Cpo.Core.Telemetry;

// M2 验收：用真实录制轨迹离线回放 + 策略评估
var dbPath = Environment.GetEnvironmentVariable("REPLAY_DB") ?? @"C:\Users\bubbl\AppData\Local\Temp\cpo-m2-replay.db";
var rules = new[]
{
    new PolicyRule
    {
        Id = "demo.build-tools", ProcessPattern = "*build*",
        Action = RuleActionKind.SetPriority, PriorityClass = 0x4000,
    },
    new PolicyRule
    {
        Id = "demo.tools", ProcessPattern = "*",
        Action = RuleActionKind.SetPriority, PriorityClass = 0x4000,
    },
};

await using var store = new SqliteTelemetryStore(dbPath);
await store.InitializeAsync();

Console.WriteLine("=== M2 回放验收：真实轨迹 ===");
Console.WriteLine($"数据库: {dbPath}");

// 统计事件类型分布
var typeCount = new Dictionary<string, long>();
var allEvents = new List<TelemetryEvent>();
await foreach (var e in store.QueryAsync(new EventQuery { Limit = 50000 }))
{
    typeCount[e.Type] = typeCount.GetValueOrDefault(e.Type) + 1;
    allEvents.Add(e);
}
Console.WriteLine($"事件总数: {allEvents.Count}");
foreach (var kv in typeCount.OrderByDescending(k => k.Value))
    Console.WriteLine($"  {kv.Key,-18} {kv.Value,6}");

// 回放评估（规则1：*build* 规则）
Console.WriteLine("\n=== 回放 1：仅 *build* 规则 ===");
var buildOnly = new[] { rules[0] };
var s1 = ReplayRunner.Evaluate(allEvents, buildOnly, coreCount: Environment.ProcessorCount);
Console.WriteLine($"帧数: {s1.FrameCount} | 总建议: {s1.TotalProposals} | 规则匹配建议: {s1.MatchedRuleProposals} | 平均/帧: {s1.AvgProposalsPerFrame:F2} | 时长: {s1.DurationMs}ms");

// 回放评估（规则2：任意进程 → 观察引擎在压力期的表现）
Console.WriteLine("\n=== 回放 2：* 全部进程规则 ===");
var s2 = ReplayRunner.Evaluate(allEvents, rules, coreCount: Environment.ProcessorCount);
Console.WriteLine($"帧数: {s2.FrameCount} | 总建议: {s2.TotalProposals} | 规则匹配建议: {s2.MatchedRuleProposals} | 平均/帧: {s2.AvgProposalsPerFrame:F2} | 时长: {s2.DurationMs}ms");

// 前台保护回放：假设 powershell 为前台 → 应少一条
Console.WriteLine("\n=== 回放 3：* 规则 + 前台保护（假设 powershell 前台）===");
var s3 = ReplayRunner.Evaluate(allEvents, rules, coreCount: Environment.ProcessorCount, foregroundPid: 9999);
Console.WriteLine($"总建议: {s3.TotalProposals}（前台保护生效时减少）");

return 0;
