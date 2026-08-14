using System.Text.Json.Serialization;

namespace Cpo.Core.Telemetry;

/// <summary>CPU 周期采样事件（schema §2 sample.cpu）。</summary>
public sealed record CpuSampleEvent(
    long TsMs,
    SampleScope Scope,
    int? Pid,
    string? Name,
    double CpuPercent,
    long? TotalCpuMs,
    int? CoreCount,
    long IntervalMs) : TelemetryEvent(TsMs)
{
    [JsonIgnore]
    public override string Type => TelemetryEventTypes.CpuSample;
}

/// <summary>内存周期采样事件（schema §3 sample.memory）。</summary>
public sealed record MemorySampleEvent(
    long TsMs,
    SampleScope Scope,
    int? Pid,
    string? Name,
    long? WorkingSetBytes,
    long? PrivateBytes,
    long? AvailableBytes,
    long? TotalBytes,
    double? CommitChargePercent) : TelemetryEvent(TsMs)
{
    [JsonIgnore]
    public override string Type => TelemetryEventTypes.MemorySample;
}

/// <summary>采样范围。</summary>
public enum SampleScope
{
    System,
    Process,
}
