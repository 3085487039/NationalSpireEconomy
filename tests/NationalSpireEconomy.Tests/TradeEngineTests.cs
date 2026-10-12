using System.Text;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Inject;
using NationalSpireEconomy.Trade;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 两段式执行：<c>Reserve</c>（不动余额）与 <c>Commit</c>（确认时<b>重新校验</b>后动账）。
/// <para>
/// 这一套盯的是三条硬契约：① Reserve 之后余额一分不动；② 确认时余额已经变了，
/// <c>Commit</c> 必须重新校验并失败，而不是照着旧校验码执行；③ 同一张单重复确认不会重复扣款。
/// </para>
/// </summary>
public static class TradeEngineTests
{
    public static Action[] Cases =>
    [
        // Reserve
        ReserveCreatesPendingOfferWithoutTouchingBalance,
        ReserveFromPlayerToNpcMovesNothingUntilCommit,
        ReserveRejectsInvalidIntent,
        ReserveExpiryDayFollowsConfig,
        ReserveIsIdempotentForRepeatedIntent,

        // Commit
        CommitAppliesBothSides,
        CommitRevalidatesWhenBalanceChanged,
        CommitRevalidatesWhenNpcWasDrained,
        CommitOnAlreadyConfirmedOfferDoesNotDoubleCharge,
        CommitOnExpiredOfferFailsAndMarksExpired,
        CommitOnUnknownOfferFails,
        CommitOnDeclinedOfferFails,
        CommitDoesNotSilentlyAdjustAmount,

        // 软失败
        SoftDeclineDoesNotCreateOfferAndWritesPendingVoice,
        SoftDeclineRecordsTriggerAndRefusedCount,
        HardFailCreatesInvalidOfferWhenSoftDisabled,

        // 编排
        DeclineMarksOfferWithoutMovingMoney,
        ExpireStaleMarksOnlyOverduePendingOffers,
        EndToEndFromMarkerTextToConfirmedTransfer,
        EngineDoesNotDoubleCountTradeCount,

        // 性格拒绝 → 记仇
        PersonalityRefusalRecordsCharityGrudge,
        SoftDeclineDoesNotRecordCharityGrudge,
        PersonalityRefusalAtConfirmAlsoRecordsGrudge,
        TechnicalRefusalDoesNotRecordGrudge,
        RefusalCodeSetMatchesValidatorOwnership
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

    private static NpcEconomy Npc(string id, long balance, string tier = "professional", string kind = "热心同好") => new()
    {
        PersonId = id,
        Balance = balance,
        TierId = tier,
        ClubId = "c00",
        PersonalityKind = kind,
        Season = 1
    };

    private static TradeEngine Engine(EconomyConfig? config = null)
    {
        var loaded = RealConfig();
        return new TradeEngine(config ?? loaded.Economy, loaded.Personality, loaded.Prompt, Silent);
    }

    private static TradeContext Ctx(NpcEconomy npc, long playerUsd = 1_000_000, int favour = 0, int day = 10,
        EconomyConfig? config = null) => new()
    {
        World = WorldWith(npc),
        Config = config ?? RealConfig().Economy,
        Personality = RealConfig().Personality,
        Prompt = RealConfig().Prompt,
        Counterparty = new NpcFacts(npc.PersonId, "职业选手", Rating: 1200, ClubId: "c00",
            PersonalityKind: npc.PersonalityKind),
        Favour = favour,
        Day = day,
        Wallet = new InMemoryPlayerWallet(playerUsd)
    };

    private static EconomyWorld WorldWith(params NpcEconomy[] npcs)
    {
        var world = new EconomyWorld { WorldId = "engine-test", LastSeason = 1, LastTickedDay = 1 };
        foreach (var npc in npcs) world.Npcs[npc.PersonId] = npc;
        return world;
    }

    private static TradeIntent Give(long amountUsd, string kind = TradeKinds.Transfer) =>
        new(kind, TradeDirections.PlayerToNpc, EconomyTier.PlayerId, "npc", amountUsd, [], "测试", "[Trade: test]");

    private static TradeIntent PayOut(long amountUsd, string kind = TradeKinds.Sponsor) =>
        new(kind, TradeDirections.NpcToPlayer, "npc", EconomyTier.PlayerId, amountUsd, [], "测试", "[Trade: test]");

    // ── Reserve ─────────────────────────────────────────────────────

    private static void ReserveCreatesPendingOfferWithoutTouchingBalance()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc);
        var result = Engine().Submit(ctx, Give(50_000));

