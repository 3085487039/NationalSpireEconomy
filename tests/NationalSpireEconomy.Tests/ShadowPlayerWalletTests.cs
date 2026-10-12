using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 阶段 4 第二步 §7.1：影子玩家钱包。
/// <para>
/// 这一套钉住的是"玩家的钱由我们管辖之后"的四条纪律：
/// 没导入不许动钱、同键不重复生效、余额不足在源头拒绝、余额与流水一起改完。
/// 它跑在<b>零依赖</b>基线里（<c>src/Economy</c> 不引用任何游戏程序集）。
/// </para>
/// </summary>
public static class ShadowPlayerWalletTests
{
    public static Action[] Cases =>
    [
        NewWorldStartsWithUnImportedZeroWallet,
        ImportConvertsInternalUnitsThroughMoneyUnits,
        ImportReadsVanillaCreditsAndFractionOnce,
        SecondImportDoesNotOverwriteBalance,
        ImportRefusesNegativeVanillaBalance,
        ApplyBeforeImportIsRejected,
        CreditMovesBalanceAndWritesAuditableEntry,
        SameReceiptKeyNeverAppliesTwice,
        ReceiptsFlushSoAKeySurvivesIntoTheNextTransaction,
        DebitRefusesToGoNegativeWithoutMovingAnything,
        LedgerRingTrimsToCapMostRecentFirst,
        TransferThroughLedgerCreditsTheShadowWallet,
        TransferEntryMetadataReachesThePlayerLedger,
        ReadOnlySnapshotCannotMoveTheShadowBalance,
        PlayerNodeSurvivesStoreRoundTrip,
        ValidationRejectsNegativePlayerBalance,
        ValidationRejectsBalanceWithoutImport,
        ValidationRejectsOverlongPlayerLedger
    ];

    // 探针已删除（2026-10-11 实测结论见 HANDOFF-PHASE-4-STEP2.md §7.2 前置）：
    // EconomyLedger.Transfer 在"NPC 腿已生效、玩家腿失败并回滚"之后，整笔收据键
    // 仍留在收据本里（ReceiptBook.Mark 已被 Move 调用，RollbackEntry 只标记了 rollback: 键），
    // 于是同一个键重放会返回 AlreadyApplied 而账上一分未动 ⇒ 状态与幂等不一致，§7.2 必须处理。

    /// <summary>真机现场的时间点（2026-10-11 12:23 UTC ≈ 20:23 本机）。</summary>
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    /// <summary>真机现场的那个世界：1638 人里的 p7（余额 $4,300，crowd 档），水位线第 452 天。</summary>
    private static EconomyWorld World(string worldId = "e35d0119")
    {
        var world = new EconomyWorld { WorldId = worldId, LastTickedDay = 452, LastSeason = 6 };
        world.Npcs["p7"] = new NpcEconomy { PersonId = "p7", Balance = 4_300, TierId = "crowd" };
        return world;
    }

    /// <summary>已导入的世界。真机现场的玩家余额是 $196,820（19682 内部单位）。</summary>
    private static EconomyWorld ImportedWorld(long playerUsd = 196_820, string worldId = "e35d0119")
    {
        var world = World(worldId);
        ShadowPlayerWallet.ImportOnce(world, playerUsd / 10, 0m, day: 452, season: 6,
            clock: new FixedEconomyClock(T0), log: Silent);
        return world;
    }

    private static LedgerEntry SponsorEntry() => new()
    {
        Day = 452,
        Season = 6,
        Kind = "赞助",
        Title = "p7 赞助玩家",
        CounterpartyId = "p7",
        OfferId = "o0001",
        Reason = "交房租"
    };

    // ── 导入 ────────────────────────────────────────────────────────

    private static void NewWorldStartsWithUnImportedZeroWallet()
    {
        var wallet = new ShadowPlayerWallet(World());

        TestRunner.False(wallet.Imported, "新世界默认未导入");
        TestRunner.Equal(0L, wallet.BalanceUsd, "未导入时余额是 0，而不是某个猜出来的数");
        TestRunner.Equal(0, wallet.LedgerCount);
    }

