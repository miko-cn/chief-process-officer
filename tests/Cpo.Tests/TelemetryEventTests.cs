using Cpo.Core.Telemetry;
using Xunit;

namespace Cpo.Tests;

/// <summary>遥测事件模型 + 序列化契约测试（对应 docs/schema.md）。</summary>
public class TelemetryEventTests
{
    [Fact]
    public void LifecycleEvent_TypeAndFields()
    {
        var evt = new ProcessLifecycleEvent(1_700_000_000_000, LifecycleKind.Started, 1234, 5678, "notepad.exe", "C:\\Windows\\System32\\notepad.exe");

        Assert.Equal(TelemetryEventTypes.ProcessLifecycle, evt.Type);
        Assert.Equal(1_700_000_000_000, evt.TsMs);
        Assert.Equal(LifecycleKind.Started, evt.Kind);
    }

    [Theory]
    [InlineData(TelemetryEventTypes.ProcessLifecycle)]
    [InlineData(TelemetryEventTypes.CpuSample)]
    [InlineData(TelemetryEventTypes.MemorySample)]
    [InlineData(TelemetryEventTypes.Foreground)]
    [InlineData(TelemetryEventTypes.PolicyDecision)]
    [InlineData(TelemetryEventTypes.PolicyAction)]
    [InlineData(TelemetryEventTypes.RuleChanged)]
    public void AllEventTypes_AreRegistered(string expectedType)
    {
        TelemetryEvent sample = expectedType switch
        {
            TelemetryEventTypes.ProcessLifecycle => new ProcessLifecycleEvent(1, LifecycleKind.Started, 1, 0, "a.exe", null),
            TelemetryEventTypes.CpuSample => new CpuSampleEvent(1, SampleScope.System, null, null, 12.5, 1000, 8, 2000),
            TelemetryEventTypes.MemorySample => new MemorySampleEvent(1, SampleScope.System, null, null, null, null, 8_000_000_000, 16_000_000_000, 50),
            TelemetryEventTypes.Foreground => new ForegroundEvent(1, 42, "chrome.exe", "Untitled"),
            TelemetryEventTypes.PolicyDecision => new PolicyDecisionEvent(1, "cpu.storm", 42, "msbuild.exe", "[]", "{}", DecisionMode.Supervised, "{}"),
            TelemetryEventTypes.PolicyAction => new PolicyActionEvent(1, ActionKind.SetPriority, 42, "msbuild.exe", "{}", "{}", ActionResult.Succeeded, null, null),
            TelemetryEventTypes.RuleChanged => new RuleChangedEvent(1, "r1", RuleChangeKind.Added, RuleChangeSource.User, "{}"),
            _ => throw new ArgumentOutOfRangeException(nameof(expectedType)),
        };

        Assert.Equal(expectedType, sample.Type);
        Assert.IsAssignableFrom<TelemetryEvent>(sample);
    }

    [Fact]
    public void Serialize_Deserialize_RoundTrips()
    {
        var evt = new CpuSampleEvent(123, SampleScope.Process, 999, "explorer.exe", 23.4, 5_000_000, null, 2000);

        var payload = TelemetryEventSerializer.Serialize(evt);
        var back = TelemetryEventSerializer.Deserialize(TelemetryEventTypes.CpuSample, payload);

        Assert.Equal(evt, back);
    }

    [Fact]
    public void Serialize_UsesCamelCase_AndOmitsType()
    {
        var evt = new ProcessLifecycleEvent(100, LifecycleKind.Started, 7, 1, "old.exe", null);
        var payload = TelemetryEventSerializer.Serialize(evt);

        Assert.Contains("\"pid\":7", payload);
        Assert.Contains("\"kind\":\"started\"", payload);
        Assert.DoesNotContain("Type", payload);          // type 不入 payload（独立列）
        Assert.DoesNotContain("\"type\"", payload);
    }

    [Fact]
    public void Deserialize_UnknownType_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TelemetryEventSerializer.Deserialize("no.such.type", "{}"));
    }

    [Fact]
    public void Serialize_OmitsNullFields()
    {
        var evt = new ProcessLifecycleEvent(1, LifecycleKind.Exited, 7, 1, "x.exe", null);
        var payload = TelemetryEventSerializer.Serialize(evt);

        Assert.DoesNotContain("path", payload);
    }
}
