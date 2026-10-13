using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 配置加载与校验。这些测试直接跑在仓库里的真实 config/ 目录上，
/// 所以它们同时也是「配置文件本身是否自洽」的回归测试。
/// </summary>
public static class ConfigLoaderTests
{
    public static Action[] Cases =>
    [
        RealConfigDirectoryIsFound,
        RealConfigLoadsWithoutErrors,
        AllCanonicalKindsAreMapped,
        AllKindsHaveRefusalLines,
        FavorTiersCoverFullRange,
        SeedTierWeightsSumToHundred,
        SeedTiersCoverTheKnownRoles,
        RenderedPromptContainsNoPlaceholders,
        StableTemplatesContainNoDigits,
        PromptTablesArePopulated,
        ContainerTableIsNotSplitIntoSingleEntryTables,
        MissingDirectoryIsReportedAsError
    ];

    /// <summary>从测试二进制所在目录向上找仓库根，兼容 dotnet run / 直接执行两种方式。</summary>
    private static string? FindConfigDirectory()
    {
        // 收敛到共享实现（原先 19 个文件各抄一份，副本之间已经开始漂移）。
        try { return TestConfig.Directory(); }
        catch (Exception) { return null; }
    }

    private static ConfigLoadResult LoadReal()
    {
        string? root = FindConfigDirectory();
        TestRunner.True(root != null, "找不到仓库的 config/ 目录，无法验证真实配置。");
        // 配置校验的日志在负向测试里会刻意产生 Error，这里静音以免污染测试输出。
        return new ConfigLoader(root!, Silent).Load();
    }

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    private static void RealConfigDirectoryIsFound()
    {
        TestRunner.True(FindConfigDirectory() != null, "找不到 config/ 目录");
    }

    private static void RealConfigLoadsWithoutErrors()
    {
        var result = LoadReal();
        if (result.HasErrors)
        {
            string detail = string.Join(" | ", result.Errors.Select(e => e.Message));
            throw new Exception("真实配置存在校验错误：" + detail);
        }
    }

    private static void AllCanonicalKindsAreMapped()
    {
        var result = LoadReal();
        var missing = ConfigLoader.CanonicalPersonalityKinds
            .Where(k => !result.Personality.Kinds.ContainsKey(k)).ToArray();
        TestRunner.True(missing.Length == 0, "缺少性格映射：" + string.Join("、", missing));
        TestRunner.Equal(24, result.Personality.Kinds.Count, "映射表应恰好覆盖 24 种性格");
    }

    private static void AllKindsHaveRefusalLines()
    {
        var result = LoadReal();
        foreach (string kind in ConfigLoader.CanonicalPersonalityKinds)
        {
            TestRunner.True(result.Personality.RefusalLines.Charity.Sharp.ContainsKey(kind), $"charity.sharp 缺少 {kind}");
            TestRunner.True(result.Personality.RefusalLines.Charity.Gentle.ContainsKey(kind), $"charity.gentle 缺少 {kind}");
        }
    }

    private static void FavorTiersCoverFullRange()
    {
        var result = LoadReal();
        foreach (int probe in new[] { -100, 0, 19, 20, 49, 50, 79, 80, 100 })
            TestRunner.True(result.Personality.FavorTiers.Tiers.Any(t => probe >= t.Min && probe <= t.Max),
                $"好感度 {probe} 没有命中任何档位");
    }

    private static void SeedTierWeightsSumToHundred()
    {
        var result = LoadReal();
        double sum = result.Economy.Seeding.Tiers.Where(t => t.Id != "default").Sum(t => t.Weight);
        TestRunner.True(Math.Abs(sum - 100) < 0.01, $"档位权重合计应为 100，实际 {sum}");
    }

    private static void SeedTiersCoverTheKnownRoles()
    {
        var result = LoadReal();
        // 这 9 个 Role 是从 0.26.7 源码里实际出现的取值，任何一个漏掉都会让那批 NPC 掉进 default 档。
        string[] roles = ["普通玩家", "青训选手", "职业选手", "世界顶尖", "退役选手", "教练", "主播", "解说员", "赛事记者"];
        foreach (string role in roles)
            TestRunner.True(result.Economy.Seeding.Tiers.Any(t => t.Roles.Contains(role)),
                $"没有档位覆盖 Role = {role}");
    }

