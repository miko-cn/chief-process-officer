namespace Cpo.Core.Sampling;

/// <summary>
/// CPU 占用率计算（纯逻辑，可单测）。
/// 基于两次快照的累计 CPU 时间差值与实际时间间隔计算平均占用率。
/// </summary>
public static class CpuUsageCalculator
{
    /// <summary>
    /// 计算某区间内平均 CPU 占用率。
    /// </summary>
    /// <param name="previousTotalMs">上次累计 CPU 时间（毫秒）。</param>
    /// <param name="currentTotalMs">本次累计 CPU 时间（毫秒）。</param>
    /// <param name="elapsedMs">两次采样实际间隔（毫秒，真实时间而非标称间隔）。</param>
    /// <param name="cores">逻辑核心数（进程级传 1，系统级传实际核心数）。</param>
    /// <returns>占用率百分比 0~100（超过 100 被钳制）。时间倒流/间隔为 0 时返回 0。</returns>
    public static double Compute(long previousTotalMs, long currentTotalMs, long elapsedMs, int cores)
    {
        if (elapsedMs <= 0 || cores <= 0 || currentTotalMs < previousTotalMs)
        {
            return 0;
        }

        var deltaMs = currentTotalMs - previousTotalMs;
        // 单核百分比 = 消耗时间 / 实际间隔；多核总占用 = 单核值（系统级 0~100 整体占用率）
        var singleCorePercent = deltaMs * 100.0 / elapsedMs;
        var percent = singleCorePercent / cores;
        return Math.Clamp(percent, 0, 100);
    }
}
