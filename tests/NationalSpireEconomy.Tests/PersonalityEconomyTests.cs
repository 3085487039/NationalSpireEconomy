using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 性格 → 经济画像。核心要证明的是<b>两条轴正交</b>：
/// 档位决定绝对量级，性格决定相对系数，且好感度只削弱"施舍敏感度"这一个轴。
/// </summary>
public static class PersonalityEconomyTests
{
    public static Action[] Cases =>
    [
        UnknownKindFallsBackToDefaults,
        KnownKindIsDetected,
        SnapshotDetectsPersonalityRewrite,
        SnapshotCanBeApplied,
        FavorTiersMapToExpectedBands,
        FavorReducesCharitySensitivity,
        FavorDoesNotTouchOtherAxes,
        OutOfRangeFavourFallsBackToWidestTier,
        SpendCapGrowsWithGiveAndShrinksWithThrift,
        SpendCapIsClampedByGlobalLimit,
        SpendFloorRespectsCharitySensitivity,
        SpendFloorIsZeroWhenCapIsZero,
        EmotionMeasuresRelativeWeight,
        ExtremeGapIdentifiesInsultingAmounts,
        CharityLineSwitchesByFavorBand,
        CapLineSubstitutesSuggestedAmount,
        SoftLineGivesTextForDefaultAndDirectionForKinds,
        AllKindsResolveToNonDefaultProfile,
        AllRefusalLinesAreNonEmpty
    ];

    private static readonly DateTimeOffset T0 = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    private static ConfigLoadResult LoadReal()
    {
        return new ConfigLoader(TestConfig.Directory(), Silent).Load();
    }

    private static void UnknownKindFallsBackToDefaults()
    {
        var config = LoadReal().Personality;
        var unknown = PersonalityEconomy.ProfileFor(config, "原版新增的某种性格");
        TestRunner.Equal(config.Defaults.Give, unknown.Give);
        TestRunner.Equal(config.Defaults.Tags, unknown.Tags);

        var empty = PersonalityEconomy.ProfileFor(config, "");
        TestRunner.Equal(config.Defaults.Give, empty.Give);
    }

    private static void KnownKindIsDetected()
    {
        var config = LoadReal().Personality;
        TestRunner.True(PersonalityEconomy.IsKnownKind(config, "热心同好"));
        TestRunner.False(PersonalityEconomy.IsKnownKind(config, "不存在"));
    }

    private static void SnapshotDetectsPersonalityRewrite()
    {
        var npc = new NpcEconomy { PersonId = "p0", PersonalityKind = "热心同好", PersonalityVersion = 3 };
        TestRunner.True(PersonalityEconomy.IsSnapshotCurrent(npc, new NpcFacts("p0", "普通玩家", PersonalityKind: "热心同好")));
        // 原版允许 AI 用 [Profile: 性格类型, ...] 改写性格，此时必须触发重算。
        TestRunner.False(PersonalityEconomy.IsSnapshotCurrent(npc, new NpcFacts("p0", "普通玩家", PersonalityKind: "暴躁喷子")));
    }

    private static void SnapshotCanBeApplied()
    {
        var npc = new NpcEconomy { PersonId = "p0" };
        PersonalityEconomy.ApplySnapshot(npc, new NpcFacts("p0", "普通玩家", PersonalityKind: "自我炫耀者", PersonalityVersion: 3));
        TestRunner.Equal("自我炫耀者", npc.PersonalityKind);
        TestRunner.Equal(3, npc.PersonalityVersion);
    }

    private static void FavorTiersMapToExpectedBands()
    {
        var config = LoadReal().Personality;
        TestRunner.Equal("stranger", PersonalityEconomy.FavorFor(config, -100).TierId);
        TestRunner.Equal("stranger", PersonalityEconomy.FavorFor(config, 0).TierId);
        TestRunner.Equal("familiar", PersonalityEconomy.FavorFor(config, 20).TierId);
        TestRunner.Equal("close", PersonalityEconomy.FavorFor(config, 50).TierId);
        TestRunner.Equal("intimate", PersonalityEconomy.FavorFor(config, 100).TierId);
        TestRunner.Equal("sharp", PersonalityEconomy.FavorFor(config, 0).LineSet);
        TestRunner.Equal("gentle", PersonalityEconomy.FavorFor(config, 90).LineSet);
    }

