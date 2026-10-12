using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Trade;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 八条校验器：逐条正例 + 反例，外加<b>优先级顺序</b>与<b>软失败</b>。
/// <para>
/// 校验链的顺序是刻意排的（先廉价的结构校验、再昂贵的性格计算），
/// 所以"同时违反多条时返回第一条"本身就是一条必须钉住的契约。
/// </para>
/// </summary>
public static class TradeValidatorTests
{
    public static Action[] Cases =>
    [
        // 第 1 条：结构
        ShapeAcceptsWellFormedIntent,
        ShapeRejectsKindOutsideWhitelist,
        ShapeRejectsBuyKindOnlyForWhitelist,
        ShapeRejectsUnknownDirection,
        ShapeRejectsSameInitiatorAndCounterparty,
        ShapeRejectsEmptyItemId,
        ShapeRejectsNonPositiveQuantity,
        ShapeRejectsUnparsableIntent,

        // 第 2 条：金额边界
        AmountAcceptsInRange,
        AmountRejectsNonPositive,
        AmountRejectsBelowMinimum,
        AmountRejectsAboveMaximum,

        // 第 3 条：目录
        CatalogPassesWhenNoItems,
        CatalogRejectsUnknownItemWithNullCatalog,
        CatalogRejectsPriceMismatch,
        CatalogAcceptsPricedItemAndRejectsOutOfStock,

        // 第 4 条：余额
        BalanceRejectsPlayerWithoutFunds,
        BalanceAcceptsPlayerWithFunds,
        BalanceRejectsNpcWithoutFundsWhenSoftFailDisabled,
        BalanceSoftDeclinesNpcWithoutFundsWhenEnabled,
        BalanceSoftDeclineCarriesInjectLine,

        // 第 5 条：频次
        DailyLimitPassesOnFirstTrade,
        DailyLimitRejectsWhenReached,
        DailyLimitIgnoresOtherDaysAndReversals,

        // 第 6 条 ★ 性格适配
        PersonalityRejectsSmallTipFromStranger,
        PersonalityAcceptsSameTipFromIntimateFavour,
        PersonalityCapDiffersBetweenGenerousAndStingy,
        PersonalityCapRejectsOversizedNpcPayout,
        PersonalityRejectsLowEmotionPayout,
        PersonalityExtremeGapRejectsEvenAtHighFavour,
        PersonalityRejectsLoanWhenBorrowDisabled,
        PersonalityAcceptsLoanWhenBorrowEnabled,
        PersonalityIgnoresNpcToNpc,
        PersonalityRequiresCounterpartyInLedger,

        // 第 7 条：好感
        FavourPassesWhenNoThresholdConfigured,
        FavourRejectsBelowConfiguredThreshold,
        FavourOnlyAppliesToNpcPayout,

        // 第 8 条：幂等
        IdempotencyPassesForFreshIntent,
        IdempotencyRejectsRepeatedIntent,
        IdempotencyIsScopedByDayAndAmount,

        // 优先级
        PriorityShapeBeatsAmountBounds,
        PriorityAmountBeatsPersonality,
        PriorityBalanceBeatsPersonality
    ];

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    private static string? _configDir;

    private static string ConfigDirectory()
    {
        if (_configDir != null) return _configDir;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "config");
            if (File.Exists(Path.Combine(candidate, ConfigLoader.EconomyFileName)))
            {
                _configDir = candidate;
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new Exception("找不到 config/ 目录");
    }

    private static ConfigLoadResult RealConfig() => new ConfigLoader(ConfigDirectory(), Silent).Load();

    private static EconomyConfig Economy() => RealConfig().Economy;
    private static PersonalityConfig Personality() => RealConfig().Personality;
    private static PromptConfig Prompt() => RealConfig().Prompt;

    /// <summary>造一个带档位与性格的 NPC。</summary>
    private static NpcEconomy Npc(string id, long balance, string tier, string kind) => new()
    {
        PersonId = id,
        Balance = balance,
        TierId = tier,
        ClubId = "c00",
        PersonalityKind = kind,
        Season = 1
    };