    /// <summary>
    /// 导入是把"原版内部单位"折算成美元的现场，因此必须走 <see cref="MoneyUnits"/>，
    /// 不能出现手写的 <c>* 10</c>——铁律三在这一步同样有效。
    /// </summary>
    private static void ImportConvertsInternalUnitsThroughMoneyUnits()
    {
        var world = World();
        ShadowPlayerWallet.ImportOnce(world, 1, 0.5m, day: 452, season: 6,
            clock: new FixedEconomyClock(T0), log: Silent);

        TestRunner.Equal(15L, world.Player.Balance, "1 + 0.5 = 1.5 内部单位 × 10 = $15");
    }

    private static void ImportReadsVanillaCreditsAndFractionOnce()
    {
        var world = World();
        var outcome = ShadowPlayerWallet.ImportOnce(world, 19_682, 0m, day: 452, season: 6,
            clock: new FixedEconomyClock(T0), log: Silent);

        TestRunner.True(outcome.Imported, "首次导入应当真的发生");
        TestRunner.False(outcome.AlreadyImported);
        TestRunner.Equal(196_820L, outcome.BalanceUsd);

        var wallet = new ShadowPlayerWallet(world);
        TestRunner.True(wallet.Imported);
        TestRunner.Equal(196_820L, wallet.BalanceUsd);
        TestRunner.Equal(19_682L, world.Player.LastImportedCredits, "水位线要记下原版 Credits 快照");
        TestRunner.Equal(0m, world.Player.LastImportedFraction);
        TestRunner.Equal(452, world.Player.ImportedDay);
        TestRunner.True(world.Player.ImportedAt.Length > 0, "导入时间戳不能为空（日后对账要用）");

        // 第一笔流水就是"钱从哪来"，于是对账不必猜。
        TestRunner.Equal(1, wallet.LedgerCount);
        var first = world.Player.Ledger[0];
        TestRunner.Equal(ShadowPlayerWallet.ImportKind, first.Kind);
        TestRunner.Equal(196_820L, first.Amount);
        TestRunner.Equal(196_820L, first.BalanceAfter);
        TestRunner.Equal(ShadowPlayerWallet.ImportEntryId(world.WorldId), first.Id, "期初流水 id 必须稳定");
        TestRunner.Contains(first.Reason, "Credits 19682", "原始内部单位要留在说明里");
    }

    private static void SecondImportDoesNotOverwriteBalance()
    {
        var world = ImportedWorld();
        var wallet = new ShadowPlayerWallet(world);
        wallet.Apply(500, "trade:e35d0119:o0001:player");       // 余额变成 $197,320

        var again = ShadowPlayerWallet.ImportOnce(world, 99_999, 0.9m, day: 470, season: 6,
            clock: new FixedEconomyClock(T0), log: Silent);

        TestRunner.False(again.Imported, "二次导入不得再改数字");
        TestRunner.True(again.AlreadyImported);
        TestRunner.Equal(197_320L, wallet.BalanceUsd, "已经变动过的余额绝不能被原版数字覆盖回去");
        TestRunner.Equal(19_682L, world.Player.LastImportedCredits, "水位线保持首次导入的快照");
    }

    /// <summary>
    /// 原版余额算出来是负数时拒绝导入，<b>且不置位水位线</b>：
    /// 让钱包停在"未导入"（一动钱就被拒）比让负数变成玩家余额安全得多。
    /// </summary>
    private static void ImportRefusesNegativeVanillaBalance()
    {
        var world = World();
        var outcome = ShadowPlayerWallet.ImportOnce(world, -5, 0m, day: 452, season: 6,
            clock: new FixedEconomyClock(T0), log: Silent);

        TestRunner.False(outcome.Imported, "负余额不得导入");
        TestRunner.False(world.Player.Imported, "水位线不能被置位，否则钱包会被永久锁死在 0");
        TestRunner.Equal(0L, world.Player.Balance);
        TestRunner.Equal(0, world.Player.Ledger.Count, "失败的导入不该留下期初流水");
    }

    // ── 动钱 ────────────────────────────────────────────────────────

