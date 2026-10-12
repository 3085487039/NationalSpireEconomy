using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 记账与结算。这一层的核心契约是<b>账实一致</b>与<b>绝不重复</b>：
/// 余额、流水、赛季统计、累计统计、收据必须在同一次调用里一起改完。
/// </summary>
public static class EconomyLedgerTests
{
    public static Action[] Cases =>
    [
        CreditUpdatesBalanceLedgerAndStats,
        DebitUpdatesBalanceLedgerAndStats,
        RepeatedCreditWithSameKeyIsIgnored,
        RepeatedDebitWithSameKeyIsIgnored,
        InsufficientFundsLeavesEverythingUntouched,
        LedgerIsRingCappedAtLimit,
        ReverseRestoresBalanceAndStats,
        ReverseIsItselfIdempotent,
        TransferOutFromPlayerMovesBothSides,
        TransferInToPlayerMovesBothSides,
        PlayerWithoutFundsLeavesNpcUntouched,
        TransferWithSameKeyDoesNotDoubleCharge,
        RolledBackTransferReleasesTheKeySoRetrySucceeds,
        LedgerEntriesAfterRoundTripStayConsistent,
        WageIsCreditedOnSeasonStart,
        SalariesArePaidExactlyOncePerSeason,
        SalaryRespectsPerNpcWatermark,
        PrizeIsPaidOncePerEventPerPerson,
        TransferFeeMatchesSourceFormula,
        SigningFeeIsRatioOfSalary,
        TransferBetweenClubsMovesMoneyBothWays,
        TransferFailsAtomicallyWhenBuyerIsShort,
        AdvanceToSkipsAlreadyTickedDays,
        AdvanceToFillsSkippedSeasons,
        FirstSeasonSalaryIsPaidAtSeasonEnd,
        SeasonRollResetsSeasonStatsButKeepsLifetime,
        ReceiptBookStaysBoundedAcrossSeasons,
        ReceiptPruningProtectsPendingOffers
    ];

    private static DelegateEconomyLog Silent => new((_, _, _, _) => { });