    private static TradeContext Ctx(NpcEconomy? npc, long playerUsd = 1_000_000, int favour = 0,
        int day = 10, TradeConfig? tradeOverride = null, ITradeCatalog? catalog = null)
    {
        var economy = Economy();
        if (tradeOverride != null) economy.Trade = tradeOverride;

        var world = new EconomyWorld { WorldId = "trade-test", LastSeason = 1, LastTickedDay = day };
        if (npc != null) world.Npcs[npc.PersonId] = npc;

        return new TradeContext
        {
            World = world,
            Config = economy,
            Personality = Personality(),
            Prompt = Prompt(),
            Counterparty = npc == null ? null : new NpcFacts(npc.PersonId, "职业选手", Rating: 1200,
                ClubId: "c00", PersonalityKind: npc.PersonalityKind),
            Favour = favour,
            Day = day,
            Wallet = new InMemoryPlayerWallet(playerUsd),
            Catalog = catalog
        };
    }

    private static TradeIntent Intent(string kind, long amountUsd, string direction,
        string initiator = "", string counterparty = "", IReadOnlyList<TradeItemLine>? items = null,
        string? parseError = null) =>
        new(kind, direction,
            initiator.Length > 0 ? initiator : direction == TradeDirections.PlayerToNpc ? EconomyTier.PlayerId : "npc",
            counterparty.Length > 0 ? counterparty : direction == TradeDirections.PlayerToNpc ? "npc" : EconomyTier.PlayerId,
            amountUsd, items ?? [], "测试", "[Trade: test]", parseError);

    private static TradeIntent Give(long amountUsd, string kind = TradeKinds.Transfer, string? error = null) =>
        Intent(kind, amountUsd, TradeDirections.PlayerToNpc, parseError: error);

    private static TradeIntent PayOut(long amountUsd, string kind = TradeKinds.Sponsor) =>
        Intent(kind, amountUsd, TradeDirections.NpcToPlayer);

    private static TradeCheck Run(ITradeValidator validator, TradeContext ctx, TradeIntent intent) =>
        validator.Validate(ctx, intent);

    // ── 第 1 条：结构 ────────────────────────────────────────────────

