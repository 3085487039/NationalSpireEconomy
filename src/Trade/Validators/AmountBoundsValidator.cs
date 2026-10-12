using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// 校验器 2：金额边界。纯粹的范围检查，不查任何余额或性格。
/// </summary>
public sealed class AmountBoundsValidator : ITradeValidator
{
    public string Name => "AmountBounds";

    public TradeCheck Validate(TradeContext ctx, TradeIntent intent)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(intent);

        long min = ctx.Config.Trade.MinAmount;
        long max = ctx.Config.Trade.MaxSingleAmount;
        long amount = intent.AmountUsd;

        if (amount <= 0)
            return TradeCheck.Fail(TradeCodes.AmountTooSmall,
                $"金额必须为正，实际 {MoneyUnits.Format(amount)}。");

        if (amount < min)
            return TradeCheck.Fail(TradeCodes.AmountTooSmall,
                $"金额低于单笔下限，至少需要 {MoneyUnits.Format(min)}（本次 {MoneyUnits.Format(amount)}）。");

        // 上限只约束"真正要动的钱"。带物品行的复合交易里 Amount 是物品价，
        // 但既然物品价也要走账，同样受 maxSingleAmount 约束，避免出现绕过上限的通道。
        if (amount > max)
            return TradeCheck.Fail(TradeCodes.AmountTooLarge,
                $"金额超过单笔上限 {MoneyUnits.Format(max)}（本次 {MoneyUnits.Format(amount)}）。");

        return TradeCheck.Pass;
    }
}