    private static EconomyConfig RealConfig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "config");
            if (File.Exists(Path.Combine(candidate, ConfigLoader.EconomyFileName)))
                return new ConfigLoader(candidate, Silent).Load().Economy;
            directory = directory.Parent;
        }
        throw new Exception("找不到 config/ 目录");
    }

    private static EconomyWorld WorldWith(params (string Id, long Balance, string Tier)[] people)
    {
        var world = new EconomyWorld { WorldId = "ledger-test", LastSeason = 1, LastTickedDay = 1 };
        foreach (var (id, balance, tier) in people)
            world.Npcs[id] = new NpcEconomy { PersonId = id, Balance = balance, TierId = tier, ClubId = "c00" };
        return world;
    }

    private static LedgerEntry Entry(string kind, string title, int day = 10, int season = 1) =>
        new() { Day = day, Season = season, Kind = kind, Title = title };

    // ── 基本记账 ─────────────────────────────────────────────────────

    private static void CreditUpdatesBalanceLedgerAndStats()
    {
        var world = WorldWith(("p0", 1_000, "crowd"));
        var ledger = new EconomyLedger(world);

        var result = ledger.Credit("p0", 5_000, Entry("打赏", "收到打赏"));

        TestRunner.Equal(MoneyMoveStatus.Applied, result.Status);
        TestRunner.Equal(6_000L, world.Npcs["p0"].Balance);
        TestRunner.Equal(6_000L, result.BalanceAfterUsd);
        TestRunner.Equal(1, world.Npcs["p0"].Ledger.Count);
        TestRunner.Equal(5_000L, world.Npcs["p0"].SeasonStats.SeasonIncome);
        TestRunner.Equal(5_000L, world.Npcs["p0"].SeasonStats.GiftIncome);
        TestRunner.Equal(5_000L, world.Npcs["p0"].Lifetime.Income);
        // BalanceAfter 是"这笔之后剩多少"，不是金额本身。
        TestRunner.Equal(6_000L, world.Npcs["p0"].Ledger[0].BalanceAfter);
    }

    private static void DebitUpdatesBalanceLedgerAndStats()
    {
        var world = WorldWith(("p0", 10_000, "crowd"));
        var ledger = new EconomyLedger(world);

        var result = ledger.Debit("p0", 4_000, Entry("赞助", "赞助他人"));

        TestRunner.Equal(MoneyMoveStatus.Applied, result.Status);
        TestRunner.Equal(6_000L, world.Npcs["p0"].Balance);
        TestRunner.Equal(4_000L, world.Npcs["p0"].SeasonStats.SeasonSpend);
        TestRunner.Equal(4_000L, world.Npcs["p0"].Lifetime.Spend);
        TestRunner.Equal(-4_000L, world.Npcs["p0"].Ledger[0].Amount,
            "出账必须记成负数，这样流水求和与余额变动天然一致");
    }

    private static void RepeatedCreditWithSameKeyIsIgnored()
    {
        var world = WorldWith(("p0", 1_000, "crowd"));
        var ledger = new EconomyLedger(world);
        string key = ledger.Key("prize", "match-1", "p0");

        var first = ledger.Credit("p0", 3_000, Entry("赛事奖金", "冠军奖金"), key);
        var second = ledger.Credit("p0", 3_000, Entry("赛事奖金", "冠军奖金"), key);

        TestRunner.Equal(MoneyMoveStatus.Applied, first.Status);
        TestRunner.Equal(MoneyMoveStatus.AlreadyApplied, second.Status);
        TestRunner.Equal(4_000L, world.Npcs["p0"].Balance, "重复入账必须一分不动");
        TestRunner.Equal(1, world.Npcs["p0"].Ledger.Count, "不应产生第二条流水");
        TestRunner.Equal(3_000L, world.Npcs["p0"].SeasonStats.SeasonIncome);
    }

    private static void RepeatedDebitWithSameKeyIsIgnored()
    {
        var world = WorldWith(("p0", 10_000, "crowd"));
        var ledger = new EconomyLedger(world);
        string key = ledger.Key("salary", "p0", "1");

        ledger.Debit("p0", 2_000, Entry("支出", "重复扣款测试"), key);
        var second = ledger.Debit("p0", 2_000, Entry("支出", "重复扣款测试"), key);

        TestRunner.Equal(MoneyMoveStatus.AlreadyApplied, second.Status);
        TestRunner.Equal(8_000L, world.Npcs["p0"].Balance, "重复扣款是灾难性的，必须被挡住");
    }

    private static void InsufficientFundsLeavesEverythingUntouched()
    {
        var world = WorldWith(("p0", 500, "crowd"));
        var ledger = new EconomyLedger(world);

        var result = ledger.Debit("p0", 900, Entry("赞助", "赞助他人"));

        TestRunner.Equal(MoneyMoveStatus.InsufficientFunds, result.Status);
        TestRunner.Equal(500L, world.Npcs["p0"].Balance, "余额不足时绝不能扣成负数");
        TestRunner.Equal(0, world.Npcs["p0"].Ledger.Count);
        TestRunner.Equal(0L, world.Npcs["p0"].SeasonStats.SeasonSpend);
    }

    private static void LedgerIsRingCappedAtLimit()
    {
        var world = WorldWith(("p0", 1_000_000, "elite"));
        var ledger = new EconomyLedger(world);
        for (int i = 0; i < NpcEconomy.MaxLedgerEntries + 20; i++)
            ledger.Credit("p0", 1, Entry("打赏", "第" + i + "笔"));

        TestRunner.Equal(NpcEconomy.MaxLedgerEntries, world.Npcs["p0"].Ledger.Count);
        // 最新的在最前。
        TestRunner.Contains(world.Npcs["p0"].Ledger[0].Title, (NpcEconomy.MaxLedgerEntries + 19).ToString());
    }

    private static void ReverseRestoresBalanceAndStats()
    {
        var world = WorldWith(("p0", 1_000, "crowd"));
        var ledger = new EconomyLedger(world);

        var credited = ledger.Credit("p0", 5_000, Entry("赛事奖金", "误发的奖金"));
        var reversed = ledger.Reverse("p0", credited.LedgerEntryId, "赛事结果更正", day: 12, season: 1);

        var npc = world.Npcs["p0"];
        // 用消息把真实状态打出来（只在断言失败时用于定位，不依赖 stderr 缓冲顺序）。
        string state = $"balance={npc.Balance} income={npc.SeasonStats.SeasonIncome} " +
                       $"spend={npc.SeasonStats.SeasonSpend} bonus={npc.SeasonStats.WinBonusIncome} " +
                       $"lifetimeIn={npc.Lifetime.Income} lifetimeOut={npc.Lifetime.Spend} " +
                       $"entries={npc.Ledger.Count}";
        TestRunner.Equal(MoneyMoveStatus.Applied, reversed.Status);
        TestRunner.Equal(1_000L, npc.Balance, "冲正后余额应回到原值 | " + state);
        TestRunner.Equal(0L, npc.SeasonStats.SeasonIncome, "赛季收入要退回去 | " + state);
        TestRunner.Equal(0L, npc.SeasonStats.SeasonSpend, "冲正收入绝不能记成支出 | " + state);
        TestRunner.Equal(0L, npc.SeasonStats.WinBonusIncome, "赛事奖金科目要退回去 | " + state);
        TestRunner.Equal(0L, npc.Lifetime.Income, "累计收入要退回去 | " + state);
        TestRunner.Equal(0L, npc.Lifetime.Spend, "累计支出不该被冲正影响 | " + state);
        TestRunner.Equal(2, npc.Ledger.Count, "保留原流水与冲正流水两条，便于审计 | " + state);
        TestRunner.Equal("冲正", npc.Ledger[0].Kind);
        TestRunner.Equal("赛事奖金", npc.Ledger[0].ApplicationKind, "冲正流水必须记住撤销的是哪个科目 | " + state);
    }

    private static void ReverseIsItselfIdempotent()
    {
        var world = WorldWith(("p0", 1_000, "crowd"));
        var ledger = new EconomyLedger(world);
        var credited = ledger.Credit("p0", 5_000, Entry("赛事奖金", "误发的奖金"));

        ledger.Reverse("p0", credited.LedgerEntryId, "更正", 12, 1);
        var again = ledger.Reverse("p0", credited.LedgerEntryId, "更正", 12, 1);

        TestRunner.Equal(MoneyMoveStatus.AlreadyApplied, again.Status);
        TestRunner.Equal(1_000L, world.Npcs["p0"].Balance, "重复冲正会白送钱，必须被挡住");
    }

    // ── 玩家转账 ─────────────────────────────────────────────────────

    private static void TransferOutFromPlayerMovesBothSides()
    {
        var world = WorldWith(("p0", 1_000, "crowd"));
        var ledger = new EconomyLedger(world);
        var wallet = new InMemoryPlayerWallet(50_000);

        var result = ledger.Transfer(5_000, "p0", fromPlayer: true, wallet, Entry("赞助", "玩家赞助"));

        TestRunner.True(result.BothApplied, "两侧都应成功");
        TestRunner.Equal(6_000L, world.Npcs["p0"].Balance);
        TestRunner.Equal(45_000L, wallet.BalanceUsd, "玩家侧必须真的扣钱");
        TestRunner.Equal(5_000L, world.Npcs["p0"].SeasonStats.SponsorIncome);
    }

    private static void TransferInToPlayerMovesBothSides()
    {
        var world = WorldWith(("p0", 90_000, "professional"));
        var ledger = new EconomyLedger(world);
        var wallet = new InMemoryPlayerWallet(1_000);

        var result = ledger.Transfer(20_000, "p0", fromPlayer: false, wallet, Entry("奖金", "NPC 发奖金"));

        TestRunner.True(result.BothApplied);
        TestRunner.Equal(70_000L, world.Npcs["p0"].Balance);
        TestRunner.Equal(21_000L, wallet.BalanceUsd);
        TestRunner.Equal(20_000L, world.Npcs["p0"].SeasonStats.SeasonSpend);
    }

    private static void PlayerWithoutFundsLeavesNpcUntouched()
    {
        var world = WorldWith(("p0", 1_000, "crowd"));
        var ledger = new EconomyLedger(world);
        var wallet = new InMemoryPlayerWallet(100);

        var result = ledger.Transfer(5_000, "p0", fromPlayer: true, wallet, Entry("赞助", "玩家赞助"));

        TestRunner.False(result.BothApplied);
        TestRunner.Equal(MoneyMoveStatus.InsufficientFunds, result.PlayerSide.Status);
        TestRunner.Equal(1_000L, world.Npcs["p0"].Balance, "玩家没钱时 NPC 侧一分都不能动");
        TestRunner.Equal(100L, wallet.BalanceUsd);
    }

    private static void TransferWithSameKeyDoesNotDoubleCharge()
    {
        var world = WorldWith(("p0", 1_000, "crowd"));
        var ledger = new EconomyLedger(world);
        var wallet = new InMemoryPlayerWallet(50_000);
        string key = "player_to_npc:p0:once";

        ledger.Transfer(5_000, "p0", true, wallet, Entry("赞助", "玩家赞助"), key);
        var second = ledger.Transfer(5_000, "p0", true, wallet, Entry("赞助", "玩家赞助"), key);

        TestRunner.Equal(MoneyMoveStatus.AlreadyApplied, second.NpcSide.Status);
        TestRunner.Equal(6_000L, world.Npcs["p0"].Balance);
        TestRunner.Equal(45_000L, wallet.BalanceUsd, "玩家的钱也不能被扣两次");
    }

    /// <summary>
    /// ★ 真机实测隐患回归（2026-10-11 22:0x）：NPC 腿已生效、玩家腿失败并回滚之后，
    /// 整笔的幂等键<b>必须被释放</b>——否则同一个键重放会返回"已经做过"，而账上一分未动，
    /// 调用方就会把一次失败误当成"已经结算过"，这笔钱永远到不了玩家手里。
    /// <para>修复后：失败的尝试<b>不留痕</b>，换一个能用的钱包重试同一个键，钱恰好走一次。</para>
    /// </summary>
    private static void RolledBackTransferReleasesTheKeySoRetrySucceeds()
    {
        var world = WorldWith(("p0", 4_300, "crowd"));
        var ledger = new EconomyLedger(world);
        var wallet = new FlakyWallet();
        string key = ledger.Key("trade", "o0099");
        var entry = Entry("赞助", "NPC 赞助玩家");

        // 第一次：玩家侧恒拒 ⇒ NPC 腿必须回滚，且键必须被释放。
        var first = ledger.Transfer(500, "p0", fromPlayer: false, wallet, entry, key);

        TestRunner.False(first.BothApplied, "玩家侧拒绝时不该算成功");
        TestRunner.Equal(MoneyMoveStatus.Rejected, first.PlayerSide.Status);
        TestRunner.Equal(4_300L, world.Npcs["p0"].Balance, "NPC 侧必须原额回滚");
        TestRunner.Equal(0, world.Npcs["p0"].Ledger.Count, "回滚后不该留下流水");
        TestRunner.False(ledger.HasReceipt(key), "★失败的尝试不许留下整笔的幂等键");
        TestRunner.False(world.Receipts.Contains(key), "存档里的收据列表同样不许留下它");

        // 第二次：钱包恢复正常，用同一个键重试——必须真的走通，而且钱只走一次。
        wallet.Accepting = true;
        var retry = ledger.Transfer(500, "p0", fromPlayer: false, wallet, entry, key);

        TestRunner.True(retry.BothApplied, "释放键之后，同一个键必须能重试成功");
        TestRunner.Equal(3_800L, world.Npcs["p0"].Balance, "NPC 侧扣一次");
        TestRunner.Equal(500L, wallet.BalanceUsd, "玩家侧收一次");
        TestRunner.Equal(1, world.Npcs["p0"].Ledger.Count, "只记一笔流水");
        TestRunner.True(ledger.HasReceipt(key), "这次成功后键要留下");

        // 第三次：同一个键再来一次，必须被幂等挡住。
        var third = ledger.Transfer(500, "p0", fromPlayer: false, wallet, entry, key);
        TestRunner.Equal(MoneyMoveStatus.AlreadyApplied, third.NpcSide.Status);
        TestRunner.Equal(3_800L, world.Npcs["p0"].Balance, "幂等挡住后余额不动");
        TestRunner.Equal(500L, wallet.BalanceUsd);
    }

    /// <summary>可控钱包：用来精确制造"玩家侧失败"这一刻。</summary>
    private sealed class FlakyWallet : IPlayerWallet
    {
        public bool Accepting { get; set; }
        public long BalanceUsd { get; private set; }

        public bool TryAdd(long deltaUsd, out string? error)
        {
            if (!Accepting)
            {
                error = "探针：钱包尚未导入，拒绝动钱";
                return false;
            }
            BalanceUsd += deltaUsd;
            error = null;
            return true;
        }
    }

    private static void LedgerEntriesAfterRoundTripStayConsistent()
    {
        // 保存再读回后，流转账仍必须自洽：余额 = 初始 + 流水求和。
        string root = Path.Combine(Path.GetTempPath(), "nse-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new FixedEconomyClock(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
            var store = new EconomyStore(root, clock, Silent);
            var world = WorldWith(("p0", 100_000, "professional"));
            var ledger = new EconomyLedger(world);

            ledger.Credit("p0", 20_000, Entry("赛事奖金", "冠军奖金"));
            ledger.Debit("p0", 30_000, Entry("转会费", "支付转会费"));
            ledger.Credit("p0", 5_000, Entry("赞助", "赞助到账"));
            ledger.FlushReceipts();

            store.SaveWorld(world, force: true);
            var reloaded = new EconomyStore(root, clock, Silent).LoadWorld(world.WorldId).World;
            var npc = reloaded.Npcs["p0"];

            long sum = npc.Ledger.Sum(e => e.Amount);
            TestRunner.Equal(100_000L + sum, npc.Balance, "余额必须等于初始值加流水求和");
            TestRunner.Equal(25_000L, npc.SeasonStats.SeasonIncome);
            TestRunner.Equal(30_000L, npc.SeasonStats.SeasonSpend);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    // ── 时间与赛季 ───────────────────────────────────────────────────

    private static void WageIsCreditedOnSeasonStart()
    {
        var config = RealConfig();
        int seasonDays = config.Season.DaysPerSeason;
        var world = WorldWith(("p0", 1_000, "professional"));
        world.LastTickedDay = seasonDays;      // 第 1 赛季最后一天
        world.LastSeason = 1;

        var result = EconomyTick.AdvanceTo(world, config, seasonDays + 1, Silent);

        TestRunner.True(result.SeasonChanged, "跨过赛季边界应当被识别");
        TestRunner.Equal(2, world.LastSeason);
        TestRunner.Equal(1, result.SalariesPaid);
        long expected = EconomyTick.SalaryFor(config, "professional");
        TestRunner.Equal(1_000L + expected, world.Npcs["p0"].Balance);
        TestRunner.Equal(2, world.Npcs["p0"].LastSalaryPaidSeason);
        // 新赛季统计必须从 0 开始：此时只应包含这一份工资。
        TestRunner.Equal(expected, world.Npcs["p0"].SeasonStats.SeasonIncome, "赛季统计应先清零再记这一份工资");
        TestRunner.Equal(expected, world.Npcs["p0"].SeasonStats.SalaryIncome);
        TestRunner.Equal(0L, world.Npcs["p0"].SeasonStats.SeasonSpend);
    }

    private static void SalariesArePaidExactlyOncePerSeason()
    {
        var config = RealConfig();
        int seasonDays = config.Season.DaysPerSeason;
        var world = WorldWith(("p0", 1_000, "professional"));
        world.LastTickedDay = seasonDays;
        world.LastSeason = 1;

        EconomyTick.AdvanceTo(world, config, seasonDays + 1, Silent);
        long afterFirst = world.Npcs["p0"].Balance;

        // 同一天重复推进：水位线挡住，不再发薪。
        var again = EconomyTick.AdvanceTo(world, config, seasonDays + 1, Silent);
        TestRunner.Equal(TickResult.Skipped, again);
        TestRunner.Equal(afterFirst, world.Npcs["p0"].Balance, "重复推进同一天不能重复发薪");

        // 推进到赛季中段：也不该再发。
        EconomyTick.AdvanceTo(world, config, seasonDays + 30, Silent);
        TestRunner.Equal(afterFirst, world.Npcs["p0"].Balance, "赛季中途不应再发工资");

        // 进入下一个赛季：发一次。
        EconomyTick.AdvanceTo(world, config, seasonDays * 2 + 1, Silent);
        TestRunner.Equal(afterFirst + EconomyTick.SalaryFor(config, "professional"), world.Npcs["p0"].Balance);
    }

    private static void SalaryRespectsPerNpcWatermark()
    {
        var config = RealConfig();
        int seasonDays = config.Season.DaysPerSeason;
        var world = WorldWith(("p0", 1_000, "professional"));
        world.LastTickedDay = seasonDays;
        world.LastSeason = 1;
        // 这个人本赛季已经领过：即使整体水位线被重置，也不该再发。
        world.Npcs["p0"].LastSalaryPaidSeason = 2;

        var result = EconomyTick.AdvanceTo(world, config, seasonDays + 1, Silent);

        TestRunner.Equal(0, result.SalariesPaid);
        TestRunner.Equal(1_000L, world.Npcs["p0"].Balance, "个人水位线是第二道防线");
    }

    private static void PrizeIsPaidOncePerEventPerPerson()
    {
        var world = WorldWith(("p0", 1_000, "professional"), ("p1", 2_000, "youth"));

        int first = EconomyTick.PayPrize(world, ["p0", "p1"], 5_000, "match-42", "联赛冠军奖金", 20, 1);
        int second = EconomyTick.PayPrize(world, ["p0", "p1"], 5_000, "match-42", "联赛冠军奖金", 20, 1);

        TestRunner.Equal(2, first);
        TestRunner.Equal(0, second, "同一场比赛重复结算必须一个人都不发");
        TestRunner.Equal(6_000L, world.Npcs["p0"].Balance);
        TestRunner.Equal(7_000L, world.Npcs["p1"].Balance);

        // 另一场比赛照常发。
        TestRunner.Equal(2, EconomyTick.PayPrize(world, ["p0", "p1"], 5_000, "match-43", "联赛冠军奖金", 21, 1));
    }

    private static void TransferFeeMatchesSourceFormula()
    {
        var config = RealConfig();
        // 0.26.7 的 ClubOperations.TryTransfer 用 Rating × 5 内部单位（钳制 4000~10000），
        // 折算成美元即 Rating × 50，钳制 40000~100000。
        TestRunner.Equal(80_000L, EconomyTick.TransferFee(config, 1600));
        TestRunner.Equal(40_000L, EconomyTick.TransferFee(config, 100), "低评分应被下限夹住");
        TestRunner.Equal(100_000L, EconomyTick.TransferFee(config, 9_000), "高评分应被上限夹住");
    }

    private static void SigningFeeIsRatioOfSalary()
    {
        var config = RealConfig();
        long salary = EconomyTick.SalaryFor(config, "professional");
        TestRunner.Equal((long)Math.Round(salary * config.Season.SigningFeeRatio),
            EconomyTick.SigningFee(config, "professional"));
    }

    private static void TransferBetweenClubsMovesMoneyBothWays()
    {
        var world = WorldWith(("buyer", 200_000, "elite"), ("seller", 5_000, "professional"));

        var (ok, error) = EconomyTick.PayTransferFee(world, "buyer", "seller", 80_000, "transfer-1", 30, 2);

        TestRunner.True(ok, error ?? "");
        TestRunner.Equal(120_000L, world.Npcs["buyer"].Balance);
        TestRunner.Equal(85_000L, world.Npcs["seller"].Balance);
        TestRunner.Equal(80_000L, world.Npcs["seller"].SeasonStats.TransferIncome);
        TestRunner.Equal(80_000L, world.Npcs["buyer"].SeasonStats.TransferSpend);
    }

    private static void TransferFailsAtomicallyWhenBuyerIsShort()
    {
        var world = WorldWith(("buyer", 10_000, "crowd"), ("seller", 5_000, "professional"));

        var (ok, error) = EconomyTick.PayTransferFee(world, "buyer", "seller", 80_000, "transfer-2", 30, 2);

        TestRunner.False(ok);
        TestRunner.True(error != null && error.Contains("余额不足"), error ?? "");
        TestRunner.Equal(10_000L, world.Npcs["buyer"].Balance, "买方不该被扣成负数");
        TestRunner.Equal(5_000L, world.Npcs["seller"].Balance, "买方付不出时卖方绝不能凭空收到钱");
    }

    private static void AdvanceToSkipsAlreadyTickedDays()
    {
        var config = RealConfig();
        var world = WorldWith(("p0", 1_000, "crowd"));
        world.LastTickedDay = 50;

        TestRunner.Equal(TickResult.Skipped, EconomyTick.AdvanceTo(world, config, 50, Silent));
        TestRunner.Equal(TickResult.Skipped, EconomyTick.AdvanceTo(world, config, 30, Silent),
            "回档到更早的日期绝不能重放结算");
        TestRunner.Equal(50, world.LastTickedDay);
    }

    private static void AdvanceToFillsSkippedSeasons()
    {
        var config = RealConfig();
        int seasonDays = config.Season.DaysPerSeason;
        var world = WorldWith(("p0", 1_000, "professional"));
        world.LastTickedDay = 1;
        world.LastSeason = 1;

        // world.LastTickedDay = 1（第 1 赛季第 1 天），目标 = 第 4 赛季第 1 天。
        // 期间的发薪日有四个：第 1 赛季末（赛季末结算），以及第 2、3、4 赛季的开端。
        var result = EconomyTick.AdvanceTo(world, config, seasonDays * 3 + 1, Silent);

        TestRunner.Equal(4, world.LastSeason);
        TestRunner.True(result.SeasonChanged);
        TestRunner.Equal(4, result.SalariesPaid, "跨过第 1 赛季末与第 2/3/4 赛季开端，应恰好发 4 份工资");
        TestRunner.Equal(4, world.Npcs["p0"].LastSalaryPaidSeason);
    }

    /// <summary>
    /// <b>防回归</b>：新世界的第 1 赛季，前 83 天不发薪，第 84 天（赛季末）准时发薪。
    /// <para>
    /// 旧行为是"第 1 天就发薪"，等于 NPC 在播种资金之外白拿一份工资——这条测试就是钉住那次调整，
    /// 别让它被改回去，也别让"赛季初发薪"这条规则悄悄回到第 1 赛季。
    /// </para>
    /// </summary>
    private static void FirstSeasonSalaryIsPaidAtSeasonEnd()
    {
        var config = RealConfig();
        int seasonDays = config.Season.DaysPerSeason;                 // 本配置为 84
        long salary = EconomyTick.SalaryFor(config, "professional");

        TestRunner.Equal(seasonDays, EconomyTick.SalaryDueDay(config, 1), "第 1 赛季的结算日必须是赛季最后一天");
        TestRunner.Equal(seasonDays + 1, EconomyTick.SalaryDueDay(config, 2), "第 2 赛季起回到赛季初结算");

        // 全新世界：整体水位线与个人发薪水位线都还是 0。
        var world = WorldWith(("p0", 1_000, "professional"));
        world.LastTickedDay = 0;
        world.LastSeason = 0;

        // ① 第 1 天到第 83 天：一分钱都不发，余额原封不动。
        var early = EconomyTick.AdvanceTo(world, config, seasonDays - 1, Silent);
        TestRunner.Equal(0, early.SalariesPaid, "第 1 赛季前 83 天不该发薪");
        TestRunner.Equal(1_000L, world.Npcs["p0"].Balance, "结算日之前余额必须原封不动");
        TestRunner.Equal(0, world.Npcs["p0"].LastSalaryPaidSeason, "还没结算，个人水位线应保持 0");
        TestRunner.Equal(seasonDays - 1, world.LastTickedDay);

        // ② 第 84 天（第 1 赛季最后一天）：准时结算一次。
        var settle = EconomyTick.AdvanceTo(world, config, seasonDays, Silent);
        TestRunner.Equal(1, settle.SalariesPaid, "第 84 天必须准时发第 1 赛季工资");
        TestRunner.Equal(1_000L + salary, world.Npcs["p0"].Balance);
        TestRunner.Equal(1, world.Npcs["p0"].LastSalaryPaidSeason);
        TestRunner.Equal(salary, world.Npcs["p0"].SeasonStats.SalaryIncome, "赛季统计要记上这一份工资");

        // ③ 第 85 天（第 2 赛季第 1 天）：按配置的赛季初时机再发一次。
        var next = EconomyTick.AdvanceTo(world, config, seasonDays + 1, Silent);
        TestRunner.Equal(1, next.SalariesPaid, "第 2 赛季起回到赛季初发薪");
        TestRunner.Equal(1_000L + salary * 2, world.Npcs["p0"].Balance);
        TestRunner.Equal(2, world.Npcs["p0"].LastSalaryPaidSeason);
    }

    private static void SeasonRollResetsSeasonStatsButKeepsLifetime()
    {
        var world = WorldWith(("p0", 10_000, "professional"));
        var ledger = new EconomyLedger(world);
        ledger.Credit("p0", 3_000, Entry("赞助", "本赛季赞助"));
        ledger.Debit("p0", 1_000, Entry("支出", "本赛季支出"));

        TestRunner.Equal(3_000L, world.Npcs["p0"].SeasonStats.SeasonIncome);
        TestRunner.Equal(1_000L, world.Npcs["p0"].SeasonStats.SeasonSpend);

        EconomyLedger.RollSeason(world.Npcs["p0"], 2);

        TestRunner.Equal(2, world.Npcs["p0"].Season);
        TestRunner.Equal(0L, world.Npcs["p0"].SeasonStats.SeasonIncome, "赛季统计必须清零");
        TestRunner.Equal(0L, world.Npcs["p0"].SeasonStats.SeasonSpend);
        TestRunner.Equal(3_000L, world.Npcs["p0"].Lifetime.Income, "累计统计必须保留");
        TestRunner.Equal(1_000L, world.Npcs["p0"].Lifetime.Spend);
    }

    private static void ReceiptPruningProtectsPendingOffers()
    {
        var world = WorldWith(("p0", 10_000, "crowd"));
        // 一笔还在等玩家确认的交易。
        world.Offers["offer-1"] = new TradeOffer
        {
            Id = "offer-1",
            Kind = TradeKinds.Sponsor,
            InitiatorId = "player",
            CounterpartyId = "p0",
            Amount = 5_000,
            State = TradeOfferStates.Pending
        };

        var ledger = new EconomyLedger(world);
        ledger.MarkReceipt("offer:ledger-test:offer-1");
        ledger.MarkReceipt("tick:ledger-test:1");
        ledger.FlushReceipts();

        world.Receipts = world.Receipts.Prepend("offer:ledger-test:offer-1").Distinct().ToList();
        var protectedKeys = ledger.ProtectedKeys();

        TestRunner.True(protectedKeys.Contains("offer:ledger-test:offer-1"),
            "Pending 交易的收据键必须出现在保护名单里，否则它可能被裁掉后重复执行");
    }

    /// <summary>
    /// <b>真机事故回归（2026-10-11 20:0x）</b>：真实世界有 <b>1632 人</b>，而发薪是
    /// "每人每季一条收据" ⇒ 5 个赛季就是 8000+ 条，撞上 <c>EconomyStore</c> 的自检
    /// （收据 &gt; <c>MaxReceipts × 2</c> 就拒绝写入），真机后果是
    /// <c>世界 e35d0119 落盘失败（推进到第 369 天）：收据条数 8160 异常膨胀</c>
    /// ——影子存档永远停在播种那天，而内存里已经跑到第 398 天。
    /// <para>
    /// 这条钉住两件事：① 推进到多个赛季末之后，收据本必须被裁到上限内且世界能通过自检；
    /// ② 裁剪<b>不能</b>破坏发薪幂等（水位线兜底，重复推进不再发钱）。
    /// </para>
    /// </summary>
    private static void ReceiptBookStaysBoundedAcrossSeasons()
    {
        var config = RealConfig();
        var world = new EconomyWorld { WorldId = "receipt-bound", LastSeason = 1, LastTickedDay = 1 };

        // 300 人 × 3 个赛季末 ≈ 900 条工资收据 > 上限 512 ⇒ 必须裁剪。
        for (int i = 0; i < 300; i++)
            world.Npcs["p" + i] = new NpcEconomy
            {
                PersonId = "p" + i,
                Balance = 0,
                TierId = "professional",
                ClubId = "c00"
            };

        int seasonDays = Math.Max(1, config.Season.DaysPerSeason);
        int lastDay = EconomyTick.SeasonEndDay(3, seasonDays);

        var tick = EconomyTick.AdvanceTo(world, config, lastDay, Silent);
        TestRunner.True(tick.SalariesPaid > 0, "前置：这轮推进里应至少发过一轮工资");
        TestRunner.True(world.Receipts.Count <= EconomyWorld.MaxReceipts,
            $"收据必须被裁到上限 {EconomyWorld.MaxReceipts} 内，实际 {world.Receipts.Count}");
        TestRunner.Equal(0, EconomyStore.Validate(world).Count,
            "裁剪之后世界必须能通过自检（否则真机上又会落盘失败）");

        // 幂等：已经推进过的日期不能重复发钱。
        long totalBefore = world.Npcs.Values.Sum(n => n.Balance);
        var again = EconomyTick.AdvanceTo(world, config, lastDay, Silent);
        TestRunner.Equal(TickResult.Skipped, again, "已推进过的日期应返回 Skipped");
        TestRunner.Equal(totalBefore, world.Npcs.Values.Sum(n => n.Balance),
            "重复推进不能重复发薪（发薪防重靠每人自己的 LastSalaryPaidSeason 水位线）");

        Console.WriteLine($"      · {world.Npcs.Count} 人推进到第 {lastDay} 天：发薪 {tick.SalariesPaid} 份｜" +
                          $"收据 {world.Receipts.Count} 条（上限 {EconomyWorld.MaxReceipts}）");
    }
}
