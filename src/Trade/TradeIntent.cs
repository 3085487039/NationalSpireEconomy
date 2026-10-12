using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// 从模型输出里解析出来的一条交易意向。
/// <para>
/// <b><see cref="AmountUsd"/> 只是"提议值"</b>：它由模型给出，但任何余额变动都不由模型决定，
/// 必须经过八条校验器与玩家确认才会动账。
/// </para>
/// </summary>
public sealed record TradeIntent(
    string Kind,
    string Direction,
    string InitiatorId,
    string CounterpartyId,
    long AmountUsd,
    IReadOnlyList<TradeItemLine> Items,
    string Reason,
    string RawText,
    string? ParseError = null)
{
    /// <summary>纯转账（无物品行）。与"购物"是两类语义，靠本属性区分。</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>
    /// 是否解析失败。
    /// <para>
    /// 判据同时覆盖 null 与空白：解析器在"没有错误"时应当返回 null，
    /// 但一旦哪天有人写成了空串，这里也不能把它误判成"有错误"。
    /// </para>
    /// </summary>
    public bool ParseFailed => !string.IsNullOrWhiteSpace(ParseError);

    /// <summary>协议里专门用来表示"购买物品"的伪 Kind，不进 <c>trade.allowedKinds</c> 白名单。</summary>
    public const string BuyKind = "购买";
}
