using System.Text.RegularExpressions;

namespace Cpo.Core.Rules;

/// <summary>
/// 进程名通配符匹配器（纯逻辑，可单测）。
/// 模式支持：*（任意串）、?（单字符）；大小写不敏感。
/// 实现：将通配符模式转换为正则（先 Regex.Escape 再替换）。
/// </summary>
public static class RuleMatcher
{
    /// <summary>判断进程名是否匹配规则模式。</summary>
    public static bool Matches(PolicyRule rule, string processName) =>
        rule.IsEnabled && Matches(rule.ProcessPattern, processName);

    /// <summary>判断进程名是否匹配通配符模式。</summary>
    public static bool Matches(string pattern, string processName)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        if (pattern == "*")
        {
            return true;
        }

        var regex = new Regex(
            "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return regex.IsMatch(processName);
    }
}
