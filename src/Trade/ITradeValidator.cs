namespace NationalSpireEconomy.Trade;

/// <summary>
/// 校验链上的一环。实现必须<b>无副作用</b>：只读 <see cref="TradeContext"/>，
/// 返回一条 <see cref="TradeCheck"/>。任何写余额、写存档的行为都只能发生在执行器里。
/// </summary>
public interface ITradeValidator
{
    /// <summary>供日志与诊断用的名字。</summary>
    string Name { get; }

    TradeCheck Validate(TradeContext ctx, TradeIntent intent);
}
