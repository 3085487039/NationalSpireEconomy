using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Tests;

/// <summary>MoneyUnits 是全模组唯一的换算出口，这里把它钉死。</summary>
public static class MoneyUnitsTests
{
    public static Action[] Cases =>
    [
        InternalToUsdFactorIsTen,
        InternalToUsdDecimalTruncates,
        UsdToInternalRequiresExactByDefault,
        UsdToInternalRoundingPolicies,
        UsdToInternalRejectsNegative,
        FormatRendersUsdDirectly,
        FormatInternalConvertsFirst,
        BalanceCombinesCreditsAndFraction,
        AddUsdKeepsFractionAndCarriesWhole,
        AddUsdAcceptsNegativeDelta,
        AddUsdDetectsOverflow,
        ParsePlainAndFormatted,
        ParseChineseUnits,
        ParseRejectsGarbage,
        WealthBandBuckets
    ];

    private static void InternalToUsdFactorIsTen()
    {
        TestRunner.Equal(10L, MoneyUnits.InternalToUsd(1L));
        TestRunner.Equal(30000L, MoneyUnits.InternalToUsd(3000L));
        TestRunner.Equal(0L, MoneyUnits.InternalToUsd(0L));
        TestRunner.Equal(-250L, MoneyUnits.InternalToUsd(-25L));
    }

    private static void InternalToUsdDecimalTruncates()
    {
        // 内部单位的分数部分不足一单位，向零截断，不四舍五入。
        TestRunner.Equal(15L, MoneyUnits.InternalToUsd(1.5m));
        TestRunner.Equal(-15L, MoneyUnits.InternalToUsd(-1.5m));
    }

    private static void UsdToInternalRequiresExactByDefault()
    {
        TestRunner.Equal(3000L, MoneyUnits.UsdToInternal(30000L));
        // 关键：非整单位必须抛异常，逼调用方显式表态，避免静默丢精度。
        TestRunner.Throws<MoneyLossException>(() => MoneyUnits.UsdToInternal(30001L));
        TestRunner.Throws<MoneyLossException>(() => MoneyUnits.UsdToInternal(5L));
    }

    private static void UsdToInternalRoundingPolicies()
    {
        // Nearest 用 AwayFromZero：35 / 10 = 3.5 -> 4
        TestRunner.Equal(4L, MoneyUnits.UsdToInternal(35L, RoundingPolicy.Nearest));
        TestRunner.Equal(3L, MoneyUnits.UsdToInternal(34L, RoundingPolicy.Nearest));
        TestRunner.Equal(3L, MoneyUnits.UsdToInternal(30L, RoundingPolicy.Floor));
        TestRunner.Equal(30L, MoneyUnits.UsdToInternal(300L, RoundingPolicy.Floor));
        // Ceiling 对正数向上取整：25 / 10 = 2.5 -> 3
        TestRunner.Equal(3L, MoneyUnits.UsdToInternal(25L, RoundingPolicy.Ceiling));
        TestRunner.Equal(3L, MoneyUnits.UsdToInternal(21L, RoundingPolicy.Ceiling));
    }

    private static void UsdToInternalRejectsNegative()
    {
        // 负数会让 Ceiling/Floor 的语义反直觉，业务上也不需要，直接拒绝。
        TestRunner.Throws<ArgumentOutOfRangeException>(() => MoneyUnits.UsdToInternal(-10L));
        TestRunner.Throws<ArgumentOutOfRangeException>(() => MoneyUnits.UsdToInternal(-35L, RoundingPolicy.Nearest));
        TestRunner.Throws<ArgumentOutOfRangeException>(() => MoneyUnits.UsdToInternalExact(-1L));
    }

    private static void FormatRendersUsdDirectly()
    {
        // 这是修复原版 PrivateMessageScreen.cs:507 那个缺陷的关键：
        // 传入的已经是美元，绝不能再乘 10。
        TestRunner.Equal("$30,000", MoneyUnits.Format(30000L));
        TestRunner.Equal("$0", MoneyUnits.Format(0L));
        TestRunner.Equal("$1,234,567", MoneyUnits.Format(1_234_567L));
        TestRunner.Equal("$500", MoneyUnits.Format(500L));
    }

    private static void FormatInternalConvertsFirst()
    {
        // 语义上等价于原版 CareerMoney.Format(units) => "$" + units*10
        TestRunner.Equal("$30,000", MoneyUnits.FormatInternal(3000L));
        TestRunner.Equal("$18,000", MoneyUnits.FormatInternal(1800L));
    }

    private static void BalanceCombinesCreditsAndFraction()
    {
        // Credits 是内部单位，CreditFraction 是不足一单位的小数。
        TestRunner.Equal(30000m, MoneyUnits.BalanceUsd(3000L, 0m));
        TestRunner.Equal(30005m, MoneyUnits.BalanceUsd(3000L, 0.5m));
        TestRunner.Equal(30000.7m, MoneyUnits.BalanceUsd(3000L, 0.07m));
    }

    private static void AddUsdKeepsFractionAndCarriesWhole()
    {
        var (credits, fraction) = MoneyUnits.AddUsd(100L, 0m, 5000L);
        TestRunner.Equal(600L, credits);          // 100 + 500 内部单位
        TestRunner.Equal(0m, fraction);

        // 5 美元不足一个内部单位，应当只进小数部分。
        (credits, fraction) = MoneyUnits.AddUsd(100L, 0m, 5L);
        TestRunner.Equal(100L, credits);
        TestRunner.Equal(0.5m, fraction);

        // 再补 5 美元，小数部分进位。
        (credits, fraction) = MoneyUnits.AddUsd(credits, fraction, 5L);
        TestRunner.Equal(101L, credits);
        TestRunner.Equal(0m, fraction);
    }

