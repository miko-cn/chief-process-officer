using System.Text.Json;
using Cpo.Core.Rules;
using Cpo.Core.Telemetry;

namespace Cpo.Core.Engine;

/// <summary>
/// 规则存储（M2 简化版）：内存规则集合 + JSON 文件持久化。
/// 每次变更产生 <see cref="RuleChangedEvent"/>（schema §7 rule.changed），
/// 由上层写入遥测存储形成决策日志闭环。
/// </summary>
public sealed class RuleStore
{
    private readonly object _gate = new();
    private readonly List<PolicyRule> _rules = new();
    private readonly List<RuleChangedEvent> _changeLog = new();

    /// <summary>当前规则（副本）。</summary>
    public IReadOnlyList<PolicyRule> Rules
    {
        get
        {
            lock (_gate)
            {
                return _rules.ToArray();
            }
        }
    }

    /// <summary>变更日志（rule.changed 事件来源）。</summary>
    public IReadOnlyList<RuleChangedEvent> ChangeLog
    {
        get
        {
            lock (_gate)
            {
                return _changeLog.ToArray();
            }
        }
    }

    /// <summary>
    /// 取走并清空变更日志（一次性消费语义：调用方把变更写入遥测存储后，
    /// 下次评估不会重复落盘同一批 rule.changed 事件）。
    /// </summary>
    public IReadOnlyList<RuleChangedEvent> DrainChanges()
    {
        lock (_gate)
        {
            var result = _changeLog.ToArray();
            _changeLog.Clear();
            return result;
        }
    }

    public void Add(PolicyRule rule, RuleChangeSource source = RuleChangeSource.User)
    {
        lock (_gate)
        {
            _rules.RemoveAll(r => r.Id == rule.Id);
            _rules.Add(rule);
            _changeLog.Add(new RuleChangedEvent(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rule.Id,
                RuleChangeKind.Added, source, JsonSerializer.Serialize(rule)));
        }
    }

    public bool Remove(string ruleId, RuleChangeSource source = RuleChangeSource.User)
    {
        lock (_gate)
        {
            var removed = _rules.RemoveAll(r => r.Id == ruleId) > 0;
            if (removed)
            {
                _changeLog.Add(new RuleChangedEvent(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ruleId,
                    RuleChangeKind.Removed, source, "{}"));
            }

            return removed;
        }
    }

    public void Clear(RuleChangeSource source = RuleChangeSource.User)
    {
        lock (_gate)
        {
            var ids = _rules.Select(r => r.Id).ToArray();
            _rules.Clear();
            foreach (var id in ids)
            {
                _changeLog.Add(new RuleChangedEvent(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), id,
                    RuleChangeKind.Removed, source, "{}"));
            }
        }
    }

    /// <summary>从 JSON 文件加载规则（幂等：先清空）。文件不存在 → 空规则集。</summary>
    public void LoadFromFile(string path)
    {
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return;
            }

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<List<PolicyRule>>(json, RuleJsonOptions) ?? new List<PolicyRule>();
            _rules.Clear();
            _rules.AddRange(loaded);
        }
    }

    /// <summary>保存到 JSON 文件。</summary>
    public void SaveToFile(string path)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_rules, RuleJsonOptions));
        }
    }

    private static readonly JsonSerializerOptions RuleJsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
