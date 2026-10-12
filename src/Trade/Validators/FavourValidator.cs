using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// 校验器 7：好感门槛。
/// <para>
/// 只在 <c>npc_to_npc</c> 之外、且配置显式要求时才把关：
/// 默认 <c>requireFavourForNpcPayout = 0</c> 表示不设门槛，
/// 此时任何好感度都能让 NPC 出钱（真正的把关交给校验器 6 的性格适配）。
/// </para>
/// </summary>
public sealed class FavourValidator : ITradeValidator
{
    public string Name => "Favour";

    public TradeCheck Validate(TradeContext ctx, TradeIntent intent)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(intent);

        // 只约束"NPC 掏钱给玩家"这一支。玩家掏钱不需要好感。
        if (intent.Direction != TradeDirections.NpcToPlayer) return TradeCheck.Pass;
        if (!ctx.Config.Trade.NpcPayoutRequiresBalance && ctx.Config.Trade.RequireFavourForNpcPayout <= 0)
            return TradeCheck.Pass;

        int required = ctx.Config.Trade.RequireFavourForNpcPayout;
        if (ctx.Favour >= required) return TradeCheck.Pass;

        return TradeCheck.Fail(TradeCodes.FavourTooLow,
            $"好感度 {ctx.Favour} 低于 NPC 愿意出钱的门槛 {required}。");
    }
}
