using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Tests;

/// <summary>占位符渲染是注入层的基础，单独钉一遍。</summary>
public static class PromptRenderTests
{
    public static Action[] Cases =>
    [
        ReplacesKnownPlaceholders,
        KeepsUnknownPlaceholdersVisible,
        HandlesEmptyTemplate,
        HandlesUnclosedBrace,
        HandlesRepeatedPlaceholder
    ];

    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static void ReplacesKnownPlaceholders()
    {
        string rendered = PromptConfig.Render("你{wealthBand}。", Values(("wealthBand", "手头宽裕")));
        TestRunner.Equal("你手头宽裕。", rendered);
    }

    private static void KeepsUnknownPlaceholdersVisible()
    {
        // 故意保留原样，让配置写错时能立刻在测试或日志里暴露，而不是静默变成空字符串。
        string rendered = PromptConfig.Render("余额 {balance} 元", Values(("other", "x")));
        TestRunner.Equal("余额 {balance} 元", rendered);
    }

    private static void HandlesEmptyTemplate()
    {
        TestRunner.Equal("", PromptConfig.Render("", Values()));
        TestRunner.Equal("", PromptConfig.Render(null!, Values()));
    }

    private static void HandlesUnclosedBrace()
    {
        TestRunner.Equal("余额 {balance", PromptConfig.Render("余额 {balance", Values(("balance", "$1"))));
    }

    private static void HandlesRepeatedPlaceholder()
    {
        string rendered = PromptConfig.Render("{a}-{a}-{b}", Values(("a", "1"), ("b", "2")));
        TestRunner.Equal("1-1-2", rendered);
    }
}
