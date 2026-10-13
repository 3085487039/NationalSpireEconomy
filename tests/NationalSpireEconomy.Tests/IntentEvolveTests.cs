using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Inject;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 意向演进：drift 累积、触发规则、意向文案选择。
/// <para>
/// 三条契约：① 增量只由 <see cref="DriftTriggers"/> 一处定义；
/// ② 分数被夹在 ±1，不能无限漂；③ 取到的措辞<b>必须零数字</b>（它会进稳定段）。
/// </para>
/// </summary>
public static class IntentEvolveTests
{
    public static Action[] Cases =>
    [
        ReceivedTriggerMakesGenerous,
        PaidTriggerMakesCautious,
        SoftDeclineMakesStingyAndRecordsDay,
        DriftScoreIsClampedAtBothEnds,
        BandsMapFromScoreCorrectly,
        CharityRefusalEntersResentfulBandWithinWindow,
        ResentfulBandExpiresAfterWindow,
        ResentfulOverridesScoreWhileInWindow,
        EveryBandHasPhraseFromConfig,
        PhraseIsZeroDigits,
        MissingBandFallsBackToSteady,
        TriggerRingIsCappedAtEight,
        LastTouchedDayIsAdvanced,
        UnknownTriggerKindIsRecordedWithZeroDelta,
        ApplyDeltaDoesNotAppendTrigger
    ];

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    private static string? _configDir;

    private static ConfigLoadResult RealConfig()
    {
        if (_configDir == null) _configDir = TestConfig.Directory();
        return new ConfigLoader(_configDir, Silent).Load();
    }

    private static NpcEconomy Npc() => new()
    {
        PersonId = "npc",
        Balance = 100_000,
        TierId = "professional",
        PersonalityKind = "热心同好",
        Season = 3
    };

    // ── 触发规则 ────────────────────────────────────────────────────

    private static void ReceivedTriggerMakesGenerous()
    {
        var npc = Npc();
        TestRunner.Equal(0.0, npc.Lifetime.DriftScore, "起点应为 0（即 steady）");

        // 收到 6 次钱 → 0.05 × 6 = 0.30 → 跨进 generous。
        for (int i = 0; i < 6; i++)
            IntentEvolve.Record(npc, DriftTriggers.TradeReceived, "赞助", day: 10 + i);

        TestRunner.True(npc.Lifetime.DriftScore > 0.29, "连续收到钱应向上漂：" + npc.Lifetime.DriftScore);
        TestRunner.Equal(DriftBands.Generous, IntentEvolve.BandIdFor(npc, day: 20));
    }

    private static void PaidTriggerMakesCautious()
    {
        var npc = Npc();
        // 掏钱 5 次 → -0.02 × 5 = -0.10，落在 cautious 区间 [-0.15, 0.02)。
        // 注意别取太多次：10 次是 -0.20，那就跨进 stingy 了（档位边界由
        // BandsMapFromScoreCorrectly 单独钉住，这里只验"方向对了并且落进 cautious"）。
        for (int i = 0; i < 5; i++)
            IntentEvolve.Record(npc, DriftTriggers.TradePaid, "赞助", day: 10 + i);

        TestRunner.True(npc.Lifetime.DriftScore < 0, "持续掏钱应向下漂：" + npc.Lifetime.DriftScore);
        TestRunner.True(npc.Lifetime.DriftScore >= -0.15, "本次用例的分数应仍在 cautious 区间");
        TestRunner.Equal(DriftBands.Cautious, IntentEvolve.BandIdFor(npc, day: 30));
    }