    private static void FavorReducesCharitySensitivity()
    {
        var config = LoadReal().Personality;
        double stranger = PersonalityEconomy.Evaluate(config, "战绩踩头者", 0).CharitySensitivity;
        double familiar = PersonalityEconomy.Evaluate(config, "战绩踩头者", 30).CharitySensitivity;
        double close = PersonalityEconomy.Evaluate(config, "战绩踩头者", 60).CharitySensitivity;
        double intimate = PersonalityEconomy.Evaluate(config, "战绩踩头者", 90).CharitySensitivity;

        TestRunner.True(stranger > familiar && familiar > close && close > intimate,
            $"好感越高施舍敏感度必须越低：{stranger} > {familiar} > {close} > {intimate}");

        // 陌生人 1.0 倍，交情 0.1 倍，量级要对得上。
        var baseValue = config.Kinds["战绩踩头者"].CharitySensitivity;
        TestRunner.True(Math.Abs(stranger - baseValue) < 1e-9, "陌生档不应打折扣");
        TestRunner.True(Math.Abs(intimate - baseValue * 0.1) < 1e-9, "交情档应为 0.1 倍");
    }

    private static void FavorDoesNotTouchOtherAxes()
    {
        var config = LoadReal().Personality;
        var low = PersonalityEconomy.Evaluate(config, "故意挑事者", -50);
        var high = PersonalityEconomy.Evaluate(config, "故意挑事者", 100);

        TestRunner.Equal(low.Give, high.Give, "好感不应改变给予倾向");
        TestRunner.Equal(low.Bargain, high.Bargain, "好感不应改变议价强度");
        TestRunner.Equal(low.Thrift, high.Thrift, "好感不应改变消费谨慎度");
        TestRunner.Equal(low.Initiative, high.Initiative, "好感不应改变主动程度");
        TestRunner.Equal(low.Borrow, high.Borrow, "好感不应改变借贷倾向");
    }

    private static void OutOfRangeFavourFallsBackToWidestTier()
    {
        var config = LoadReal().Personality;
        // 超出配置区间时取最宽容的一档，而不是抛异常或返回 0 倍。
        var adjustment = PersonalityEconomy.FavorFor(config, 9999);
        TestRunner.Equal("intimate", adjustment.TierId);
        TestRunner.True(adjustment.Multiplier > 0 && adjustment.Multiplier <= 1);
    }

    private static void SpendCapGrowsWithGiveAndShrinksWithThrift()
    {
        var loaded = LoadReal();
        var config = loaded.Economy;

        var generous = loaded.Personality.Kinds["狂热拥护者"];   // give 1.35 / thrift 0.15
        var stingy = loaded.Personality.Kinds["尖刻挑刺者"];     // give 0.40 / thrift 0.80

        long capGenerous = PersonalityEconomy.SpendCap(config, "professional", 100_000, generous);
        long capStingy = PersonalityEconomy.SpendCap(config, "professional", 100_000, stingy);
        TestRunner.True(capGenerous > capStingy, $"慷慨的上限必须更高：{capGenerous} vs {capStingy}");

        // 公式核对：balance × tierCapRatio × give × (1 - 0.4 × thrift)
        double ratio = config.Season.TierCapRatio["professional"];
        long expectedGenerous = (long)Math.Floor(100_000 * ratio * 1.35 * (1 - 0.4 * 0.15));
        TestRunner.Equal(Math.Min(expectedGenerous, config.Trade.MaxSingleAmount), capGenerous);
    }

    private static void SpendCapIsClampedByGlobalLimit()
    {
        var loaded = LoadReal();
        var config = loaded.Economy;
        var profile = loaded.Personality.Kinds["夸张吹捧者"];
        // 余额巨大时也必须被 trade.maxSingleAmount 夹住。
        long cap = PersonalityEconomy.SpendCap(config, "elite", 100_000_000, profile);
        TestRunner.Equal(config.Trade.MaxSingleAmount, cap);

        TestRunner.Equal(0L, PersonalityEconomy.SpendCap(config, "crowd", 0, profile), "零余额没有可支配上限");
    }

