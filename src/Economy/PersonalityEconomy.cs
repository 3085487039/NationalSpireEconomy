using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Economy;

/// <summary>一次交易求值用到的性格数值。已经叠加了好感度修正，可直接代入金额公式。</summary>
public readonly record struct PersonalityEconomyValues(
    double Give,
    double CharitySensitivity,
    double Bargain,
    double Thrift,
    bool Borrow,
    double Initiative,
    double CharityMultiplier,
    string FavorTierId,
    string LineSet);

/// <summary>
/// 性格 → 经济画像的查表与变更侦测。
/// <para>
/// 核心原则：<b>性格决定"意愿与相对量级"，财富档位决定"绝对量级"。</b>
/// 两者正交相乘，所以"余额同样 10 万，骄傲的愿出 8 万、拮据的最多出 1 万"是两条轴共同作用的结果，
/// 而不是硬编码了两种人格。
/// </para>
/// </summary>
public static class PersonalityEconomy
{
    /// <summary>未知性格（原版新增但我们没跟上）时的兜底标签。</summary>
    public const string UnknownTag = "普通";

    /// <summary>
    /// 情绪化目标：让"给钱"这件事看起来够不够分量。
    /// <c>情绪值 = 金额 / (余额 × Scale)</c>，小于 1 就是"在打发人"。
    /// 分母设了下限，避免余额很小的人拿到不合理的宽容度。
    /// </summary>
    public const double EmotionScale = 0.10;
    public const long EmotionFloorUsd = 500;

    /// <summary>好感度修正后的档位。</summary>
    public readonly record struct FavorAdjustment(string TierId, double Multiplier, string LineSet);

