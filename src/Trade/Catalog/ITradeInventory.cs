namespace NationalSpireEconomy.Trade;

/// <summary>
/// 物品持有量。<b>本期是空实现</b>：真正的实现要落到影子存档或原版道具系统上，
/// 那是后续"购买物品"活动的事。
/// </summary>
public interface ITradeInventory
{
    int CountOf(string ownerId, string itemId);
    void Grant(string ownerId, string itemId, int quantity);
    void Consume(string ownerId, string itemId, int quantity);
}
