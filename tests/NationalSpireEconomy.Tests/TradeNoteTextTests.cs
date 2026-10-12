using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Inject;
using NationalSpireEconomy.Trade;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// <b>阶段 4 第三步：账本真相的措辞与落点</b>（零依赖那一套）。
/// <para>
/// 它钉三件事：
/// <list type="number">
/// <item><b>措辞里必须有"钱有没有动"</b>——真机的病根就是玩家与模型都不知道这笔到底成没成；</item>
/// <item><b>结算的每种出口都要在影子存档上留下结论</b>（<see cref="EconomyWorld.LastTrade"/>），
/// 包括最容易被忽略的"开关没开 / 方向未接线"；</item>
/// <item><b>这些文本只能进 V 段</b>（它们一定带金额，S 段零数字是铁律）。</item>
/// </list>
/// </para>
/// </summary>
public static class TradeNoteTextTests
{
    public static Action[] Cases =>
    [
        LedgerRuleSaysOnlySettledCounts,
        ReceiptLineCarriesBothBalancesAndAmount,
        PlayerToNpcReceiptSaysYouPaidNotNpcPaid,
        AlreadyAppliedReceiptSaysNoMoneyMovedAgain,
        NotSettledLineNamesReasonAndAmount,
        IncompleteNoteSaysNoOfferWasMade,
        DeclinedNoteIsDistinctFromRefused,
        ReceiptDetailListsBalancesAndLedgerId,
        FailedDetailSaysNoMoneyMoved,
        RecentSettlementsReadsAuditNewestFirst,
        RecentSettlementsIsNullWhenEmpty,
        LedgerSummarySeparatesRealTransfersFromTalk,
        LedgerSummarySaysNotMovedForRefusals,
        LedgerSummaryIsNullWithoutRecord,
        SettlementSuccessWritesSettledNote,
        SecondSettlementWritesAlreadySettledNote,
        NotWiredSettlementStillWritesNote,
        AlreadySettledNoteSurvivesReload,
        PlayerToNpcNoteCarriesDirectionAndNpc,
        NoteTextLandsInVolatileSegmentNotStable,
        TruthChannelChangesVolatileOnlyNotStable,
        OldSaveWithoutLastTradeStillRenders,
        ReloadedReceiptBecomesTheVolatileLine
    ];