        TestRunner.True(result.Ok, "校验应通过：" + result.Check.Message);
        TestRunner.True(result.OfferId != null, "应生成交易单 id");
        TestRunner.Equal(TradeCodes.Ok, result.Check.Code, "预校验码应记为通过");
        TestRunner.False(result.SoftDeclined);

        TestRunner.Equal(50_000L, npc.Balance, "Reserve 绝不能动任何余额");
        TestRunner.Equal(1_000_000L, ctx.Wallet.BalanceUsd, "玩家钱包也不能动");
        TestRunner.Equal(0, npc.Ledger.Count, "Reserve 不该产生流水");

        var offer = ctx.World.Offers[result.OfferId!];
        TestRunner.True(TradeOfferStates.IsPending(offer.State), "交易单应为 pending");
        TestRunner.Equal(50_000L, offer.Amount);
        TestRunner.Equal(TradeDirections.PlayerToNpc, offer.Direction);
    }

    private static void ReserveFromPlayerToNpcMovesNothingUntilCommit()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc);
        var engine = Engine();

        var reserved = engine.Submit(ctx, Give(50_000));
        TestRunner.Equal(50_000L, npc.Balance, "Reserve 后 NPC 余额不变");

        var committed = engine.Confirm(ctx, reserved.OfferId!);
        TestRunner.True(committed.Ok, "确认应当成功：" + committed.Check.Message);
        TestRunner.Equal(100_000L, npc.Balance, "确认后 NPC 收到钱");
        TestRunner.Equal(950_000L, ctx.Wallet.BalanceUsd, "玩家钱包扣款");
    }

    private static void ReserveRejectsInvalidIntent()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc);
        var result = Engine().Submit(ctx, new TradeIntent(
            "行贿", TradeDirections.PlayerToNpc, EconomyTier.PlayerId, "npc", 5_000, [], "", "[Trade: test]"));

        TestRunner.False(result.Ok);
        TestRunner.Equal(TradeCodes.BadKind, result.Check.Code);
        TestRunner.Equal(0, ctx.World.Offers.Count, "被拒的意图不该留下交易单");
        TestRunner.Equal(50_000L, npc.Balance, "被拒时余额不动");
    }

    private static void ReserveExpiryDayFollowsConfig()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc, day: 10);
        var result = Engine().Submit(ctx, Give(5_000));

        var offer = ctx.World.Offers[result.OfferId!];
        TestRunner.Equal(10 + ctx.Config.Trade.OfferExpiryDays, offer.ExpiresDay, "过期日 = 当天 + offerExpiryDays");
        TestRunner.Equal(10, offer.CreatedDay);
    }

    private static void ReserveIsIdempotentForRepeatedIntent()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc);
        var engine = Engine();

        TestRunner.True(engine.Submit(ctx, Give(5_000)).Ok, "第一次应通过");

        // 模型重试时又发了一遍同样的标记：必须被幂等校验挡下。
        var second = engine.Submit(ctx, Give(5_000));
        TestRunner.False(second.Ok, "同一意图重复提交应被拒");
        TestRunner.Equal(TradeCodes.AlreadyApplied, second.Check.Code);
        TestRunner.Equal(1, ctx.World.Offers.Count, "不该留下第二张单");
    }

    // ── Commit ──────────────────────────────────────────────────────

    private static void CommitAppliesBothSides()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc);
        var engine = Engine();

        var reserved = engine.Submit(ctx, Give(50_000));
        var committed = engine.Confirm(ctx, reserved.OfferId!);

        TestRunner.True(committed.Ok);
        TestRunner.True(committed.LedgerEntryId != null && committed.LedgerEntryId!.Length > 0, "应留下流水 id");
        TestRunner.Equal(100_000L, committed.NpcBalanceAfter);
        TestRunner.Equal(950_000L, committed.PlayerBalanceAfter);

        var offer = ctx.World.Offers[reserved.OfferId!];
        TestRunner.Equal(TradeOfferStates.Confirmed, offer.State);
        TestRunner.Equal(committed.LedgerEntryId, offer.LedgerEntryId, "交易单应记下流水 id 以便对账");
    }

    /// <summary>
    /// 验收清单的原话：<c>Reserve</c> 后余额已变 → <c>Commit</c> 必须重新校验并失败，
    /// 而不是照着旧校验码执行。
    /// </summary>
    private static void CommitRevalidatesWhenBalanceChanged()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc, playerUsd: 50_000);
        var engine = Engine();

        var reserved = engine.Submit(ctx, Give(50_000));
        TestRunner.True(reserved.Ok, "Reserve 时玩家余额刚好够");

        // 报价与确认之间玩家把钱花掉了。
        ctx.Wallet.TryAdd(-49_000, out _);
        TestRunner.Equal(1_000L, ctx.Wallet.BalanceUsd);

        var committed = engine.Confirm(ctx, reserved.OfferId!);
        TestRunner.False(committed.Ok, "余额已变，确认时必须重新校验并失败");
        TestRunner.Equal(TradeCodes.PlayerInsufficient, committed.Check.Code);
        TestRunner.Equal(TradeOfferStates.Invalid, ctx.World.Offers[reserved.OfferId!].State);
        TestRunner.Equal(50_000L, npc.Balance, "失败时 NPC 侧一分不动");
    }

    private static void CommitRevalidatesWhenNpcWasDrained()
    {
        var npc = Npc("npc", 100_000, "elite");
        var ctx = Ctx(npc);
        var engine = Engine();

        var reserved = engine.Submit(ctx, PayOut(50_000));
        TestRunner.True(reserved.Ok, "Reserve 时 NPC 余额充足：" + reserved.Check.Message);

        // NPC 在等待确认期间被扣了钱（转会、发薪……）。
        npc.Balance = 1_000;

        var committed = engine.Confirm(ctx, reserved.OfferId!);
        TestRunner.False(committed.Ok, "NPC 余额已经不足，确认必须失败");
        TestRunner.Equal(TradeOfferStates.Invalid, ctx.World.Offers[reserved.OfferId!].State);
        TestRunner.Equal(1_000L, npc.Balance, "失败时余额不动");
    }

    private static void CommitOnAlreadyConfirmedOfferDoesNotDoubleCharge()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc);
        var engine = Engine();

        var reserved = engine.Submit(ctx, Give(50_000));
        TestRunner.True(engine.Confirm(ctx, reserved.OfferId!).Ok);

        long npcAfterFirst = npc.Balance;
        long playerAfterFirst = ctx.Wallet.BalanceUsd;

        // 玩家（或网络重试）又点了一次确认。
        var second = engine.Confirm(ctx, reserved.OfferId!);
        TestRunner.False(second.Ok, "重复确认不该再动账");
        TestRunner.Equal(TradeCodes.OfferNotPending, second.Check.Code);
        TestRunner.Equal(npcAfterFirst, npc.Balance, "余额不能变");
        TestRunner.Equal(playerAfterFirst, ctx.Wallet.BalanceUsd, "玩家钱包不能变");
    }

    private static void CommitOnExpiredOfferFailsAndMarksExpired()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc, day: 10);
        var engine = Engine();

        var reserved = engine.Submit(ctx, Give(5_000));
        var offer = ctx.World.Offers[reserved.OfferId!];

        // 时间推进到过期之后。
        ctx.World.LastTickedDay = offer.ExpiresDay + 1;
        var later = Ctx(npc, day: offer.ExpiresDay + 1, config: ctx.Config);
        later.World.Offers[reserved.OfferId!] = offer;

        var committed = engine.Confirm(later, reserved.OfferId!);
        TestRunner.False(committed.Ok);
        TestRunner.Equal(TradeCodes.OfferExpired, committed.Check.Code);
        TestRunner.Equal(TradeOfferStates.Expired, offer.State, "过期应被置为 expired");
    }

    private static void CommitOnUnknownOfferFails()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc);
        var committed = Engine().Confirm(ctx, "o9999");

        TestRunner.False(committed.Ok);
        TestRunner.Equal(TradeCodes.OfferNotFound, committed.Check.Code);
    }

    private static void CommitOnDeclinedOfferFails()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc);
        var engine = Engine();

        var reserved = engine.Submit(ctx, Give(5_000));
        engine.Decline(ctx, reserved.OfferId!, "不想要");

        var committed = engine.Confirm(ctx, reserved.OfferId!);
        TestRunner.False(committed.Ok);
        TestRunner.Equal(TradeCodes.OfferNotPending, committed.Check.Code);
        TestRunner.Equal(50_000L, npc.Balance);
    }

    /// <summary>
    /// 重新校验失败时<b>不静默改金额</b>：原版是重新报错让玩家重谈
    /// （<c>PrivateContracts.Confirm</c> 的做法）。这条是行为契约，必须钉住。
    /// </summary>
    private static void CommitDoesNotSilentlyAdjustAmount()
    {
        var npc = Npc("npc", 100_000, "elite");
        var ctx = Ctx(npc);
        var engine = Engine();

        var reserved = engine.Submit(ctx, PayOut(50_000));
        long originalAmount = ctx.World.Offers[reserved.OfferId!].Amount;

        npc.Balance = 10_000;                     // 现在只给得起 1 万
        var committed = engine.Confirm(ctx, reserved.OfferId!);

        TestRunner.False(committed.Ok);
        TestRunner.Equal(originalAmount, ctx.World.Offers[reserved.OfferId!].Amount,
            "失败时交易单的金额必须保持原样，不能被悄悄改成 1 万");
        TestRunner.Equal(10_000L, npc.Balance, "失败时余额不动");
    }

    // ── 软失败 ──────────────────────────────────────────────────────

    private static void SoftDeclineDoesNotCreateOfferAndWritesPendingVoice()
    {
        var npc = Npc("npc", 1_000, "crowd");
        var ctx = Ctx(npc);
        var result = Engine().Submit(ctx, PayOut(50_000));

        TestRunner.False(result.Ok);
        TestRunner.True(result.SoftDeclined, "NPC 余额不足应走软失败");
        TestRunner.Equal(TradeCodes.NpcSoftDecline, result.Check.Code);
        TestRunner.True(result.InjectLine.Length > 0, "软失败必须带注入素材");

        // 三条最关键的断言：不落单、不动账、留下"未竟之言"。
        TestRunner.Equal(0, ctx.World.Offers.Count, "软失败绝不能生成 TradeOffer");
        TestRunner.Equal(1_000L, npc.Balance, "软失败不动余额");
        TestRunner.Equal(0, npc.Ledger.Count, "软失败不产生流水");
        TestRunner.Equal(result.InjectLine, npc.PendingVoice, "素材应写进 PendingVoice");
    }

    private static void SoftDeclineRecordsTriggerAndRefusedCount()
    {
        var npc = Npc("npc", 1_000, "crowd");
        var ctx = Ctx(npc);
        Engine().Submit(ctx, PayOut(50_000));

        TestRunner.Equal(1, npc.Lifetime.RefusedCount, "软失败应累加 RefusedCount");
        TestRunner.Equal(1, npc.IntentTriggers.Count, "应记一条意向触发");
        TestRunner.Equal("trade_soft_declined", npc.IntentTriggers[0].Kind);
        TestRunner.Equal(-0.05, npc.IntentTriggers[0].Delta, "软失败应让意向向下漂移");
        TestRunner.Equal(10, npc.LastTouchedDay, "应更新最后变动日");
    }

    private static void HardFailCreatesInvalidOfferWhenSoftDisabled()
    {
        var config = RealConfig().Economy;
        config.Trade.SoftFailOnBalance = false;

        var npc = Npc("npc", 1_000, "crowd");
        var ctx = Ctx(npc, config: config);
        var result = Engine(config).Submit(ctx, PayOut(50_000));

        TestRunner.False(result.Ok);
        TestRunner.False(result.SoftDeclined, "关掉开关后应是硬失败");
        TestRunner.Equal(TradeCodes.NpcInsufficient, result.Check.Code);
        TestRunner.Equal("", npc.PendingVoice, "硬失败不写 PendingVoice");
    }

    // ── 编排 ────────────────────────────────────────────────────────

    private static void DeclineMarksOfferWithoutMovingMoney()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc);
        var engine = Engine();

        var reserved = engine.Submit(ctx, Give(5_000));
        engine.Decline(ctx, reserved.OfferId!, "改主意了");

        var offer = ctx.World.Offers[reserved.OfferId!];
        TestRunner.Equal(TradeOfferStates.Declined, offer.State);
        TestRunner.Equal("改主意了", offer.Report);
        TestRunner.Equal(50_000L, npc.Balance);
        TestRunner.Equal(1_000_000L, ctx.Wallet.BalanceUsd);
    }

    private static void ExpireStaleMarksOnlyOverduePendingOffers()
    {
        var npc = Npc("npc", 500_000, "elite");
        var ctx = Ctx(npc, day: 1);
        var engine = Engine();

        // 直接摆三张单进世界，绕开 Reserve 的校验链——这里验的是 ExpireStale 本身。
        var offers = ctx.World.Offers;
        offers["oA"] = new TradeOffer
        {
            Id = "oA", Kind = TradeKinds.Transfer, Direction = TradeDirections.PlayerToNpc,
            InitiatorId = EconomyTier.PlayerId, CounterpartyId = "npc", Amount = 5_000,
            State = TradeOfferStates.Pending, CreatedDay = 1, ExpiresDay = 5
        };
        offers["oB"] = new TradeOffer
        {
            Id = "oB", Kind = TradeKinds.Transfer, Direction = TradeDirections.PlayerToNpc,
            InitiatorId = EconomyTier.PlayerId, CounterpartyId = "npc", Amount = 6_000,
            State = TradeOfferStates.Pending, CreatedDay = 1, ExpiresDay = 400
        };
        offers["oC"] = new TradeOffer
        {
            Id = "oC", Kind = TradeKinds.Transfer, Direction = TradeDirections.PlayerToNpc,
            InitiatorId = EconomyTier.PlayerId, CounterpartyId = "npc", Amount = 7_000,
            State = TradeOfferStates.Declined, CreatedDay = 1, ExpiresDay = 2
        };
        // ExpiresDay == 0 表示"不设过期"，任何一天都不该被清掉。
        offers["oD"] = new TradeOffer
        {
            Id = "oD", Kind = TradeKinds.Transfer, Direction = TradeDirections.PlayerToNpc,
            InitiatorId = EconomyTier.PlayerId, CounterpartyId = "npc", Amount = 8_000,
            State = TradeOfferStates.Pending, CreatedDay = 1, ExpiresDay = 0
        };

        int marked = engine.ExpireStale(ctx.World, day: 100);

        TestRunner.Equal(1, marked, "只应清理那一张真正过期的 pending 单");
        TestRunner.Equal(TradeOfferStates.Expired, offers["oA"].State);
        TestRunner.True(TradeOfferStates.IsPending(offers["oB"].State), "未过期的应保持 pending");
        TestRunner.Equal(TradeOfferStates.Declined, offers["oC"].State, "已终结的单不该被改动");
        TestRunner.True(TradeOfferStates.IsPending(offers["oD"].State), "ExpiresDay=0 表示不设过期");
        TestRunner.Equal(500_000L, npc.Balance, "清理过期单不该动任何余额");
    }

    /// <summary>
    /// 端到端：一段模型正文 → 解析 → Submit → 玩家确认 → 动账。
    /// 这条走通才算第 5 步真的接通了。
    /// </summary>
    private static void EndToEndFromMarkerTextToConfirmedTransfer()
    {
        const string modelText = "这局你打得漂亮。[Trade: 赞助, Amount: 5万, Reason: 这赛季刚拿工资，正好赞助你一点] 收着吧。";

        var parsed = TradeProtocol.ParseAll(modelText);
        TestRunner.Equal(1, parsed.Count, "应解析出一条意图");
        TestRunner.Equal("赞助", parsed[0].Kind);
        TestRunner.Equal(50_000L, parsed[0].AmountUsd);

        // 协议层不知道"谁对谁"，方向与双方 id 由接线层补上。
        var intent = parsed[0] with
        {
            Direction = TradeDirections.NpcToPlayer,
            InitiatorId = "npc",
            CounterpartyId = EconomyTier.PlayerId
        };

        string clean = TradeProtocol.StripMarkers(modelText);
        TestRunner.NotContains(clean, "[Trade:", "正文里不能残留标记");

        var npc = Npc("npc", 500_000, "elite");
        var ctx = Ctx(npc);
        var engine = Engine();

        var reserved = engine.Submit(ctx, intent);
        TestRunner.True(reserved.Ok, "应生成待确认单：" + reserved.Check.Message);
        TestRunner.Equal(500_000L, npc.Balance, "Reserve 不动余额");

        var committed = engine.Confirm(ctx, reserved.OfferId!);
        TestRunner.True(committed.Ok, "确认应成功：" + committed.Check.Message);
        TestRunner.Equal(450_000L, npc.Balance, "NPC 出钱后余额减少");
        TestRunner.Equal(1_050_000L, ctx.Wallet.BalanceUsd, "玩家收到钱");
        TestRunner.Equal(-50_000L, npc.Ledger[0].Amount, "NPC 出钱应记为负数（流水约定：正入负出）");
        TestRunner.Equal("赞助", npc.Ledger[0].Kind, "流水科目应为「赞助」");
        TestRunner.Equal(reserved.OfferId, npc.Ledger[0].OfferId, "流水应关联到交易单");
        TestRunner.Equal(450_000L, npc.Ledger[0].BalanceAfter, "流水应记下记账后的余额快照");
    }

    /// <summary>
    /// <c>Lifetime.TradeCount</c> 由 <see cref="EconomyLedger"/> 内部累加，
    /// 引擎<b>不能</b>再重复加一次，否则统计会翻倍。
    /// </summary>
    private static void EngineDoesNotDoubleCountTradeCount()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc);
        var engine = Engine();

        var reserved = engine.Submit(ctx, Give(5_000));
        TestRunner.True(engine.Confirm(ctx, reserved.OfferId!).Ok);

        TestRunner.Equal(1, npc.Lifetime.TradeCount, "一次成交只应记一次 TradeCount，不能翻倍");
    }

    // ── 性格拒绝 → 记仇（resenful 档的数据来源） ─────────────────────

    /// <summary>
    /// 硬失败里的"性格拒绝"必须记入意向漂移，否则"The NPC 被小看而记仇"这个机制等于白做。
    /// </summary>
    private static void PersonalityRefusalRecordsCharityGrudge()
    {
        // 战绩踩头者 + 陌生好感：小额打赏必被拒。
        var npc = Npc("npc", 50_000, "professional", "战绩踩头者");
        var ctx = Ctx(npc, favour: 0);
        var result = Engine().Submit(ctx, Give(1_000, TradeKinds.Tip));

        TestRunner.False(result.Ok);
        TestRunner.Equal(TradeCodes.PersonalityRefusesCharity, result.Check.Code);

        TestRunner.Equal(10, npc.Lifetime.LastRefusedCharityDay, "必须记下被小看的日子");
        TestRunner.True(npc.Lifetime.DriftScore < 0, "意向应向下漂：" + npc.Lifetime.DriftScore);
        TestRunner.True(npc.IntentTriggers.Count > 0, "应留下触发记录");
        TestRunner.Equal(DriftTriggers.CharityRefused, npc.IntentTriggers[0].Kind);
        TestRunner.Contains(npc.IntentTriggers[0].Detail, TradeCodes.PersonalityRefusesCharity,
            "触发记录里应带上拒绝原因，便于排查");
    }

    /// <summary>
    /// 软失败是"想给但给不起"，与"被小看"是两件事，<b>绝不能</b>记成记仇。
    /// 记错了会让一个只是缺钱的 NPC 无端端变得刻薄。
    /// </summary>
    private static void SoftDeclineDoesNotRecordCharityGrudge()
    {
        var npc = Npc("npc", 1_000, "crowd");
        var ctx = Ctx(npc);
        var result = Engine().Submit(ctx, PayOut(50_000));

        TestRunner.True(result.SoftDeclined, "前置：这一笔应走软失败");
        TestRunner.Equal(0, npc.Lifetime.LastRefusedCharityDay,
            "软失败不该写 LastRefusedCharityDay —— 余额不足不是被小看");
        TestRunner.Equal(DriftTriggers.SoftDeclined, npc.IntentTriggers[0].Kind,
            "软失败记的是自己的触发类型，而不是 charity_refused");
        // 分数可以因为软失败往下漂（那是"手头紧"的正确表达），但档位不该变成 resentful。
        TestRunner.NotEqual(DriftBands.Resentful, IntentEvolve.BandIdFor(npc, ctx.Day),
            "软失败不该让 NPC 进入 resentful 档");
    }

    /// <summary>确认阶段被性格拒了，同样要记仇，且交易单要落到 invalid。</summary>
    private static void PersonalityRefusalAtConfirmAlsoRecordsGrudge()
    {
        // 用"故意挑事者"：上限台词带占位符，且 give 较低容易被 CAP 拦下。
        var npc = Npc("npc", 200_000, "elite", "故意挑事者");
        var ctx = Ctx(npc);
        var engine = Engine();

        // 先用一个能通过 Reserve 的金额拿到待确认单，再让确认阶段失败：
        // 小额会撞上情绪门槛，大额会撞上性格上限。这里用"小额 → 情绪不足"制造失败，
        // 但情绪不足同样属于 PersonalityFitValidator 的拒绝码之一。
        var reserved = engine.Submit(ctx, PayOut(1_000));
        if (!reserved.Ok)
        {
            // Reserve 就被拒了：那也应当已经记过仇，直接验证这一点。
            TestRunner.True(PersonalityFitValidator.IsPersonalityRefusal(reserved.Check.Code),
                "Reserve 被拒的原因应是性格拒绝，实际 " + reserved.Check.Code);
            TestRunner.Equal(10, npc.Lifetime.LastRefusedCharityDay, "Reserve 阶段也应记仇");
            return;
        }

        int before = npc.IntentTriggers.Count;
        var committed = engine.Confirm(ctx, reserved.OfferId!);
        TestRunner.False(committed.Ok, "确认阶段应被重新校验拒掉");
        TestRunner.True(npc.IntentTriggers.Count > before, "确认阶段的性格拒绝也应留下触发记录");
    }

    /// <summary>非性格类的硬失败（例如 Kind 不在白名单）不该被误判成记仇。</summary>
    private static void TechnicalRefusalDoesNotRecordGrudge()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc);
        var result = Engine().Submit(ctx, new TradeIntent(
            "行贿", TradeDirections.PlayerToNpc, EconomyTier.PlayerId, "npc", 5_000, [], "", "[Trade: test]"));

        TestRunner.False(result.Ok);
        TestRunner.Equal(TradeCodes.BadKind, result.Check.Code);
        TestRunner.Equal(0, npc.Lifetime.LastRefusedCharityDay, "技术性拒绝不该被记成被小看");
        TestRunner.Equal(0, npc.IntentTriggers.Count, "技术性拒绝不该留下意向触发");
        TestRunner.Equal(0.0, npc.Lifetime.DriftScore, "技术性拒绝不该改变意向分数");
    }

    /// <summary>
    /// 拒绝码的归属只有一个定义点。软失败与几个非性格码都必须在集合之外。
    /// </summary>
    private static void RefusalCodeSetMatchesValidatorOwnership()
    {
        TestRunner.True(PersonalityFitValidator.IsPersonalityRefusal(TradeCodes.PersonalityRefusesCharity));
        TestRunner.True(PersonalityFitValidator.IsPersonalityRefusal(TradeCodes.PersonalityCap));

        TestRunner.False(PersonalityFitValidator.IsPersonalityRefusal(TradeCodes.NpcSoftDecline),
            "软失败绝不能被算作性格拒绝");
        TestRunner.False(PersonalityFitValidator.IsPersonalityRefusal(TradeCodes.PlayerInsufficient));
        TestRunner.False(PersonalityFitValidator.IsPersonalityRefusal(TradeCodes.DailyLimit));
        TestRunner.False(PersonalityFitValidator.IsPersonalityRefusal(TradeCodes.BadKind));

        // 反过来：校验器 6 真正会返回的码，都必须在集合里，否则那条规则拒绝时不会记仇。
        TestRunner.True(PersonalityFitValidator.IsPersonalityRefusal(TradeCodes.PersonalityLowEmotion),
            "情绪不足也属于性格拒绝");
        TestRunner.True(PersonalityFitValidator.IsPersonalityRefusal(TradeCodes.PersonalityRefusesLoan),
            "拒绝借钱也属于性格拒绝");
    }
}
