using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// 校验器 3：物品目录。
/// <para>
/// <b>本期是"接口就绪、功能未启用"</b>：<see cref="Items"/> 为空（纯转账）时直接通过；
/// 一旦带物品行，就用 <see cref="TradeContext.Catalog"/> 去解析，解析不出即拒绝。
/// 未接线时 <see cref="NullTradeCatalog"/> 恒返回 false，于是任何购物意图都会被
/// <c>ITEM_UNKNOWN</c> 挡下——<b>宁可明确拒绝，也不要静默放行一笔买不到东西的扣款</b>。
/// </para>
/// </summary>
public sealed class CatalogValidator : ITradeValidator
{
    public string Name => "Catalog";

    public TradeCheck Validate(TradeContext ctx, TradeIntent intent)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(intent);

        // 纯转账：本期的主路径，直接通过。
        if (!intent.HasItems) return TradeCheck.Pass;

        var catalog = ctx.Catalog ?? NullTradeCatalog.Instance;
        long expected = 0;

        foreach (var line in intent.Items)
        {
            if (!catalog.TryResolveItem(line.ItemId, out var item))
                return TradeCheck.Fail(TradeCodes.ItemUnknown,
                    $"目录里没有「{line.ItemId}」这个物品。");

            if (!item.IsUnlimited && item.Stock < line.Quantity)
                return TradeCheck.Fail(TradeCodes.ItemOutOfStock,
                    $"「{item.Name}」库存不足（需要 {line.Quantity}，只剩 {item.Stock}）。");

            expected += item.UnitPriceUsd * line.Quantity;
        }

        // 单价与总额必须对得上：Amount 是模型给的提议值，不能成为绕过定价的通道。
        if (expected != intent.AmountUsd)
            return TradeCheck.Fail(TradeCodes.ItemPriceMismatch,
                $"报价 {MoneyUnits.Format(intent.AmountUsd)} 与目录价 {MoneyUnits.Format(expected)} 不一致。");

        return TradeCheck.Pass;
    }
}
