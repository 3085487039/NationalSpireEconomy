using NationalSpire;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Game;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 玩家钱包：把 <c>CareerData.Credits</c> + <c>CareerLifeState.CreditFraction</c>
/// 接成 <see cref="IPlayerWallet"/>。
/// <para>
/// 这一套是铁律三（单位制只允许一个出口）的主要防线：
/// 原版内部单位是 1 单位 = 10 美元，任何手写的 <c>/ 10</c> 或 <c>* 10</c> 都会在这里露馅。
/// </para>
/// </summary>
public static class PlayerWalletTests
{
    public static Action[] Cases =>
    [
        BalanceReadsCreditsTimesTen,
        BalanceIncludesCreditFraction,
        AddUsdUpdatesCreditsAndFraction,
        AddUsdKeepsFractionAcrossCalls,
        NegativeDeltaSpendsAndKeepsFraction,
        OverspendIsRejectedWithoutMovingAnything,
        ExactBalanceSpendSucceeds,
        LargeDeltaThrowsInsteadOfSilentlyTruncating,
        SnapshotExposesRawVanillaFields,
        WalletSatisfiesLedgerTransferContract
    ];

    private static CareerData DataWith(int credits, decimal fraction = 0m) => new()
    {
        Credits = credits,
        Life = new CareerLifeState { CreditFraction = fraction }
    };

    // ── 读余额 ──────────────────────────────────────────────────────

    private static void BalanceReadsCreditsTimesTen()
    {
        // 1 内部单位 = 10 美元。50000 内部单位 = $500,000。
        var wallet = new PlayerWallet(DataWith(50_000));
        TestRunner.Equal(500_000L, wallet.BalanceUsd, "Credits 是内部单位，要按 10 折算成美元");
    }

    private static void BalanceIncludesCreditFraction()
    {
        var wallet = new PlayerWallet(DataWith(1_000, 0.5m));
        TestRunner.Equal(10_005L, wallet.BalanceUsd, "0.5 内部单位 = $5");
    }

    // ── 加钱 ────────────────────────────────────────────────────────

    private static void AddUsdUpdatesCreditsAndFraction()
    {
        var data = DataWith(0);
        var wallet = new PlayerWallet(data);

        TestRunner.True(wallet.TryAdd(50_000, out string? error), "应成功：" + error);
        TestRunner.Equal(5_000, data.Credits, "$50,000 = 5000 内部单位");
        TestRunner.Equal(0m, data.Life.CreditFraction, "整除时不该留分数");
        TestRunner.Equal(50_000L, wallet.BalanceUsd, "余额应同步");
    }

    private static void AddUsdKeepsFractionAcrossCalls()
    {
        var data = DataWith(0);
        var wallet = new PlayerWallet(data);

        wallet.TryAdd(3, out _);                        // $3 = 0.3 内部单位
        TestRunner.Equal(3L, wallet.BalanceUsd, "$3 不足一个内部单位，回读仍是 $3");

        wallet.TryAdd(3, out _);                        // 再 $3 → 0.6 单位
        wallet.TryAdd(3, out _);                        // 再 $3 → 0.9 单位
        TestRunner.Equal(9L, wallet.BalanceUsd, "多次小额必须累加，不能每次都被截断丢掉");

        wallet.TryAdd(3, out _);                        // 累计 $12 = 1.2 单位 → 进位
        TestRunner.Equal(12L, wallet.BalanceUsd, "累计到 1 整单位时应进位到 Credits");
        TestRunner.True(data.Credits >= 1, "进位后 Credits 应至少为 1，实际 " + data.Credits);
    }

    private static void NegativeDeltaSpendsAndKeepsFraction()
    {
        var data = DataWith(5_000);                     // $50,000
        var wallet = new PlayerWallet(data);

        TestRunner.True(wallet.TryAdd(-25_000, out string? error), "应成功：" + error);
        TestRunner.Equal(25_000L, wallet.BalanceUsd, "$50,000 - $25,000 = $25,000");
    }

    // ── 余额不足 ────────────────────────────────────────────────────

