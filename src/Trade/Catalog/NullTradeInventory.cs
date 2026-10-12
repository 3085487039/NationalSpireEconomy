namespace NationalSpireEconomy.Trade;

/// <summary>物品持有量空实现：永远没有、也不接受任何物品。</summary>
public sealed class NullTradeInventory : ITradeInventory
{
    public static readonly NullTradeInventory Instance = new();

    public int CountOf(string ownerId, string itemId) => 0;

    public void Grant(string ownerId, string itemId, int quantity)
    {
        // 本期刻意不做任何事：真实实现在"购买物品"活动里落地。
    }

    public void Consume(string ownerId, string itemId, int quantity)
    {
        // 同上。
    }
}