    private static void SpendFloorRespectsCharitySensitivity()
    {
        var loaded = LoadReal();
        var config = loaded.Economy;

        // 同一档位、同一上限，敏感度越高下限越高（越容易被小额冒犯）。
        long proud = PersonalityEconomy.SpendFloor(config, "professional", 50_000, 0.82);
        long easy = PersonalityEconomy.SpendFloor(config, "professional", 50_000, 0.10);
        TestRunner.True(proud > easy, $"骄傲的下限必须更高：{proud} vs {easy}");

        double ratio = config.Season.MinRatio["professional"];
        TestRunner.Equal((long)Math.Floor(50_000 * ratio * (0.5 + 0.82)), proud);
    }

    private static void SpendFloorIsZeroWhenCapIsZero()
    {
        var config = LoadReal().Economy;
        TestRunner.Equal(0L, PersonalityEconomy.SpendFloor(config, "crowd", 0, 0.9),
            "上限为 0 时不应制造'必须给钱'的假约束");
    }

    private static void EmotionMeasuresRelativeWeight()
    {
        // 余额 10 万，系数 0.1 → 基准 1 万。给 1 万情绪值 1.0，给 1000 情绪值 0.1。
        TestRunner.True(Math.Abs(PersonalityEconomy.Emotion(10_000, 100_000) - 1.0) < 1e-9);
        TestRunner.True(Math.Abs(PersonalityEconomy.Emotion(1_000, 100_000) - 0.1) < 1e-9);

        // 余额很小时分母有下限，避免过度宽容。
        TestRunner.True(PersonalityEconomy.Emotion(1_000, 100) > 1.0, "小余额下 1000 元应当显得很重");
    }

    private static void ExtremeGapIdentifiesInsultingAmounts()
    {
        var config = LoadReal().Economy;
        // extremeGapRatio 默认 0.0005：余额 10 万时，低于 50 元算极端差距。
        TestRunner.True(PersonalityEconomy.IsExtremeGap(config, 10, 100_000));
        TestRunner.False(PersonalityEconomy.IsExtremeGap(config, 5_000, 100_000));
    }

    private static void CharityLineSwitchesByFavorBand()
    {
        var config = LoadReal().Personality;
        string sharp = PersonalityEconomy.CharityLine(config, "sharp", "自我炫耀者");
        string gentle = PersonalityEconomy.CharityLine(config, "gentle", "自我炫耀者");
        TestRunner.Contains(sharp, "羞辱");
        TestRunner.NotEqual(sharp, gentle);

        // 未知性格回退 default，而不是抛异常或返回空串。
        string fallback = PersonalityEconomy.CharityLine(config, "sharp", "不存在");
        TestRunner.True(fallback.Length > 0);
    }

    private static void CapLineSubstitutesSuggestedAmount()
    {
        var config = LoadReal().Personality;
        string line = PersonalityEconomy.CapLine(config, "故意挑事者", 30_000);
        TestRunner.Contains(line, "$30,000");
        TestRunner.NotContains(line, "{suggested}");
    }

    private static void SoftLineGivesTextForDefaultAndDirectionForKinds()
    {
        var config = LoadReal().Personality;
        string defaultLine = PersonalityEconomy.SoftLine(config, "不存在");
        TestRunner.Equal(config.RefusalLines.Soft.DefaultLine, defaultLine);
        TestRunner.Contains(defaultLine, "口头支持");

        // 性格变体给的是"语气方向"而非台词，这是刻意的：避免模型机械复述。
        string variant = PersonalityEconomy.SoftLine(config, "自嘲找同伴者");
        TestRunner.NotEqual(defaultLine, variant);
        TestRunner.True(variant.Length > 0);
    }

    private static void AllKindsResolveToNonDefaultProfile()
    {
        var config = LoadReal().Personality;
        foreach (string kind in ConfigLoader.CanonicalPersonalityKinds)
        {
            var profile = PersonalityEconomy.ProfileFor(config, kind);
            TestRunner.True(profile.Tags.Count > 0, $"{kind} 缺少标签");
            TestRunner.NotEqual(config.Defaults.Tags[0], profile.Tags[0],
                $"{kind} 落到了 defaults，说明映射没生效");
        }
    }

    private static void AllRefusalLinesAreNonEmpty()
    {
        var config = LoadReal().Personality;
        foreach (string kind in ConfigLoader.CanonicalPersonalityKinds)
            foreach (string set in new[] { "sharp", "gentle" })
            {
                string line = PersonalityEconomy.CharityLine(config, set, kind);
                TestRunner.True(line.Length > 2, $"{kind} / {set} 的台词过短：{line}");
            }
    }
}