    private static void SoftDeclineMakesStingyAndRecordsDay()
    {
        var npc = Npc();
        // 四笔软失败各自还叠加了"记恨"，所以分数必然进 stingy 区间。
        for (int i = 0; i < 4; i++)
            IntentEvolve.Record(npc, DriftTriggers.SoftDeclined, "打赏", day: 50);

        TestRunner.Equal(50, npc.Lifetime.LastRefusedCharityDay,
            "软失败必须记下「被小钱试探」的日子，Resentful 档靠它判定");
        TestRunner.True(npc.Lifetime.DriftScore < -0.15, "反复软失败应显著向下漂");

        // 积分本身到 stingy，但记恨叠加优先，所以当天是 resentful。
        TestRunner.Equal(DriftBands.Stingy, IntentEvolve.BandIdForScore(npc.Lifetime.DriftScore),
            "纯分数应落进 stingy");
        TestRunner.Equal(DriftBands.Resentful, IntentEvolve.BandIdFor(npc, day: 50),
            "窗口内记恨优先于纯分数");
    }

    private static void DriftScoreIsClampedAtBothEnds()
    {
        var npc = Npc();
        for (int i = 0; i < 100; i++) IntentEvolve.Record(npc, DriftTriggers.TradeReceived, "赞助", day: 1);
        TestRunner.Equal(IntentEvolve.MaxDrift, npc.Lifetime.DriftScore, "上限应被夹住");

        var other = Npc();
        for (int i = 0; i < 200; i++) IntentEvolve.Record(other, DriftTriggers.TradePaid, "赞助", day: 1);
        TestRunner.Equal(-IntentEvolve.MaxDrift, other.Lifetime.DriftScore, "下限应被夹住");
    }

    private static void BandsMapFromScoreCorrectly()
    {
        TestRunner.Equal(DriftBands.Generous, IntentEvolve.BandIdForScore(0.30), "0.30 是 generous 的下界");
        TestRunner.Equal(DriftBands.Generous, IntentEvolve.BandIdForScore(1.0));
        TestRunner.Equal(DriftBands.Steady, IntentEvolve.BandIdForScore(0.29));
        TestRunner.Equal(DriftBands.Steady, IntentEvolve.BandIdForScore(0.02), "0.02 是 steady 的下界");
        TestRunner.Equal(DriftBands.Cautious, IntentEvolve.BandIdForScore(0.01));
        TestRunner.Equal(DriftBands.Cautious, IntentEvolve.BandIdForScore(-0.15), "-0.15 是 cautious 的下界");
        TestRunner.Equal(DriftBands.Stingy, IntentEvolve.BandIdForScore(-0.16));
        TestRunner.Equal(DriftBands.Stingy, IntentEvolve.BandIdForScore(-1.0));
        // 全部档位都必须可达，否则配置里那句措辞永远用不上。
        var reachable = new[] { 1.0, 0.1, -0.1, -1.0 }.Select(IntentEvolve.BandIdForScore).Distinct().ToArray();
        TestRunner.Equal(4, reachable.Length, "除 resentful 外四个档位都应可达：" + string.Join(",", reachable));
    }

    private static void CharityRefusalEntersResentfulBandWithinWindow()
    {
        var npc = Npc();
        IntentEvolve.Record(npc, DriftTriggers.CharityRefused, "打赏被拒", day: 100);

        TestRunner.Equal(100, npc.Lifetime.LastRefusedCharityDay);
        TestRunner.True(IntentEvolve.IsResentful(npc, day: 100), "当天应在记恨窗口内");
        TestRunner.True(IntentEvolve.IsResentful(npc, day: 107), "第 7 天仍应在窗口内");
    }

    private static void ResentfulBandExpiresAfterWindow()
    {
        var npc = Npc();
        IntentEvolve.Record(npc, DriftTriggers.CharityRefused, "打赏被拒", day: 100);

        TestRunner.False(IntentEvolve.IsResentful(npc, day: 108), "第 8 天应已出窗口");
        TestRunner.NotEqual(DriftBands.Resentful, IntentEvolve.BandIdFor(npc, day: 108),
            "出窗口后应回到纯分数分档");
        // 自定义窗口
        TestRunner.True(IntentEvolve.IsResentful(npc, day: 130, resentfulWindowDays: 30));
    }