    private const string NpcId = "crowd-5-4";

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 15, 0, 0, TimeSpan.Zero);

    private static SettlementOutcome Applied() => new(
        SettlementStatus.Applied, "o0002", TradeKinds.Sponsor, TradeDirections.NpcToPlayer,
        AmountUsd: 500, NpcId: NpcId,
        NpcBalanceBefore: 4_500, NpcBalanceAfter: 4_000,
        PlayerBalanceBefore: 196_820, PlayerBalanceAfter: 197_320,
        ReceiptKey: "trade:world:o0002", LedgerEntryId: "3e23733789abcdef", Detail: "已结算（弹窗）");

    // ── 措辞 ────────────────────────────────────────────────────────

    /// <summary>
    /// <b>本阶段最重要的一句话</b>：只有写着"已结算"的交易才算钱真的动过。
    /// 真机三轮里模型都宣布"钱转过去了"而账上一分没动，这句就是给它（和给玩家）的规则。
    /// </summary>
    private static void LedgerRuleSaysOnlySettledCounts()
    {
        string rule = TradeNoteText.LedgerRuleLine;
        TestRunner.Contains(rule, "已结算", "口径行必须点名「已结算」这个状态");
        TestRunner.Contains(rule, "不会让钱动", "口径行必须说清「嘴上说转了」不会动钱");
        TestRunner.False(System.Text.RegularExpressions.Regex.IsMatch(rule, @"\d"),
            "口径行是常量、不该含数字：<" + rule + ">");
    }

    private static void ReceiptLineCarriesBothBalancesAndAmount()
    {
        string line = TradeNoteText.ReceiptLine(Applied(), NpcId);

        TestRunner.Contains(line, "已结算", "成功回执必须一眼看出「成了」");
        TestRunner.Contains(line, "$500", "要有金额");
        TestRunner.Contains(line, "$196,820", "要有玩家余额的「之前」");
        TestRunner.Contains(line, "$197,320", "要有玩家余额的「之后」");
        TestRunner.Contains(line, "$4,000", "要有 NPC 结算后的余额");
        TestRunner.Contains(line, "o0002", "要有单号");
        TestRunner.Contains(line, "3e237337", "要有流水号（截断到 8 位即可）");
        TestRunner.False(line.Contains('\n'), "V 段回执必须是单行（一行一条真相）");
    }

    /// <summary>
    /// <b>★ 阶段 4 第五步 A 项：`player_to_npc` 的回执必须说"你付钱"，不能说反。</b>
    /// <para>
    /// 原先 <see cref="TradeNoteText.ReceiptLine"/> 把「（{who} → 你）」写死了，
    /// 那在"只开通 NPC→玩家"的首笔范围里是对的；放开 <c>player_to_npc</c> 之后，
    /// 玩家出钱的交易会被渲染成"NPC 给你钱"——<b>一句直接骗玩家的假话</b>。
    /// </para>
    /// </summary>
    private static void PlayerToNpcReceiptSaysYouPaidNotNpcPaid()
    {
        var paid = new SettlementOutcome(
            SettlementStatus.Applied, "o0002", TradeKinds.Hire, TradeDirections.PlayerToNpc,
            AmountUsd: 10_000, NpcId: NpcId,
            NpcBalanceBefore: 90_000, NpcBalanceAfter: 100_000,
            PlayerBalanceBefore: 196_820, PlayerBalanceAfter: 186_820,
            ReceiptKey: "trade:world:o0002", LedgerEntryId: "3e23733789abcdef", Detail: "已结算（弹窗）");

        string line = TradeNoteText.ReceiptLine(paid, NpcId);
        TestRunner.Contains(line, "你 → " + NpcId, "★必须说「你 → NPC」");
        TestRunner.NotContains(line, NpcId + " → 你", "★绝不能说成「NPC → 你」");
        TestRunner.Contains(line, "减少", "玩家侧要标出「减少」——它是「钱少了」的唯一线索");
        TestRunner.Contains(line, "增加", "NPC 侧要标出「增加」");
        TestRunner.Contains(line, "$196,820 → $186,820", "余额前后都要在，且方向是往下走");
        TestRunner.False(line.Contains('\n'), "回执仍然必须是单行");

        // 反方向的旧行为不能被这次改动改坏（这是既有真机验收的那一条）。
        string incoming = TradeNoteText.ReceiptLine(Applied(), NpcId);
        TestRunner.Contains(incoming, NpcId + " → 你", "NPC 出钱的回执仍是「NPC → 你」");
        TestRunner.NotContains(incoming, "你 → " + NpcId, "NPC 出钱不能被说成玩家出钱");

        // 账本口径摘要同样要按方向说。
        var note = new LastTradeNote
        {
            OfferId = "o0002", Kind = TradeKinds.Hire, AmountUsd = 10_000, NpcId = NpcId,
            Direction = TradeDirections.PlayerToNpc, Status = TradeNoteStatuses.Settled
        };
        TestRunner.Contains(TradeNoteText.LedgerSummary(note)!, "你已给过 " + NpcId,
            "玩家出钱的账本口径必须说「你已给过」");
        TestRunner.Contains(TradeNoteText.LedgerSummary(note)!, "钱确实动过");

        // 未成立那几句也不能说反（方向闸挡下 `player_to_npc` 的时代已经过去，
        // 但"没落单/被拒"这些出口仍然会出现在玩家出钱的交易上）。
        var refused = new LastTradeNote
        {
            OfferId = "o0009", Kind = TradeKinds.Hire, AmountUsd = 10_000, NpcId = NpcId,
            Direction = TradeDirections.PlayerToNpc, Status = TradeNoteStatuses.Refused, Reason = "PERSONALITY_CAP"
        };
        string notSettled = TradeNoteText.NotSettledLine(refused, NpcId);
        TestRunner.Contains(notSettled, "你 → " + NpcId, "未成立那句也要说对方向");
        TestRunner.Contains(notSettled, "钱一分没动");
    }

    private static void AlreadyAppliedReceiptSaysNoMoneyMovedAgain()
    {
        var applied = Applied();
        var again = applied with
        {
            Status = SettlementStatus.AlreadyApplied,
            NpcBalanceAfter = applied.NpcBalanceBefore,
            PlayerBalanceAfter = applied.PlayerBalanceBefore
        };

        string line = TradeNoteText.ReceiptLine(again, NpcId);

        TestRunner.Contains(line, "已结算", "幂等命中仍然是「已结算」这一档（钱确实在某一次动过）");
        TestRunner.Contains(line, "没有再动钱", "但必须说清这一次没有再动钱");
        TestRunner.Contains(line, "$196,820", "余额应报当前值（与结算前一致）");
    }

    private static void NotSettledLineNamesReasonAndAmount()
    {
        var note = new LastTradeNote
        {
            Day = 452, OfferId = "o0003", Kind = TradeKinds.Sponsor,
            Direction = TradeDirections.NpcToPlayer, AmountUsd = 710,
            NpcId = NpcId, Status = TradeNoteStatuses.Refused,
            Reason = "PERSONALITY_LOW_EMOTION：这点钱像是在打发我。"
        };

        string line = TradeNoteText.NotSettledLine(note, NpcId);

        TestRunner.Contains(line, "未成立", "被拒时必须有「未成立」这个可检索的标记");
        TestRunner.Contains(line, "PERSONALITY_LOW_EMOTION", "必须带拒绝码（模型据此换金额，玩家据此知道为什么）");
        TestRunner.Contains(line, "$710", "必须带金额（「该要多少钱」是可操作信息）");
        TestRunner.Contains(line, "钱一分没动", "★必须明说钱没动——这就是真机那句谎话的对症药");
        TestRunner.Contains(line, "o0003", "要能对上单号");
        TestRunner.False(line.Contains('\n'), "未成立注记也必须是单行");
    }

    private static void IncompleteNoteSaysNoOfferWasMade()
    {
        var note = new LastTradeNote
        {
            Kind = TradeKinds.Sponsor, AmountUsd = 300,
            Status = TradeNoteStatuses.Incomplete, Reason = "方向说不清（标记没写 kind 也没配置）"
        };

        string line = TradeNoteText.NotSettledLine(note, "");

        TestRunner.Contains(line, "未成立");
        TestRunner.Contains(line, "没说清", "补全失败要说清失败在哪一步");
        TestRunner.Contains(line, "钱一分没动");
        TestRunner.Contains(line, "对方", "拿不到对手方时也要有可读的称呼，不能留空");
    }

    private static void DeclinedNoteIsDistinctFromRefused()
    {
        var declined = new LastTradeNote
        {
            OfferId = "o0004", Kind = TradeKinds.Sponsor, AmountUsd = 500,
            NpcId = NpcId, Status = TradeNoteStatuses.Declined, Reason = "玩家拒绝"
        };
        var refused = new LastTradeNote
        {
            OfferId = "o0004", Kind = TradeKinds.Sponsor, AmountUsd = 500,
            NpcId = NpcId, Status = TradeNoteStatuses.Refused, Reason = "PERSONALITY_CAP"
        };

        string a = TradeNoteText.NotSettledLine(declined, NpcId);
        string b = TradeNoteText.NotSettledLine(refused, NpcId);

        TestRunner.Contains(a, "被你拒绝", "玩家自己拒的与被系统拒的必须分开说");
        TestRunner.NotContains(b, "被你拒绝", "被系统拒的不能说成玩家拒的");
        TestRunner.Contains(b, "PERSONALITY_CAP", "被系统拒的要说清拒绝码");
    }

    private static void ReceiptDetailListsBalancesAndLedgerId()
    {
        var outcome = Applied();
        string detail = TradeNoteText.ReceiptDetail(outcome, NpcId);

        TestRunner.Contains(detail, TradeNoteText.ReceiptLine(outcome, NpcId),
            "正文首行必须与 V 段那行同源（否则界面与日志会分叉）");
        TestRunner.Contains(detail, "双方余额", "弹窗里要同时给出两侧余额");
        TestRunner.Contains(detail, "$4,500 → $4,000", "NPC 侧要给出前后变化");
        TestRunner.Contains(detail, "流水号：3e237337", "要给流水号");
        TestRunner.Contains(detail, "幂等键", "要说清重复结算不会再动钱");
        TestRunner.Contains(detail, "audit/", "要告诉玩家这笔记进审计、重启后仍在");
    }

    private static void FailedDetailSaysNoMoneyMoved()
    {
        var failed = new SettlementOutcome(
            SettlementStatus.DirectionNotWired, "o0005", TradeKinds.Hire, TradeDirections.PlayerToNpc,
            10_000, "pro-1", 0, 0, 0, 0, "", "", "首笔范围只开通「NPC → 玩家」；钱不会动。");

        string detail = TradeNoteText.ReceiptDetail(failed, "pro-1");
        string title = GameResultTitleProbe(failed.Status);

        TestRunner.Contains(detail, "钱一分都没有动", "★失败的当场回执必须明说钱没动");
        TestRunner.Contains(detail, "首笔范围", "要说清原因");
        TestRunner.Contains(title, "钱没动", "标题也要一眼看出钱没动");
    }

    /// <summary>
    /// 结果弹窗的标题在游戏层（零依赖构建里不含 src/Game），
    /// 这里用同一套映射核对"钱没动"这件事不会在标题上丢。
    /// </summary>
    private static string GameResultTitleProbe(SettlementStatus status) => status switch
    {
        SettlementStatus.Applied => "✅ 交易已结算（钱已到账）",
        SettlementStatus.AlreadyApplied => "✅ 这笔之前已经结算过（本次未再动钱）",
        SettlementStatus.NotWired => "⏸ 结算尚未接线（钱没动）",
        SettlementStatus.NotReady => "⏳ 还不能结算（钱没动）",
        SettlementStatus.DirectionNotWired => "⛔ 这个方向还没接线（钱没动）",
        _ => "❌ 结算失败（钱没动）"
    };

    // ── 审计回读（§7.4）─────────────────────────────────────────────

    private static void RecentSettlementsReadsAuditNewestFirst()
    {
        var records = new List<AuditRecord>
        {
            new() { Kind = AuditKinds.TradeSettled, Day = 450, PersonId = "npc-a", AmountUsd = 300,
                    OfferId = "o0001", Direction = TradeDirections.NpcToPlayer,
                    BalanceAfterUsd = 4_700, PlayerBalanceAfterUsd = 196_820 },
            new() { Kind = AuditKinds.LedgerEntry, Day = 450, PersonId = "npc-a" },
            new() { Kind = AuditKinds.TradeSettled, Day = 451, PersonId = "npc-b", AmountUsd = 400,
                    OfferId = "o0002", Direction = TradeDirections.NpcToPlayer,
                    BalanceAfterUsd = 4_600, PlayerBalanceAfterUsd = 197_220 },
            new() { Kind = AuditKinds.TradeSettled, Day = 452, PersonId = NpcId, AmountUsd = 500,
                    OfferId = "o0003", Direction = TradeDirections.NpcToPlayer,
                    BalanceAfterUsd = 4_000, PlayerBalanceAfterUsd = 197_320 }
        };

        string? text = TradeNoteText.RecentSettlements(records, take: 2);

        TestRunner.True(text != null, "有结算记录时必须给出回读结果");
        TestRunner.Contains(text!, "共 3 笔", "统计只算 trade_settled，别的审计行不能混进来");
        TestRunner.Contains(text!, "o0003", "最近的排最前");
        TestRunner.Contains(text!, "o0002");
        TestRunner.NotContains(text!, "o0001", "take 之外的更早记录不该出现");
        TestRunner.Contains(text!, "$197,320", "要带上玩家影子余额（对账靠它）");
    }

    private static void RecentSettlementsIsNullWhenEmpty()
    {
        TestRunner.True(TradeNoteText.RecentSettlements([]) == null, "没有任何审计行时不该多出一个空标题");
        TestRunner.True(TradeNoteText.RecentSettlements(null) == null, "读不到审计时也不该报错");
        TestRunner.True(TradeNoteText.RecentSettlements(
            [new AuditRecord { Kind = AuditKinds.LedgerEntry }]) == null,
            "只有非结算行时同样不该输出");
    }

    // ── 账本口径摘要（哨兵行用）─────────────────────────────────────

    /// <summary>
    /// "嘴上说转了"那条哨兵必须能一眼分开两种情况：
    /// ① 钱真的动过（模型在复述）；② 钱没动（模型在编）。
    /// </summary>
    private static void LedgerSummarySeparatesRealTransfersFromTalk()
    {
        var settled = new LastTradeNote
        {
            OfferId = "o0002", Kind = TradeKinds.Sponsor, AmountUsd = 500,
            NpcId = NpcId, Status = TradeNoteStatuses.Settled, Reason = "已结算（弹窗）"
        };
        string a = TradeNoteText.LedgerSummary(settled)!;
        TestRunner.Contains(a, "已给过你 $500", "真转过就要明说钱动过");
        TestRunner.Contains(a, "钱确实动过", "★与「未成立」必须用不同的话");
        TestRunner.Contains(a, "o0002", "要能对上单号");

        var again = new LastTradeNote
        {
            OfferId = "o0002", AmountUsd = 500, NpcId = NpcId,
            Status = TradeNoteStatuses.AlreadySettled, Reason = "幂等"
        };
        string b = TradeNoteText.LedgerSummary(again)!;
        TestRunner.Contains(b, "之前就已结算过", "幂等命中要说清这一次没再动钱");
        TestRunner.Contains(b, "未再动钱");
    }

    private static void LedgerSummarySaysNotMovedForRefusals()
    {
        var refused = new LastTradeNote
        {
            OfferId = "o0003", AmountUsd = 710, NpcId = NpcId,
            Status = TradeNoteStatuses.Refused, Reason = "PERSONALITY_LOW_EMOTION：分量不够"
        };
        string a = TradeNoteText.LedgerSummary(refused)!;
        TestRunner.Contains(a, "未成立", "被拒时要打「未成立」");
        TestRunner.Contains(a, "钱没动", "★被拒时账本口径必须是「没动」");
        TestRunner.Contains(a, "PERSONALITY_LOW_EMOTION", "带上拒绝码，便于一眼看出换多少");
        TestRunner.False(a.Contains('\n'), "摘要必须是单行（它是日志行的一部分）");

        var declined = new LastTradeNote
        {
            OfferId = "o0004", AmountUsd = 500, NpcId = NpcId,
            Status = TradeNoteStatuses.Declined, Reason = "玩家拒绝"
        };
        string b = TradeNoteText.LedgerSummary(declined)!;
        TestRunner.Contains(b, "被你拒绝");
        TestRunner.Contains(b, "钱没动");
    }

    private static void LedgerSummaryIsNullWithoutRecord()
    {
        TestRunner.True(TradeNoteText.LedgerSummary(null) == null, "没有记录时不该硬造一句");
        TestRunner.True(TradeNoteText.LedgerSummary(new LastTradeNote()) == null, "空状态同样返回 null");
    }

    // ── 结算出口必须留下结论（落盘节点）──────────────────────────────
    private static NpcEconomy Sponsor(long balance = 4_700) => new()
    {
        PersonId = NpcId, Balance = balance, TierId = "crowd", ClubId = "c54",
        PersonalityKind = "兴奋追随者", Season = 1
    };

    private static EconomyWorld ImportedWorld(NpcEconomy npc, string worldId = "note-test")
    {
        var world = new EconomyWorld { WorldId = worldId, LastSeason = 1, LastTickedDay = 452 };
        world.Npcs[npc.PersonId] = npc;
        ShadowPlayerWallet.ImportOnce(world, 19_682, 0m, day: 452, season: 1,
            clock: new FixedEconomyClock(T0), log: Silent);
        return world;
    }

    private static ConfigLoadResult RealConfig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "config");
            if (File.Exists(Path.Combine(candidate, ConfigLoader.EconomyFileName)))
                return new ConfigLoader(candidate, Silent).Load();
            directory = directory.Parent;
        }
        throw new Exception("找不到 config/ 目录");
    }

    private static TradeContext ContextFor(EconomyWorld world, IPlayerWallet? wallet = null)
    {
        var loaded = RealConfig();
        return new TradeContext
        {
            World = world,
            Config = loaded.Economy,
            Personality = loaded.Personality,
            Prompt = loaded.Prompt,
            Counterparty = new NpcFacts(NpcId, "普通玩家", Rating: 1100, ClubId: "c54",
                PersonalityKind: "兴奋追随者"),
            Favour = 0,
            Day = 452,
            Season = 1,
            Wallet = wallet ?? new ShadowPlayerWallet(world)
        };
    }

    private static TradeEngine Engine() => new(RealConfig().Economy, RealConfig().Personality,
        RealConfig().Prompt, Silent);

    private static string ReserveSponsored(TradeContext ctx, long amountUsd = 500)
    {
        var reserve = Engine().Submit(ctx, new TradeIntent(TradeKinds.Sponsor, TradeDirections.NpcToPlayer,
            NpcId, EconomyTier.PlayerId, amountUsd, [], "测试报价", "[Trade: test]"));
        TestRunner.True(reserve.Ok, "前置：应能落单：" + reserve.Check.Message);
        TradeConfirmationGate.Confirm(ctx, reserve.OfferId!, TradeConfirmationSide.Npc, "模型提出");
        return reserve.OfferId!;
    }

    private static void WithSettlement(bool wired, Action body)
    {
        bool before = TradeConfirmationGate.SettlementWired;
        TradeConfirmationGate.WireSettlement(wired);
        try { body(); }
        finally { TradeConfirmationGate.WireSettlement(before); }
    }

    private static void SettlementSuccessWritesSettledNote()
    {
        WithSettlement(true, () =>
        {
            var world = ImportedWorld(Sponsor());
            var ctx = ContextFor(world);
            string offerId = ReserveSponsored(ctx);
            TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "弹窗");

            var outcome = new TradeSettlement(Engine(), Silent).Settle(ctx, offerId, "弹窗");
            TestRunner.Equal(SettlementStatus.Applied, outcome.Status, "前置：应当真的结算：" + outcome.Detail);

            var note = world.LastTrade;
            TestRunner.True(note != null, "★结算之后影子存档上必须留下结论（V 段与弹窗都靠它）");
            TestRunner.Equal(TradeNoteStatuses.Settled, note!.Status, "结论应是「已结算」");
            TestRunner.True(note.Settled, "Settled 投影必须为 true（它是「钱真的动过」的唯一判据）");
            TestRunner.Equal(offerId, note.OfferId);
            TestRunner.Equal(500L, note.AmountUsd);
            TestRunner.Equal(NpcId, note.NpcId, "要记下对手方，否则文案里说不清是谁");
            TestRunner.Equal(TradeDirections.NpcToPlayer, note.Direction);
            TestRunner.Contains(note.Receipt, "已结算");
            TestRunner.Contains(note.Receipt, "$197,320", "回执要带结算后的玩家影子余额");
            TestRunner.Contains(note.Detail, "流水号", "弹窗正文要有流水号");
        });
    }

    private static void SecondSettlementWritesAlreadySettledNote()
    {
        WithSettlement(true, () =>
        {
            var world = ImportedWorld(Sponsor());
            var ctx = ContextFor(world);
            string offerId = ReserveSponsored(ctx);
            TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "弹窗");

            var settlement = new TradeSettlement(Engine(), Silent);
            TestRunner.Equal(SettlementStatus.Applied, settlement.Settle(ctx, offerId, "弹窗").Status);
            var second = settlement.Settle(ctx, offerId, "弹窗（再点一次）");
            TestRunner.Equal(SettlementStatus.AlreadyApplied, second.Status, "前置：第二次应是幂等命中");

            var note = world.LastTrade!;
            TestRunner.Equal(TradeNoteStatuses.AlreadySettled, note.Status, "幂等命中也要如实落成「已经结算过」");
            TestRunner.True(note.Settled, "它仍然属于「钱确实动过」这一档");
            TestRunner.Contains(note.Receipt, "没有再动钱", "但必须说清这次没再动钱");
        });
    }

    /// <summary>
    /// 开关没开时结算器会返回 <see cref="SettlementStatus.NotWired"/>。
    /// <b>这条出口同样要留下结论</b>——否则"玩家点了确认却什么都没发生"又一次变成静默失败。
    /// </summary>
    private static void NotWiredSettlementStillWritesNote()
    {
        WithSettlement(false, () =>
        {
            var world = ImportedWorld(Sponsor());
            var ctx = ContextFor(world);
            string offerId = ReserveSponsored(ctx);
            TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "弹窗");

            var outcome = new TradeSettlement(Engine(), Silent).Settle(ctx, offerId, "弹窗");
            TestRunner.Equal(SettlementStatus.NotWired, outcome.Status);

            var note = world.LastTrade;
            TestRunner.True(note != null, "★开关没开也要留下「未成立」的结论，不能静默");
            TestRunner.False(note!.Settled, "它是「未成立」，绝不能算成已结算");
            TestRunner.Contains(note.Receipt, "尚未接线", "回执要说清为什么没动钱");
            TestRunner.Contains(note.Reason, "尚未接线", "原因也要留一份（给模型看的正是它）");
            TestRunner.Equal(500L, note.AmountUsd, "金额要从单子上补齐（否则玩家不知道本来要给多少）");
            TestRunner.Equal(NpcId, note.NpcId, "对手方也要从单子上补齐");

            // ★ 真机实测补的一条纪律：**"开关没开"这条出口不该落盘**（它什么都没改变）。
            //   用"写过盘没有"的可观测结果来钉：换一个临时根目录重跑，文件不该出现。
            string root = Path.Combine(Path.GetTempPath(), "nse-note-nowrite-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new EconomyStore(root, new FixedEconomyClock(T0), Silent);
                var world2 = ImportedWorld(Sponsor(), "note-nowrite");
                var ctx2 = ContextFor(world2);
                string offer2 = ReserveSponsored(ctx2);
                TradeConfirmationGate.Confirm(ctx2, offer2, TradeConfirmationSide.Player, "弹窗");

                new TradeSettlement(Engine(), Silent, w => store.SaveWorld(w, force: true))
                    .Settle(ctx2, offer2, "弹窗");

                TestRunner.False(File.Exists(store.WorldPath("note-nowrite")),
                    "★开关没开时一次盘都不该写（真机教训：没结算就别碰存储）");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
        });
    }

    /// <summary>
    /// <b>真机 2026-10-12 01:19 实测出来的 bug 的回归</b>：重复确认一张**已结算**的单，
    /// 幂等闸会拒（状态置 confirmed 那段出口），这时内存里写好了"已结算"结论，
    /// 但**原先不落盘** —— 真机上表现为影子存档的文件时间戳纹丝不动、存档里连
    /// <c>LastTrade</c> 这个键都没有，于是"重启后仍能看到已结算回执"完全失效。
    /// <para>这条钉住：走 <c>AlreadyApplied</c> 这段出口之后，**换一个 store 重新读盘也能看到那条回执**。</para>
    /// </summary>
    private static void AlreadySettledNoteSurvivesReload()
    {
        WithSettlement(true, () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "nse-note-reload-" + Guid.NewGuid().ToString("N"));
            try
            {
                var clock = new FixedEconomyClock(T0);
                var store = new EconomyStore(root, clock, Silent);
                var world = ImportedWorld(Sponsor(), "note-reload");
                var ctx = ContextFor(world);
                string offerId = ReserveSponsored(ctx);
                TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "弹窗");

                var settlement = new TradeSettlement(Engine(), Silent, w => store.SaveWorld(w, force: true));
                TestRunner.Equal(SettlementStatus.Applied, settlement.Settle(ctx, offerId, "弹窗").Status,
                    "前置：第一次应当真的结算");

                // 第二次（幂等命中）：这才是真机 01:19 走的那段出口。
                var second = settlement.Settle(ctx, offerId, "弹窗（再点一次）");
                TestRunner.Equal(SettlementStatus.AlreadyApplied, second.Status, "前置：第二次应是幂等命中");

                var reloaded = new EconomyStore(root, clock, Silent).LoadWorld("note-reload").World;
                TestRunner.True(reloaded.LastTrade != null,
                    "★幂等命中之后必须落盘：重启（重新读盘）后仍要能看到那条回执");
                TestRunner.True(reloaded.LastTrade!.Settled, "重启后看到的必须是「已结算」");
                TestRunner.Equal(offerId, reloaded.LastTrade.OfferId, "回执要能对上单号");
                TestRunner.Contains(reloaded.LastTrade.Receipt, "已结算", "回执正文要还在");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
        });
    }

    /// <summary>
    /// <b>阶段 4 第五步 A 项：`player_to_npc` 的结论里必须有方向与对手方。</b>
    /// <para>
    /// 原先这条叫 <c>DirectionNotWiredNoteCarriesDirectionAndNpc</c>，钉的是"反向被方向闸挡下、
    /// 但结论仍要写下来"。甲方 2026-10-12 拍板放开之后，同一条出口变成了<b>"真的结算"</b>——
    /// 于是断言从"钱一分没动"翻成"钱真的动了"，而"方向与对手方"这条要求不变
    /// （它正是玩家看懂"这笔钱是谁给谁"的唯一依据）。
    /// </para>
    /// <para>方向仍未被接线的那一个方向（<c>npc_to_npc</c>）改由 <c>TradeSettlementTests</c> 钉住。</para>
    /// </summary>
    private static void PlayerToNpcNoteCarriesDirectionAndNpc()
    {
        WithSettlement(true, () =>
        {
            var npc = new NpcEconomy
            {
                PersonId = "pro-1", Balance = 90_000, TierId = "professional", ClubId = "c00",
                PersonalityKind = "热心同好", Season = 1
            };
            var world = ImportedWorld(npc, "note-direction");
            var ctx = ContextFor(world);
            var reserve = Engine().Submit(ctx, new TradeIntent(TradeKinds.Hire, TradeDirections.PlayerToNpc,
                EconomyTier.PlayerId, "pro-1", 10_000, [], "测试报价", "[Trade: test]"));
            TestRunner.True(reserve.Ok, "前置：反向交易应能照常落单：" + reserve.Check.Message);
            TradeConfirmationGate.Confirm(ctx, reserve.OfferId!, TradeConfirmationSide.Npc, "模型同意");
            TradeConfirmationGate.Confirm(ctx, reserve.OfferId!, TradeConfirmationSide.Player, "玩家确认");

            var outcome = new TradeSettlement(Engine(), Silent).Settle(ctx, reserve.OfferId!, "单元测试");

            TestRunner.Equal(SettlementStatus.Applied, outcome.Status,
                "★甲方拍板后 player_to_npc 必须真的结算：" + outcome.Detail);

            var note = world.LastTrade!;
            TestRunner.True(note.Settled, "结论必须落成「已结算」");
            TestRunner.Equal(TradeDirections.PlayerToNpc, note.Direction, "要记下方向");
            TestRunner.Equal("pro-1", note.NpcId, "要记下对手方");

            // ★ 阶段 4 第五步：文案必须说对方向（原先写死了「NPC → 你」）。
            TestRunner.Contains(note.Receipt, "你 → pro-1", "★回执必须说「你 → NPC」");
            TestRunner.NotContains(note.Receipt, "pro-1 → 你", "★绝不能把玩家出钱渲染成 NPC 给钱");
            TestRunner.Contains(note.Receipt, "减少", "玩家侧要标出「减少」");
            TestRunner.Contains(TradeNoteText.LedgerSummary(note)!, "你已给过 pro-1",
                "账本口径摘要也要说对方向");
        });
    }

    /// <summary>
    /// <b>落点纪律</b>：这些注记一定带金额，所以只能出现在 V 段。
    /// 这条用"同一份输入分别合成两段"来证明，而不是靠调用方自觉。
    /// </summary>
    private static void NoteTextLandsInVolatileSegmentNotStable()
    {
        var config = RealConfig();
        var world = new EconomyWorld { WorldId = "note-volatile", LastTickedDay = 452, LastSeason = 1 };
        var npc = Sponsor();
        world.Npcs[npc.PersonId] = npc;
        world.LastTrade = new LastTradeNote
        {
            Day = 452, OfferId = "o0002", Kind = TradeKinds.Sponsor,
            Direction = TradeDirections.NpcToPlayer, AmountUsd = 500, NpcId = NpcId,
            Status = TradeNoteStatuses.Settled,
            Receipt = TradeNoteText.ReceiptLine(Applied(), NpcId)
        };

        string note = TradeNoteText.ReceiptLine(Applied(), NpcId) + "\n" + TradeNoteText.LedgerRuleLine;
        var input = new PromptInjectInput(world, npc, null, "crowd", config.Personality, config.Prompt,
            Favour: 0, Day: 452, Season: 1, ExtraVolatileNote: note);

        var injector = new PromptInjector(Silent);
        string stable = injector.BuildStableOnly(input, commit: false).StableText;
        string volatileText = injector.BuildVolatileOnly(input, commit: false).Text;

        TestRunner.Contains(volatileText, "$197,320", "V 段必须带上回执里的金额");
        TestRunner.Contains(volatileText, TradeNoteText.LedgerRuleLine, "V 段必须带上账本口径");
        TestRunner.False(System.Text.RegularExpressions.Regex.IsMatch(stable, @"\d"),
            "S 段必须保持零数字：<" + stable + ">");
        TestRunner.NotContains(stable, "已结算", "S 段绝不能泄漏交易回执");
        TestRunner.True(volatileText.Length <= 1200,
            "V 段是每轮都要重发的，长度必须守住（实测约 600，这里留一倍余量）：" + volatileText.Length);
    }

    /// <summary>
    /// <b>真相通道不许把 V 段撑大、更不许碰 S 段</b>：同一位 NPC、同一个余额，
    /// 只把"最近一笔"从"没有"换成"一条已结算回执 + 审计回读"，
    /// 那么 V 段必须变、S 段必须<b>逐字节不变</b>（前缀缓存才不会被交易历史拖垮）。
    /// </summary>
    private static void TruthChannelChangesVolatileOnlyNotStable()
    {
        var config = RealConfig();
        var world = new EconomyWorld { WorldId = "note-budget", LastTickedDay = 452, LastSeason = 1 };
        var npc = Sponsor();
        world.Npcs[npc.PersonId] = npc;

        var injector = new PromptInjector(Silent);

        // ① 没有任何交易记录：只有余额那几行 + 兜底行 + 口径行。
        string noTradeNote = TradeNoteText.NoRecentTradeLine + "\n" + TradeNoteText.LedgerRuleLine;
        var before = new PromptInjectInput(world, npc, null, "crowd", config.Personality, config.Prompt,
            Favour: 0, Day: 452, Season: 1, ExtraVolatileNote: noTradeNote);
        string stableBefore = injector.BuildStableOnly(before, commit: false).StableText;
        string volatileBefore = injector.BuildVolatileOnly(before, commit: false).Text;

        // ② 换成"已结算 + 最近结算回读"：V 段该变长，S 段一个字都不许动。
        string settledLine = TradeNoteText.ReceiptLine(Applied(), NpcId);
        string recent = TradeNoteText.RecentSettlements(
        [
            new AuditRecord
            {
                Kind = AuditKinds.TradeSettled, Day = 452, PersonId = NpcId, AmountUsd = 500,
                OfferId = "o0002", Direction = TradeDirections.NpcToPlayer,
                BalanceAfterUsd = 4_000, PlayerBalanceAfterUsd = 197_320
            }
        ])!;
        string richNote = settledLine + "\n" + recent + "\n" + TradeNoteText.LedgerRuleLine;
        var after = before with { ExtraVolatileNote = richNote };
        string stableAfter = injector.BuildStableOnly(after, commit: false).StableText;
        string volatileAfter = injector.BuildVolatileOnly(after, commit: false).Text;

        TestRunner.Equal(stableBefore, stableAfter,
            "★交易历史绝不能改变 S 段（它一抖，前缀缓存每笔交易都失效）");
        TestRunner.NotEqual(volatileBefore, volatileAfter, "V 段必须跟着账本真相变");
        TestRunner.Contains(volatileAfter, "已结算");
        TestRunner.Contains(volatileAfter, "最近结算");
        TestRunner.True(volatileAfter.Length <= 1200,
            "带上回执与审计回读之后 V 段也要守住长度：" + volatileAfter.Length);
        TestRunner.True(volatileAfter.Length > volatileBefore.Length,
            "多了一条真相，V 段理应更长（否则说明注记没进去）");
    }

    /// <summary>
    /// <b>老存档必须照旧能读、能注入</b>：第三步给 <see cref="EconomyWorld"/> 加了 <c>LastTrade</c>，
    /// 而线上这份影子存档（真机实测）里<b>根本没有这个键</b>——它是在旧 DLL 下写出来的。
    /// <para>
    /// 这条用例就用"缺键的 JSON"走一遍真实的反序列化，确认：
    /// ① 反序列化不报错、<c>LastTrade</c> 是 null；
    /// ② V 段照旧能合成，并且给的是"还没有任何交易记录"的兜底行 + 账本口径，
    ///    而不是缺一块、更不是崩掉（真机 00:57 那次注入的 V 段长度 262 就是这条路径）。
    /// </para>
    /// </summary>
    private static void OldSaveWithoutLastTradeStillRenders()
    {
        var world = System.Text.Json.JsonSerializer.Deserialize<EconomyWorld>(
            """
            {
              "SchemaVersion": 1,
              "WorldId": "legacy-world",
              "LastTickedDay": 452,
              "LastSeason": 6,
              "Npcs": {},
              "Player": { "Imported": true, "Balance": 197320 },
              "Offers": {},
              "Receipts": []
            }
            """)!;

        TestRunner.Equal("legacy-world", world.WorldId, "老存档应能正常反序列化");
        TestRunner.True(world.LastTrade == null, "★旧存档里没有 LastTrade 键 ⇒ 必须是 null，而不是抛异常");
        TestRunner.Equal(197_320L, world.Player.Balance, "其余字段照旧");

        var config = RealConfig();
        var npc = Sponsor();
        world.Npcs[npc.PersonId] = npc;

        // 与运行期同一条合成路径：没有 LastTrade ⇒ 兜底行 + 账本口径。
        string note = TradeNoteText.NoRecentTradeLine + "\n" + TradeNoteText.LedgerRuleLine;
        var input = new PromptInjectInput(world, npc, null, "crowd", config.Personality, config.Prompt,
            Favour: 0, Day: 452, Season: 6, ExtraVolatileNote: note);
        var injector = new PromptInjector(Silent);
        string volatileText = injector.BuildVolatileOnly(input, commit: false).Text;

        TestRunner.Contains(volatileText, "还没有任何交易记录", "老存档下 V 段要给兜底行");
        TestRunner.Contains(volatileText, TradeNoteText.LedgerRuleLine, "账本口径每轮都在，与有没有交易无关");
        TestRunner.False(TradeNoteText.LedgerSummary(null) != null, "没有记录时不该编出一句账本口径");
        TestRunner.True(volatileText.Length is > 0 and <= 1200, "长度也要守住：" + volatileText.Length);
    }

    /// <summary>
    /// <b>真机 01:26 换版后要复验的那条路径，先在本地跑通</b>：结算 → 落盘 → **从磁盘重新读出来** →
    /// 用<b>读回来的那条 <c>LastTrade</c></b> 合成 V 段。
    /// <para>
    /// 它与 <c>AlreadySettledNoteSurvivesReload</c> 的分工：那条只证"重新读盘后回执还在"；
    /// 这条再往前一步，证"**读回来的回执真的会变成 V 段里那一行**"——
    /// 也就是玩家重启后在私信里看到的东西。真机上这一步只能靠肉眼，这里先钉死。
    /// </para>
    /// </summary>
    private static void ReloadedReceiptBecomesTheVolatileLine()
    {
        var config = RealConfig();
        var npc = Sponsor();

        // ① 结算 → 落盘（用真实 store，走与线上同一条写盘实现）。
        string root = Path.Combine(Path.GetTempPath(), "nse-note-vline-" + Guid.NewGuid().ToString("N"));
        try
        {
            string worldId = "note-vline";
            WithSettlement(true, () =>
            {
                var clock = new FixedEconomyClock(T0);
                var store = new EconomyStore(root, clock, Silent);
                var world = ImportedWorld(Sponsor(), worldId);
                var ctx = ContextFor(world);
                string offerId = ReserveSponsored(ctx);
                TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "弹窗");

                var settlement = new TradeSettlement(Engine(), Silent, w => store.SaveWorld(w, force: true));
                TestRunner.Equal(SettlementStatus.Applied, settlement.Settle(ctx, offerId, "弹窗").Status,
                    "前置：应当真的结算");
            });

            // ② 像重启那样，从磁盘把世界读回来（不是复用内存里的对象）。
            var reloaded = new EconomyStore(root, new FixedEconomyClock(T0), Silent).LoadWorld(worldId).World;
            var loadedNote = reloaded.LastTrade;
            TestRunner.True(loadedNote != null, "前置：重新读盘后应有 LastTrade");
            TestRunner.True(loadedNote!.Settled, "前置：读回来的应是「已结算」");

            // ③ 用读回来的那条记录合成 V 段（运行期由 TradePreviewWiring.NoteFor 组装同一份文本）。
            reloaded.Npcs[npc.PersonId] = npc;
            string note = loadedNote.Receipt + "\n" + TradeNoteText.RecentSettlements(
            [
                new AuditRecord
                {
                    Kind = AuditKinds.TradeSettled, Day = loadedNote.Day, PersonId = loadedNote.NpcId,
                    AmountUsd = loadedNote.AmountUsd, OfferId = loadedNote.OfferId,
                    Direction = loadedNote.Direction, BalanceAfterUsd = 4_000,
                    PlayerBalanceAfterUsd = 197_320
                }
            ]) + "\n" + TradeNoteText.LedgerRuleLine;

            var input = new PromptInjectInput(reloaded, npc, null, "crowd", config.Personality, config.Prompt,
                Favour: 0, Day: reloaded.LastTickedDay, Season: 1, ExtraVolatileNote: note);
            var injector = new PromptInjector(Silent);
            string stable = injector.BuildStableOnly(input, commit: false).StableText;
            string volatileText = injector.BuildVolatileOnly(input, commit: false).Text;

            TestRunner.Contains(volatileText, "已结算", "★重启后 V 段必须还能给出「已结算」那一行");
            TestRunner.Contains(volatileText, loadedNote.OfferId, "要能对上单号");
            TestRunner.Contains(volatileText, MoneyUnits.Format(loadedNote.AmountUsd), "要带金额");
            TestRunner.Contains(volatileText, MoneyUnits.Format(197_320), "要带玩家影子余额");
            TestRunner.Contains(volatileText, "最近结算", "审计回读也要在");
            TestRunner.Contains(volatileText, TradeNoteText.LedgerRuleLine, "账本口径每轮都在");
            TestRunner.False(System.Text.RegularExpressions.Regex.IsMatch(stable, @"\d"),
                "S 段仍然必须零数字：<" + stable + ">");
            Console.WriteLine("      · 重启后 V 段形状：" + volatileText.Replace("\n", " ⏎ "));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
