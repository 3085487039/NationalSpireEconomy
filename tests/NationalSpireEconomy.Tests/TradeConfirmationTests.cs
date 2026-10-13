// 阶段 4 第一步：只读成交预览 + 双人确认。零依赖（Core/Economy/Trade 三层），默认配置下也跑。
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Trade;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// <b>只读成交预览</b>的自证。核心契约只有一条：
/// <b>预览不得改动任何余额、任何流水、任何收据</b>——而且它"说会发生什么"必须与真跑一遍的结果一致。
/// </summary>
public static class TradePreviewTests
{
    public static Action[] Cases =>
    [
        PreviewIsReadOnly,
        PreviewDeltasMatchRealExecution,
        PreviewReportsBlockingValidator,
        PreviewTextCarriesNumbersAndIsNotStableSegment,
        PreviewUsesDisplayNameAndHasNoDuplicateRow,
        PreviewSaysWhoPaysInPlainLanguage
    ];

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    /// <summary>预览前后必须逐项一致：余额、流水条数、收据条数、交易单条数。</summary>
    private static void PreviewIsReadOnly()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc);
        var engine = Engine();

        // 先落一张单（Reserve 本身也不动余额），再对"意图"和"交易单"各预览一次。
        var submit = engine.Submit(ctx, Give(50_000));
        TestRunner.True(submit.Ok, "前置：应能落单：" + submit.Check.Message);
        var offer = ctx.World.Offers[submit.OfferId!];

        var snapshot = Snapshot(ctx);

        var byIntent = TradePreviewBuilder.ForIntent(ctx, Give(50_000), engine);
        var byOffer = TradePreviewBuilder.ForOffer(ctx, offer, engine);

        TestRunner.False(byIntent.MutatesLedger, "预览的只读契约必须恒为 false");
        TestRunner.False(byOffer.MutatesLedger, "预览的只读契约必须恒为 false");
        TestRunner.Equal(snapshot, Snapshot(ctx), "预览绝不允许改动余额/流水/收据/交易单");

        TestRunner.True(byOffer.OfferExists, "对已落单的交易单预览应报 OfferExists=true");
        TestRunner.True(byOffer.OfferPending, "刚落的单应是 pending");
        Console.WriteLine("      · " + byOffer.ReadOnlyText.Split('\n')[0].Trim());
    }

    /// <summary>
    /// <b>预览不能撒谎</b>：预览里算出的双方 delta，必须等于真跑一遍（Submit + Confirm）之后的余额差。
    /// 这条把"预览的算法"与"执行器的算法"钉在一起，防止两边各自漂移。
    /// </summary>
    private static void PreviewDeltasMatchRealExecution()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc);
        var engine = Engine();

        long npcBefore = npc.Balance;
        long playerBefore = ctx.Wallet.BalanceUsd;

        var preview = TradePreviewBuilder.ForIntent(ctx, Give(50_000), engine);
        TestRunner.True(preview.WouldSucceed, "前置：这笔应当可成交");

        var submit = engine.Submit(ctx, Give(50_000));
        TestRunner.True(submit.Ok, "前置：应能落单");
        var commit = engine.Confirm(ctx, submit.OfferId!);
        TestRunner.True(commit.Ok, "前置：应能执行：" + commit.Check.Message);

        long npcDelta = npc.Balance - npcBefore;
        long playerDelta = ctx.Wallet.BalanceUsd - playerBefore;

        TestRunner.Equal(preview.Counterparty.DeltaUsd, npcDelta,
            "预览给 NPC 的 delta 必须等于真跑一遍的结果");
        TestRunner.Equal(preview.Player.DeltaUsd, playerDelta,
            "预览给玩家的 delta 必须等于真跑一遍的结果");
        Console.WriteLine($"      · 预览 NPC {preview.Counterparty.DeltaUsd:+0;-0} / 玩家 {preview.Player.DeltaUsd:+0;-0}" +
                          $"｜实跑 NPC {npcDelta:+0;-0} / 玩家 {playerDelta:+0;-0}");
    }

    /// <summary>被校验链挡下时必须如实报出是哪一条、什么码——预览的价值就在这里。</summary>
    private static void PreviewReportsBlockingValidator()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc);

        var preview = TradePreviewBuilder.ForIntent(ctx, Give(999_999_999), Engine());

        TestRunner.False(preview.WouldSucceed, "超出金额上限的意图必须被预览判为不可成交");
        TestRunner.Equal(TradeCodes.AmountTooLarge, preview.BlockingCode!,
            "应指出是金额上限那条：" + preview.BlockingCode);
        TestRunner.True(preview.Checks.Any(c => !c.Ok), "未通过的校验项必须留在清单里（供界面显示）");
        Console.WriteLine($"      · 阻断码 {preview.BlockingCode}｜{preview.BlockingMessage}");
    }

    /// <summary>
    /// 预览文案里一定有金额（数字），所以<b>只能</b>进 V 段（易变段）或日志；
    /// 这条钉住"它不含稳定段标题"，避免以后有人顺手把它塞进 S 段。
    /// </summary>
    private static void PreviewTextCarriesNumbersAndIsNotStableSegment()
    {
        var npc = Npc("npc", 50_000);
        var ctx = Ctx(npc);
        var preview = TradePreviewBuilder.ForIntent(ctx, Give(50_000), Engine());

        TestRunner.Contains(preview.ReadOnlyText, "成交预览", "文案要有可检索的标题");
        TestRunner.Contains(preview.ReadOnlyText, "$", "文案要给出金额（因此只能进 V 段）");
        // 2026-10-12：预览文案人话化时把「幂等键」改成了「单号」（与回执/未成立的措辞统一）。
        // 这条断言的本意是"文案要能对上账"，单号同样承担这个职责。
        TestRunner.Contains(preview.ReadOnlyText, "单号", "文案要给出单号，便于对账");
        TestRunner.NotContains(preview.ReadOnlyText, "【经济状况】", "绝不能用稳定段的标题（那是零数字段）");
        TestRunner.Contains(preview.ReadOnlyText, "不动任何余额", "文案本身要声明只读");
    }

    /// <summary>
    /// <b>★ 2026-10-12 真机截图暴露的观感问题的回归：预览必须用「人话 + 人名」，不能是内部 id 拼盘。</b>
    /// <para>
    /// 真机原文案长这样（玩家截图里就是它）：
    /// </para>
    /// <code>【成交预览·只读】雇佣（你 → NPC）｜交易单 o0003
    ///   发起方：player｜对手方：pro2_5
    ///   player：$196,820 → $193,820（−$3,000）（发起方）
    ///   pro2_5：$821,000 → $824,000（+$3,000）（对手方）
    ///   player：$196,820 → $193,820（−$3,000）（你）      ← 玩家那行出现了【两次】</code>
    /// <para>
    /// 玩家的原话是"格式太过生疏死板"。修法有两条，都要钉住：
    /// <b>① 用原版显示名</b>（「酸橙汽水」而不是 <c>pro2_5</c>）；
    /// <b>② 每人只占一行</b>（去掉那个把玩家列两遍的"发起方/对手方 + 你"三行结构）。
    /// </para>
    /// </summary>
    private static void PreviewUsesDisplayNameAndHasNoDuplicateRow()
    {
        var npc = Npc("pro2_5", 800_000);
        var ctx = Ctx(npc, npcName: "酸橙汽水");
        var preview = TradePreviewBuilder.ForIntent(ctx, Give(3_000, "pro2_5"), Engine());
        string text = preview.ReadOnlyText;

        TestRunner.Contains(text, "酸橙汽水", "★要用原版显示名，而不是内部 id");
        TestRunner.NotContains(text, "pro2_5", "★正文里不该再出现内部 id（它看着像调试输出）");
        TestRunner.Contains(text, "你：", "玩家一行应以「你」开头");

        // 玩家只能占一行：数一数"你："出现了几次。
        int youRows = text.Split('\n').Count(l => l.Contains("你：", StringComparison.Ordinal));
        TestRunner.Equal(1, youRows, "★玩家那一行只能出现一次（真机上是两次）");

        // 也不能再出现"发起方/对手方"那种 id 拼盘。
        TestRunner.NotContains(text, "发起方：", "不该再用「发起方：<id>」的写法");
        TestRunner.NotContains(text, "对手方：", "不该再用「对手方：<id>」的写法");
    }

    /// <summary>
    /// <b>预览要用大白话说清"谁付钱"</b>，而不是让玩家自己从余额增减里推断方向。
    /// 两个方向都要说对；<c>你自己出钱</c>那条尤其不能写成"你将收到"。
    /// </summary>
    private static void PreviewSaysWhoPaysInPlainLanguage()
    {
        var npc = Npc("npc", 800_000);

        // 玩家出钱：标题应写「你将支付」。
        string playerPays = TradePreviewBuilder
            .ForIntent(Ctx(npc, npcName: "酸橙汽水"), Give(3_000), Engine()).ReadOnlyText;
        TestRunner.Contains(playerPays, "你将支付", "★玩家出钱时必须写「你将支付」");
        TestRunner.Contains(playerPays, "$3,000", "金额要出现在标题行");
        TestRunner.NotContains(playerPays, "将支付 $0", "不能把金额算成 0");

        // NPC 出钱：标题应写「<人名> 将支付」。
        string npcPays = TradePreviewBuilder
            .ForIntent(Ctx(npc, npcName: "酸橙汽水"), PayOut(500), Engine()).ReadOnlyText;
        TestRunner.Contains(npcPays, "酸橙汽水 将支付", "★NPC 出钱时必须点名是谁付");
        TestRunner.NotContains(npcPays, "你将支付", "NPC 出钱时不能说成玩家付");

        // 「幂等键」这个内部术语已经换成「单号」，两处都不该再出现旧词。
        TestRunner.NotContains(playerPays, "幂等键", "对外文案不该出现「幂等键」这类内部术语");
    }

    // ── 脚手架（与 TradeEngineTests 同风格，但保持本套件自足）────────

    private static ConfigLoadResult RealConfig()
    {
        return new ConfigLoader(TestConfig.Directory(), Silent).Load();
    }

    private static TradeEngine Engine() => new(RealConfig().Economy, RealConfig().Personality,
        RealConfig().Prompt, Silent);

    private static TradeContext Ctx(NpcEconomy npc, long playerUsd = 1_000_000, int day = 10,
        string npcName = "") => new()
    {
        World = WorldWith(npc),
        Config = RealConfig().Economy,
        Personality = RealConfig().Personality,
        Prompt = RealConfig().Prompt,
        Counterparty = new NpcFacts(npc.PersonId, "职业选手", Rating: 1200, ClubId: "c00",
            PersonalityKind: npc.PersonalityKind, Name: npcName),
        Favour = 0,
        Day = day,
        Wallet = new InMemoryPlayerWallet(playerUsd)
    };

    private static EconomyWorld WorldWith(params NpcEconomy[] npcs)
    {
        var world = new EconomyWorld { WorldId = "preview-test", LastSeason = 1, LastTickedDay = 1 };
        foreach (var npc in npcs) world.Npcs[npc.PersonId] = npc;
        return world;
    }

    internal static NpcEconomy Npc(string id, long balance, string kind = "热心同好", string tier = "professional") =>
        new()
        {
            PersonId = id,
            Balance = balance,
            TierId = tier,
            ClubId = "c00",
            PersonalityKind = kind,
            Season = 1
        };

    internal static TradeIntent Give(long amountUsd) => Give(amountUsd, "npc");

    internal static TradeIntent Give(long amountUsd, string npcId) =>
        new(TradeKinds.Transfer, TradeDirections.PlayerToNpc, EconomyTier.PlayerId, npcId, amountUsd, [],
            "测试", "[Trade: test]");

    internal static TradeIntent PayOut(long amountUsd) =>
        new(TradeKinds.Sponsor, TradeDirections.NpcToPlayer, "npc", EconomyTier.PlayerId, amountUsd, [],
            "测试", "[Trade: test]");

    /// <summary>把"钱与账"的全部可观测量压成一个字符串，用于前后比对。</summary>
    internal static string Snapshot(TradeContext ctx)
    {
        var parts = new List<string> { "wallet=" + ctx.Wallet.BalanceUsd, "offers=" + ctx.World.Offers.Count,
            "receipts=" + ctx.World.Receipts.Count };
        foreach (var (id, npc) in ctx.World.Npcs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            parts.Add($"{id}:{npc.Balance}:{npc.Ledger.Count}:{npc.LastSalaryPaidSeason}");
        return string.Join("|", parts);
    }

    /// <summary>
    /// 只含<b>钱</b>的口径：余额与流水条数（<b>不含收据、不含交易单</b>）。
    /// <para>
    /// 双人确认必须用这个口径：收据是幂等记录，确认这件事本来就要写它；
    /// 而"不动钱"指的是余额与流水一字不变。
    /// </para>
    /// </summary>
    internal static string BalanceSnapshot(TradeContext ctx)
    {
        var parts = new List<string> { "wallet=" + ctx.Wallet.BalanceUsd };
        foreach (var (id, npc) in ctx.World.Npcs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            parts.Add($"{id}:{npc.Balance}:{npc.Ledger.Count}:{npc.LastSalaryPaidSeason}");
        return string.Join("|", parts);
    }

    /// <summary>
    /// 只含<b>钱</b>的口径：余额、流水条数、交易单条数（<b>不含收据</b>）。
    /// <para>
    /// 双人确认必须用这个口径：收据是幂等记录，确认这件事本来就要写它；
    /// 而"不动钱"指的是余额与流水一字不变。
    /// </para>
    /// </summary>
    internal static string MoneySnapshot(TradeContext ctx)
    {
        var parts = new List<string> { "wallet=" + ctx.Wallet.BalanceUsd, "offers=" + ctx.World.Offers.Count };
        foreach (var (id, npc) in ctx.World.Npcs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            parts.Add($"{id}:{npc.Balance}:{npc.Ledger.Count}:{npc.LastSalaryPaidSeason}");
        return string.Join("|", parts);
    }
}

/// <summary>
/// <b>双人确认闸门</b>的自证：两边各一票、重复点击幂等、<b>全程不动钱</b>，
/// 且"双方都确认"之后也只到"待结算"为止（结算属于阶段 4 第二步）。
/// </summary>
public static class TradeConfirmationTests
{
    public static Action[] Cases =>
    [
        ConfirmIsIdempotentPerSide,
        BothSidesReadyToSettleWithoutMovingMoney,
        PlayerConfirmDoesNotCountAsNpc,
        ConfirmRefusesExpiredOffer,
        DeclineStopsTheDeal,
        SettlementIsDeliberatelyNotWired,
        WithSettlementRestoresTheSwitchEvenWhenTheBodyThrows
    ];

    private static void ConfirmIsIdempotentPerSide()
    {
        var ctx = PendingOffer(out string offerId);
        var gate = TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Npc, "私信里答应");
        TestRunner.True(gate.NpcAccepted, "NPC 确认后应记为已确认");
        TestRunner.False(gate.PlayerConfirmed, "玩家那一票不能被 NPC 的确认顶掉");

        int receiptsAfterFirst = ctx.World.Receipts.Count;
        var again = TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Npc, "重复点击");
        TestRunner.True(again.NpcAccepted, "重复确认仍应是已确认状态");
        TestRunner.Equal(receiptsAfterFirst, ctx.World.Receipts.Count,
            "重复确认不能重复登记收据（幂等键必须挡住）");
        TestRunner.Contains(again.DetailText, "重复确认", "第二次必须如实说明是按幂等忽略的");
    }

    private static void BothSidesReadyToSettleWithoutMovingMoney()
    {
        var ctx = PendingOffer(out string offerId);
        // 注意用「钱」的快照：收据是幂等记录，确认本来就该写它；余额与流水必须一字不变。
        string before = TradePreviewTests.MoneySnapshot(ctx);

        TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Npc, "私信里答应");
        var status = TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "界面点击");

        TestRunner.True(status.BothConfirmed, "两边各一票之后应记为双方已确认");
        TestRunner.True(status.ReadyToSettle, "双方确认后应进入「待结算」");
        TestRunner.False(status.MutatesLedger, "确认闸门恒不动账");
        TestRunner.Equal(before, TradePreviewTests.MoneySnapshot(ctx),
            "★双人确认全程不得改动任何余额与流水（收据是幂等记录，不计入「钱」的口径）");
        TestRunner.Contains(status.DetailText, "待结算", "状态文案要说明已到「待结算」");
        Console.WriteLine("      · " + status.DetailText);
    }

    private static void PlayerConfirmDoesNotCountAsNpc()
    {
        var ctx = PendingOffer(out string offerId);
        var status = TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player);
        TestRunner.True(status.PlayerConfirmed, "玩家确认应记上");
        TestRunner.False(status.NpcAccepted, "NPC 还没答应，不能算双方已确认");
        TestRunner.False(status.ReadyToSettle, "只差一方时不能进入待结算");
        TestRunner.Equal(TradeConfirmationGate.ConfirmPendingCode, status.BlockingCode!,
            "应报「还差一方确认」");
    }

    private static void ConfirmRefusesExpiredOffer()
    {
        var npc = TradePreviewTests.Npc("npc", 50_000);
        var ctx = Ctx(npc, day: 10);
        var engine = Engine();
        var submit = engine.Submit(ctx, TradePreviewTests.PayOut(10_000));
        TestRunner.True(submit.Ok, "前置：应能落单");

        // 把当天推到过期之后再看。
        ctx = Ctx(npc, day: 10 + engine.Config.Trade.OfferExpiryDays + 1, world: ctx.World);
        var status = TradeConfirmationGate.Confirm(ctx, submit.OfferId!, TradeConfirmationSide.Npc);

        TestRunner.Equal(TradeCodes.OfferExpired, status.BlockingCode!, "过期单必须被拒绝确认");
        TestRunner.False(status.ReadyToSettle);
    }

    private static void DeclineStopsTheDeal()
    {
        var ctx = PendingOffer(out string offerId);
        var status = TradeConfirmationGate.Decline(ctx, offerId, TradeConfirmationSide.Npc, "我今天不想掏钱");

        TestRunner.Equal(TradeOfferStates.Declined, status.State, "拒绝后交易单应落到 declined");
        TestRunner.False(status.ReadyToSettle, "被拒的交易绝不能进入待结算");
        TestRunner.Contains(status.DetailText, "拒绝", "状态文案要说清是被拒");
    }

    private static void SettlementIsDeliberatelyNotWired()
    {
        TestRunner.False(TradeConfirmationGate.SettlementWired,
            "阶段 4 第一步不得接线结算（动钱）；第二步才翻这个开关");
        TestRunner.Contains(TradeConfirmationGate.SettlementIsNextPhase, "第二步",
            "说明里要写清结算属于第二步");
    }

    /// <summary>
    /// <see cref="TradeConfirmationGate.WithSettlement"/> 必须在离开作用域时<b>无条件</b>还原，
    /// 包括作用域里抛异常的那条路径。
    /// <para>
    /// 为什么值得单独钉一条：<c>SettlementWired</c> 是<b>进程级</b>静态量，一旦泄漏，
    /// 同进程里后续每一个用例都会在"钱可以动"的状态下运行 —— 那是本项目最贵的副作用。
    /// 用 <c>using</c> 把这个纪律交给编译器，而不是交给"我记得写 finally"。
    /// </para>
    /// </summary>
    private static void WithSettlementRestoresTheSwitchEvenWhenTheBodyThrows()
    {
        TestRunner.False(TradeConfirmationGate.SettlementWired,
            "前置：进入用例时开关应当是关的（默认值）");

        using (TradeConfirmationGate.WithSettlement(true))
        {
            TestRunner.True(TradeConfirmationGate.SettlementWired, "作用域内应当是开着的");
        }

        TestRunner.False(TradeConfirmationGate.SettlementWired, "★离开作用域必须还原成关");

        // 异常路径：using 的 Dispose 仍然要跑。
        try
        {
            using (TradeConfirmationGate.WithSettlement(true))
            {
                TestRunner.True(TradeConfirmationGate.SettlementWired, "作用域内应当是开着的");
                throw new InvalidOperationException("故意抛，模拟断言失败/中途异常");
            }
        }
        catch (InvalidOperationException)
        {
            // 预期之内。
        }

        TestRunner.False(TradeConfirmationGate.SettlementWired,
            "★作用域里抛异常时也必须还原（否则一次失败的用例会污染后面所有用例）");

        // 嵌套时应当逐层还原到进入前那一层的值，而不是一律还原成 false。
        using (TradeConfirmationGate.WithSettlement(true))
        {
            using (TradeConfirmationGate.WithSettlement(false))
                TestRunner.False(TradeConfirmationGate.SettlementWired, "内层应当临时关掉");

            TestRunner.True(TradeConfirmationGate.SettlementWired, "★离开内层应当还原成外层那一层的值");
        }

        TestRunner.False(TradeConfirmationGate.SettlementWired, "最外层离开后回到默认");
    }

    // ── 脚手架 ───────────────────────────────────────────────────────

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    private static ConfigLoadResult RealConfig()
    {
        return new ConfigLoader(TestConfig.Directory(), Silent).Load();
    }

    private static TradeEngine Engine() => new(RealConfig().Economy, RealConfig().Personality,
        RealConfig().Prompt, Silent);

    private static TradeContext Ctx(NpcEconomy npc, long playerUsd = 1_000_000, int day = 10,
        EconomyWorld? world = null) => new()
    {
        World = world ?? WorldWith(npc),
        Config = RealConfig().Economy,
        Personality = RealConfig().Personality,
        Prompt = RealConfig().Prompt,
        Counterparty = new NpcFacts(npc.PersonId, "职业选手", Rating: 1200, ClubId: "c00",
            PersonalityKind: npc.PersonalityKind),
        Favour = 0,
        Day = day,
        Wallet = new InMemoryPlayerWallet(playerUsd)
    };

    private static EconomyWorld WorldWith(params NpcEconomy[] npcs)
    {
        var world = new EconomyWorld { WorldId = "confirm-test", LastSeason = 1, LastTickedDay = 1 };
        foreach (var npc in npcs) world.Npcs[npc.PersonId] = npc;
        return world;
    }

    /// <summary>造一张已落单、还没人确认的交易单。</summary>
    private static TradeContext PendingOffer(out string offerId)
    {
        var npc = TradePreviewTests.Npc("npc", 50_000);
        var ctx = Ctx(npc);
        var submit = Engine().Submit(ctx, TradePreviewTests.PayOut(10_000));
        TestRunner.True(submit.Ok, "前置：应能落单：" + submit.Check.Message);
        offerId = submit.OfferId!;
        TestRunner.True(TradeOfferStates.IsPending(ctx.World.Offers[offerId].State), "前置：应为 pending");
        return ctx;
    }
}

