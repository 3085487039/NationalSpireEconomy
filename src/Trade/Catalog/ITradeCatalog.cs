namespace NationalSpireEconomy.Trade;

/// <summary>
/// 物品目录。本期只提供 <see cref="NullTradeCatalog"/>（任何 ItemId 都解析不出），
/// 接口先就位是为了让 <c>CatalogValidator</c> 与 <c>PurchaseExecutor</c> 的结构不再返工。
/// </summary>
public interface ITradeCatalog
{
    bool TryResolveItem(string itemId, out TradeItem item);

    IReadOnlyList<TradeItem> All { get; }
}
