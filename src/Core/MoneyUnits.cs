using System.Globalization;

namespace NationalSpireEconomy.Core;

/// <summary>
/// 原版 NationalSpire 的「领域」。同一份源码里存在两套互不相同的记账单位，
/// 必须显式声明当前在哪个领域，否则会出现「AI 说 5 万、账上扣 5 千」。
/// </summary>
public enum MoneyDomain
{
    /// <summary>
    /// 内部记账单位，1 单位 = 10 美元。
    /// 适用：<c>CareerData.Credits</c>、<c>CareerLifeState.CreditFraction</c>、
    /// <c>ClubOffer.SigningBonus/SeasonPay/WinBonus/GoalReward</c>、
    /// <c>SponsorContract.Retainer/WinBonus</c>、<c>OwnedPlayerContract.Signing/Wage/WinBonus</c>、
    /// <c>CareerClub.Budget</c>。
    /// </summary>
    InternalUnits,

    /// <summary>
    /// 美元。这是<b>我们模组的唯一内部单位</b>。
    /// 适用：<c>PrivateOffer.Signing/Wage/WinBonus</c>（模型直接输出、玩家直接核对的那一套），
    /// 以及影子存档里的全部金额。
    /// </summary>
    Usd
}

/// <summary>换算无法无损表示时抛出，避免静默丢精度。</summary>
public sealed class MoneyLossException : Exception
{
    public MoneyLossException(string message) : base(message) { }
}

/// <summary>
/// <b>全模组唯一的金额换算出口。</b>任何跨出本类型直接写 <c>* 10</c> 或 <c>/ 10</c> 的代码都视为缺陷。
/// 单位约定的依据：<c>OwnedClubs.cs:62-63</c> 用 <c>o.Signing / 10m == contract.Signing</c>
/// 比较两类合同的金额，证明换算因子恒为 10。
/// </summary>
public static class MoneyUnits
{
    /// <summary>1 内部单位 = 10 美元。</summary>
    public const int UsdPerInternalUnit = 10;

    /// <summary>`Credits` 是 int，可用总额受此上限约束。</summary>
    public const long MaxUsdInCredits = int.MaxValue * (long)UsdPerInternalUnit;

    // ── 内部单位 → 美元 ────────────────────────────────────────────────

    public static long InternalToUsd(long internalUnits) => checked(internalUnits * UsdPerInternalUnit);

    public static long InternalToUsd(decimal internalUnits) => (long)decimal.Truncate(internalUnits * UsdPerInternalUnit);

    // ── 美元 → 内部单位 ────────────────────────────────────────────────

    /// <summary>
    /// 美元 → 内部单位。<paramref name="policy"/> 决定遇到非整单位时如何处理。
    /// <b>前置条件：金额非负。</b>负数会让 Ceiling/Floor 的语义变得反直觉，
    /// 而业务上金额恒为非负，因此这里直接拒绝而不是给一个容易误用的结果。
    /// </summary>
    public static long UsdToInternal(long usd, RoundingPolicy policy = RoundingPolicy.RequireExact)
    {
        if (usd < 0) throw new ArgumentOutOfRangeException(nameof(usd), usd, "金额不能为负。");
        if (usd % UsdPerInternalUnit == 0) return usd / UsdPerInternalUnit;
        return policy switch
        {
            RoundingPolicy.RequireExact => throw new MoneyLossException(
                $"{usd} 美元不是 {UsdPerInternalUnit} 的整数倍，无法无损表示为一个内部单位；" +
                $"若确实要写入原版字段，请显式选择 RoundingPolicy.Nearest 或 Floor。"),
            RoundingPolicy.Nearest => (long)Math.Round(usd / (decimal)UsdPerInternalUnit, MidpointRounding.AwayFromZero),
            RoundingPolicy.Floor => usd / UsdPerInternalUnit,
            RoundingPolicy.Ceiling => (usd + UsdPerInternalUnit - 1) / UsdPerInternalUnit,
            _ => throw new ArgumentOutOfRangeException(nameof(policy))
        };
    }

    /// <summary>美元 → 内部单位，保留分数。金额必须非负。</summary>
    public static decimal UsdToInternalExact(long usd)
    {
        if (usd < 0) throw new ArgumentOutOfRangeException(nameof(usd), usd, "金额不能为负。");
        return usd / (decimal)UsdPerInternalUnit;
    }