    /// <summary>
    /// 超额支出必须被拒绝，且<b>一分钱都不动</b>——
    /// 与 <c>EconomyLedger</c> 的"余额不足在源头拒绝"是同一条纪律。
    /// </summary>
    private static void OverspendIsRejectedWithoutMovingAnything()
    {
        var data = DataWith(100, 0.3m);                 // $1,003
        var wallet = new PlayerWallet(data);
        int creditsBefore = data.Credits;
        decimal fractionBefore = data.Life.CreditFraction;

        TestRunner.False(wallet.TryAdd(-2_000, out string? error), "余额不够时不该成功");
        TestRunner.True(error != null && error.Length > 0, "应给出可读的失败原因");
        TestRunner.Contains(error!, "余额不足", "原因应说明是余额问题");
        TestRunner.Equal(creditsBefore, data.Credits, "被拒时 Credits 不能动");
        TestRunner.Equal(fractionBefore, data.Life.CreditFraction, "被拒时 CreditFraction 不能动");
        TestRunner.Equal(1_003L, wallet.BalanceUsd, "被拒时余额不变");
    }

    private static void ExactBalanceSpendSucceeds()
    {
        var data = DataWith(100, 0.3m);                 // $1,003
        var wallet = new PlayerWallet(data);

        TestRunner.True(wallet.TryAdd(-1_003, out string? error), "恰好花光应成功：" + error);
        TestRunner.Equal(0L, wallet.BalanceUsd, "花光后余额为 0");
    }

    /// <summary>
    /// 余额接近 int 上限时，加钱应该抛 OverflowException 而不是静默截断。
    /// 静默截断等于凭空吞掉玩家的钱。
    /// </summary>
    private static void LargeDeltaThrowsInsteadOfSilentlyTruncating()
    {
        var data = DataWith(int.MaxValue / 2);
        var wallet = new PlayerWallet(data);

        bool threw = false;
        try
        {
            wallet.TryAdd(MoneyUnits.MaxUsdInCredits, out _);
        }
        catch (OverflowException)
        {
            threw = true;
        }

        TestRunner.True(threw, "溢出必须显式抛出，不能静默截断");
    }

    // ── 自检与接线 ──────────────────────────────────────────────────

    private static void SnapshotExposesRawVanillaFields()
    {
        var data = DataWith(1_234, 0.6m);
        var wallet = new PlayerWallet(data);
        var (credits, fraction, balance) = wallet.Snapshot();

        TestRunner.Equal(1_234, credits, "快照要给出原版字段原值");
        TestRunner.Equal(0.6m, fraction);
        TestRunner.Equal(12_346L, balance, "1_234.6 内部单位 = $12,346");
    }

    /// <summary>
    /// 真正要保证的是"它能当 <see cref="IPlayerWallet"/> 用"：
    /// 用真实的 <c>EconomyLedger.Transfer</c> 做一次玩家 ↔ NPC 转账，
    /// 确认两侧余额都按美元正确变动。
    /// </summary>
    private static void WalletSatisfiesLedgerTransferContract()
    {
        var data = DataWith(1_000);                     // $10,000
        IPlayerWallet wallet = new PlayerWallet(data);

        var world = new EconomyWorld { WorldId = "wallet-test", LastSeason = 1 };
        world.Npcs["npc"] = new NpcEconomy { PersonId = "npc", Balance = 0, TierId = "crowd" };

        var ledger = new EconomyLedger(world);
        var result = ledger.Transfer(4_000, "npc", fromPlayer: true, wallet, new LedgerEntry
        {
            Day = 10, Season = 1, Kind = "打赏", Title = "给 NPC 打赏"
        }, ledger.Key("test", "wallet"));

        TestRunner.True(result.BothApplied, "转账两侧都应成功");
        TestRunner.Equal(6_000L, wallet.BalanceUsd, "玩家应扣掉 $4,000（起始 $10,000）");
        TestRunner.Equal(4_000L, world.Npcs["npc"].Balance, "NPC 应收到 $4,000");

        // 反向：NPC 给玩家钱。
        var back = ledger.Transfer(1_500, "npc", fromPlayer: false, wallet, new LedgerEntry
        {
            Day = 11, Season = 1, Kind = "赞助", Title = "NPC 回赠"
        }, ledger.Key("test", "wallet-back"));

        TestRunner.True(back.BothApplied, "反向转账也应成功");
        TestRunner.Equal(7_500L, wallet.BalanceUsd, "玩家应收到 $1,500");
        TestRunner.Equal(2_500L, world.Npcs["npc"].Balance, "NPC 应扣掉 $1,500");
    }
}
