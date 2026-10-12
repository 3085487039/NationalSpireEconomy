using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// 校验器 6：<b>性格适配</b>。这是交易系统的核心玩法，也是本步唯一"有血有肉"的一环。
/// <para>
/// 四条子规则，按 (a) → (d) 的顺序判定，第一条命中即返回：
/// <list type="bullet">
/// <item><b>(a) 上限</b>：NPC 出钱超过"性格愿意出的上限" → <c>PERSONALITY_CAP</c>，附议价台词。</item>
/// <item><b>(b) 施舍门槛</b>：玩家给骄傲的人塞小钱 → <c>PERSONALITY_REFUSES_CHARITY</c>，附拒绝台词。</item>
/// <item><b>(c) 情绪值</b>：NPC 出钱但分量不够 → <c>PERSONALITY_LOW_EMOTION</c>。</item>
/// <item><b>(d) 高好感极端拒绝</b>：交情再好，小到离谱的金额仍然会被拒。</item>
/// </list>
/// </para>
/// <para>
/// <b>设计要点</b>：性格决定"意愿与相对量级"，财富档位决定"绝对量级"。
/// 两者正交相乘，才有"同样余额 10 万，骄傲的愿出 8 万、拮据的最多出 1 万"。
/// 感情只削弱 <c>charitySensitivity</c> 一个轴，其余五轴不受影响。
/// </para>
/// </summary>
public sealed class PersonalityFitValidator : ITradeValidator
{
    public string Name => "PersonalityFit";

    /// <summary>只有这三种 Kind 会被"施舍门槛"审视：它们天然是"给钱 / 施与"的语气。</summary>
    public static readonly string[] CharityKinds =
        [TradeKinds.Tip, TradeKinds.Sponsor, TradeKinds.Transfer];

    /// <summary>判定 (d) 用的"交情档"阈值：好感倍率不高于它才算有交情。</summary>
    public const double IntimateMultiplierThreshold = 0.10;

    /// <summary>
    /// 哪些失败码属于"<b>性格上的拒绝</b>"，而不是"余额/形状/频次"那类技术性拒绝。
    /// <para>
    /// 只有这几个码会让人<b>记仇</b>（<see cref="Inject.IntentEvolve"/> 的
    /// <c>trade_charity_refused</c> 触发）。软失败 <c>NPC_SOFT_DECLINE</c> <b>不在其中</b>：
    /// 那是余额不足，不是被人小看。
    /// </para>
    /// <para>
    /// <b>这份清单必须覆盖本校验器所有"拒绝"分支</b>。漏一个的后果是：
    /// 那条规则拒绝时不会记仇，机制静默失效且没有任何报错。
    /// 对应回归测试：<c>RefusalCodeSetMatchesValidatorOwnership</c>。
    /// </para>
    /// </summary>
    public static readonly string[] RefusalCodes =
    [
        TradeCodes.PersonalityRefusesCharity,   // (b) 施舍门槛
        TradeCodes.PersonalityCap,              // (a) 上限
        TradeCodes.PersonalityLowEmotion,       // (c) 情绪值不足
        TradeCodes.PersonalityRefusesLoan       // 不开口借钱
    ];

    /// <summary>这个失败码是否代表一次性格上的拒绝（值得记仇）。</summary>
    public static bool IsPersonalityRefusal(string code) =>
        RefusalCodes.Contains(code, StringComparer.Ordinal);

    public TradeCheck Validate(TradeContext ctx, TradeIntent intent)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(intent);

        // NPC 与 NPC 之间不走性格适配：那不是"NPC 对玩家的态度"问题。
        if (intent.Direction == TradeDirections.NpcToNpc) return TradeCheck.Pass;

        string npcId = ctx.IsPlayer(intent.InitiatorId) ? intent.CounterpartyId : intent.InitiatorId;
        var npc = ctx.NpcOf(npcId);
        if (npc == null)
            return TradeCheck.Fail(TradeCodes.NoCounterparty, $"账本里没有 {npcId}，无法判断性格。");

        string kind = BalanceValidator.KindOf(ctx, npcId);
        var profile = PersonalityEconomy.ProfileFor(ctx.Personality, kind);
        var values = PersonalityEconomy.Evaluate(ctx.Personality, kind, ctx.Favour);

        long balance = npc.Balance;
        long amount = intent.AmountUsd;

        // 上限必须用【未被好感修正】的 profile：感情不改变一个人"最多愿意出多少"的量级，
        // 它只削弱 charitySensitivity。用 values（已乘好感）会重复计入好感。
        long cap = PersonalityEconomy.SpendCap(ctx.Config, npc.TierId, balance, profile);
        long floor = PersonalityEconomy.SpendFloor(ctx.Config, npc.TierId, cap, values.CharitySensitivity);
        double emotion = PersonalityEconomy.Emotion(amount, balance);

        // ── (a) 上限：性格决定的"最多愿意出多少" ──
        if (intent.Direction == TradeDirections.NpcToPlayer && amount > cap)
            return TradeCheck.Fail(TradeCodes.PersonalityCap,
                PersonalityEconomy.CapLine(ctx.Personality, kind, cap));

        // ── (b) 施舍门槛：骄傲的人被小额冒犯 ──
        if (intent.Direction == TradeDirections.PlayerToNpc
            && CharityKinds.Contains(intent.Kind, StringComparer.Ordinal)
            && amount < floor)
            return TradeCheck.Fail(TradeCodes.PersonalityRefusesCharity,
                PersonalityEconomy.CharityLine(ctx.Personality, values.LineSet, kind));

        // ── (c) 情绪值：金额分量够不够 ──
        if (intent.Direction == TradeDirections.NpcToPlayer
            && emotion < ctx.Config.Trade.MinEmotionToAccept)
            return TradeCheck.Fail(TradeCodes.PersonalityLowEmotion,
                $"这点钱在他看来分量不够（情绪值 {emotion:0.00} 低于门槛 {ctx.Config.Trade.MinEmotionToAccept:0.00}）。");

        // ── (d) 高好感仍然拒绝（极端差距）──
        // 只在"交情档"生效：陌生人对小额本来就该拒，那条已经由 (b) 覆盖。
        if (intent.Direction == TradeDirections.PlayerToNpc
            && values.CharityMultiplier <= IntimateMultiplierThreshold
            && PersonalityEconomy.IsExtremeGap(ctx.Config, amount, balance))
            return TradeCheck.Fail(TradeCodes.PersonalityRefusesCharity,
                PersonalityEconomy.CharityLine(ctx.Personality, values.LineSet, kind));

        // ── 借款：只有 borrow == true 的性格才会开口 ──
        // 借钱方向是玩家出钱（player_to_npc），所以放在最后、且不参与上面的施舍判定。
        if (intent.Kind == TradeKinds.Loan
            && intent.Direction == TradeDirections.PlayerToNpc
            && !values.Borrow)
            return TradeCheck.Fail(TradeCodes.PersonalityRefusesLoan,
                $"「{kind}」这种性格不会向人开口借钱。");

        return TradeCheck.Pass;
    }
}