    // ── 显示 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 渲染成美元文本。等价于原版 <c>CareerMoney.Format</c>，但入参是美元而非内部单位，
    /// 避免把「已经换算过的值」再乘一次 10（原版 <c>PrivateMessageScreen.cs:507</c> 正是这个缺陷）。
    /// </summary>
    public static string Format(long usd)
        => "$" + usd.ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>渲染内部单位：先换算成美元再渲染，语义上等同原版 <c>CareerMoney.Format</c>。</summary>
    public static string FormatInternal(long internalUnits) => Format(InternalToUsd(internalUnits));

    // ── 余额与分数进位 ────────────────────────────────────────────────

    /// <summary>
    /// 原版余额 = <c>Credits</c>（整单位）+ <c>CreditFraction</c>（不足一单位的小数部分）。
    /// 返回值一律折算为美元，小数部分按需要保留。
    /// </summary>
    public static decimal BalanceUsd(long credits, decimal creditFraction)
        => (credits + creditFraction) * UsdPerInternalUnit;

    /// <summary>
    /// 把一笔美元金额加进「整单位 + 分数」结构。返回新的整数部分与小数部分。
    /// 溢出不静默截断，直接抛 <see cref="OverflowException"/>。
    /// <para>
    /// <b><paramref name="usd"/> 允许为负</b>（支出 / 冲正 / 退款都是这一支），
    /// 所以这里<b>不能</b>直接把入参丢给 <see cref="UsdToInternalExact"/>——
    /// 后者刻意拒绝负数（负数在 Ceiling/Floor 下语义反直觉），
    /// 把负号剥掉再补回来即可：金额恒为 10 的整数倍时，除法与符号可交换。
    /// </para>
    /// <para>
    /// 注意 <see cref="BalanceUsd"/> 返回的是<b>美元</b>，所以本方法在美元域上做加减，
    /// 调用方不需要自己折算。
    /// </para>
    /// </summary>
    public static (long Credits, decimal Fraction) AddUsd(long credits, decimal fraction, long usd)
    {
        decimal delta = usd < 0 ? -UsdToInternalExact(-usd) : UsdToInternalExact(usd);

        decimal totalInternal = credits + fraction + delta;
        long whole = checked((long)decimal.Floor(totalInternal));
        return (whole, totalInternal - whole);
    }

    // ── 解析 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 解析玩家或模型给出的金额文本。接受 <c>50000</c>、<c>$50,000</c>、<c>5万</c>、<c>1.2万</c>、
    /// <c>3亿</c>、<c>5万5千</c>。返回美元。
    /// </summary>
    public static bool TryParseUsd(string? text, out long usd)
    {
        usd = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim().Replace("$", "").Replace("＄", "").Replace(",", "").Replace("，", "").Replace(" ", "");

        decimal total = 0m;
        int index = 0;
        bool any = false;

        while (index < s.Length)
        {
            int start = index;
            while (index < s.Length && (char.IsDigit(s[index]) || s[index] == '.')) index++;
            if (index == start) return false;                       // 既不是数字也不是单位，拒绝
            if (!decimal.TryParse(s[start..index], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value))
                return false;

            decimal scale = 1m;
            if (index < s.Length)
            {
                switch (s[index])
                {
                    case '万': scale = 10_000m; index++; break;
                    case '亿': scale = 100_000_000m; index++; break;
                    case '千': scale = 1_000m; index++; break;
                    case '百': scale = 100m; index++; break;
                    case '美':
                        if (index + 1 < s.Length && s[index + 1] == '元') { index += 2; }
                        break;
                    default:
                        return false;                                    // 出现未知字符
                }
            }
            total += value * scale;
            any = true;
        }

        if (!any || total < 0 || total > long.MaxValue) return false;
        usd = (long)decimal.Truncate(total);
        return true;
    }

    public static long ParseUsd(string? text)
        => TryParseUsd(text, out long usd)
            ? usd
            : throw new FormatException($"无法解析金额：{text ?? "<null>"}");

    /// <summary>把大额数字写成人话档位，用于 Prompt 的稳定段（不含数字，保护前缀缓存）。</summary>
    public static string WealthBand(long usd) => usd switch
    {
        < 0 => "负债",
        < 1_000 => "几乎没有积蓄",
        < 10_000 => "手头很紧",
        < 50_000 => "不算宽裕",
        < 200_000 => "有些积蓄",
        < 800_000 => "手头宽裕",
        _ => "相当富裕"
    };
}

/// <summary>美元 → 内部单位时的取整策略。</summary>
public enum RoundingPolicy
{
    /// <summary>默认。不是 10 的整数倍就抛异常，逼调用方显式表态。</summary>
    RequireExact,
    /// <summary>四舍五入到最接近的整单位。</summary>
    Nearest,
    /// <summary>向下取整（对正数是截断）。</summary>
    Floor,
    /// <summary>向上取整。</summary>
    Ceiling
}
