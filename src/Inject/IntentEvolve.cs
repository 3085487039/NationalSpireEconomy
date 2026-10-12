using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Inject;

/// <summary>
/// 意向漂移的档位。决定 Prompt 稳定段取哪一句 <c>intentPhrase</c>。
/// </summary>
public static class DriftBands
{
    public const string Generous = "generous";
    public const string Steady = "steady";
    public const string Cautious = "cautious";
    public const string Stingy = "stingy";
    public const string Resentful = "resentful";

    /// <summary>全部档位，从最慷慨到最谨慎。</summary>
    public static readonly string[] All = [Generous, Steady, Cautious, Stingy, Resentful];
}

/// <summary>
/// 意向演进的触发类型与漂移增量。
/// <para>
/// <b>这些常量是唯一的定义点</b>：<c>TradeEngine</c> 记触发时用它，
/// 审计与测试也用它，避免各处写字符串导致行为与文档分叉。
/// </para>
/// </summary>
public static class DriftTriggers
{
    /// <summary>收到玩家的钱（被赞助 / 被打赏 / 被雇佣）。倾向更慷慨。</summary>
    public const string TradeReceived = "trade_received";
    /// <summary>自己掏钱给玩家。倾向更谨慎。</summary>
    public const string TradePaid = "trade_paid";
    /// <summary>想给钱但余额不足，走了软失败。这个人会记住这件事。</summary>
    public const string SoftDeclined = "trade_soft_declined";
    /// <summary>小额施舍被本人拒了（校验器 6 的 <c>PERSONALITY_REFUSES_CHARITY</c>）。</summary>
    public const string CharityRefused = "trade_charity_refused";

    public const double ReceivedDelta = +0.05;
    public const double PaidDelta = -0.02;
    public const double SoftDeclinedDelta = -0.05;
    public const double CharityRefusedDelta = -0.03;

    /// <summary>按触发类型取标准增量。</summary>
    public static double DeltaFor(string kind) => kind switch
    {
        TradeReceived => ReceivedDelta,
        TradePaid => PaidDelta,
        SoftDeclined => SoftDeclinedDelta,
        CharityRefused => CharityRefusedDelta,
        _ => 0
    };
}

/// <summary>
/// 意向演进：把历史行为累积成一个 <c>DriftScore</c>，再翻译成人话措辞。
/// <para>
/// <b>它不负责把文字拼进 Prompt</b>——那是 <see cref="StableSegment"/> 的事。
/// 这样"演进规则"和"注入模板"可以各自演进，互不牵扯。
/// </para>
/// <para>
/// 起点是 0（即 <see cref="DriftBands.Steady"/>）：性格本身已经由六轴表达，
/// 意向演进只表达"这个人<b>最近</b>对钱的态度变了没有"。
/// </para>
/// </summary>
public static class IntentEvolve
{
    /// <summary>漂移分数的上限/下限。防止一个人被反复赞助后变成"无限慷慨"。</summary>
    public const double MaxDrift = 1.0;

    /// <summary>档位分界，从慷慨到谨慎。</summary>
    public static readonly (double Min, string BandId)[] Bands =
    [
        (0.30, DriftBands.Generous),
        (0.02, DriftBands.Steady),
        (-0.15, DriftBands.Cautious),
        (double.NegativeInfinity, DriftBands.Stingy)
    ];

    /// <summary>
    /// 累积一次行为。
    /// <para>
    /// 增量缺省由 <see cref="DriftTriggers.DeltaFor"/> 给出，只有"记恨类"触发才需要覆盖它。
    /// </para>
    /// </summary>
    public static string Record(NpcEconomy npc, string kind, string detail, int day, double? deltaOverride = null)
    {
        ArgumentNullException.ThrowIfNull(npc);

        double delta = deltaOverride ?? DriftTriggers.DeltaFor(kind);
        Accumulate(npc, kind, detail, day, delta);
        return detail;
    }

    /// <summary>只改分数、不记触发。用于批量重算。</summary>
    public static void ApplyDelta(NpcEconomy npc, double delta)
    {
        ArgumentNullException.ThrowIfNull(npc);
        npc.Lifetime.DriftScore = Math.Clamp(npc.Lifetime.DriftScore + delta, -MaxDrift, MaxDrift);
    }

    /// <summary>
    /// 把漂移分数翻译成档位 id。
    /// <para>
    /// <see cref="DriftBands.Resentful"/> 是<b>叠加</b>在分数之上的：分数不为负的人也仍然会
    /// 介意"前几天被人拿小钱试探"，所以它优先于纯分数的分档。
    /// </para>
    /// </summary>
    public static string BandIdFor(NpcEconomy npc, int day, int resentfulWindowDays = 7)
    {
        ArgumentNullException.ThrowIfNull(npc);
        if (IsResentful(npc, day, resentfulWindowDays)) return DriftBands.Resentful;
        return BandIdForScore(npc.Lifetime.DriftScore);
    }

    /// <summary>纯分数的分档（不含记恨叠加）。</summary>
    public static string BandIdForScore(double driftScore)
    {
        foreach (var (min, bandId) in Bands)
            if (driftScore >= min) return bandId;

        return DriftBands.Stingy;
    }

    /// <summary>是否还在"记恨窗口"里。</summary>
    public static bool IsResentful(NpcEconomy npc, int day, int resentfulWindowDays = 7)
    {
        ArgumentNullException.ThrowIfNull(npc);
        int last = npc.Lifetime.LastRefusedCharityDay;
        return last > 0 && day > 0 && day - last <= Math.Max(1, resentfulWindowDays);
    }

    /// <summary>
    /// 取该档位的措辞。缺项时回退到 <c>steady</c>，再缺就回退到 <c>default</c>，最后给一句内置兜底。
    /// <b>返回值必须零数字</b>——它会进稳定段，破坏前缀缓存是不可接受的。
    /// </summary>
    public static string PhraseFor(PromptConfig prompt, string bandId)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var table = prompt.Table("intentPhrase");

        if (table.TryGetValue(bandId, out var phrase) && phrase.Length > 0) return phrase;
        if (table.TryGetValue(DriftBands.Steady, out var steady) && steady.Length > 0) return steady;
        if (table.TryGetValue("default", out var fallback) && fallback.Length > 0) return fallback;
        return "花钱有自己的分寸";
    }

    /// <summary>一句话摘要，用于审计与日志。<b>不含金额</b>，可以安全地进日志。</summary>
    public static string Describe(NpcEconomy npc, int day) =>
        $"{BandIdFor(npc, day)}（{npc.Lifetime.DriftScore:0.000}）";

    private static void Accumulate(NpcEconomy npc, string kind, string detail, int day, double delta)
    {
        ApplyDelta(npc, delta);
        npc.AppendTrigger(new IntentTrigger
        {
            Day = day,
            Kind = kind,
            Detail = detail,
            Delta = delta
        });

        // 被"小钱试探"过的日子要单独留痕：Resentful 档靠它判定，而不是靠分数。
        if (kind == DriftTriggers.SoftDeclined || kind == DriftTriggers.CharityRefused)
            npc.Lifetime.LastRefusedCharityDay = day;

        npc.LastTouchedDay = Math.Max(npc.LastTouchedDay, day);
    }
}
