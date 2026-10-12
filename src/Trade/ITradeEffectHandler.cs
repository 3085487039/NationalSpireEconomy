using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// 一笔交易真正执行完之后的结果回执。副作用处理器拿它做后续动作
/// （发货、加心情、签合同……）。
/// </summary>
public sealed record TradeReceipt(
    string OfferId,
    string Kind,
    string Direction,
    string InitiatorId,
    string CounterpartyId,
    long AmountUsd,
    IReadOnlyList<TradeItemLine> Items,
    string ReceiptKey,
    string LedgerEntryId,
    int Day,
    int Season,
    long BeneficiaryBalanceAfterUsd,
    string? Error = null)
{
    public bool Ok => string.IsNullOrEmpty(Error);
}

/// <summary>
/// 购买成功后的副作用处理器，按 <see cref="EffectKey"/> 分发。
/// <b>本期只有接口与注册表</b>，不实现任何真实效果。
/// </summary>
public interface ITradeEffectHandler
{
    /// <summary>例如 <c>inventory.grant</c> / <c>mood.boost</c> / <c>contract.hire</c>。</summary>
    string EffectKey { get; }

    /// <summary>执行前的可行性判断。返回失败即整笔不执行。</summary>
    TradeCheck CanApply(TradeContext ctx, TradeIntent intent);

    void Apply(TradeContext ctx, TradeIntent intent, TradeReceipt receipt);
}

/// <summary>
/// 副作用处理器的注册表。当前是纯内存静态表，<b>不</b>参与影子存档的序列化。
/// </summary>
public static class TradeEffectRegistry
{
    private static readonly List<ITradeEffectHandler> Registered = [];
    private static readonly object Gate = new();

    public static void Register(ITradeEffectHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (Gate)
        {
            if (Registered.Any(h => string.Equals(h.EffectKey, handler.EffectKey, StringComparison.Ordinal))) return;
            Registered.Add(handler);
        }
    }

    public static IReadOnlyList<ITradeEffectHandler> Handlers
    {
        get { lock (Gate) return Registered.ToArray(); }
    }

    /// <summary>按 EffectKey 找处理器；找不到返回 null。</summary>
    public static ITradeEffectHandler? Find(string effectKey)
    {
        lock (Gate) return Registered.FirstOrDefault(h => string.Equals(h.EffectKey, effectKey, StringComparison.Ordinal));
    }

    /// <summary>仅供测试：清空注册表。</summary>
    public static void ClearForTests()
    {
        lock (Gate) Registered.Clear();
    }
}