    private static void ApplyBeforeImportIsRejected()
    {
        var world = World();
        var wallet = new ShadowPlayerWallet(world);

        var result = wallet.Apply(500, "trade:e35d0119:o0001:player");

        TestRunner.Equal(MoneyMoveStatus.Rejected, result.Status, "未导入就必须拒绝");
        TestRunner.Contains(result.Detail ?? "", "尚未导入");
        TestRunner.Equal(0L, wallet.BalanceUsd, "被拒时余额不动");
        TestRunner.Equal(0, wallet.LedgerCount, "被拒时不留流水");
        TestRunner.Equal(0, world.Receipts.Count, "被拒时不留收据（否则重放会误判成做过）");
    }

    private static void CreditMovesBalanceAndWritesAuditableEntry()
    {
        var world = ImportedWorld();
        var wallet = new ShadowPlayerWallet(world);

        var result = wallet.Credit(500, SponsorEntry(), "trade:e35d0119:o0001:player");

        TestRunner.Equal(MoneyMoveStatus.Applied, result.Status);
        TestRunner.Equal(197_320L, result.BalanceAfterUsd);
        TestRunner.Equal(197_320L, wallet.BalanceUsd);
        TestRunner.Equal("trade:e35d0119:o0001:player", result.ReceiptKey);

        var top = world.Player.Ledger[0];
        TestRunner.Equal(500L, top.Amount, "入账记正数");
        TestRunner.Equal(197_320L, top.BalanceAfter);
        TestRunner.Equal("赞助", top.Kind);
        TestRunner.Equal("p7", top.CounterpartyId, "对手方要记成 NPC，而不是空");
        TestRunner.Equal("o0001", top.OfferId, "交易单号要能追回去");
        TestRunner.Equal(452, top.Day);
        TestRunner.Equal(EconomyLedger.IdFor("trade:e35d0119:o0001:player"), top.Id, "流水 id 由幂等键派生");
        TestRunner.Equal(452, world.Player.LastTouchedDay);
        TestRunner.Equal(2, wallet.LedgerCount, "期初导入 + 这一笔");
    }

    private static void SameReceiptKeyNeverAppliesTwice()
    {
        var world = ImportedWorld();
        var wallet = new ShadowPlayerWallet(world);
        const string key = "trade:e35d0119:o0001:player";

        wallet.Credit(500, SponsorEntry(), key);
        var second = wallet.Credit(500, SponsorEntry(), key);

        TestRunner.Equal(MoneyMoveStatus.AlreadyApplied, second.Status);
        TestRunner.Equal(197_320L, wallet.BalanceUsd, "第二次绝不能再加一次");
        TestRunner.Equal(2, wallet.LedgerCount, "第二次不得多写一条流水");
        TestRunner.Equal(EconomyLedger.IdFor(key), second.LedgerEntryId, "AlreadyApplied 要能报出原来那条流水");
    }

    /// <summary>
    /// 收据只有刷回存档才会活过重启。这条测试模拟"下一笔结算"：
    /// 新记账器 + 新钱包，仍然必须认得上一笔留下的键。
    /// </summary>
    private static void ReceiptsFlushSoAKeySurvivesIntoTheNextTransaction()
    {
        var world = ImportedWorld();
        var ledger = new EconomyLedger(world);
        var wallet = new ShadowPlayerWallet(world, ledger);
        const string key = "trade:e35d0119:o0002:player";

        wallet.Credit(500, SponsorEntry(), key);
        TestRunner.False(world.Receipts.Contains(key), "调用方还没刷回时，收据只在内存里");

        wallet.FlushReceipts();
        TestRunner.True(world.Receipts.Contains(key), "刷回后收据必须进存档列表");

        var next = new ShadowPlayerWallet(world, new EconomyLedger(world));
        var again = next.Credit(500, SponsorEntry(), key);

        TestRunner.Equal(MoneyMoveStatus.AlreadyApplied, again.Status);
        TestRunner.Equal(197_320L, next.BalanceUsd);
    }

