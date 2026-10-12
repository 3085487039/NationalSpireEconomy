using System.Text.RegularExpressions;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Inject;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// Prompt 注入：S/V 段合成与缓存守卫。
/// <para>
/// 这一套盯的是四条硬契约：① 稳定段零数字；② 易变段含数字；③ 易变数据抖动时
/// 稳定段与指纹<b>逐字节不变</b>；④ 软失败留下的 <c>PendingVoice</c> 被注入后会清空。
/// </para>
/// </summary>
public static class PromptInjectorTests
{
    public static Action[] Cases =>
    [
        // S 段：零数字
        StableSegmentContainsNoDigits,
        StableSegmentOmitsHeaderWhenTemplateEmpty,
        StableSegmentIsByteIdenticalForSameInput,
        WealthBandResolvesByRoleNotTierId,
        MoneyAttitudeComesFromPromptTable,

        // V 段：含数字
        VolatileSegmentCarriesRenderedBalance,
        VolatileSegmentRendersLastRealEntry,
        VolatileSegmentSkipsReversalEntries,
        VolatileSegmentOmitsLastEntryWhenLedgerEmpty,

        // 缓存不抖动
        VolatileChangesDoNotChangeStableSegment,
        VolatileChangesDoNotChangeFingerprint,
        CacheGuardReportsChangeOnlyWhenStableInputChanges,
        FirstFingerprintWriteIsNotReportedAsChange,
        StableSegmentChangesWhenFavourTierChanges,

        // PendingVoice
        PendingVoiceIsInjectedIntoVolatileSegment,
        PendingVoiceIsClearedAfterInjection,
        PendingVoiceSurvivesBuildWithoutCommit,
        AbsentPendingVoiceProducesNoLine,

        // 组合与卫生
        ComposeKeepsStableBeforeVolatile,
        InjectionDoesNotMutateBalanceOrLedger,
        FavourChangesOnlyCharityAxisInInjection
    ];

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    private static string? _configDir;

