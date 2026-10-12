using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Economy;

/// <summary>一次日推进的结果。</summary>
public sealed record TickResult(
    int Day,
    int Season,
    bool SeasonChanged,
    int SalariesPaid,
    long SalaryTotalUsd)
{
    public static readonly TickResult Skipped = new(0, 0, false, 0, 0);
}

/// <summary>
/// 时间推进与赛季结算。
/// <para>
/// 全部落实"幂等水位线"：<c>world.LastTickedDay</c> 一旦推进，重复调用同一天或更早的日期都直接跳过。
/// 于是跳天、回档、重进游戏、读档重放都不会重复发薪或重复发奖金。
/// 每个 NPC 自己还有一道 <c>LastSalaryPaidSeason</c>，即使水位线被人为弄丢也不会重复发薪。
/// </para>
/// </summary>
public static class EconomyTick
{
    /// <summary>由游戏天数推导赛季号。<c>seasonDays</c> 来自配置（长赛季 84 天）。</summary>
    public static int SeasonOf(int day, int seasonDays)
    {
        if (seasonDays <= 0) seasonDays = 84;
        int safeDay = Math.Max(1, day);
        return (safeDay - 1) / seasonDays + 1;
    }

    /// <summary>赛季第一天。</summary>
    public static int SeasonStartDay(int season, int seasonDays)
    {
        if (seasonDays <= 0) seasonDays = 84;
        return (Math.Max(1, season) - 1) * seasonDays + 1;
    }

    /// <summary>赛季最后一天。</summary>
    public static int SeasonEndDay(int season, int seasonDays) => SeasonStartDay(season, seasonDays) + Math.Max(1, seasonDays) - 1;

    /// <summary>
    /// 第 1 赛季：工资改在<b>赛季最后一天</b>结算，而不是赛季第 1 天。
    /// <para>
    /// 理由：世界刚建立时水位线是 <c>0</c>、<c>LastSalaryPaidSeason</c> 也是 <c>0</c>，
    /// 于是"赛季第 1 天发薪"意味着<b>第一次推进就白拿一份工资</b>——NPC 手里既有播种资金、
    /// 又立刻多一份薪水，前 83 天没有任何收入压力的经济体验就没了。
    /// 改成赛季末结算后，第 1 天到第 83 天只花播种资金，第 84 天（本配置 84 天赛季）才结算。
    /// </para>
    /// <para>
    /// <b>只影响第 1 赛季</b>：第 2 赛季起回到配置的 <c>salaryPayoutMode</c>，
    /// 所以那一段既有行为与既有测试都不受影响。
    /// </para>
    /// </summary>
    /// <param name="config">用来取赛季长度与发薪时机。</param>
    /// <param name="season">赛季号（从 1 开始）。</param>
    public static int SalaryDueDay(EconomyConfig config, int season)
    {
        ArgumentNullException.ThrowIfNull(config);

        int seasonDays = Math.Max(1, config.Season.DaysPerSeason);
        if (season <= FirstSeason) return SeasonEndDay(season, seasonDays);

        bool startMode = !string.Equals(config.Season.SalaryPayoutMode, "seasonEnd", StringComparison.Ordinal);
        return startMode ? SeasonStartDay(season, seasonDays) : SeasonEndDay(season, seasonDays);
    }

    /// <summary>按其工资在赛季末结算的第一个赛季。</summary>
    public const int FirstSeason = 1;

    /// <summary>
    /// 推进到指定游戏日。已经推进过或日期更早时返回 <see cref="TickResult.Skipped"/>。
    /// 中间被跳过的日期会逐日补齐，不会漏掉季节切换。
    /// </summary>
    public static TickResult AdvanceTo(EconomyWorld world, EconomyConfig config, int targetDay, IEconomyLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(config);

        if (targetDay <= world.LastTickedDay) return TickResult.Skipped;

        int seasonDays = Math.Max(1, config.Season.DaysPerSeason);
        int start = Math.Max(1, world.LastTickedDay + 1);
        bool seasonChanged = false;
        int salariesPaid = 0;
        long salaryTotal = 0;
        int lastSeason = world.LastSeason;

        for (int day = start; day <= targetDay; day++)
        {
            int season = SeasonOf(day, seasonDays);
            if (season != lastSeason)
            {
                lastSeason = season;
                seasonChanged = true;
                RollSeasonForAll(world, season);
            }

            var (paid, total) = PaySalariesIfDue(world, config, day, season);
            salariesPaid += paid;
            salaryTotal += total;

            world.LastTickedDay = day;
            world.LastSeason = season;
        }

        // 推进结束再兜一次裁剪：覆盖工资以外的收据来源（赛事奖金等），
        // 并保证"一次跳过很多天"之后收据本也是收敛的。
        {
            var ledger = new EconomyLedger(world);
            ledger.PruneReceipts();
            ledger.FlushReceipts();
        }

        if (salariesPaid > 0)
            log?.Info("tick",
                $"第 {targetDay} 天推进完成（第 {world.LastSeason} 赛季）：发放 {salariesPaid} 份工资，" +
                $"合计 {MoneyUnits.Format(salaryTotal)}。");

        return new TickResult(targetDay, world.LastSeason, seasonChanged, salariesPaid, salaryTotal);
    }