/// <summary>
/// <b>意图补全</b>的自证：协议层不猜方向与双方，这层必须把它们补出来（或明确拒绝）。
/// </summary>
public static class TradeIntentCompleterTests
{
    public static Action[] Cases =>
    [
        MapsKindToDirection,
        ExplicitDirectionWins,
        UnknownKindIsRefused,
        NpcToNpcIsRefused,
        EchoOfPlayerTextIsDetected
    ];

    private const string Npc = "tour-c00-5";
    private const string Player = "player";

    private static TradeIntent Raw(string kind, string direction = "",
        string raw = "[Trade: 赞助, Amount: 5000]") =>
        new(kind, direction, "", "", 5000, [], "", raw);

    /// <summary>没写方向时按配置映射补出来，并把双方补成 (NPC, 玩家)。</summary>
    private static void MapsKindToDirection()
    {
        var (intent, reason) = TradeIntentCompleter.Complete(Raw("赞助"), Npc, Player, new TradeConfig());

        TestRunner.True(intent != null, "赞助应能补全，实际原因：" + reason);
        TestRunner.Equal(TradeDirections.NpcToPlayer, intent!.Direction, "赞助应是 NPC → 玩家");
        TestRunner.Equal(Npc, intent.InitiatorId, "发起方应是 NPC");
        TestRunner.Equal(Player, intent.CounterpartyId, "对手方应是玩家");
        TestRunner.Equal("赞助", intent.Kind, "补全不能改动 Kind");
        TestRunner.Equal(5000L, intent.AmountUsd, "补全不能改动金额");
    }

