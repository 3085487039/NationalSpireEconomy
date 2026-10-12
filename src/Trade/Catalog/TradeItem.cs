namespace NationalSpireEconomy.Trade;

/// <summary>
/// 目录里的一个可购买物品。<b>本期只是数据契约</b>，不接原版道具系统。
/// </summary>
public sealed class TradeItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>单价（美元）。金额一律美元整数，换算只走 <c>MoneyUnits</c>。</summary>
    public long UnitPriceUsd { get; init; }

    public string Currency { get; init; } = "USD";

    /// <summary>库存。-1 表示无限。</summary>
    public int Stock { get; init; } = -1;

    /// <summary>人话标签，例如 ["消耗品","礼物"]。</summary>
    public List<string> Tags { get; init; } = [];

    /// <summary>购买成功后要触发哪些副作用（见 <see cref="ITradeEffectHandler.EffectKey"/>）。</summary>
    public List<string> EffectKeys { get; init; } = [];

    /// <summary>是否无限库存。</summary>
    public bool IsUnlimited => Stock < 0;
}