    /// <summary>跨赛季：把所有人的本赛季累计清零。</summary>
    public static int RollSeasonForAll(EconomyWorld world, int season)
    {
        ArgumentNullException.ThrowIfNull(world);
        foreach (var npc in world.Npcs.Values) EconomyLedger.RollSeason(npc, season);
        return world.Npcs.Count;
    }

    /// <summary>
    /// 到点发薪。第 1 赛季按<b>赛季末</b>结算，第 2 赛季起按配置的
    /// <c>salaryPayoutMode</c>（见 <see cref="SalaryDueDay"/>）。
    /// <b>每个人自己带水位线</b>，所以即使 <c>LastTickedDay</c> 因故被重置也不会重复发。
    /// </summary>
    private static (int Paid, long Total) PaySalariesIfDue(EconomyWorld world, EconomyConfig config, int day, int season)
    {
        int dueDay = SalaryDueDay(config, season);

        // 只有真的走到结算日才发。世界刚建立时 LastTickedDay 是 0、LastSalaryPaidSeason 也是 0，
        // 但第 1 赛季的结算日被刻意推到赛季末（见 SalaryDueDay），所以"第一天就发一轮工资"
        // 这件事在源头就不会发生。
        if (day != dueDay || season <= 0) return (0, 0);

        var ledger = new EconomyLedger(world);
        int paid = 0;
        long total = 0;

        foreach (var npc in world.Npcs.Values)
        {
            if (npc.Retired) continue;
            if (npc.LastSalaryPaidSeason >= season) continue;

            long salary = SalaryFor(config, npc.TierId);
            if (salary <= 0) continue;

            var result = ledger.Credit(npc.PersonId, salary, new LedgerEntry
            {
                Day = day,
                Season = season,
                Kind = "工资",
                Title = "第 " + season + " 赛季工资",
                CounterpartyId = npc.ClubId.Length > 0 ? npc.ClubId : ""
            }, ledger.Key("salary", npc.PersonId, season.ToString()));

            if (!result.Applied) continue;
            npc.LastSalaryPaidSeason = season;
            paid++;
            total += salary;
        }

        // ★ 收据必须裁剪（真机实测事故，2026-10-11 20:0x）：
        //   发薪是"每人每季一条收据"，而真实世界有 1632 人 ⇒ 一季就是 1632 条，5 季 8000+ 条。
        //   不裁的话会撞上 EconomyStore 的自检（收据 > MaxReceipts*2 就拒绝写入），
        //   后果是【影子存档永远停在播种那天，而内存里已经跑到第 398 天】——真机上就是这么炸的：
        //   `世界 e35d0119 落盘失败（推进到第 369 天）：收据条数 8160 异常膨胀，可能未裁剪`。
        //   裁剪是安全的：发薪防重主要靠 npc.LastSalaryPaidSeason 水位线（见本方法开头），
        //   收据只是第二道闸；Pending 交易引用的键由 Prune 内部保护，不会被裁掉。
        ledger.PruneReceipts();
        ledger.FlushReceipts();
        return (paid, total);
    }

