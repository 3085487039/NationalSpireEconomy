using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// 购买物品的执行器骨架。
/// <para>
/// <b>本期刻意不实现真实逻辑</b>：物品目录、库存与副作用都要接到原版道具系统上，
/// 那是后续"购买物品"活动的事。这里把结构定死，保证将来只是<b>填充</b>而不是重构。
/// </para>
/// <para>
/// 它与 <see cref="TransferExecutor"/> 共用同一条动账路径：
/// <c>TradeOffer.Amount</c> 与 <c>TradeOffer.Items</c> 是<b>并列字段</b>，
/// 所以"用 3 万加一件道具换某人加盟"这类复合交易不需要改数据结构。
/// </para>
/// </summary>
public sealed class PurchaseExecutor
{
    private readonly TransferExecutor _transfers = new();

    /// <summary>物品持有量。未接线时用空实现。</summary>
    public ITradeInventory Inventory { get; init; } = NullTradeInventory.Instance;

    /// <summary>购买是否已启用。未启用时执行会明确失败，而不是静默扣钱不发货。</summary>
    public bool Enabled { get; init; }

    public ExecOutcome Execute(TradeContext ctx, TradeOffer offer, string receiptKey, string title)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(offer);

        if (!Enabled)
            return ExecOutcome.Failed("购买物品功能尚未启用。");

        if (offer.Items.Count == 0)
            return ExecOutcome.Failed("购买交易缺少物品行。");

        // 钱与货的顺序：先动账（TransferExecutor 内部是双向原子的），
        // 再发货。发货失败要把钱退回去，绝不能出现"扣了钱没拿到东西"。
        var outcome = _transfers.Execute(ctx, offer, receiptKey, title);
        if (!outcome.Applied) return outcome;

        var catalog = ctx.Catalog ?? NullTradeCatalog.Instance;
        foreach (var line in offer.Items)
        {
            if (!catalog.TryResolveItem(line.ItemId, out var item))
                continue;                                  // 目录校验已在链上拦过，这里只是兜底

            Inventory.Grant(offer.Direction == TradeDirections.PlayerToNpc
                ? offer.CounterpartyId
                : offer.InitiatorId, item.Id, line.Quantity);
        }

        return outcome;
    }
}
