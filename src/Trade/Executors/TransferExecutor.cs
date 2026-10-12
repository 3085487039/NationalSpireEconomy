using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// 转账类执行器：赞助 / 打赏 / 雇佣 / 奖金 / 转账 / 借款。
/// <para>
/// 它是<b>唯一</b>允许调用 <see cref="EconomyLedger"/> 改余额的地方之一
/// （另一个是 <see cref="PurchaseExecutor"/>）。校验器一律无副作用，
/// 所有金额变动都在这里发生，且一律带幂等键。
/// </para>
/// </summary>
public sealed class TransferExecutor
{
    /// <summary>
    /// 真正动账。
    /// <para>
    /// <b>玩家 ↔ NPC</b> 走 <see cref="EconomyLedger.Transfer"/>，它的顺序是"NPC 先动、玩家后动"：
    /// NPC 的钱在我们手里、失败可回滚；玩家的钱在游戏本体里、动过不好撤。
    /// </para>
    /// <para>
    /// <b>NPC ↔ NPC</b> 走 <see cref="EconomyTick.PayTransferFee"/> 的双向原子模式，
    /// 买方不足则整笔作废，卖方不会凭空收到钱。
    /// </para>
    /// </summary>
    public ExecOutcome Execute(TradeContext ctx, TradeOffer offer, string receiptKey, string title)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(offer);

        var ledger = new EconomyLedger(ctx.World);
        int day = ctx.Day;
        int season = ctx.EffectiveSeason;

        // 幂等闸：命中即整体短路，绝不重复改动。
        if (ledger.HasReceipt(receiptKey))
            return ExecOutcome.Already(receiptKey, ExistingEntryId(ctx, offer, receiptKey));

        if (offer.Direction == TradeDirections.NpcToNpc)
            return ExecuteNpcToNpc(ctx, ledger, offer, receiptKey, day, season);

        bool fromPlayer = offer.Direction == TradeDirections.PlayerToNpc;
        string npcId = fromPlayer ? offer.CounterpartyId : offer.InitiatorId;

        var entry = new LedgerEntry
        {
            Day = day,
            Season = season,
            Kind = offer.Kind,
            Title = title,
            CounterpartyId = fromPlayer ? EconomyTier.PlayerId : npcId,
            OfferId = offer.Id,
            Reason = offer.Reason
        };

        var transfer = ledger.Transfer(offer.Amount, npcId, fromPlayer, ctx.Wallet, entry, receiptKey);

        if (transfer.NpcSide.Status == MoneyMoveStatus.AlreadyApplied)
            return ExecOutcome.Already(receiptKey, transfer.NpcSide.LedgerEntryId);

        if (!transfer.BothApplied)
        {
            string detail = !transfer.NpcSide.Applied
                ? transfer.NpcSide.Detail ?? "NPC 侧未通过。"
                : transfer.PlayerSide.Detail ?? "玩家侧未通过。";

            // NPC 出钱但余额不足 → 软失败。执行器只负责如实回报，是否落卡片由引擎决定。
            bool soft = !transfer.NpcSide.Applied
                && transfer.NpcSide.Status == MoneyMoveStatus.InsufficientFunds
                && ctx.Config.Trade.SoftFailOnBalance;

            return ExecOutcome.Failed(detail, soft);
        }

