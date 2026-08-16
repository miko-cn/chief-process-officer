namespace Cpo.Core.Engine;

/// <summary>
/// 启发式 v1 配置（响应性保护，会话⑲定案）。
///
/// 设计哲学：启发式的目标是**保持 OS 与前台程序的高响应性**，降 CPU 占用是手段不是硬性指标。
/// 因此触发条件是"三条件齐备"而非"进程 CPU 高"：
///   ① 系统 CPU 饱和（无余量给 OS/前台） ② 进程是挤占者 ③ 进程非关键（非前台/非系统关键）
/// 系统还有余量时，任何进程吃满 CPU 都合理 → 不干预（绝不降级本来能跑的进程）。
///
/// 参数全部可配置（SPEC §7：配置化 + 可测试化 + 回放与线上同一管线）。
/// </summary>
public sealed record HeuristicConfig
{
    /// <summary>系统 CPU 饱和阈值（0~100）：系统总占用 ≥ 此值视为"无余量给 OS/前台"。</summary>
    public double SystemSaturationPercent { get; init; } = 90;

    /// <summary>挤占者判定：进程 CPU 占用 ≥ 此值（相对单核百分比，50 = 吃满半个核）。</summary>
    public double ProcessCpuPercent { get; init; } = 50;

    /// <summary>干预时长（毫秒）：超时由执行路径自动恢复原值（保守：不留永久干预）。</summary>
    public long DurationMs { get; init; } = 30_000;

    /// <summary>目标优先级类（0x4000 = BelowNormal，保守起步，不高于此强度）。</summary>
    public int PriorityClass { get; init; } = 0x4000;

    // ─── 近期前台程序（用户高频使用）的温和降级参数（会话⑳b 定案）───

    /// <summary>温和降级窗口（毫秒）：窗口内曾为前台 → 按温和参数谨慎降级。</summary>
    public long RecentForegroundWindowMs { get; init; } = 60 * 60_000;

    /// <summary>温和降级的挤占阈值（0~100）：比标准更高 = 更严重的挤占才动它。</summary>
    public double RecentForegroundCpuPercent { get; init; } = 80;

    /// <summary>温和降级时长（毫秒）：更短 = 更快松手。</summary>
    public long RecentForegroundDurationMs { get; init; } = 10_000;
}
