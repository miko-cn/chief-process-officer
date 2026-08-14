using System.Text.Json.Serialization;

namespace Cpo.Core.Telemetry;

/// <summary>前台窗口变化事件（schema §4 ui.foreground）。M2 由 GUI 侧 SetWinEventHook 产生。</summary>
public sealed record ForegroundEvent(
    long TsMs,
    int Pid,
    string Name,
    string? WindowTitle) : TelemetryEvent(TsMs)
{
    [JsonIgnore]
    public override string Type => TelemetryEventTypes.Foreground;
}

/// <summary>策略引擎决策事件（schema §5 policy.decision）。M2 由策略引擎产生。</summary>
public sealed record PolicyDecisionEvent(
    long TsMs,
    string Trigger,
    int TargetPid,
    string TargetName,
    string ProposedActionsJson,
    string InputSnapshotJson,
    DecisionMode Mode,
    string ConclusionJson) : TelemetryEvent(TsMs)
{
    [JsonIgnore]
    public override string Type => TelemetryEventTypes.PolicyDecision;
}

/// <summary>决策模式。</summary>
public enum DecisionMode
{
    Supervised,
    Automatic,
}

/// <summary>动作执行与恢复事件（schema §6 policy.action）。M2 由执行路径产生。</summary>
public sealed record PolicyActionEvent(
    long TsMs,
    ActionKind Kind,
    int TargetPid,
    string TargetName,
    string ParametersJson,
    string? PreviousJson,
    ActionResult Result,
    string? Error,
    long? DurationMs) : TelemetryEvent(TsMs)
{
    [JsonIgnore]
    public override string Type => TelemetryEventTypes.PolicyAction;
}

/// <summary>动作类型。</summary>
public enum ActionKind
{
    SetPriority,
    SetAffinity,
    Throttle,
    Restore,
}

/// <summary>执行结果。</summary>
public enum ActionResult
{
    Succeeded,
    Failed,
}

/// <summary>规则变更事件（schema §7 rule.changed）。M2 由规则管理产生。</summary>
public sealed record RuleChangedEvent(
    long TsMs,
    string RuleId,
    RuleChangeKind ChangeKind,
    RuleChangeSource Source,
    string RuleJson) : TelemetryEvent(TsMs)
{
    [JsonIgnore]
    public override string Type => TelemetryEventTypes.RuleChanged;
}

public enum RuleChangeKind
{
    Added,
    Updated,
    Removed,
    Enabled,
    Disabled,
}

public enum RuleChangeSource
{
    User,
    Suggestion,
}