    private static void DebitRefusesToGoNegativeWithoutMovingAnything()
    {
        var world = ImportedWorld(playerUsd: 300);              // $300
        var wallet = new ShadowPlayerWallet(world);

        var denied = wallet.Debit(500, SponsorEntry(), "k1");

        TestRunner.Equal(MoneyMoveStatus.InsufficientFunds, denied.Status);
        TestRunner.Contains(denied.Detail ?? "", "不足以支出");
        TestRunner.Equal(300L, wallet.BalanceUsd, "被拒时一分不动");
        TestRunner.Equal(1, wallet.LedgerCount, "被拒时不留流水");
        TestRunner.False(world.Receipts.Contains("k1"), "失败的尝试不该留下收据");

        var exact = wallet.Debit(300, SponsorEntry(), "k2");

        TestRunner.Equal(MoneyMoveStatus.Applied, exact.Status);
        TestRunner.Equal(0L, wallet.BalanceUsd, "恰好花光应成功");
    }

    private static void LedgerRingTrimsToCapMostRecentFirst()
    {
        var world = ImportedWorld();
        var wallet = new ShadowPlayerWallet(world);
        int extra = 6;

        for (int i = 0; i < PlayerEconomy.MaxLedgerEntries + extra; i++)
            wallet.Credit(1, new LedgerEntry { Day = 452 + i, Kind = "打赏", Title = "t" + i }, "k" + i);

        TestRunner.Equal(PlayerEconomy.MaxLedgerEntries, wallet.LedgerCount, "流水必须裁到上限");
        TestRunner.Equal("t" + (PlayerEconomy.MaxLedgerEntries + extra - 1), world.Player.Ledger[0].Title,
            "最新的必须在最前");
        TestRunner.Equal(196_820L + PlayerEconomy.MaxLedgerEntries + extra, wallet.BalanceUsd,
            "裁剪只裁流水，余额一分不少");
    }

    // ── 与现有动账链路合流 ──────────────────────────────────────────

    /// <summary>
    /// 最要紧的一条：把影子钱包塞进<b>现有的</b> <see cref="EconomyLedger.Transfer"/>，
    /// NPC→玩家的钱要真的到账，且"再点一次确认"不得再动钱。
    /// </summary>
    private static void TransferThroughLedgerCreditsTheShadowWallet()
    {
        var world = ImportedWorld();
        var ledger = new EconomyLedger(world);
        var wallet = new ShadowPlayerWallet(world, ledger);
        string key = ledger.Key("trade", "o0001");

        var first = ledger.Transfer(500, "p7", fromPlayer: false, wallet, SponsorEntry(), key);

        TestRunner.True(first.BothApplied, "两侧都应成功");
        TestRunner.Equal(197_320L, wallet.BalanceUsd, "玩家侧应收到 $500");
        TestRunner.Equal(3_800L, world.Npcs["p7"].Balance, "NPC 侧应付出 $500");
        wallet.FlushReceipts();

        // 玩家再点一次「确认」：同一个键，一分钱都不许再动。
        var second = ledger.Transfer(500, "p7", fromPlayer: false, wallet, SponsorEntry(), key);

        TestRunner.Equal(MoneyMoveStatus.AlreadyApplied, second.NpcSide.Status);
        TestRunner.Equal(197_320L, wallet.BalanceUsd, "重复结算不得再加钱");
        TestRunner.Equal(3_800L, world.Npcs["p7"].Balance, "重复结算不得再扣钱");

        // 直接对钱包重放同一个键，也必须被挡（第二道闸）。
        var replay = wallet.Apply(500, key);

        TestRunner.Equal(MoneyMoveStatus.AlreadyApplied, replay.Status);
        TestRunner.Equal(197_320L, wallet.BalanceUsd);
    }

