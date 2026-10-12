using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// 校验器 1：结构形状。最便宜的一环，所以排在链首。
/// <list type="bullet">
/// <item><c>Kind</c> 必须在 <c>trade.allowedKinds</c> 白名单内
/// （<c>购买</c> 是协议保留值，走物品通道，不受白名单约束）。</item>
/// <item>发起方与对手方都要有、且不能是同一个人。</item>
/// <item><c>Direction</c> 必须是三个已知方向之一。</item>
/// <item>物品行的 <c>ItemId</c> 非空、<c>Quantity &gt; 0</c>。</item>
/// </list>
/// </summary>
public sealed class IntentShapeValidator : ITradeValidator
{
    public string Name => "IntentShape";

    public TradeCheck Validate(TradeContext ctx, TradeIntent intent)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(intent);

        // 解析失败的意图先在这里挡下：带着 ParseError 往下走只会得到误导性的错误码。
        if (intent.ParseFailed)
            return TradeCheck.Fail(TradeCodes.ParseError, "交易标记无法解析：" + intent.ParseError);

        if (intent.Kind.Length == 0)
            return TradeCheck.Fail(TradeCodes.BadKind, "交易标记里没有 Kind。");

        bool isBuy = intent.Kind == TradeIntent.BuyKind;
        if (!isBuy && !ctx.Config.Trade.AllowedKinds.Contains(intent.Kind, StringComparer.Ordinal))
            return TradeCheck.Fail(TradeCodes.BadKind,
                $"「{intent.Kind}」不在允许的交易类型里（允许：{string.Join("、", ctx.Config.Trade.AllowedKinds)}）。");

        if (!IsKnownDirection(intent.Direction))
            return TradeCheck.Fail(TradeCodes.BadShape, $"未知的交易方向：{intent.Direction}。");

        if (string.IsNullOrWhiteSpace(intent.InitiatorId) || string.IsNullOrWhiteSpace(intent.CounterpartyId))
            return TradeCheck.Fail(TradeCodes.BadShape, "交易缺少发起方或对手方。");

        if (string.Equals(intent.InitiatorId, intent.CounterpartyId, StringComparison.Ordinal))
            return TradeCheck.Fail(TradeCodes.BadShape, "发起方与对手方不能是同一个人。");

        // 有物品行时，付款方与收货方必须一个是我们账本里的 NPC、一个是玩家：
        // NPC 与 NPC 之间的物品倒手不在本期范围内。
        if (intent.HasItems)
        {
            if (ctx.IsPlayer(intent.InitiatorId) && ctx.IsPlayer(intent.CounterpartyId))
                return TradeCheck.Fail(TradeCodes.BadShape, "购买物品必须有一方是 NPC。");
        }

        foreach (var line in intent.Items)
        {
            if (string.IsNullOrWhiteSpace(line.ItemId))
                return TradeCheck.Fail(TradeCodes.BadShape, "物品行缺少 ItemId。");
            if (line.Quantity <= 0)
                return TradeCheck.Fail(TradeCodes.ItemQuantityInvalid,
                    $"物品「{line.ItemId}」的数量必须为正，实际 {line.Quantity}。");
        }

        return TradeCheck.Pass;
    }

    public static bool IsKnownDirection(string direction) =>
        direction is TradeDirections.PlayerToNpc or TradeDirections.NpcToPlayer or TradeDirections.NpcToNpc;
}
