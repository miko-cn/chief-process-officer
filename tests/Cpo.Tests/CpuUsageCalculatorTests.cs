using Cpo.Core.Sampling;
using Xunit;

namespace Cpo.Tests;

public class CpuUsageCalculatorTests
{
    [Theory]
    [InlineData(0, 100, 1000, 1, 10.0)]     // 100ms 消耗 / 1000ms 间隔 / 1 核 = 10%
    [InlineData(0, 1000, 1000, 1, 100.0)]   // 满核
    [InlineData(0, 2000, 1000, 2, 100.0)]   // 2 核都跑满 = 100% 整体占用
    [InlineData(0, 500, 2000, 4, 6.25)]     // 4 核中 0.5s 消耗 = 12.5% 单核 / 4 = 6.25%
    public void Compute_BasicCases(long prev, long curr, long elapsed, int cores, double expected)
    {
        var actual = CpuUsageCalculator.Compute(prev, curr, elapsed, cores);
        Assert.Equal(expected, actual, 2);
    }

    [Theory]
    [InlineData(100, 0, 1000, 1)]  // 时间倒流 → 0
    [InlineData(0, 100, 0, 1)]     // 间隔 0 → 0
    [InlineData(0, 100, 1000, 0)]  // 核数 0 → 0
    [InlineData(0, 100, -5, 1)]    // 负间隔 → 0
    public void Compute_InvalidInputs_ReturnZero(long prev, long curr, long elapsed, int cores)
    {
        Assert.Equal(0, CpuUsageCalculator.Compute(prev, curr, elapsed, cores));
    }

    [Fact]
    public void Compute_ClampsAbove100()
    {
        // 单核：消耗 300ms / 间隔 100ms（计数器异常快）→ 钳制 100
        Assert.Equal(100, CpuUsageCalculator.Compute(0, 300, 100, 1));
    }
}
