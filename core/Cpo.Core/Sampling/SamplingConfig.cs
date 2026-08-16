namespace Cpo.Core.Sampling;

/// <summary>
/// 采样与存储配置（schema §0/§8：采样频率与保留策略配置化 + 可测试化 + 可用户配置）。
/// 默认值 + 配置文件 + 运行时调整共用同一管线；回放测试与线上走同一参数。
/// </summary>
public sealed record SamplingConfig
{
    /// <summary>系统级采样间隔（毫秒）。默认 2000ms（SPEC 默认）。</summary>
    public int SystemSampleIntervalMs { get; init; } = 2000;

    /// <summary>进程级采样间隔（毫秒）。默认 2000ms。</summary>
    public int ProcessSampleIntervalMs { get; init; } = 2000;

    /// <summary>进程生命周期比对周期（毫秒）。默认与系统采样一致。</summary>
    public int LifecycleScanIntervalMs { get; init; } = 2000;

    /// <summary>
    /// 热表 samples 保留时长（毫秒，schema §8.1）。
    /// 高频采样只存短期 Buffer——够启发式决策（滑动窗口 5s）+ "为什么卡"近期快照。默认 1 小时。
    /// </summary>
    public long SamplesRetentionMs { get; init; } = (long)TimeSpan.FromHours(1).TotalMilliseconds;

    /// <summary>
    /// 冷表 event_log 保留时长（毫秒，schema §8.1）。
    /// 操作日志价值长期存在（审阅/诊断/AI 语料）。默认 30 天。
    /// </summary>
    public long EventLogRetentionMs { get; init; } = (long)TimeSpan.FromDays(30).TotalMilliseconds;

    /// <summary>过期事件清理周期（毫秒）。默认 1 小时。</summary>
    public int PurgeIntervalMs { get; init; } = 3_600_000;

    /// <summary>采样窗口内 CPU 采样校准系数：进程 CPU 百分比按 (t2 - t1) 实际间隔计算。</summary>
    public bool CpuPercentUseMeasuredInterval { get; init; } = true;
}