    /// <summary>按档位取工资；未配置则按档位名给一个保守默认，避免整档人一分钱拿不到。</summary>
    public static long SalaryFor(EconomyConfig config, string tierId)
    {
        ArgumentNullException.ThrowIfNull(config);
        var table = config.Season.SalaryByTier;
        if (table.TryGetValue(tierId, out long exact)) return exact;
        return tierId switch
        {
            "elite" => 60_000,
            "professional" => 24_000,
            "veteran" => 15_000,
            "youth" => 9_000,
            "media" => 6_000,
            "crowd" => 600,
            _ => 0
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // 收入入口（供补丁层调用）
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 赛事奖金入账。幂等键里带 <paramref name="eventId"/>，
    /// 所以同一场比赛无论被结算几次，每个人只会入账一次。
    /// </summary>
    public static int PayPrize(EconomyWorld world, IEnumerable<string> personIds, long amountUsd,
        string eventId, string title, int day, int season, IEconomyLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (amountUsd <= 0) return 0;

        var ledger = new EconomyLedger(world);
        int paid = 0;
        foreach (string personId in personIds.Distinct(StringComparer.Ordinal))
        {
            var result = ledger.Credit(personId, amountUsd, new LedgerEntry
            {
                Day = day,
                Season = season,
                Kind = "赛事奖金",
                Title = title,
                CounterpartyId = "player",
                Reason = "（" + eventId + "）"
            }, ledger.Key("prize", eventId, personId));

            if (result.Applied) paid++;
        }

        ledger.FlushReceipts();
        if (paid > 0) log?.Info("prize", $"{title}：{paid} 人各入账 {MoneyUnits.Format(amountUsd)}。");
        return paid;
    }

    /// <summary>
    /// NPC 间转会费。买方出钱、卖方收钱。
    /// <para>
    /// <b>双向原子性</b>：买方余额不足时整笔作废，卖方不会凭空收到钱。
    /// </para>
    /// </summary>
    public static (bool Ok, string? Error) PayTransferFee(EconomyWorld world, string buyerId, string sellerId,
        long feeUsd, string transferId, int day, int season)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (feeUsd <= 0) return (false, "转会费必须为正。");

        var ledger = new EconomyLedger(world);
        string key = ledger.Key("transfer", transferId);

        if (ledger.HasReceipt(key)) return (true, null);

        var spent = ledger.Debit(buyerId, feeUsd, new LedgerEntry
        {
            Day = day,
            Season = season,
            Kind = "转会费",
            Title = "支付转会费",
            CounterpartyId = sellerId,
            Reason = "（" + transferId + "）"
        }, ledger.Key("transfer-out", transferId));

        if (spent.Status == MoneyMoveStatus.InsufficientFunds)
            return (false, $"余额不足，需要 {MoneyUnits.Format(feeUsd)}，当前 {MoneyUnits.Format(spent.BalanceAfterUsd)}。");
        if (spent.Status != MoneyMoveStatus.Applied)
            return (false, spent.Detail ?? "转账失败。");

        var received = ledger.Credit(sellerId, feeUsd, new LedgerEntry
        {
            Day = day,
            Season = season,
            Kind = "转会费",
            Title = "收到转会费",
            CounterpartyId = buyerId,
            Reason = "（" + transferId + "）"
        }, ledger.Key("transfer-in", transferId));

        if (!received.Applied)
        {
            // 卖方入账失败：冲正买方，保证不凭空少钱。
            ledger.Reverse(buyerId, spent.LedgerEntryId, "卖方入账失败，已冲正。", day, season);
            return (false, "卖方入账失败，已冲正买方支出。");
        }

        ledger.MarkReceipt(key);
        ledger.FlushReceipts();
        return (true, null);
    }

    /// <summary>
    /// 转会费。公式与 0.26.7 的 <c>ClubOperations.TryTransfer</c>（<c>Rating × 5</c>，钳制 4000~10000 内部单位）
    /// 对齐：折算成美元就是 <c>Rating × 50</c>，钳制 40000~100000。
    /// </summary>
    public static long TransferFee(EconomyConfig config, int rating)
    {
        ArgumentNullException.ThrowIfNull(config);
        long fee = (long)Math.Round(rating * config.Season.TransferFeeRatingTimes * MoneyUnits.UsdPerInternalUnit);
        long low = config.Season.TransferFeeClamp.Count > 0 ? config.Season.TransferFeeClamp[0] : 0;
        long high = config.Season.TransferFeeClamp.Count > 1 ? config.Season.TransferFeeClamp[1] : long.MaxValue;
        return Math.Clamp(fee, low, high);
    }

    /// <summary>签字费：按赛季工资的一个比例。</summary>
    public static long SigningFee(EconomyConfig config, string tierId)
    {
        ArgumentNullException.ThrowIfNull(config);
        return (long)Math.Round(SalaryFor(config, tierId) * Math.Max(0, config.Season.SigningFeeRatio));
    }
}
