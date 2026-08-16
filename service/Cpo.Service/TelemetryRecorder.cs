using Cpo.Core.Sampling;
using Cpo.Core.Storage;
using Cpo.Core.Telemetry;
using Cpo.Interop;

namespace Cpo.Service;

/// <summary>
/// 遥测录制器：周期采样（系统级 + 进程级）→ 转为 core 事件（lifecycle/cpu/memory）→ 写入 SQLite。
/// 采样频率与保留策略来自 <see cref="SamplingConfig"/>（配置化 + 可测试化）。
/// </summary>
public sealed class TelemetryRecorder : IAsyncDisposable
{
    private readonly ITelemetryStore _store;
    private readonly SamplingConfig _config;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();

    private readonly Dictionary<int, ProcessIdentity> _knownProcesses = new();
    private readonly Dictionary<int, long> _processCpuMs = new();
    private long _systemCpuMs;
    private long _lastSampleUtcMs;
    private long _lastProcessSampleUtcMs;

    /// <param name="store">遥测存储。</param>
    /// <param name="config">采样配置。测试可传入短间隔/长保留配置。</param>
    public TelemetryRecorder(ITelemetryStore store, SamplingConfig config)
    {
        _store = store;
        _config = config;
        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(config.SystemSampleIntervalMs));
    }

    /// <summary>开始录制（挂起直到取消；先立即采一次，之后按配置间隔）。</summary>
    public async Task RunAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, token);
        await _store.InitializeAsync(linked.Token);
        await SampleOnceAsync(linked.Token);

        while (await _timer.WaitForNextTickAsync(linked.Token))
        {
            try
            {
                await SampleOnceAsync(linked.Token);
            }
            catch (Exception ex)
            {
                // 单次采样/落盘失败不杀进程（实机教训：SQLite disk I/O error 曾让整个 service 崩溃，
                // 系统高负载/杀软扫描锁文件时可能发生）。记录后下一轮继续。
                Console.Error.WriteLine($"[TelemetryRecorder] 采样异常: {ex.Message}");
            }
        }
    }

    /// <summary>停止录制（供 Dispose 前的显式停止）。</summary>
    public void Stop() => _cts.Cancel();

    private async Task SampleOnceAsync(CancellationToken ct)
    {
        var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var events = new List<TelemetryEvent>();

        // ---- 系统级：CPU + 内存 ----
        var system = SystemSampler.Snapshot();
        var elapsedMs = _lastSampleUtcMs == 0 ? _config.SystemSampleIntervalMs : nowUtcMs - _lastSampleUtcMs;
        var systemCpuPercent = _lastSampleUtcMs == 0
            ? 0
            : CpuUsageCalculator.Compute(_systemCpuMs, system.TotalCpuMs, elapsedMs, system.CoreCount);

        events.Add(new CpuSampleEvent(
            TsMs: nowUtcMs, Scope: SampleScope.System,
            Pid: null, Name: null,
            CpuPercent: systemCpuPercent,
            TotalCpuMs: system.TotalCpuMs,
            CoreCount: system.CoreCount,
            IntervalMs: elapsedMs));

        double? commitChargePercent = system.TotalBytes > 0
            ? (1 - (double)system.AvailableBytes / system.TotalBytes) * 100
            : null;
        events.Add(new MemorySampleEvent(
            TsMs: nowUtcMs, Scope: SampleScope.System,
            Pid: null, Name: null,
            WorkingSetBytes: null, PrivateBytes: null,
            AvailableBytes: system.AvailableBytes, TotalBytes: system.TotalBytes,
            CommitChargePercent: commitChargePercent));

        _systemCpuMs = system.TotalCpuMs;
        _lastSampleUtcMs = nowUtcMs;

        // ---- 进程级：生命周期 + CPU + 内存 ----
        var snapshots = ProcessSampler.SnapshotAll();
        var current = snapshots.ToDictionary(
            s => s.Pid,
            s => new ProcessIdentity(s.Pid, s.ParentPid, s.Name));

        var diff = ProcessLifecycleDetector.Diff(_knownProcesses, current);
        foreach (var started in diff.Started)
        {
            events.Add(new ProcessLifecycleEvent(nowUtcMs, LifecycleKind.Started,
                started.Pid, started.ParentPid, started.Name, PathOf(snapshots, started.Pid)));
        }

        foreach (var exited in diff.Exited)
        {
            events.Add(new ProcessLifecycleEvent(nowUtcMs, LifecycleKind.Exited,
                exited.Pid, exited.ParentPid, exited.Name, PathOf(snapshots, exited.Pid)));
            _processCpuMs.Remove(exited.Pid);
        }

        foreach (var s in snapshots)
        {
            var procElapsed = _lastProcessSampleUtcMs == 0 ? _config.ProcessSampleIntervalMs : nowUtcMs - _lastProcessSampleUtcMs;
            var hasPrev = _processCpuMs.TryGetValue(s.Pid, out var prevCpuMs);
            var cpuPercent = hasPrev
                ? CpuUsageCalculator.Compute(prevCpuMs, s.TotalCpuMs, procElapsed, 1)
                : 0;
            _processCpuMs[s.Pid] = s.TotalCpuMs;

            events.Add(new CpuSampleEvent(
                TsMs: nowUtcMs, Scope: SampleScope.Process,
                Pid: s.Pid, Name: s.Name,
                CpuPercent: cpuPercent,
                TotalCpuMs: s.TotalCpuMs,
                CoreCount: null,
                IntervalMs: procElapsed));

            events.Add(new MemorySampleEvent(
                TsMs: nowUtcMs, Scope: SampleScope.Process,
                Pid: s.Pid, Name: s.Name,
                WorkingSetBytes: s.WorkingSetBytes, PrivateBytes: s.PrivateBytes,
                AvailableBytes: null, TotalBytes: null,
                CommitChargePercent: null));
        }

        _lastProcessSampleUtcMs = nowUtcMs;
        _knownProcesses.Clear();
        foreach (var kv in current)
        {
            _knownProcesses[kv.Key] = kv.Value;
        }

        await _store.AppendBatchAsync(events, ct);
    }

    private static string? PathOf(IReadOnlyList<ProcessSnapshot> snapshots, int pid) =>
        snapshots.FirstOrDefault(s => s.Pid == pid)?.Path;

    public async ValueTask DisposeAsync()
    {
        Stop();
        _cts.Dispose();
        _timer.Dispose();
        await Task.CompletedTask;
    }
}