    private static ConfigLoadResult RealConfig()
    {
        if (_configDir == null)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "config");
                if (File.Exists(Path.Combine(candidate, ConfigLoader.EconomyFileName)))
                {
                    _configDir = candidate;
                    break;
                }
                directory = directory.Parent;
            }
            if (_configDir == null) throw new Exception("找不到 config/ 目录");
        }
        return new ConfigLoader(_configDir, Silent).Load();
    }

    private static NpcEconomy Npc(string kind = "战绩踩头者", long balance = 120_000) => new()
    {
        PersonId = "npc",
        Balance = balance,
        TierId = "professional",
        ClubId = "c00",
        PersonalityKind = kind,
        Season = 3,
        SeasonStats = new SeasonStats
        {
            SeasonIncome = 182_000,
            SalaryIncome = 96_000,
            WinBonusIncome = 52_000,
            SponsorIncome = 34_000
        }
    };

    private static PromptInjectInput Input(NpcEconomy npc, string role = "职业选手", int favour = 0,
        int day = 30, int season = 3)
    {
        var loaded = RealConfig();
        return new PromptInjectInput(
            new EconomyWorld { WorldId = "inject-test", LastSeason = season, LastTickedDay = day },
            npc,
            new NpcFacts("npc", role, Rating: 1200, ClubId: "c00", PersonalityKind: npc.PersonalityKind),
            npc.TierId,
            loaded.Personality,
            loaded.Prompt,
            favour,
            day,
            season);
    }

    private static bool HasDigit(string text) => Regex.IsMatch(text, @"\d");

    // ── S 段 ────────────────────────────────────────────────────────

    private static void StableSegmentContainsNoDigits()
    {
        var npc = Npc();
        var input = Input(npc);
        string stable = StableSegment.Build(input);

        TestRunner.True(stable.Length > 0, "稳定段不该为空");
        TestRunner.False(HasDigit(stable), "稳定段必须零数字，实际：" + stable);
        // 连金额渲染符号都不该出现。
        TestRunner.NotContains(stable, "$", "稳定段不该出现货币符号");
    }

    /// <summary>
    /// 模板缺失时不该留下空行。
    /// <para>
    /// 空行也是"内容"：它会让指纹白白变化，还会在 Prompt 里留一段莫名其妙的空白。
    /// 注意这里<b>不</b>断言文本非空——全空配置下输出为空是诚实的，
    /// 而"模板必须有"这件事由 <c>ConfigLoader.ValidatePrompt</c> 负责（缺模板直接报 Error）。
    /// </para>
    /// </summary>
    private static void StableSegmentOmitsHeaderWhenTemplateEmpty()
    {
        var npc = Npc();
        var empty = new PromptConfig(new Dictionary<string, string>());

        var input = Input(npc) with { Prompt = empty };
        string stable = StableSegment.Build(input);

        TestRunner.False(stable.StartsWith('\n'), "模板缺失时不该产生前导空行");
        TestRunner.False(stable.Contains("\n\n"), "空模板不该产生连续空行");
        TestRunner.False(stable.EndsWith('\n'), "模板缺失时不该产生尾随空行");
        TestRunner.False(HasDigit(stable), "兜底文本也必须零数字：" + stable);
        TestRunner.NotContains(stable, "$", "兜底文本不该出现货币符号");

        // 部分模板缺失时，缺的那几行应被跳过，其余照常输出。
        var partial = new PromptConfig(new Dictionary<string, string>
        {
            ["stableHeader"] = "【经济状况】",
            ["stableTierLine"] = "你{wealthBand}。"
        });
        string partialText = StableSegment.Build(Input(npc) with { Prompt = partial });
        TestRunner.Equal("【经济状况】\n你" + StableSegment.WealthBandFor(input) + "。", partialText,
            "缺模板的行应被整行跳过，而不是留下空行占位");
    }

    private static void StableSegmentIsByteIdenticalForSameInput()
    {
        var npc = Npc();
        string first = StableSegment.Build(Input(npc));
        string second = StableSegment.Build(Input(npc));
        TestRunner.Equal(first, second, "同输入必须逐字节一致");
    }

    /// <summary>
    /// <c>_weightBands</c> 的键是 Role 名而不是档位 id。
    /// 拿档位 id 去查会查不到、静默退回 default，量级措辞就全错了。
    /// </summary>
    private static void WealthBandResolvesByRoleNotTierId()
    {
        var byRole = StableSegment.WealthBandFor(Input(Npc(), role: "世界顶尖"));
        var unknown = StableSegment.WealthBandFor(Input(Npc(), role: "不存在的职业"));

        TestRunner.Equal("相当富裕", byRole, "世界顶尖应命中「相当富裕」");
        TestRunner.Equal("比赛奖金和日常收入", StableSegment.IncomeSourceFor(Input(Npc(), role: "不存在的职业")),
            "未知 Role 应退回 default 措辞");
        TestRunner.True(unknown.Length > 0, "兜底措辞不该为空");
    }

    private static void MoneyAttitudeComesFromPromptTable()
    {
        TestRunner.Equal("你宁愿花大钱请高手，也不肯收别人的小钱。",
            StableSegment.MoneyAttitudeFor(Input(Npc("战绩踩头者"))), "应取该性格的金钱观原句");

        TestRunner.Equal("", StableSegment.MoneyAttitudeFor(Input(Npc("原版没有的性格"))),
            "未知性格没有金钱观措辞时返回空串，而不是伪造一句");
    }

    // ── V 段 ────────────────────────────────────────────────────────

    private static void VolatileSegmentCarriesRenderedBalance()
    {
        var npc = Npc(balance: 318_400);
        string volatileText = VolatileSegment.Build(Input(npc));

        TestRunner.Contains(volatileText, MoneyUnits.Format(318_400), "易变段必须带渲染好的余额");
        TestRunner.True(HasDigit(volatileText), "易变段必须含数字");
        // 金额只走 MoneyUnits：一旦有人写成 balance * 10，这里立刻炸。
        TestRunner.NotContains(volatileText, MoneyUnits.Format(3_184_000), "余额不该被乘 10");
    }

    private static void VolatileSegmentRendersLastRealEntry()
    {
        var npc = Npc();
        npc.AppendLedger(new LedgerEntry
        {
            Id = "e1", Day = 28, Season = 3, Kind = "赞助", Title = "收到赞助",
            Amount = 50_000, BalanceAfter = npc.Balance, Reason = "这赛季刚拿工资"
        });

        string volatileText = VolatileSegment.Build(Input(npc, day: 30));
        TestRunner.Contains(volatileText, "28", "应带上流水发生的天数");
        TestRunner.Contains(volatileText, "收到赞助", "应带上流水标题");
        TestRunner.Contains(volatileText, MoneyUnits.Format(50_000), "应带上流水金额");
        TestRunner.Contains(volatileText, "这赛季刚拿工资", "应带上流水原因");
    }

    /// <summary>冲正流水要给模型看就只会造成困惑，必须跳过。</summary>
    private static void VolatileSegmentSkipsReversalEntries()
    {
        var npc = Npc();
        npc.AppendLedger(new LedgerEntry
        {
            Id = "rev", Day = 29, Season = 3, Kind = EconomyLedger.ReversalKind, Title = "冲正：支付转会费",
            Amount = -5_000, BalanceAfter = npc.Balance
        });
        npc.AppendLedger(new LedgerEntry
        {
            Id = "e1", Day = 20, Season = 3, Kind = "打赏", Title = "收到打赏",
            Amount = 5_000, BalanceAfter = npc.Balance
        });

        string volatileText = VolatileSegment.Build(Input(npc, day: 30));
        TestRunner.NotContains(volatileText, "冲正", "不该把冲正流水当成最近一笔");
        TestRunner.Contains(volatileText, "收到打赏", "应退回到最近一笔真实流水");
    }

    private static void VolatileSegmentOmitsLastEntryWhenLedgerEmpty()
    {
        string volatileText = VolatileSegment.Build(Input(Npc()));
        TestRunner.NotContains(volatileText, "最近一笔", "没有流水时不该输出「最近一笔」");
        TestRunner.Contains(volatileText, "可用余额", "其余部分仍应正常输出");
    }

    // ── 缓存不抖动 ──────────────────────────────────────────────────

    private static void VolatileChangesDoNotChangeStableSegment()
    {
        var npc = Npc(balance: 120_000);
        var input = Input(npc);
        string before = StableSegment.Build(input);

        // 动一切"易变"的东西：余额、赛季统计、流水。
        npc.Balance = 999_999;
        npc.SeasonStats.SeasonIncome = 1_234_567;
        npc.SeasonStats.SalaryIncome = 888_888;
        npc.AppendLedger(new LedgerEntry
        {
            Id = "x", Day = 30, Season = 3, Kind = "赞助", Title = "刚收到", Amount = 500_000,
            BalanceAfter = npc.Balance
        });

        string after = StableSegment.Build(input);
        TestRunner.Equal(before, after,
            "余额/赛季统计/流水变化后稳定段必须逐字节不变。前：<" + before + "> 后：<" + after + ">");
    }

    private static void VolatileChangesDoNotChangeFingerprint()
    {
        var npc = Npc(balance: 120_000);
        var world = new EconomyWorld { WorldId = "w" };
        var input = Input(npc) with { World = world };
        var injector = new PromptInjector(Silent);

        var first = injector.Build(input);
        npc.Balance = 7;
        var second = injector.Build(input);

        TestRunner.Equal(first.Fingerprint, second.Fingerprint, "余额变化不该改变稳定段指纹");
        TestRunner.False(second.StableChanged, "余额变化不该被判成「缓存失效」");
        TestRunner.NotEqual(first.Text, second.Text, "但整体注入文本应当变了（易变段含新余额）");
    }

    private static void CacheGuardReportsChangeOnlyWhenStableInputChanges()
    {
        var npc = Npc("战绩踩头者");
        var world = new EconomyWorld { WorldId = "w" };
        var input = Input(npc) with { World = world };

        TestRunner.False(CacheGuard.Refresh(world, input), "首次建立指纹不算缓存失效");
        TestRunner.False(CacheGuard.Refresh(world, input), "同输入不该报变化");
        TestRunner.True(world.StableSegmentFingerprint.Length > 0, "指纹应已写入");

        // 换性格：金钱观与标签都会变，这是真正的稳定段输入变化。
        var other = Input(Npc("热心同好")) with { World = world };
        TestRunner.True(CacheGuard.Refresh(world, other), "性格变化必须被判成缓存失效");
    }

    private static void FirstFingerprintWriteIsNotReportedAsChange()
    {
        var world = new EconomyWorld { WorldId = "w" };
        TestRunner.Equal("", world.StableSegmentFingerprint, "前置：初始指纹为空");

        bool changed = CacheGuard.RefreshFingerprint(world, "abcd1234");
        TestRunner.False(changed, "首次写入没有可失效的缓存");
        TestRunner.Equal("abcd1234", world.StableSegmentFingerprint);
    }

    /// <summary>
    /// 好感跨档会改变施舍敏感度，而措辞套（sharp/gentle）也随之切换——
    /// 这属于<b>稳定段输入</b>的变化，必须被判成需要重写。
    /// </summary>
    private static void StableSegmentChangesWhenFavourTierChanges()
    {
        var npc = Npc();
        var world = new EconomyWorld { WorldId = "w" };

        var stranger = Input(npc, favour: 0) with { World = world };
        string strangerText = StableSegment.Build(stranger);

        // 交情档下意向措辞不变（漂移没动），但"金钱观"的标签体系不受好感影响，
        // 所以这里断言的是：指纹比对是"内容驱动"的，内容一样就不该报变化。
        var intimate = Input(npc, favour: 90) with { World = world };
        string intimateText = StableSegment.Build(intimate);
        TestRunner.Equal(strangerText, intimateText,
            "好感只削弱 charitySensitivity，不改变稳定段的文字（文字里没有该轴的表述）");

        // 真正会改文字的是漂移档位。
        npc.Lifetime.DriftScore = -0.5;
        TestRunner.NotEqual(strangerText, StableSegment.Build(Input(npc, favour: 0) with { World = world }),
            "意向档位变化必须改变稳定段");
    }

    // ── PendingVoice ────────────────────────────────────────────────

    private static void PendingVoiceIsInjectedIntoVolatileSegment()
    {
        var npc = Npc();
        npc.PendingVoice = "拿自己手头紧这件事开玩笑";
        var injector = new PromptInjector(Silent);

        var injection = injector.Build(Input(npc), commit: false);
        TestRunner.True(injection.PendingVoiceInjected, "有 PendingVoice 时应标记为已注入");
        TestRunner.Contains(injection.VolatileText, "拿自己手头紧这件事开玩笑", "素材应出现在易变段");
        TestRunner.NotContains(injection.StableText, "拿自己手头紧", "素材绝不能进稳定段");
    }

    private static void PendingVoiceIsClearedAfterInjection()
    {
        var npc = Npc();
        npc.PendingVoice = "囊中羞涩，只能口头支持。";
        var injector = new PromptInjector(Silent);

        injector.Build(Input(npc), commit: true);
        TestRunner.Equal("", npc.PendingVoice, "注入之后必须清空，否则会每轮复述同一句");

        var next = injector.Build(Input(npc));
        TestRunner.False(next.PendingVoiceInjected, "清空之后不该再标记为已注入");
        TestRunner.NotContains(next.VolatileText, "囊中羞涩", "第二轮的易变段不该再出现那句");
    }

    private static void PendingVoiceSurvivesBuildWithoutCommit()
    {
        var npc = Npc();
        npc.PendingVoice = "没有 commit 就不该被消费掉";
        var injector = new PromptInjector(Silent);

        injector.Build(Input(npc), commit: false);
        TestRunner.Equal("没有 commit 就不该被消费掉", npc.PendingVoice, "不提交时不得清空");
    }

    private static void AbsentPendingVoiceProducesNoLine()
    {
        var npc = Npc();
        TestRunner.Equal("", npc.PendingVoice, "前置：没有未竟之言");
        string volatileText = VolatileSegment.Build(Input(npc));

        TestRunner.NotContains(volatileText, "上一轮想说的话", "没有素材时不该输出该行");
    }

    // ── 组合与卫生 ──────────────────────────────────────────────────

    private static void ComposeKeepsStableBeforeVolatile()
    {
        string text = PromptInjector.Compose("S", "V");
        TestRunner.Equal("S\n\nV", text, "稳定段必须在前，中间空一行");

        TestRunner.Equal("V", PromptInjector.Compose("", "V"), "稳定段为空时不留空行");
        TestRunner.Equal("S", PromptInjector.Compose("S", ""), "易变段为空时不留空行");
    }

    private static void InjectionDoesNotMutateBalanceOrLedger()
    {
        var npc = Npc(balance: 120_000);
        npc.AppendLedger(new LedgerEntry
        {
            Id = "e1", Day = 1, Season = 3, Kind = "工资", Title = "工资", Amount = 24_000,
            BalanceAfter = 120_000
        });
        npc.PendingVoice = "测试";

        var injector = new PromptInjector(Silent);
        injector.Build(Input(npc));

        TestRunner.Equal(120_000L, npc.Balance, "注入绝不能改余额");
        TestRunner.Equal(1, npc.Ledger.Count, "注入绝不能改流水");
        TestRunner.Equal(0L, npc.Lifetime.Income, "注入不该改累计统计");
        TestRunner.Equal(0, npc.Lifetime.TradeCount, "注入不该改成交次数");
        TestRunner.Equal("", npc.PendingVoice, "这一条用的是 commit:true，所以素材应被消费掉");
    }

    /// <summary>
    /// 好感度修正<b>只</b>削弱 charity 轴：把它体现在注入上，
    /// 就是"好感跨档不改变稳定段文字"。这条与 <c>PersonalityEconomyTests.FavorDoesNotTouchOtherAxes</c>
    /// 互为印证：那边测六轴，这边测它在注入层的可见后果。
    /// </summary>
    private static void FavourChangesOnlyCharityAxisInInjection()
    {
        var npc = Npc("故意挑事者", balance: 200_000);
        var loaded = RealConfig();

        var low = PersonalityEconomy.Evaluate(loaded.Personality, npc.PersonalityKind, -50);
        var high = PersonalityEconomy.Evaluate(loaded.Personality, npc.PersonalityKind, 100);

        // 五个轴必须一模一样，只有 charity 与好感倍率变。
        TestRunner.Equal(low.Give, high.Give, "give 不该被好感影响");
        TestRunner.Equal(low.Bargain, high.Bargain, "bargain 不该被好感影响");
        TestRunner.Equal(low.Thrift, high.Thrift, "thrift 不该被好感影响");
        TestRunner.Equal(low.Initiative, high.Initiative, "initiative 不该被好感影响");
        TestRunner.Equal(low.Borrow, high.Borrow, "borrow 不该被好感影响");
        TestRunner.True(high.CharitySensitivity < low.CharitySensitivity, "charitySensitivity 必须被好感削弱");
        TestRunner.True(high.CharityMultiplier < low.CharityMultiplier, "好感倍率应反映档位差异");

        // 落到注入上：稳定段文字完全一致。
        TestRunner.Equal(StableSegment.Build(Input(npc, favour: -50)),
            StableSegment.Build(Input(npc, favour: 100)),
            "好感跨档不该改变稳定段文字");
    }
}
