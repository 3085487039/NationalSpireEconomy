using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// 校验器 5：同一 NPC 的每日交易次数上限。
/// <para>
/// 数的是<b>我们账本里</b>当天的交易流水，而不是另记一个计数器：
/// 账本本身就有 <c>Day</c> 字段，另立计数器反而会与"账实一致"的契约打架。
/// 只统计会动余额的科目，冲正流水不算一次新交易。
/// </para>
/// </summary>
public sealed class DailyLimitValidator : ITradeValidator
{
    public string Name => "DailyLimit";

    /// <summary>计入"每日交易次数"的科目。</summary>
    public static readonly string[] CountedKinds =
        [TradeKinds.Sponsor, TradeKinds.Tip, TradeKinds.Hire, TradeKinds.Bonus, TradeKinds.Transfer, TradeKinds.Loan, "购买"];

    public TradeCheck Validate(TradeContext ctx, TradeIntent intent)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(intent);

        int limit = ctx.Config.Trade.DailyLimitPerNpc;
        if (limit <= 0) return TradeCheck.Pass;             // 0 或负数表示不限制

        // 限额是挂在 NPC 身上的，玩家侧不参与计数。
        string npcId = ctx.IsPlayer(intent.InitiatorId) ? intent.CounterpartyId : intent.InitiatorId;
        if (ctx.IsPlayer(npcId)) return TradeCheck.Pass;    // 双方都是玩家，交给校验器 1 处理

        var npc = ctx.NpcOf(npcId);
        if (npc == null) return TradeCheck.Pass;            // 不存在的 NPC 交给校验器 4 报错

        int today = CountOn(npc, ctx.Day);
        if (today >= limit)
            return TradeCheck.Fail(TradeCodes.DailyLimit,
                $"{npcId} 今天已经交易 {today} 次，达到每日上限 {limit} 次。");

        return TradeCheck.Pass;
    }

    /// <summary>数某个 NPC 在指定游戏日的成交次数。</summary>
    public static int CountOn(NpcEconomy npc, int day)
    {
        ArgumentNullException.ThrowIfNull(npc);
        int count = 0;
        foreach (var entry in npc.Ledger)
        {
            if (entry.Day != day) continue;
            if (entry.Kind == EconomyLedger.ReversalKind) continue;
            if (CountedKinds.Contains(entry.Kind, StringComparer.Ordinal)) count++;
        }
        return count;
    }
}
