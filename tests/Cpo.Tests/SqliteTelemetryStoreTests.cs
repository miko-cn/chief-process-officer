using Cpo.Core.Storage;
using Cpo.Core.Telemetry;
using Xunit;

namespace Cpo.Tests;

/// <summary>SQLite 存储测试（schema §8 落盘形态）。全部用内存库，避免文件残留。</summary>
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
    public async Task PurgeBefore_RemovesOldEvents()
    {
        await Store.AppendBatchAsync(new TelemetryEvent[]
        {
            new CpuSampleEvent(100, SampleScope.System, null, null, 1, 1, 8, 1000),
            new CpuSampleEvent(200, SampleScope.System, null, null, 2, 2, 8, 1000),
            new CpuSampleEvent(300, SampleScope.System, null, null, 3, 3, 8, 1000),
        });

        var removed = await Store.PurgeBeforeAsync(250);
        Assert.Equal(2, removed);
        Assert.Equal(1, await Store.CountAsync());
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
