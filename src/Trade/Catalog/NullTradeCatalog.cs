namespace NationalSpireEconomy.Trade;

/// <summary>
/// 目录空实现。<c>TryResolveItem</c> 恒返回 false，于是任何带物品行的交易都会被
/// <c>CatalogValidator</c> 以 <c>ITEM_UNKNOWN</c> 拒绝。
/// <para>
/// 这是"接口就绪但功能未启用"的<b>正确</b>状态：宁可明确拒绝，也不要静默放行一笔买不到东西的扣款。
/// </para>
/// </summary>
public sealed class NullTradeCatalog : ITradeCatalog
{
    /// <summary>单例。校验器在未接线时用它。</summary>
    public static readonly NullTradeCatalog Instance = new();

    private static readonly TradeItem[] None = [];

    public IReadOnlyList<TradeItem> All => None;

    public bool TryResolveItem(string itemId, out TradeItem item)
    {
        item = null!;
        return false;
    }
}