    /// <summary>按性格名取画像；缺失则回退 defaults。</summary>
    public static EconomyProfile ProfileFor(PersonalityConfig config, string? kind)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!string.IsNullOrWhiteSpace(kind) && config.Kinds.TryGetValue(kind, out var profile) && profile != null)
            return profile;
        return config.Defaults;
    }

    public static bool IsKnownKind(PersonalityConfig config, string? kind) =>
        !string.IsNullOrWhiteSpace(kind) && config.Kinds.ContainsKey(kind);

    /// <summary>
    /// 快照是否与当前性格一致。为 false 时说明原版的 <c>[Profile: 性格类型, ...]</c> 改写了性格，
    /// 调用方应重算画像并追加一条意向触发。
    /// </summary>
    public static bool IsSnapshotCurrent(NpcEconomy npc, INpcFacts facts)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(facts);
        return string.Equals(npc.PersonalityKind, facts.PersonalityKind, StringComparison.Ordinal);
    }

    /// <summary>把当前性格快照写进经济数据。</summary>
    public static void ApplySnapshot(NpcEconomy npc, INpcFacts facts)
    {
        npc.PersonalityKind = facts.PersonalityKind;
        npc.PersonalityVersion = facts.PersonalityVersion;
    }

    /// <summary>按好感度找档位；没有命中任何档位时回退到最宽的一档（multiplier 最小者）。</summary>
    public static FavorAdjustment FavorFor(PersonalityConfig config, int favour)
    {
        ArgumentNullException.ThrowIfNull(config);
        var tiers = config.FavorTiers.Tiers;
        if (tiers.Count == 0) return new FavorAdjustment("unknown", 1.0, "sharp");

        foreach (var tier in tiers)
            if (favour >= tier.Min && favour <= tier.Max)
                return new FavorAdjustment(tier.Id, tier.CharityMultiplier, tier.Line);

        // 好感度超出全部区间（例如原版放宽了 -100..100）时，取最宽容的一档。
        var widest = tiers.OrderBy(t => t.CharityMultiplier).First();
        return new FavorAdjustment(widest.Id, widest.CharityMultiplier, widest.Line);
    }

    /// <summary>求值：叠加好感修正后的六个轴。</summary>
    public static PersonalityEconomyValues Evaluate(PersonalityConfig config, string? kind, int favour)
    {
        var profile = ProfileFor(config, kind);
        var favor = FavorFor(config, favour);
        return new PersonalityEconomyValues(
            profile.Give,
            profile.CharitySensitivity * favor.Multiplier,
            profile.Bargain,
            profile.Thrift,
            profile.Borrow,
            profile.Initiative,
            favor.Multiplier,
            favor.TierId,
            favor.LineSet);
    }

    /// <summary>
    /// 支出上限：<c>余额 × tierCapRatio × give × (1 - 0.4 × thrift)</c>，再被全局单笔上限夹住。
    /// </summary>
    public static long SpendCap(EconomyConfig config, string tierId, long balance, EconomyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(profile);
        if (balance <= 0) return 0;

        double ratio = config.Season.TierCapRatio.TryGetValue(tierId, out var r) ? r : 0.30;
        double value = balance * ratio * profile.Give * (1 - 0.4 * profile.Thrift);
        long cap = (long)Math.Floor(value);
        return Math.Clamp(cap, 0, config.Trade.MaxSingleAmount);
    }

    /// <summary>
    /// 金额下限：<c>基准上限 × minRatio × (0.5 + charitySensitivity)</c>。
    /// 上限为 0（例如余额为零）时下限也为 0，不制造"必须给钱"的假约束。
    /// </summary>
    public static long SpendFloor(EconomyConfig config, string tierId, long cap, double charitySensitivity)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (cap <= 0) return 0;

        double ratio = config.Season.MinRatio.TryGetValue(tierId, out var r) ? r : 0.03;
        long floor = (long)Math.Floor(cap * ratio * (0.5 + charitySensitivity));
        return Math.Clamp(floor, 0, cap);
    }

    /// <summary>情绪值：金额相对余额×系数的比例。小于 1 表示"分量不够"。</summary>
    public static double Emotion(long amount, long balance)
    {
        long basis = Math.Max(EmotionFloorUsd, (long)(balance * EmotionScale));
        return amount / (double)basis;
    }

    /// <summary>
    /// 高好感仍然拒绝的判据：金额相对余额的比例低到 <c>extremeGapRatio</c> 以下。
    /// 这样高好感不等于"来者不拒"，交易系统保留张力。
    /// </summary>
    public static bool IsExtremeGap(EconomyConfig config, long amount, long balance)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (balance <= 0) return amount <= 0;
        return amount < balance * config.Season.ExtremeGapRatio;
    }

    /// <summary>按性格名取拒绝台词；缺失则回退 default。</summary>
    public static string CharityLine(PersonalityConfig config, string lineSet, string? kind)
    {
        ArgumentNullException.ThrowIfNull(config);
        var table = lineSet == "gentle" ? config.RefusalLines.Charity.Gentle : config.RefusalLines.Charity.Sharp;
        if (!string.IsNullOrWhiteSpace(kind) && table.TryGetValue(kind, out var line) && line.Length > 0) return line;
        return table.TryGetValue("default", out var fallback) ? fallback : "这钱你先收着吧。";
    }

    /// <summary>议价台词；<c>{suggested}</c> 会被替换成建议金额。</summary>
    public static string CapLine(PersonalityConfig config, string? kind, long suggestedUsd)
    {
        ArgumentNullException.ThrowIfNull(config);
        var table = config.RefusalLines.Cap;
        string template = !string.IsNullOrWhiteSpace(kind) && table.TryGetValue(kind, out var line) && line.Length > 0
            ? line
            : table.TryGetValue("default", out var fallback) ? fallback : "这个金额太夸张了。";
        return template.Replace("{suggested}", MoneyUnits.Format(suggestedUsd), StringComparison.Ordinal);
    }

    /// <summary>
    /// 软失败素材（余额不足时的"口头支持"）。
    /// <c>default</c> 给的是<b>台词原文</b>；性格变体给的是<b>语气方向</b>，
    /// 由模型自行措辞，避免机械复述同一句。
    /// </summary>
    public static string SoftLine(PersonalityConfig config, string? kind)
    {
        ArgumentNullException.ThrowIfNull(config);
        var soft = config.RefusalLines.Soft;
        if (!string.IsNullOrWhiteSpace(kind) && soft.ByKind.TryGetValue(kind, out var direction) && direction.Length > 0)
            return direction;
        return soft.DefaultLine;
    }

    /// <summary>拼接用于 Prompt 稳定段的「金钱观」人话素材。</summary>
    public static string TagText(EconomyProfile profile, PromptConfig prompt, string? kind)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(prompt);
        string tags = profile.Tags.Count > 0 ? string.Join("、", profile.Tags) : UnknownTag;
        if (!string.IsNullOrWhiteSpace(kind) && prompt.Table("moneyAttitude").TryGetValue(kind, out var attitude)
            && attitude.Length > 0)
            return tags + "。" + attitude;
        return tags + "。";
    }
}