    /// <summary>
    /// 玩家自己的流水也要能读出"这笔钱是什么业务"。
    /// 走 <see cref="IPlayerWallet"/> 的带明细重载，天/赛季/类别/对手方/交易单号都要到位。
    /// </summary>
    private static void TransferEntryMetadataReachesThePlayerLedger()
    {
        var world = ImportedWorld();
        var ledger = new EconomyLedger(world);
        var wallet = new ShadowPlayerWallet(world, ledger);

        ledger.Transfer(500, "p7", fromPlayer: false, wallet, SponsorEntry(), ledger.Key("trade", "o0001"));

        var top = world.Player.Ledger[0];
        TestRunner.Equal(452, top.Day);
        TestRunner.Equal(6, top.Season);
        TestRunner.Equal("赞助", top.Kind);
        TestRunner.Equal("p7 赞助玩家", top.Title);
        TestRunner.Equal("p7", top.CounterpartyId);
        TestRunner.Equal("o0001", top.OfferId);
        TestRunner.Equal("交房租", top.Reason);
    }

    private static void ReadOnlySnapshotCannotMoveTheShadowBalance()
    {
        var world = ImportedWorld();
        var wallet = new ShadowPlayerWallet(world);

        var snapshot = wallet.ReadOnlySnapshot();
        TestRunner.Equal(196_820L, snapshot.BalanceUsd, "快照要给出当前余额");

        snapshot.TryAdd(1_000_000, out _);

        TestRunner.Equal(196_820L, wallet.BalanceUsd, "改动快照绝不能让影子余额跟着变");
        TestRunner.Equal(1, wallet.LedgerCount, "快照也不该能写流水");
    }

    // ── 落盘与自检 ──────────────────────────────────────────────────

    private static void PlayerNodeSurvivesStoreRoundTrip()
    {
        string root = Path.Combine(Path.GetTempPath(), "nse-wallet-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new FixedEconomyClock(T0);
            var store = new EconomyStore(root, clock, Silent);
            var world = ImportedWorld();
            var wallet = new ShadowPlayerWallet(world);
            const string key = "trade:e35d0119:o0001:player";
            wallet.Credit(500, SponsorEntry(), key);

            TestRunner.True(store.SaveWorld(world, force: true), "带影子钱包的世界必须写得进去");

            var reloaded = new EconomyStore(root, clock, Silent).LoadWorld(world.WorldId).World;

            TestRunner.True(reloaded.Player.Imported, "导入水位线要能读回来（否则重启后钱包永远拒绝动钱）");
            TestRunner.Equal(197_320L, reloaded.Player.Balance, "余额要能读回来");
            TestRunner.Equal(19_682L, reloaded.Player.LastImportedCredits);
            TestRunner.Equal(452, reloaded.Player.ImportedDay);
            TestRunner.Equal(2, reloaded.Player.Ledger.Count);
            TestRunner.Equal(500L, reloaded.Player.Ledger[0].Amount);
            TestRunner.Equal("p7", reloaded.Player.Ledger[0].CounterpartyId);
            TestRunner.True(reloaded.Receipts.Contains(key),
                "收据要一起落盘，否则重进游戏就能重复发一次钱");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { /* 清理失败不应让测试失败 */ }
        }
    }

    private static void ValidationRejectsNegativePlayerBalance()
    {
        var world = World();
        world.Player.Imported = true;
        world.Player.Balance = -1;

        var problems = EconomyStore.Validate(world);

        TestRunner.True(problems.Any(p => p.Contains("玩家影子余额为负")),
            "自检要拦住负余额：" + string.Join("；", problems));
    }

    private static void ValidationRejectsBalanceWithoutImport()
    {
        var world = World();
        world.Player.Balance = 5_000;                          // 未导入却有余额 = 凭空造钱

        var problems = EconomyStore.Validate(world);

        TestRunner.True(problems.Any(p => p.Contains("导入水位线")),
            "自检要拦住'没导入却有余额'：" + string.Join("；", problems));
    }

    private static void ValidationRejectsOverlongPlayerLedger()
    {
        var world = ImportedWorld();
        for (int i = 0; i <= PlayerEconomy.MaxLedgerEntries; i++)
            world.Player.Ledger.Add(new LedgerEntry { Id = "e" + i, Kind = "打赏", Amount = 1 });

        var problems = EconomyStore.Validate(world);

        TestRunner.True(problems.Any(p => p.Contains("玩家流水条数")),
            "自检要拦住超上限的玩家流水：" + string.Join("；", problems));
    }
}