    private static void AddUsdDetectsOverflow()
    {
        // AddUsd 内部把结果落在 long 域，所以真正的溢出边界是 long。
        TestRunner.Throws<OverflowException>(() => MoneyUnits.AddUsd(long.MaxValue, 0m, 1000L));
        // 正常范围内不受影响。
        TestRunner.Equal(1000L, MoneyUnits.AddUsd(0L, 0m, 10_000L).Credits);
    }

    /// <summary>
    /// <b>回归测试：一笔真实缺陷。</b><c>AddUsd</c> 是"加一笔美元增量"的入口，
    /// 而支出/冲正/退款都是负增量。它原先直接把入参丢给 <c>UsdToInternalExact</c>，
    /// 而后者刻意拒绝负数——于是任何"从玩家余额里扣钱"的路径都会抛
    /// <c>ArgumentOutOfRangeException</c>，玩家的钱根本扣不动。
    /// </summary>
    private static void AddUsdAcceptsNegativeDelta()
    {
        // 从 5000 内部单位（$50,000）扣掉 $25,000（= 2,500 内部单位）。
        // 记住换算：1 内部单位 = $10，所以 $25,000 是 2500 而不是 25000。
        var (credits, fraction) = MoneyUnits.AddUsd(5000L, 0m, -25_000L);
        TestRunner.Equal(2500L, credits, "负数增量必须能正常抵扣");
        TestRunner.Equal(0m, fraction);

        // 扣到不足一个内部单位时，小数部分应为正（表示还剩一点）。
        (credits, fraction) = MoneyUnits.AddUsd(100L, 0m, -5L);
        TestRunner.Equal(99L, credits, "0.5 个内部单位应从整单位里借位");
        TestRunner.Equal(0.5m, fraction, "借位后小数部分应为 0.5");

        // 恰好扣光。
        (credits, fraction) = MoneyUnits.AddUsd(100L, 0m, -1000L);
        TestRunner.Equal(0L, credits);
        TestRunner.Equal(0m, fraction);

        // 扣成负数：AddUsd 本身不拦（它只负责算），由调用方判断余额是否够。
        (credits, fraction) = MoneyUnits.AddUsd(100L, 0m, -2000L);
        TestRunner.True(credits < 0 || (credits == 0 && fraction < 0), "应算出负余额供调用方拒绝");

        // 正负可逆：加回来应该回到原值。
        var (back, backFraction) = MoneyUnits.AddUsd(2500L, 0m, 25_000L);
        TestRunner.Equal(5000L, back, "加减应当可逆");
        TestRunner.Equal(0m, backFraction);
    }

    private static void ParsePlainAndFormatted()
    {
        TestRunner.True(MoneyUnits.TryParseUsd("50000", out long a));
        TestRunner.Equal(50000L, a);
        TestRunner.True(MoneyUnits.TryParseUsd("$50,000", out long b));
        TestRunner.Equal(50000L, b);
        // 前后空白与千分位逗号都应被容忍。
        TestRunner.True(MoneyUnits.TryParseUsd("  1,200  ", out long c));
        TestRunner.Equal(1200L, c);
    }

    private static void ParseChineseUnits()
    {
        TestRunner.True(MoneyUnits.TryParseUsd("5万", out long wan));
        TestRunner.Equal(50000L, wan);

        TestRunner.True(MoneyUnits.TryParseUsd("1.2万", out long point));
        TestRunner.Equal(12000L, point);

        TestRunner.True(MoneyUnits.TryParseUsd("3亿", out long yi));
        TestRunner.Equal(300_000_000L, yi);

        // 复合写法：5万5千 = 55000
        TestRunner.True(MoneyUnits.TryParseUsd("5万5千", out long compound));
        TestRunner.Equal(55000L, compound);

        TestRunner.True(MoneyUnits.TryParseUsd("8000美元", out long usd));
        TestRunner.Equal(8000L, usd);
    }

    private static void ParseRejectsGarbage()
    {
        TestRunner.False(MoneyUnits.TryParseUsd("", out _));
        TestRunner.False(MoneyUnits.TryParseUsd(null, out _));
        TestRunner.False(MoneyUnits.TryParseUsd("abc", out _));
        TestRunner.False(MoneyUnits.TryParseUsd("5万abc", out _));
        TestRunner.False(MoneyUnits.TryParseUsd("-100", out _));
        TestRunner.Throws<FormatException>(() => MoneyUnits.ParseUsd("呵呵"));
    }

    private static void WealthBandBuckets()
    {
        TestRunner.Equal("几乎没有积蓄", MoneyUnits.WealthBand(500));
        TestRunner.Equal("手头很紧", MoneyUnits.WealthBand(5000));
        TestRunner.Equal("不算宽裕", MoneyUnits.WealthBand(20000));
        TestRunner.Equal("有些积蓄", MoneyUnits.WealthBand(60000));
        TestRunner.Equal("手头宽裕", MoneyUnits.WealthBand(300000));
        TestRunner.Equal("相当富裕", MoneyUnits.WealthBand(900000));
    }
}