        ledger.FlushReceipts();
        return ExecOutcome.Succeeded(receiptKey, transfer.NpcSide.LedgerEntryId,
            transfer.NpcSide.BalanceAfterUsd, ctx.Wallet.BalanceUsd);
    }

    /// <summary>
    /// 找出某个幂等键已经落下的流水 id。
    /// <para>
    /// 流水 id 是 <see cref="EconomyLedger.IdFor"/> 由幂等键派生的，所以"用键找流水"
    /// 不需要额外索引：先算出 id，再在两侧账本里找一笔对得上的。
    /// 找不到就回退到派生 id 本身（它仍然是对的，只是那笔不在最近的流水环里了）。
    /// </para>
    /// </summary>
    private static string ExistingEntryId(TradeContext ctx, TradeOffer offer, string receiptKey)
    {
        string derived = EconomyLedger.IdFor(receiptKey);
        foreach (string personId in new[] { offer.InitiatorId, offer.CounterpartyId })
        {
            if (ctx.IsPlayer(personId)) continue;
            var npc = ctx.NpcOf(personId);
            if (npc == null) continue;

            // 内层键（:out / :in）也要一并找，否则 NPC↔NPC 的二次确认会报一个空流水 id。
            foreach (string candidate in new[] { derived, EconomyLedger.IdFor(receiptKey + ":out"), EconomyLedger.IdFor(receiptKey + ":in") })
                if (npc.Ledger.Any(e => string.Equals(e.Id, candidate, StringComparison.Ordinal)))
                    return candidate;
        }
        return derived;
    }

    private static ExecOutcome ExecuteNpcToNpc(TradeContext ctx, EconomyLedger ledger, TradeOffer offer,
        string receiptKey, int day, int season)
    {
        string buyer = offer.InitiatorId;
        string seller = offer.CounterpartyId;

        // 外层键（receiptKey）用来表达"这张单已经执行过"，内层键区分进出两笔。
        string outKey = receiptKey + ":out";
        string inKey = receiptKey + ":in";

        var spent = ledger.Debit(buyer, offer.Amount, new LedgerEntry
        {
            Day = day,
            Season = season,
            Kind = offer.Kind,
            Title = "支付：" + offer.Kind,
            CounterpartyId = seller,
            OfferId = offer.Id,
            Reason = offer.Reason
        }, outKey);

        if (spent.Status == MoneyMoveStatus.AlreadyApplied)
            return ExecOutcome.Already(receiptKey, spent.LedgerEntryId);

        if (spent.Status != MoneyMoveStatus.Applied)
            return ExecOutcome.Failed(spent.Detail ?? "出钱方未通过。",
                soft: spent.Status == MoneyMoveStatus.InsufficientFunds && ctx.Config.Trade.SoftFailOnBalance);

        var received = ledger.Credit(seller, offer.Amount, new LedgerEntry
        {
            Day = day,
            Season = season,
            Kind = offer.Kind,
            Title = "收到：" + offer.Kind,
            CounterpartyId = buyer,
            OfferId = offer.Id,
            Reason = offer.Reason
        }, inKey);

        if (!received.Applied)
        {
            // 收款失败 → 冲正付款方，保证不凭空少钱。
            ledger.Reverse(buyer, spent.LedgerEntryId, "收款方入账失败，已冲正。", day, season);
            return ExecOutcome.Failed("收款方入账失败，已冲正付款方支出。");
        }

        ledger.MarkReceipt(receiptKey);
        ledger.FlushReceipts();

        long buyerAfter = ctx.NpcOf(buyer)?.Balance ?? 0;
        return ExecOutcome.Succeeded(receiptKey, spent.LedgerEntryId, buyerAfter, ctx.Wallet.BalanceUsd);
    }
}

/// <summary>一次动账的结果。</summary>
public sealed record ExecOutcome(
    ExecStatus Status,
    string ReceiptKey,
    string LedgerEntryId = "",
    long NpcBalanceAfter = 0,
    long PlayerBalanceAfter = 0,
    string? Detail = null,
    bool Soft = false)
{
    /// <summary>是否真的动了账。</summary>
    public bool Applied => Status == ExecStatus.Applied;

    /// <summary>构造一个成功结果。命名不用 <c>Applied</c> 以免与上面的属性撞名。</summary>
    public static ExecOutcome Succeeded(string key, string entryId, long npcAfter, long playerAfter) =>
        new(ExecStatus.Applied, key, entryId, npcAfter, playerAfter);

    public static ExecOutcome Already(string key, string? entryId) =>
        new(ExecStatus.AlreadyApplied, key, entryId ?? "", Detail: "该幂等键已经执行过。");

    public static ExecOutcome Failed(string detail, bool soft = false) =>
        new(ExecStatus.Failed, "", Detail: detail, Soft: soft);
}

public enum ExecStatus
{
    Applied,
    AlreadyApplied,
    Failed
}
