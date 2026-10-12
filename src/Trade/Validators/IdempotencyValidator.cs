using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// 校验器 8：幂等。防的是"同一个意图被提交两次"。
/// <para>
/// 它查的是<b>收据本</b>（已经执行过的事实），而不是交易单状态——
/// 交易单状态由执行器在 <c>Commit</c> 里把关。两道闸各管一段：
/// 收据本管"这件事提交过没有"，交易单状态机管"这张单还能不能动"。
/// </para>
/// <para>
/// <b>只在 Reserve 阶段使用</b>：确认阶段若再跑这一条就会自锁——
/// 意图键在 Reserve 成功时就登记了，于是玩家一点确认就被判成"重复提交"。
/// 确认阶段的防重由"交易单状态必须仍是 pending"与"动账幂等键"共同保证。
/// </para>
/// </summary>
public sealed class IdempotencyValidator : ITradeValidator
{
    public string Name => "Idempotency";

    public TradeCheck Validate(TradeContext ctx, TradeIntent intent)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return Check(new EconomyLedger(ctx.World), intent, ctx.Day);
    }

    /// <summary>纯判定：该意图是否已经提交过。把它独立出来，提交与确认两处才能各自按需调用。</summary>
    public static TradeCheck Check(EconomyLedger ledger, TradeIntent intent, int day)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(intent);

        if (ledger.HasReceipt(IntentKey(ledger, intent, day)))
            return TradeCheck.Fail(TradeCodes.AlreadyApplied, "这笔交易已经提交过了，不会重复执行。");

        return TradeCheck.Pass;
    }

    /// <summary>
    /// 意图级幂等键：同一个 NPC + 同一天 + 同方向 + 同金额的意图只允许提交一次。
    /// 这是"模型重试时又发了一遍同样的标记"这类重复的真实来源。
    /// </summary>
    public static string IntentKey(EconomyLedger ledger, TradeIntent intent, int day)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(intent);

        return ledger.Key("intent",
            intent.InitiatorId, intent.CounterpartyId, intent.Kind,
            intent.AmountUsd.ToString(), day.ToString());
    }
}