    /// <summary>标记里显式写了方向时，<b>必须以标记为准</b>（配置只是兜底）。</summary>
    private static void ExplicitDirectionWins()
    {
        var (intent, reason) = TradeIntentCompleter.Complete(Raw("赞助", "playerToNpc"), Npc, Player,
            new TradeConfig());

        TestRunner.True(intent != null, "应能补全：" + reason);
        TestRunner.Equal(TradeDirections.PlayerToNpc, intent!.Direction, "显式方向必须覆盖配置映射");
        TestRunner.Equal(Player, intent.InitiatorId, "方向反了，发起方也该跟着反转");
        TestRunner.Equal(Npc, intent.CounterpartyId, "方向反了，对手方也该跟着反转");
    }

    /// <summary>没配到方向的 Kind 必须<b>拒绝</b>——绝不猜钱的方向。</summary>
    private static void UnknownKindIsRefused()
    {
        var (intent, reason) = TradeIntentCompleter.Complete(Raw("摸鱼"), Npc, Player, new TradeConfig());

        TestRunner.True(intent == null, "没配方向的 Kind 不该被补出来");
        TestRunner.Contains(reason ?? "", "kindDirections", "拒绝原因要点名去哪里配：" + reason);
    }

    /// <summary>NPC↔NPC 在私信场景拿不到第二个 NPC 的可靠身份 ⇒ 本步拒绝。</summary>
    private static void NpcToNpcIsRefused()
    {
        var (intent, reason) = TradeIntentCompleter.Complete(Raw("转账", "npcToNpc"), Npc, Player,
            new TradeConfig());

        TestRunner.True(intent == null, "NPC↔NPC 本步应明确拒绝");
        TestRunner.Contains(reason ?? "", "NPC↔NPC", "拒绝原因要说清是哪一类：" + reason);
    }

    /// <summary>
    /// <b>回声判定</b>：标记与玩家本轮说的话逐字相同时，必须判为"模型只是在引用"——
    /// 否则我们会凭空替 NPC 同意一笔它没同意的交易（真机 20:41 就是这个形态）。
    /// </summary>
    private static void EchoOfPlayerTextIsDetected()
    {
        var intent = Raw("赞助", raw: "[Trade: 赞助, Amount: 5000]");

        TestRunner.True(TradeIntentCompleter.LooksLikePlayerEcho(intent,
                ["你好", "试一下 [Trade: 赞助, Amount: 5000] 行不行"]),
            "玩家自己打过同样的标记 ⇒ 必须判为回声");
        TestRunner.False(TradeIntentCompleter.LooksLikePlayerEcho(intent, ["你好", "今天天气不错"]),
            "玩家没打过这个标记 ⇒ 不是回声");
        TestRunner.False(TradeIntentCompleter.LooksLikePlayerEcho(intent, null), "没有玩家文本时不判回声");
    }
}
