using NationalSpire;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Game;

/// <summary>
/// 玩家的钱包：直接读写原版的 <c>CareerData.Credits</c> 与 <c>CareerLifeState.CreditFraction</c>。
/// <para>
/// <b>这是铁律三的唯一体现点。</b>原版内部单位是"1 单位 = 10 美元"，
/// 而整个经济系统内部一律用美元整数。两边的换算全部经
/// <see cref="MoneyUnits.UsdToInternalExact"/>，这里不出现任何手写的 <c>/ 10</c>。
/// </para>
/// <para>
/// 余额的权威定义就是原版 <c>CareerMoney.Balance(d) = d.Credits + d.Life.CreditFraction</c>，
/// 我们直接调用它而不是自己重算一遍——两处各算一次迟早会分叉。
/// </para>
/// <para>
/// <b>它不是影子存档的一部分</b>：玩家的钱在原版 <c>CareerData</c> 里，
/// 我们只是它的一个视图。<see cref="TryAdd"/> 改的是内存中的 <c>CareerData</c>，
/// 原版自己会在正常时机保存它。
/// </para>
/// </summary>
public sealed class PlayerWallet : IPlayerWallet
{
    private readonly CareerData _data;

    public PlayerWallet(CareerData data) =>
        _data = data ?? throw new ArgumentNullException(nameof(data));

    /// <summary>当前可用余额（美元）。</summary>
    public long BalanceUsd => (long)MoneyUnits.BalanceUsd(_data.Credits, _data.Life.CreditFraction);

    /// <summary>
    /// 增减玩家的钱。
    /// <para>
    /// 返回 false 表示这次变动会让余额变成负数，<b>且保证一分钱都没动</b>——
    /// 与 <see cref="EconomyLedger"/> 的"余额不足在源头拒绝"是同一条纪律。
    /// </para>
    /// <para>
    /// <b>负数增量必须走 <see cref="MoneyUnits.AddUsd"/></b>，不能自己调
    /// <c>UsdToInternalExact</c>：后者<b>拒绝负数入参</b>（这是刻意的设计，
    /// 因为负数的 Floor/Ceiling 语义反直觉）。<c>AddUsd</c> 内部按"加"处理，
    /// 天然支持负增量，且它就是铁律三要求的那个唯一出口。
    /// </para>
    /// </summary>
    public bool TryAdd(long deltaUsd, out string? error)
    {
        // AddUsd 返回 (整数部分, 小数部分)，先算再判负——判断本身不改任何状态。
        var (credits, fraction) = MoneyUnits.AddUsd(_data.Credits, _data.Life.CreditFraction, deltaUsd);

        if (credits < 0 || (credits == 0 && fraction < 0))
        {
            error = $"玩家余额不足：当前 {MoneyUnits.Format(BalanceUsd)}，" +
                    $"本次需要 {MoneyUnits.Format(Math.Abs(deltaUsd))}。";
            return false;
        }

        _data.Credits = checked((int)credits);
        _data.Life.CreditFraction = fraction;

        error = null;
        return true;
    }

    /// <summary>只读快照，用于日志与自检：原版那两个字段的原始值。</summary>
    public (int Credits, decimal Fraction, long BalanceUsd) Snapshot() =>
        (_data.Credits, _data.Life.CreditFraction, BalanceUsd);
}
