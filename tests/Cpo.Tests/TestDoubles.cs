using Cpo.Core.Engine;
using Cpo.Core.Storage;
using Cpo.Core.Telemetry;

namespace Cpo.Tests;

/// <summary>内存进程控制器 fake：记录调用序列，供断言干预执行/恢复路径。</summary>
internal sealed class FakeProcessController : IProcessController
{
    public Dictionary<int, ProcessControlState> Processes { get; } = new();
    public List<string> Calls { get; } = new();

    public FakeProcessController(params (int Pid, string Name, int Priority, ulong Mask)[] processes)
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
}

/// <summary>内存事件存储 fake：支持 PolicyRunner 需要的窗口/前缀查询（按 ts_ms 升序）。</summary>
internal sealed class FakeTelemetryStore : ITelemetryStore
{
    public List<TelemetryEvent> Events { get; } = new();

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task AppendAsync(TelemetryEvent evt, CancellationToken ct = default)
    {
        Events.Add(evt);
        return Task.CompletedTask;
    }

    public Task AppendBatchAsync(IEnumerable<TelemetryEvent> events, CancellationToken ct = default)
    {
        Events.AddRange(events);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<TelemetryEvent> QueryAsync(
        EventQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        IEnumerable<TelemetryEvent> result = Events;
        if (query.FromMs is long from)
        {
            result = result.Where(e => e.TsMs >= from);
        }

        if (query.TypePrefix is string prefix)
        {
            result = result.Where(e => e.Type.StartsWith(prefix, StringComparison.Ordinal));
        }

        if (query.Type is string exactType)
        {
            result = result.Where(e => e.Type == exactType);
        }

        foreach (var evt in result.OrderBy(e => e.TsMs))
        {
            yield return evt;
        }
    }

    public Task<long> CountAsync(CancellationToken ct = default) => Task.FromResult((long)Events.Count);

    public Task<long> CountAsync(TelemetryTable table, CancellationToken ct = default) =>
        Task.FromResult((long)Events.Count);

    public Task<long> PurgeBeforeAsync(TelemetryTable table, long tsMsBefore, CancellationToken ct = default) =>
        Task.FromResult(0L);
}