    private static void ShapeAcceptsWellFormedIntent()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        TestRunner.True(Run(new IntentShapeValidator(), ctx, Give(5_000)).Ok, "合法意图应通过");
    }

    private static void ShapeRejectsKindOutsideWhitelist()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        var check = Run(new IntentShapeValidator(), ctx, Give(5_000, "行贿"));

        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.BadKind, check.Code);
    }

    private static void ShapeRejectsBuyKindOnlyForWhitelist()
    {
        // 「购买」是协议保留值：它不受 allowedKinds 约束，否则购物通道永远打不开。
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        var intent = Intent(TradeIntent.BuyKind, 3_000, TradeDirections.PlayerToNpc,
            items: [new TradeItemLine { ItemId = "potion", Quantity = 1 }]);

        TestRunner.False(Economy().Trade.AllowedKinds.Contains(TradeIntent.BuyKind), "「购买」不应出现在白名单里");
        var check = Run(new IntentShapeValidator(), ctx, intent);
        TestRunner.True(check.Ok, "「购买」应绕过白名单检查：" + check.Message);
    }

    private static void ShapeRejectsUnknownDirection()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        var check = Run(new IntentShapeValidator(), ctx,
            Intent(TradeKinds.Transfer, 5_000, "npc_to_npc_to_npc", "a", "b"));

        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.BadShape, check.Code);
    }

    private static void ShapeRejectsSameInitiatorAndCounterparty()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        var check = Run(new IntentShapeValidator(), ctx,
            Intent(TradeKinds.Transfer, 5_000, TradeDirections.NpcToNpc, "npc", "npc"));

        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.BadShape, check.Code);
    }

    private static void ShapeRejectsEmptyItemId()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        var check = Run(new IntentShapeValidator(), ctx,
            Intent(TradeIntent.BuyKind, 3_000, TradeDirections.PlayerToNpc,
                items: [new TradeItemLine { ItemId = "", Quantity = 1 }]));

        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.BadShape, check.Code);
    }

    private static void ShapeRejectsNonPositiveQuantity()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        var check = Run(new IntentShapeValidator(), ctx,
            Intent(TradeIntent.BuyKind, 3_000, TradeDirections.PlayerToNpc,
                items: [new TradeItemLine { ItemId = "potion", Quantity = 0 }]));

        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.ItemQuantityInvalid, check.Code);
    }

    private static void ShapeRejectsUnparsableIntent()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        var check = Run(new IntentShapeValidator(), ctx, Give(5_000, error: "标记没有闭合（缺少 ]）。"));

        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.ParseError, check.Code);
    }

    // ── 第 2 条：金额边界 ────────────────────────────────────────────

    private static void AmountAcceptsInRange()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        TestRunner.True(Run(new AmountBoundsValidator(), ctx, Give(50_000)).Ok, "区间内金额应通过");
        TestRunner.True(Run(new AmountBoundsValidator(), ctx, Give(100)).Ok, "恰好等于下限应通过");
    }

    private static void AmountRejectsNonPositive()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        var check = Run(new AmountBoundsValidator(), ctx, Give(0));
        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.AmountTooSmall, check.Code);
    }

    private static void AmountRejectsBelowMinimum()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        var check = Run(new AmountBoundsValidator(), ctx, Give(99));
        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.AmountTooSmall, check.Code);
    }

    private static void AmountRejectsAboveMaximum()
    {
        var ctx = Ctx(Npc("npc", 5_000_000, "elite", "热心同好"));
        var check = Run(new AmountBoundsValidator(), ctx, Give(200_001));
        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.AmountTooLarge, check.Code);
    }

    // ── 第 3 条：目录 ────────────────────────────────────────────────

    private static void CatalogPassesWhenNoItems()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        TestRunner.True(Run(new CatalogValidator(), ctx, Give(5_000)).Ok, "纯转账应直接通过目录校验");
    }

    private static void CatalogRejectsUnknownItemWithNullCatalog()
    {
        // 未接线时是 NullTradeCatalog：任何 ItemId 都解析不出，必须明确拒绝。
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        var intent = Intent(TradeIntent.BuyKind, 3_000, TradeDirections.PlayerToNpc,
            items: [new TradeItemLine { ItemId = "potion_energy", Quantity = 1 }]);

        var check = Run(new CatalogValidator(), ctx, intent);
        TestRunner.False(check.Ok, "未接线时必须拒绝购物，而不是静默放行一笔买不到东西的扣款");
        TestRunner.Equal(TradeCodes.ItemUnknown, check.Code);
    }

    private static void CatalogRejectsPriceMismatch()
    {
        var catalog = new FakeCatalog(new TradeItem { Id = "potion_energy", Name = "能量药水", UnitPriceUsd = 3_000 });
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"), catalog: catalog);
        var intent = Intent(TradeIntent.BuyKind, 1_000, TradeDirections.PlayerToNpc,
            items: [new TradeItemLine { ItemId = "potion_energy", Quantity = 1 }]);

        var check = Run(new CatalogValidator(), ctx, intent);
        TestRunner.False(check.Ok, "报价与目录价不一致必须拒绝，否则 Amount 会成为绕过定价的通道");
        TestRunner.Equal(TradeCodes.ItemPriceMismatch, check.Code);
    }

    private static void CatalogAcceptsPricedItemAndRejectsOutOfStock()
    {
        var catalog = new FakeCatalog(new TradeItem { Id = "potion_energy", Name = "能量药水", UnitPriceUsd = 3_000, Stock = 1 });
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"), catalog: catalog);

        var ok = Intent(TradeIntent.BuyKind, 3_000, TradeDirections.PlayerToNpc,
            items: [new TradeItemLine { ItemId = "potion_energy", Quantity = 1 }]);
        TestRunner.True(Run(new CatalogValidator(), ctx, ok).Ok, "价格与库存都对时应通过");

        var tooMany = Intent(TradeIntent.BuyKind, 6_000, TradeDirections.PlayerToNpc,
            items: [new TradeItemLine { ItemId = "potion_energy", Quantity = 2 }]);
        var check = Run(new CatalogValidator(), ctx, tooMany);
        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.ItemOutOfStock, check.Code);
    }

    // ── 第 4 条：余额 ────────────────────────────────────────────────

    private static void BalanceRejectsPlayerWithoutFunds()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"), playerUsd: 1_000);
        var check = Run(new BalanceValidator(), ctx, Give(50_000));

        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.PlayerInsufficient, check.Code);
        TestRunner.False(check.Soft, "玩家缺钱是硬失败，不该走软失败通道");
    }

    private static void BalanceAcceptsPlayerWithFunds()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"), playerUsd: 100_000);
        TestRunner.True(Run(new BalanceValidator(), ctx, Give(50_000)).Ok, "余额充足应通过");
    }

    private static void BalanceRejectsNpcWithoutFundsWhenSoftFailDisabled()
    {
        var trade = Economy().Trade;
        trade.SoftFailOnBalance = false;
        var ctx = Ctx(Npc("npc", 1_000, "crowd", "热心同好"), tradeOverride: trade);

        var check = Run(new BalanceValidator(), ctx, PayOut(50_000));
        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.NpcInsufficient, check.Code);
        TestRunner.False(check.Soft, "关掉 softFailOnBalance 后应当是硬失败");
    }

    private static void BalanceSoftDeclinesNpcWithoutFundsWhenEnabled()
    {
        var trade = Economy().Trade;
        trade.SoftFailOnBalance = true;
        var ctx = Ctx(Npc("npc", 1_000, "crowd", "热心同好"), tradeOverride: trade);

        var check = Run(new BalanceValidator(), ctx, PayOut(50_000));
        TestRunner.False(check.Ok);
        TestRunner.True(check.Soft, "NPC 余额不足且开关打开时必须走软失败");
        TestRunner.Equal(TradeCodes.NpcSoftDecline, check.Code);
        TestRunner.True(check.Message.Length == 0, "软失败不该带硬报错文案（它不会被显示）");
    }

    private static void BalanceSoftDeclineCarriesInjectLine()
    {
        var trade = Economy().Trade;
        trade.SoftFailOnBalance = true;
        var npc = Npc("npc", 1_000, "crowd", "自嘲找同伴者");
        var ctx = Ctx(npc, tradeOverride: trade);

        var check = Run(new BalanceValidator(), ctx, PayOut(50_000));
        TestRunner.True(check.Soft);
        TestRunner.True(check.InjectLine.Length > 0, "软失败必须带上要注入 Prompt 的素材");
    }

    // ── 第 5 条：频次 ────────────────────────────────────────────────

    private static void DailyLimitPassesOnFirstTrade()
    {
        var npc = Npc("npc", 500_000, "elite", "热心同好");
        var ctx = Ctx(npc);
        TestRunner.True(Run(new DailyLimitValidator(), ctx, Give(5_000)).Ok, "当天还没交易过应通过");
    }

    private static void DailyLimitRejectsWhenReached()
    {
        var npc = Npc("npc", 500_000, "elite", "热心同好");
        var ctx = Ctx(npc);
        var ledger = new EconomyLedger(ctx.World);

        int limit = ctx.Config.Trade.DailyLimitPerNpc;
        for (int i = 0; i < limit; i++)
            ledger.Credit("npc", 1_000, new LedgerEntry
            {
                Day = ctx.Day,
                Season = 1,
                Kind = TradeKinds.Sponsor,
                Title = "第 " + i + " 笔"
            }, "seed:" + i);

        TestRunner.Equal(limit, DailyLimitValidator.CountOn(npc, ctx.Day), "前置：应已成交 limit 次");
        var check = Run(new DailyLimitValidator(), ctx, Give(5_000));
        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.DailyLimit, check.Code);
    }

    private static void DailyLimitIgnoresOtherDaysAndReversals()
    {
        var npc = Npc("npc", 500_000, "elite", "热心同好");
        var ctx = Ctx(npc);
        var ledger = new EconomyLedger(ctx.World);

        // 昨天的交易不该计入今天。
        ledger.Credit("npc", 1_000, new LedgerEntry { Day = ctx.Day - 1, Season = 1, Kind = TradeKinds.Sponsor, Title = "昨天" }, "y:1");
        // 冲正流水不是"一次新交易"。
        ledger.Credit("npc", 1_000, new LedgerEntry { Day = ctx.Day, Season = 1, Kind = EconomyLedger.ReversalKind, Title = "冲正" }, "r:1");

        TestRunner.Equal(0, DailyLimitValidator.CountOn(npc, ctx.Day), "昨天与冲正都不该计入当天次数");
        TestRunner.True(Run(new DailyLimitValidator(), ctx, Give(5_000)).Ok);
    }

    // ── 第 6 条 ★ 性格适配 ──────────────────────────────────────────

    /// <summary>
    /// 验收清单的原话：<c>战绩踩头者</c>（charity 0.82）对陌生玩家的小额打赏被拒。
    /// 计算过程：balance 50000 → cap = 50000 × 0.50 × 0.65 × 0.78 = 12675；
    /// floor = 12675 × 0.08 × (0.5 + 0.82) = 1338；打赏 1000 &lt; 1338 → 拒。
    /// </summary>
    private static void PersonalityRejectsSmallTipFromStranger()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "战绩踩头者"), favour: 0);
        var check = Run(new PersonalityFitValidator(), ctx, Give(1_000, TradeKinds.Tip));

        TestRunner.False(check.Ok, "骄傲的人被小额打赏冒犯时应拒绝");
        TestRunner.Equal(TradeCodes.PersonalityRefusesCharity, check.Code);
        TestRunner.True(check.Message.Length > 0, "拒绝时应带上该性格的台词");
    }

    /// <summary>
    /// 同一金额、同一性格，但好感 90（×0.10）：floor = 12675 × 0.08 × (0.5 + 0.082) = 590。
    /// 打赏 1000 ≥ 590 → 通过。<b>感情只削弱 charitySensitivity 一个轴。</b>
    /// </summary>
    private static void PersonalityAcceptsSameTipFromIntimateFavour()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "战绩踩头者"), favour: 90);
        var check = Run(new PersonalityFitValidator(), ctx, Give(1_000, TradeKinds.Tip));

        TestRunner.True(check.Ok,
            "对交情玩家（favour 90）同样金额应通过，实际被拒：" + check.Message);
    }

    private static void PersonalityCapDiffersBetweenGenerousAndStingy()
    {
        // 同一余额、同一档位，只有性格不同。
        var generous = Npc("g", 100_000, "professional", "热心同好");
        var stingy = Npc("s", 100_000, "professional", "尖刻挑刺者");

        var capGenerous = PersonalityEconomy.SpendCap(Economy(), "professional", generous.Balance,
            PersonalityEconomy.ProfileFor(Personality(), generous.PersonalityKind));
        var capStingy = PersonalityEconomy.SpendCap(Economy(), "professional", stingy.Balance,
            PersonalityEconomy.ProfileFor(Personality(), stingy.PersonalityKind));

        TestRunner.True(capGenerous > capStingy,
            $"慷慨性格的上限应高于刻薄性格：{capGenerous} vs {capStingy}");

        // 上限直接决定了"给不起"：同一笔钱，刻薄的人会以 PERSONALITY_CAP 拒掉。
        // 注意发起方 id 必须与账本里的键一致，否则会被判成"对手方不存在"。
        var amount = (capGenerous + capStingy) / 2;
        var ctx = Ctx(stingy);
        var check = Run(new PersonalityFitValidator(), ctx,
            Intent(TradeKinds.Sponsor, amount, TradeDirections.NpcToPlayer, initiator: "s"));

        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.PersonalityCap, check.Code);
        TestRunner.True(check.Message.Length > 0, "议价拒绝必须带上一句台词");
    }

    private static void PersonalityCapRejectsOversizedNpcPayout()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        var check = Run(new PersonalityFitValidator(), ctx, PayOut(100_000));

        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.PersonalityCap, check.Code);

        // 议价台词里的 {suggested} 必须被替换成"建议金额"，且金额走 MoneyUnits 渲染。
        // 配置里目前只有「故意挑事者」那条带占位符，所以用它来验真实契约。
        string rendered = PersonalityEconomy.CapLine(ctx.Personality, "故意挑事者", 12_345);
        TestRunner.NotContains(rendered, "{suggested}", "占位符必须被替换掉");
        TestRunner.Contains(rendered, MoneyUnits.Format(12_345), "替换进去的应当是渲染好的金额");
        TestRunner.Contains(rendered, "$", "金额应带货币符号");
    }

    private static void PersonalityRejectsLowEmotionPayout()
    {
        // 情绪值 = amount / max(500, balance × 0.10)。余额 50 万时基准是 5 万，
        // 给 1 万的情绪值只有 0.2，远低于默认门槛 1.0。
        var ctx = Ctx(Npc("npc", 500_000, "elite", "热心同好"));
        var check = Run(new PersonalityFitValidator(), ctx, PayOut(10_000));

        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.PersonalityLowEmotion, check.Code);
    }

    /// <summary>
    /// 高好感 ≠ 来者不拒。交情档（倍率 ≤ 0.10）下，小到离谱的金额仍然会被拒。
    /// 这里把极端差距阈值放大，好让它稳定触发：
    /// 金额 1 万 &lt; 余额 50 万 × 0.5 = 25 万。
    /// </summary>
    private static void PersonalityExtremeGapRejectsEvenAtHighFavour()
    {
        var economy = Economy();
        economy.Season.ExtremeGapRatio = 0.5;

        var npc = Npc("npc", 500_000, "elite", "热心同好");
        var ctx = Ctx(npc, favour: 90);
        var check = Run(new PersonalityFitValidator(), ctx, Give(10_000, TradeKinds.Transfer));

        TestRunner.False(check.Ok, "极端差距在交情档下也必须被拒");
        TestRunner.Equal(TradeCodes.PersonalityRefusesCharity, check.Code);
    }

    private static void PersonalityRejectsLoanWhenBorrowDisabled()
    {
        // 「热心同好」的 borrow 是 false。
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        var check = Run(new PersonalityFitValidator(), ctx, Give(5_000, TradeKinds.Loan));

        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.PersonalityRefusesLoan, check.Code);
    }

    private static void PersonalityAcceptsLoanWhenBorrowEnabled()
    {
        // 「自嘲找同伴者」的 borrow 是 true。
        var ctx = Ctx(Npc("npc", 50_000, "professional", "自嘲找同伴者"));
        var check = Run(new PersonalityFitValidator(), ctx, Give(5_000, TradeKinds.Loan));

        TestRunner.True(check.Ok, "开了 borrow 的性格应当允许借钱：" + check.Message);
    }

    private static void PersonalityIgnoresNpcToNpc()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "尖刻挑刺者"));
        ctx.World.Npcs["other"] = Npc("other", 10, "crowd", "暴躁喷子");

        var check = Run(new PersonalityFitValidator(), ctx,
            Intent(TradeKinds.Transfer, 50_000, TradeDirections.NpcToNpc, "npc", "other"));

        TestRunner.True(check.Ok, "NPC 之间的交易不走「对玩家的态度」这套适配");
    }

    private static void PersonalityRequiresCounterpartyInLedger()
    {
        var ctx = Ctx(null);
        var check = Run(new PersonalityFitValidator(), ctx, Give(5_000, TradeKinds.Tip));

        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.NoCounterparty, check.Code);
    }

    // ── 第 7 条：好感 ────────────────────────────────────────────────

    private static void FavourPassesWhenNoThresholdConfigured()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"), favour: 0);
        TestRunner.Equal(0, ctx.Config.Trade.RequireFavourForNpcPayout, "默认门槛应为 0");
        TestRunner.True(Run(new FavourValidator(), ctx, PayOut(5_000)).Ok, "默认不该设好感门槛");
    }

    private static void FavourRejectsBelowConfiguredThreshold()
    {
        var trade = Economy().Trade;
        trade.RequireFavourForNpcPayout = 50;
        var ctx = Ctx(Npc("npc", 500_000, "elite", "热心同好"), favour: 10, tradeOverride: trade);

        var check = Run(new FavourValidator(), ctx, PayOut(5_000));
        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.FavourTooLow, check.Code);
    }

    private static void FavourOnlyAppliesToNpcPayout()
    {
        var trade = Economy().Trade;
        trade.RequireFavourForNpcPayout = 50;
        var ctx = Ctx(Npc("npc", 500_000, "elite", "热心同好"), favour: 0, tradeOverride: trade);

        TestRunner.True(Run(new FavourValidator(), ctx, Give(5_000)).Ok,
            "玩家给钱不该被好感门槛拦住");
    }

    // ── 第 8 条：幂等 ────────────────────────────────────────────────

    private static void IdempotencyPassesForFreshIntent()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        TestRunner.True(Run(new IdempotencyValidator(), ctx, Give(5_000)).Ok, "全新意图应通过");
    }

    private static void IdempotencyRejectsRepeatedIntent()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        var intent = Give(5_000);

        var ledger = new EconomyLedger(ctx.World);
        ledger.MarkReceipt(IdempotencyValidator.IntentKey(ledger, intent, ctx.Day));
        ledger.FlushReceipts();

        var check = Run(new IdempotencyValidator(), ctx, intent);
        TestRunner.False(check.Ok);
        TestRunner.Equal(TradeCodes.AlreadyApplied, check.Code);
    }

    private static void IdempotencyIsScopedByDayAndAmount()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        var ledger = new EconomyLedger(ctx.World);
        ledger.MarkReceipt(IdempotencyValidator.IntentKey(ledger, Give(5_000), ctx.Day));
        ledger.FlushReceipts();

        TestRunner.True(Run(new IdempotencyValidator(), ctx, Give(6_000)).Ok, "金额不同就是另一笔");
        TestRunner.True(Run(new IdempotencyValidator(), ctx, Give(5_000)).Ok == false, "同金额同天应被拒");

        var nextDay = Ctx(Npc("npc", 50_000, "professional", "热心同好"), day: ctx.Day + 1);
        nextDay.World.Receipts = ctx.World.Receipts;
        TestRunner.True(Run(new IdempotencyValidator(), nextDay, Give(5_000)).Ok, "换了天就是另一笔");
    }

    // ── 优先级顺序 ──────────────────────────────────────────────────

    /// <summary>同时违反"结构"与"金额"时，必须返回结构错误（链上更靠前）。</summary>
    private static void PriorityShapeBeatsAmountBounds()
    {
        var ctx = Ctx(Npc("npc", 50_000, "professional", "热心同好"));
        var engine = new TradeEngine(ctx.Config, ctx.Personality, ctx.Prompt, Silent);

        // 既超上限、Kind 也不在白名单。
        var check = engine.RunValidators(ctx, Give(999_999, "行贿"));
        TestRunner.Equal(TradeCodes.BadKind, check.Code, "结构校验应排在金额之前");
    }

    /// <summary>同时违反"金额上限"与"性格上限"时，必须返回金额错误。</summary>
    private static void PriorityAmountBeatsPersonality()
    {
        var ctx = Ctx(Npc("npc", 500_000, "elite", "尖刻挑刺者"));
        var engine = new TradeEngine(ctx.Config, ctx.Personality, ctx.Prompt, Silent);

        // 200001 既超 maxSingleAmount(200000)，也超性格上限。
        var check = engine.RunValidators(ctx, PayOut(200_001));
        TestRunner.Equal(TradeCodes.AmountTooLarge, check.Code, "金额边界应排在性格适配之前");
    }

    /// <summary>同时"余额不足"与"性格会拒"时，必须返回余额错误。</summary>
    private static void PriorityBalanceBeatsPersonality()
    {
        var ctx = Ctx(Npc("npc", 1_000, "crowd", "热心同好"));
        var engine = new TradeEngine(ctx.Config, ctx.Personality, ctx.Prompt, Silent);

        // 给 5 万：既超余额，也超性格上限。
        var check = engine.RunValidators(ctx, PayOut(50_000));
        TestRunner.Equal(TradeCodes.NpcSoftDecline, check.Code, "余额校验应排在性格适配之前");
    }

    /// <summary>测试用的固定目录。</summary>
    private sealed class FakeCatalog(params TradeItem[] items) : ITradeCatalog
    {
        private readonly Dictionary<string, TradeItem> _items =
            items.ToDictionary(i => i.Id, StringComparer.Ordinal);

        public IReadOnlyList<TradeItem> All => _items.Values.ToArray();

        public bool TryResolveItem(string itemId, out TradeItem item) => _items.TryGetValue(itemId, out item!);
    }
}
