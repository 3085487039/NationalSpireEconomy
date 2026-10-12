using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// 校验器 4：出钱方余额是否够。
/// <para>
/// <b>软失败的产生点就是这里</b>：NPC 出钱但余额不足、且
/// <c>trade.softFailOnBalance == true</c> 时，返回的不是硬失败，而是一条
/// <see cref="TradeCheck.Soft"/> 结果——不落 <c>TradeOffer</c>、不弹报错，
/// 只把一句素材交给调用方写进 <c>NpcEconomy.PendingVoice</c>，
/// 让 NPC 用自己的性格口吻在对话里说出来。
/// </para>
/// </summary>
public sealed class BalanceValidator : ITradeValidator
{
    public string Name => "Balance";

    public TradeCheck Validate(TradeContext ctx, TradeIntent intent)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(intent);

        long amount = intent.AmountUsd;

        // 玩家出钱：看接入层给的钱包。玩家的钱在游戏本体里，不在我们的账本里。
        if (intent.Direction == TradeDirections.PlayerToNpc)
        {
            if (ctx.Wallet.BalanceUsd < amount)
                return TradeCheck.Fail(TradeCodes.PlayerInsufficient,
                    $"你的余额不足以支付 {MoneyUnits.Format(amount)}（当前 {MoneyUnits.Format(ctx.Wallet.BalanceUsd)}）。");

            return TradeCheck.Pass;
        }

        // NPC 出钱：看我们账本里的余额。
        if (intent.Direction == TradeDirections.NpcToPlayer)
        {
            var payer = ctx.NpcOf(intent.InitiatorId);
            if (payer == null)
                return TradeCheck.Fail(TradeCodes.NoCounterparty, $"账本里没有 {intent.InitiatorId}。");

            if (payer.Balance >= amount) return TradeCheck.Pass;

            // 余额不足。软失败是"NPC 想给但给不起"的表达方式，只对 NPC 出钱这一支生效。
            if (ctx.Config.Trade.SoftFailOnBalance)
            {
                string line = PersonalityEconomy.SoftLine(ctx.Personality, KindOf(ctx, intent.InitiatorId));
                return TradeCheck.SoftDecline(TradeCodes.NpcSoftDecline, line);
            }

            return TradeCheck.Fail(TradeCodes.NpcInsufficient,
                $"{intent.InitiatorId} 的余额是 {MoneyUnits.Format(payer.Balance)}，" +
                $"不足以支付 {MoneyUnits.Format(amount)}。");
        }

        // NPC ↔ NPC：出钱方是发起方。
        if (intent.Direction == TradeDirections.NpcToNpc)
        {
            // 只有显式要求时才校验余额：NPC 之间的结算（例如俱乐部拨款）由调用方决定是否预检。
            if (!ctx.Config.Trade.NpcPayoutRequiresBalance) return TradeCheck.Pass;

            var payer = ctx.NpcOf(intent.InitiatorId);
            if (payer == null)
                return TradeCheck.Fail(TradeCodes.NoCounterparty, $"账本里没有 {intent.InitiatorId}。");

            if (payer.Balance < amount)
                return TradeCheck.Fail(TradeCodes.NpcInsufficient,
                    $"{intent.InitiatorId} 的余额不足以支付 {MoneyUnits.Format(amount)}。");

            return TradeCheck.Pass;
        }

        // 方向不合法由校验器 1 负责，这里放行以免错误码串台。
        return TradeCheck.Pass;
    }

    /// <summary>
    /// 取某人的性格快照。
    /// 优先用 <see cref="TradeContext.Counterparty"/>，缺省时回落到影子存档里的快照，
    /// 这样只传了 World 的调用方也能拿到性格台词。
    /// </summary>
    public static string KindOf(TradeContext ctx, string personId)
    {
        if (ctx.Counterparty != null
            && (string.Equals(ctx.Counterparty.Id, personId, StringComparison.Ordinal) || personId.Length == 0))
            return ctx.Counterparty.PersonalityKind;

        return ctx.NpcOf(personId)?.PersonalityKind ?? "";
    }
}
