using Cpo.Core.Rules;
using Xunit;

namespace Cpo.Tests;

public class RuleMatcherTests
{
    [Theory]
    [InlineData("msbuild.exe", "msbuild.exe", true)]
    [InlineData("MSBUILD.EXE", "msbuild.exe", true)]          // 大小写不敏感
    [InlineData("*build*", "msbuild.exe", true)]
    [InlineData("*.exe", "chrome.exe", true)]
    [InlineData("chrome*.exe", "chrome.exe", true)]
    [InlineData("chrome*.exe", "chrome123.exe", true)]
    [InlineData("chrome*.exe", "chromium.exe", false)]        // * 不跨过 .exe 后缀匹配
    [InlineData("?", "a", true)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    [InlineData("*", "anything", true)]
    [InlineData("msbuild.exe", "notepad.exe", false)]
    [InlineData("", "anything", false)]
    public void Matches_Patterns(string pattern, string processName, bool expected)
    {
        Assert.Equal(expected, RuleMatcher.Matches(pattern, processName));
    }

    [Fact]
    public void Matches_DisabledRule_NeverMatches()
    {
        var rule = new PolicyRule
        {
            Id = "r1",
            ProcessPattern = "*",
            Action = RuleActionKind.SetPriority,
            PriorityClass = 0x4000,
            IsEnabled = false,
        };
        Assert.False(RuleMatcher.Matches(rule, "anything.exe"));
    }

    [Fact]
    public void Matches_RegexSpecialChars_AreEscaped()
    {
        // 模式中的正则特殊字符按字面处理（"[" 不应被当字符类）
        Assert.False(RuleMatcher.Matches("a[bc].exe", "ab.exe"));
        Assert.True(RuleMatcher.Matches("a[bc].exe", "a[bc].exe"));
    }
}