    private static void RenderedPromptContainsNoPlaceholders()
    {
        var result = LoadReal();
        var values = new Dictionary<string, string>
        {
            ["wealthBand"] = "手头宽裕",
            ["incomeSource"] = "俱乐部工资与胜场奖金",
            ["tags"] = "骄傲、要面子",
            ["kindTrait"] = "骄傲",
            ["moneyAttitude"] = "你出钱是为了让人看见你的本事。",
            ["intentPhrase"] = "愿意为成绩和人情花钱",
            ["balance"] = "$318,400",
            ["season"] = "10",
            ["seasonIncome"] = "$182,000",
            ["salaryIncome"] = "$96,000",
            ["winBonusIncome"] = "$52,000",
            ["sponsorIncome"] = "$34,000",
            ["day"] = "752",
            ["title"] = "支付「雇佣玩家担任教练」",
            ["amount"] = "$80,000",
            ["reason"] = "（原因：冲 A10）",
            ["line"] = "囊中羞涩，只能口头支持。"
        };

        foreach (string key in new[] { "stableTierLine", "stableMoneyView", "stableIntentLine", "volatileCurrent", "volatileLastEntry", "volatilePendingVoice" })
        {
            string template = result.Prompt.Get(key);
            string rendered = PromptConfig.Render(template, values);
            TestRunner.NotContains(rendered, "{", $"{key} 渲染后仍残留占位符");
            TestRunner.NotContains(rendered, "}", $"{key} 渲染后仍残留占位符");
            TestRunner.True(rendered.Length > 0, $"{key} 渲染结果为空");
        }
    }

    private static void StableTemplatesContainNoDigits()
    {
        var result = LoadReal();
        foreach (string key in new[] { "stableTierLine", "stableMoneyView", "stableIntentLine" })
        {
            string template = result.Prompt.Get(key);
            TestRunner.False(System.Text.RegularExpressions.Regex.IsMatch(template, @"\d"),
                $"{key} 含有数字，会破坏前缀缓存：{template}");
        }
    }

    private static void PromptTablesArePopulated()
    {
        var result = LoadReal();
        foreach (string table in new[] { "moneyAttitude", "intentPhrase", "incomeSource", "weightBands" })
            TestRunner.True(result.Prompt.Table(table).Count > 0, $"配置表 {table} 为空");

        // 24 种性格都应当有一句金钱观，否则注入的「金钱观」只有标签没有语气。
        foreach (string kind in ConfigLoader.CanonicalPersonalityKinds)
            TestRunner.True(result.Prompt.Table("moneyAttitude").ContainsKey(kind),
                $"moneyAttitude 缺少 {kind}");
    }

    /// <summary>
    /// 这条是回归测试：曾经因为把容器对象的内部键当成独立表，
    /// 导致 <c>_weightBands</c> 被拆成 10 张单条目表，weightBands 反而为空。
    /// </summary>
    private static void ContainerTableIsNotSplitIntoSingleEntryTables()
    {
        var result = LoadReal();

        // weightBands 必须是一张 9 条目的表（8 个国家 + default）。
        int bands = result.Prompt.Table("weightBands").Count;
        TestRunner.True(bands >= 9, $"weightBands 应有 9 条以上，实际 {bands}");

        // 不能出现以国家名命名的单条目表。
        foreach (string key in new[] { "普通玩家", "青训选手", "职业选手", "世界顶尖" })
            TestRunner.Equal(0, result.Prompt.Table(key).Count, $"出现了不该存在的单条目表 {key}");
    }

    private static void MissingDirectoryIsReportedAsError()
    {
        string bogus = Path.Combine(Path.GetTempPath(), "nse-missing-" + Guid.NewGuid().ToString("N"));
        var result = new ConfigLoader(bogus, Silent).Load();
        TestRunner.True(result.HasErrors, "目录不存在时应当报错");

        // 三份文件都必须被点名；缺失还会连带产生"配置为空"的二次错误，所以只要求"至少 3 条"。
        foreach (string file in new[] { ConfigLoader.EconomyFileName, ConfigLoader.PersonalityFileName, ConfigLoader.PromptFileName })
            TestRunner.True(result.Errors.Any(e => e.File == file), $"{file} 缺失未被报告");
    }
}