    private static void ResentfulOverridesScoreWhileInWindow()
    {
        var npc = Npc();
        // 先把自己漂到很慷慨。
        for (int i = 0; i < 10; i++) IntentEvolve.Record(npc, DriftTriggers.TradeReceived, "赞助", day: 1);
        TestRunner.Equal(DriftBands.Generous, IntentEvolve.BandIdFor(npc, day: 1));

        // 然后被人拿小钱试探了一次：措辞必须立刻转向"介意被小看"。
        IntentEvolve.Record(npc, DriftTriggers.SoftDeclined, "打赏", day: 2);
        TestRunner.Equal(DriftBands.Resentful, IntentEvolve.BandIdFor(npc, day: 2),
            "记恨应压过「我最近很慷慨」这件事");
    }

    // ── 措辞 ────────────────────────────────────────────────────────

    private static void EveryBandHasPhraseFromConfig()
    {
        var prompt = RealConfig().Prompt;
        foreach (string band in DriftBands.All)
        {
            string phrase = IntentEvolve.PhraseFor(prompt, band);
            TestRunner.True(phrase.Length > 0, $"档位 {band} 没有措辞");
            TestRunner.True(prompt.Table("intentPhrase").ContainsKey(band),
                $"配置里应显式提供 {band} 的措辞，而不是靠回退");
        }
    }

    /// <summary>措辞会进稳定段，零数字是硬要求。</summary>
    private static void PhraseIsZeroDigits()
    {
        var prompt = RealConfig().Prompt;
        foreach (string band in DriftBands.All)
        {
            string phrase = IntentEvolve.PhraseFor(prompt, band);
            TestRunner.False(System.Text.RegularExpressions.Regex.IsMatch(phrase, @"\d"),
                $"档位 {band} 的措辞含有数字：{phrase}");
        }
    }

    private static void MissingBandFallsBackToSteady()
    {
        var empty = new PromptConfig(new Dictionary<string, string>());
        string phrase = IntentEvolve.PhraseFor(empty, DriftBands.Generous);

        TestRunner.True(phrase.Length > 0, "表全空时也要给出内置兜底，不能返回空串");
        TestRunner.False(System.Text.RegularExpressions.Regex.IsMatch(phrase, @"\d"), "兜底也必须零数字");
    }

    // ── 环形与水位线 ────────────────────────────────────────────────

    private static void TriggerRingIsCappedAtEight()
    {
        var npc = Npc();
        for (int i = 0; i < 20; i++) IntentEvolve.Record(npc, DriftTriggers.TradeReceived, "第" + i, day: i);

        TestRunner.Equal(NpcEconomy.MaxIntentTriggers, npc.IntentTriggers.Count,
            "触发记录是环形，最多 8 条");
        TestRunner.Equal("第19", npc.IntentTriggers[0].Detail, "最新一条应在最前");
    }

    private static void LastTouchedDayIsAdvanced()
    {
        var npc = Npc();
        npc.LastTouchedDay = 5;
        IntentEvolve.Record(npc, DriftTriggers.TradeReceived, "赞助", day: 40);

        TestRunner.Equal(40, npc.LastTouchedDay, "应推进到最新变动日");

        IntentEvolve.Record(npc, DriftTriggers.TradeReceived, "赞助", day: 3);
        TestRunner.Equal(40, npc.LastTouchedDay, "更早的日子不该把水位线往回拉");
    }

    private static void UnknownTriggerKindIsRecordedWithZeroDelta()
    {
        var npc = Npc();
        IntentEvolve.Record(npc, "某个还没定义的触发", "测试", day: 1);

        TestRunner.Equal(0.0, npc.Lifetime.DriftScore, "未知触发不该改分数");
        TestRunner.Equal(1, npc.IntentTriggers.Count, "但仍应留下记录，便于排查");
        TestRunner.Equal(0.0, npc.IntentTriggers[0].Delta);
    }

    private static void ApplyDeltaDoesNotAppendTrigger()
    {
        var npc = Npc();
        IntentEvolve.ApplyDelta(npc, 0.25);

        TestRunner.Equal(0.25, npc.Lifetime.DriftScore, "应只改分数");
        TestRunner.Equal(0, npc.IntentTriggers.Count, "批量重算时不该塞满触发记录");
    }
}
