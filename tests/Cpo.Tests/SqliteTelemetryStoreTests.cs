using Cpo.Core.Storage;
using Cpo.Core.Telemetry;
using Xunit;

namespace Cpo.Tests;

/// <summary>SQLite 存储测试（schema §8 落盘形态）。全部用内存库，避免文件残留。</summary>
[Collection("NonParallelGrpc")]  // 与 gRPC 测试串行：DisposeAsync 的 ClearAllPools 影响共享内存库
public class SqliteTelemetryStoreTests : IAsyncLifetime
{
    private SqliteTelemetryStore? _store;

    public async Task InitializeAsync()
    {
        _store = SqliteTelemetryStore.CreateInMemory();
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }
    }

    private SqliteTelemetryStore Store => _store!;

    [Fact]
    public async Task Append_Then_Count_IsOne()
    {
        await Store.AppendAsync(new ProcessLifecycleEvent(100, LifecycleKind.Started, 1, 0, "a.exe", null));
        Assert.Equal(1, await Store.CountAsync());
    }

    [Fact]
    public async Task AppendBatch_CommitsAll()
    {
        var events = Enumerable.Range(0, 50)
            .Select(i => (TelemetryEvent)new CpuSampleEvent(i, SampleScope.System, null, null, i, i * 10, 8, 1000));
        await Store.AppendBatchAsync(events);

        Assert.Equal(50, await Store.CountAsync());
    }

    [Fact]
    public async Task Query_OrderedByTs_Ascending()
    {
        await Store.AppendBatchAsync(new TelemetryEvent[]
        {
            new CpuSampleEvent(300, SampleScope.System, null, null, 1, 1, 8, 1000),
            new CpuSampleEvent(100, SampleScope.System, null, null, 2, 2, 8, 1000),
            new CpuSampleEvent(200, SampleScope.System, null, null, 3, 3, 8, 1000),
        });

        var result = await ToListAsync(Store.QueryAsync(new EventQuery()));
        Assert.Equal(new long[] { 100, 200, 300 }, result.Select(e => e.TsMs));
    }

    [Fact]
    public async Task Query_FilterByType()
    {
        await Store.AppendBatchAsync(new TelemetryEvent[]
        {
            new ProcessLifecycleEvent(100, LifecycleKind.Started, 1, 0, "a.exe", null),
            new CpuSampleEvent(200, SampleScope.System, null, null, 5, 5, 8, 1000),
        });

        var result = await ToListAsync(Store.QueryAsync(new EventQuery { Type = TelemetryEventTypes.CpuSample }));
        Assert.Single(result);
        Assert.IsType<CpuSampleEvent>(result[0]);
    }

    [Fact]
    public async Task Query_FilterByPid_UsesPayloadJson()
    {
        await Store.AppendBatchAsync(new TelemetryEvent[]
        {
            new CpuSampleEvent(100, SampleScope.Process, 42, "a.exe", 1, 1, null, 1000),
            new CpuSampleEvent(200, SampleScope.Process, 43, "b.exe", 2, 2, null, 1000),
        });

        var result = await ToListAsync(Store.QueryAsync(new EventQuery { Pid = 42 }));
        var cpu = Assert.IsType<CpuSampleEvent>(Assert.Single(result));
        Assert.Equal(42, cpu.Pid);
    }

    [Fact]
    public async Task Query_TimeRange()
    {
        await Store.AppendBatchAsync(new TelemetryEvent[]
        {
            new CpuSampleEvent(100, SampleScope.System, null, null, 1, 1, 8, 1000),
            new CpuSampleEvent(200, SampleScope.System, null, null, 2, 2, 8, 1000),
            new CpuSampleEvent(300, SampleScope.System, null, null, 3, 3, 8, 1000),
        });

        var result = await ToListAsync(Store.QueryAsync(new EventQuery { FromMs = 150, ToMs = 250 }));
        var single = Assert.Single(result);
        Assert.Equal(200, single.TsMs);
    }

    [Fact]
    public async Task Query_Limit()
    {
        await Store.AppendBatchAsync(Enumerable.Range(0, 10).Select(i =>
            (TelemetryEvent)new CpuSampleEvent(i, SampleScope.System, null, null, i, i, 8, 1000)));

        var result = await ToListAsync(Store.QueryAsync(new EventQuery { Limit = 3 }));
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task Query_Descending_ReturnsLatestFirst()
    {
        await Store.AppendBatchAsync(Enumerable.Range(0, 10).Select(i =>
            (TelemetryEvent)new CpuSampleEvent(i * 10, SampleScope.System, null, null, i, i, 8, 1000)));

        var result = await ToListAsync(Store.QueryAsync(new EventQuery { Limit = 3, Descending = true }));

        Assert.Equal(new long[] { 90, 80, 70 }, result.Select(e => e.TsMs));
    }

    [Fact]
    public async Task Query_Descending_TieBreak_IsStableByInsertionOrder()
    {
        // 同一毫秒多条事件（一轮决策+动作常同 ms 落库）：次级键 id（插入序）保证顺序稳定，
        // 否则轮询间顺序翻转会让 app 增量合并把视口内行删掉重插（表现为行闪烁消失）
        await Store.AppendBatchAsync(new TelemetryEvent[]
        {
            new ProcessLifecycleEvent(100, LifecycleKind.Started, 1, 0, "first", null),
            new ProcessLifecycleEvent(100, LifecycleKind.Started, 2, 0, "second", null),
            new ProcessLifecycleEvent(100, LifecycleKind.Started, 3, 0, "third", null),
        });

        var result = await ToListAsync(Store.QueryAsync(new EventQuery
        {
            Table = TelemetryTable.EventLog,
            Descending = true,
        }));

        // id DESC = 插入序倒序
        Assert.Equal(new[] { "third", "second", "first" },
            result.Select(e => ((ProcessLifecycleEvent)e).Name));
    }

    [Fact]
    public async Task Query_Union_TieBreak_IsDeterministic()
    {
        // 跨表 UNION 用 type 破平（类型按表路由，同 ts 同 type 不可能跨表 → 全序确定）
        await Store.AppendBatchAsync(new TelemetryEvent[]
        {
            new ProcessLifecycleEvent(100, LifecycleKind.Started, 1, 0, "p", null),
            new CpuSampleEvent(100, SampleScope.System, null, null, 1, 1, 8, 1000),
        });

        var result = await ToListAsync(Store.QueryAsync(new EventQuery { Descending = true }));

        // type DESC："sample.cpu" > "process.lifecycle"
        Assert.Equal(new[] { TelemetryEventTypes.CpuSample, TelemetryEventTypes.ProcessLifecycle },
            result.Select(e => e.Type));
    }

    [Fact]
    public async Task Query_Descending_WithTypeFilter()
    {
        await Store.AppendBatchAsync(new TelemetryEvent[]
        {
            new CpuSampleEvent(100, SampleScope.System, null, null, 1, 1, 8, 1000),
            new ProcessLifecycleEvent(200, LifecycleKind.Started, 1, 0, "a.exe", null),
            new CpuSampleEvent(300, SampleScope.System, null, null, 3, 3, 8, 1000),
        });

        var result = await ToListAsync(Store.QueryAsync(new EventQuery
        {
            Type = TelemetryEventTypes.CpuSample,
            Descending = true,
        }));

        Assert.Equal(new long[] { 300, 100 }, result.Select(e => e.TsMs));
    }

    [Fact]
    public async Task PurgeBefore_RemovesOldEvents()
    {
        await Store.AppendBatchAsync(new TelemetryEvent[]
        {
            new CpuSampleEvent(100, SampleScope.System, null, null, 1, 1, 8, 1000),
            new CpuSampleEvent(200, SampleScope.System, null, null, 2, 2, 8, 1000),
            new CpuSampleEvent(300, SampleScope.System, null, null, 3, 3, 8, 1000),
        });

        var removed = await Store.PurgeBeforeAsync(TelemetryTable.Samples, 250);
        Assert.Equal(2, removed);
        Assert.Equal(1, await Store.CountAsync());
    }

    // ─── 双表分层（schema §8）───

    [Fact]
    public async Task Router_AssignsTablesByType()
    {
        Assert.Equal(TelemetryTable.Samples, TelemetryTableRouter.TableFor(TelemetryEventTypes.CpuSample));
        Assert.Equal(TelemetryTable.Samples, TelemetryTableRouter.TableFor(TelemetryEventTypes.MemorySample));
        Assert.Equal(TelemetryTable.EventLog, TelemetryTableRouter.TableFor(TelemetryEventTypes.ProcessLifecycle));
        Assert.Equal(TelemetryTable.EventLog, TelemetryTableRouter.TableFor(TelemetryEventTypes.PolicyDecision));
        Assert.Equal(TelemetryTable.EventLog, TelemetryTableRouter.TableFor(TelemetryEventTypes.PolicyAction));
        Assert.Equal(TelemetryTable.EventLog, TelemetryTableRouter.TableFor(TelemetryEventTypes.RuleChanged));
        Assert.Equal(TelemetryTable.EventLog, TelemetryTableRouter.TableFor(TelemetryEventTypes.Foreground));
    }

    [Fact]
    public async Task Append_RoutesToCorrectTables()
    {
        await Store.AppendBatchAsync(new TelemetryEvent[]
        {
            new CpuSampleEvent(100, SampleScope.System, null, null, 1, 1, 8, 1000),
            new PolicyDecisionEvent(200, "cpu.storm", 6, "d.exe", "[]", "{}", DecisionMode.Automatic, "{}"),
            new MemorySampleEvent(300, SampleScope.System, null, null, null, null, 8000, 16000, 50),
            new RuleChangedEvent(400, "r1", RuleChangeKind.Added, RuleChangeSource.User, "{}"),
        });

        Assert.Equal(2, await Store.CountAsync(TelemetryTable.Samples));
        Assert.Equal(2, await Store.CountAsync(TelemetryTable.EventLog));
        Assert.Equal(4, await Store.CountAsync());
    }

    [Fact]
    public async Task Query_TypeFilter_RoutesToInferredTable()
    {
        await Store.AppendBatchAsync(new TelemetryEvent[]
        {
            new CpuSampleEvent(100, SampleScope.System, null, null, 1, 1, 8, 1000),
            new PolicyDecisionEvent(200, "cpu.storm", 6, "d.exe", "[]", "{}", DecisionMode.Automatic, "{}"),
        });

        var cpu = await ToListAsync(Store.QueryAsync(new EventQuery { Type = TelemetryEventTypes.CpuSample }));
        Assert.Single(cpu);
        Assert.IsType<CpuSampleEvent>(cpu[0]);

        var decision = await ToListAsync(Store.QueryAsync(new EventQuery { Type = TelemetryEventTypes.PolicyDecision }));
        Assert.Single(decision);
        Assert.IsType<PolicyDecisionEvent>(decision[0]);
    }

    [Fact]
    public async Task Query_TypePrefix_FiltersAcrossEvents()
    {
        await Store.AppendBatchAsync(new TelemetryEvent[]
        {
            new PolicyDecisionEvent(100, "cpu.storm", 6, "d.exe", "[]", "{}", DecisionMode.Automatic, "{}"),
            new PolicyActionEvent(200, ActionKind.SetPriority, 7, "e.exe", "{}", "{}", ActionResult.Succeeded, null, null),
            new RuleChangedEvent(300, "r1", RuleChangeKind.Added, RuleChangeSource.User, "{}"),
            new CpuSampleEvent(400, SampleScope.System, null, null, 1, 1, 8, 1000),
        });

        var result = await ToListAsync(Store.QueryAsync(new EventQuery { TypePrefix = "policy." }));

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.StartsWith("policy.", e.Type));
    }

    [Fact]
    public async Task Query_ExplicitTable_OnlyScansThatTable()
    {
        await Store.AppendBatchAsync(new TelemetryEvent[]
        {
            new CpuSampleEvent(100, SampleScope.System, null, null, 1, 1, 8, 1000),
            new PolicyDecisionEvent(200, "cpu.storm", 6, "d.exe", "[]", "{}", DecisionMode.Automatic, "{}"),
        });

        var samples = await ToListAsync(Store.QueryAsync(new EventQuery { Table = TelemetryTable.Samples }));
        Assert.Single(samples);
        Assert.IsType<CpuSampleEvent>(samples[0]);

        var logs = await ToListAsync(Store.QueryAsync(new EventQuery { Table = TelemetryTable.EventLog }));
        Assert.Single(logs);
        Assert.IsType<PolicyDecisionEvent>(logs[0]);
    }

    [Fact]
    public async Task Purge_Tiered_OnlyAffectsTargetTable()
    {
        await Store.AppendBatchAsync(new TelemetryEvent[]
        {
            new CpuSampleEvent(100, SampleScope.System, null, null, 1, 1, 8, 1000),   // samples
            new CpuSampleEvent(200, SampleScope.System, null, null, 2, 2, 8, 1000),   // samples
            new PolicyDecisionEvent(100, "cpu.storm", 6, "d.exe", "[]", "{}", DecisionMode.Automatic, "{}"), // event_log
            new PolicyDecisionEvent(300, "cpu.storm", 6, "d.exe", "[]", "{}", DecisionMode.Automatic, "{}"), // event_log
        });

        // 只清 samples 的旧数据
        var removedSamples = await Store.PurgeBeforeAsync(TelemetryTable.Samples, 150);
        Assert.Equal(1, removedSamples);
        Assert.Equal(1, await Store.CountAsync(TelemetryTable.Samples));
        Assert.Equal(2, await Store.CountAsync(TelemetryTable.EventLog)); // event_log 不受影响

        // 再清 event_log
        var removedLogs = await Store.PurgeBeforeAsync(TelemetryTable.EventLog, 150);
        Assert.Equal(1, removedLogs);
        Assert.Equal(1, await Store.CountAsync(TelemetryTable.EventLog));
    }

    [Fact]
    public async Task RoundTrip_AllEventTypes()
    {
        TelemetryEvent[] events =
        {
            new ProcessLifecycleEvent(1, LifecycleKind.Started, 1, 0, "a.exe", "C:\\a.exe"),
            new CpuSampleEvent(2, SampleScope.Process, 2, "b.exe", 33.3, 444, null, 1000),
            new MemorySampleEvent(3, SampleScope.System, null, null, null, null, 8000, 16000, 50),
            new ForegroundEvent(4, 5, "c.exe", "Title"),
            new PolicyDecisionEvent(5, "cpu.storm", 6, "d.exe", "[]", "{}", DecisionMode.Supervised, "{}"),
            new PolicyActionEvent(6, ActionKind.Restore, 7, "e.exe", "{}", "{}", ActionResult.Succeeded, null, 5000),
            new RuleChangedEvent(7, "r1", RuleChangeKind.Enabled, RuleChangeSource.User, "{}"),
        };

        await Store.AppendBatchAsync(events);

        var result = await ToListAsync(Store.QueryAsync(new EventQuery()));
        Assert.Equal(events.Length, result.Count);

        for (var i = 0; i < events.Length; i++)
        {
            Assert.Equal(events[i].GetType(), result[i].GetType());
            Assert.Equal(events[i].TsMs, result[i].TsMs);
        }
    }

    private static async Task<List<TelemetryEvent>> ToListAsync(IAsyncEnumerable<TelemetryEvent> source)
    {
        var list = new List<TelemetryEvent>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
