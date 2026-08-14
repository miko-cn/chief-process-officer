using System.Text.Json.Serialization;

namespace Cpo.Core.Telemetry;

/// <summary>进程生命周期事件（schema §1 process.lifecycle）。</summary>
public sealed record ProcessLifecycleEvent(
    long TsMs,
    LifecycleKind Kind,
    int Pid,
    int Ppid,
    string Name,
    string? Path) : TelemetryEvent(TsMs)
{
    [JsonIgnore]
    public override string Type => TelemetryEventTypes.ProcessLifecycle;
}

/// <summary>生命周期类型。</summary>
public enum LifecycleKind
{
    Started,
    Exited,
}
