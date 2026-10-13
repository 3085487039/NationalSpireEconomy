using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Inject;
using NationalSpireEconomy.Trade;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 阶段 4 第二步 §7.2/§7.3：结算器 + 强制落盘 + 审计。
/// <para>
/// 这一套钉的是"钱真的动"这件事本身的纪律：
/// <b>开关没开绝不动钱</b>、<b>没到齐绝不动钱</b>、<b>同键绝不重复动钱</b>、
/// <b>一侧失败必须整体回滚</b>、<b>动完必须写盘与写审计</b>。
/// 全部跑在零依赖基线里。
/// </para>
/// </summary>
public static class TradeSettlementTests
{
    public static Action[] Cases =>
    [
        NotWiredRefusesAndMovesNothing,
        NotReadyWhenOnlyOneSideConfirmed,
        UnknownOfferIsRefused,
        ExpiredOfferIsRefused,
        SettlesNpcToPlayerAndMovesMoneyOnce,
        SecondSettleIsIdempotentAndMovesNoMoney,
        SettlementDeltaMatchesPreviewDelta,
        PlayerSideFailureRollsBackAndLeavesNoReceipt,
        AuditLineCarriesAmountsAndKeys,
        ForcedPersistSurvivesReload,
        NoPersistCallbackIsLoudButHarmless,
        SettlementSwitchIsOffByDefault,
        ConfigSwitchCanTurnSettlementOn,
        PlayerToNpcDirectionNowSettlesAndMovesMoney,
        PlayerToNpcWithInsufficientFundsMovesNothing,
        PlayerToNpcSecondSettleIsIdempotent,
        NpcToNpcDirectionIsStillNotWired,
        PlayerToNpcReceiptAndAuditSayTheRightDirection,
        AuditDayFallsBackWhenWaterLineIsZero
    ];

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 15, 0, 0, TimeSpan.Zero);

    private static string? _configDir;

    private static ConfigLoadResult RealConfig()
    {
        if (_configDir == null) _configDir = TestConfig.Directory();
        return new ConfigLoader(_configDir, Silent).Load();
    }

    /// <summary>真机现场那个 NPC：余额 $4,700 的 crowd（$500 正好过情绪闸，也过性格上限）。</summary>
    private static NpcEconomy Sponsor(long balance = 4_700) => new()
    {
        PersonId = "crowd-6-4",
        Balance = balance,
        TierId = "crowd",
        ClubId = "c64",
        PersonalityKind = "兴奋追随者",
        Season = 1
    };

    /// <summary>已导入的影子钱包世界（未导入时钱包会拒绝动钱，那是另一条用例）。</summary>
    private static EconomyWorld ImportedWorld(NpcEconomy npc, long playerUsd = 196_820, string worldId = "settle-test")
    {
        var world = new EconomyWorld { WorldId = worldId, LastSeason = 1, LastTickedDay = 452 };
        world.Npcs[npc.PersonId] = npc;
        ShadowPlayerWallet.ImportOnce(world, playerUsd / 10, 0m, day: 452, season: 1,
            clock: new FixedEconomyClock(T0), log: Silent);
        return world;
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
            Counterparty = new NpcFacts(world.Npcs.Values.First().PersonId, "普通玩家", Rating: 1100,
                ClubId: "c64", PersonalityKind: world.Npcs.Values.First().PersonalityKind),
            Favour = 0,
            Day = 452,
            Season = 1,
            Wallet = wallet ?? new ShadowPlayerWallet(world)
        };
    }

    private static TradeEngine Engine() => new(RealConfig().Economy, RealConfig().Personality,
        RealConfig().Prompt, Silent);

    /// <summary>落一张待确认单，并把 NPC 那一票投上（模拟"模型提出 → NPC 已同意"）。</summary>
    private static string ReserveSponsored(TradeContext ctx, long amountUsd = 500)
    {
        var engine = Engine();
        var reserve = engine.Submit(ctx, new TradeIntent(TradeKinds.Sponsor, TradeDirections.NpcToPlayer,
            ctx.World.Npcs.Values.First().PersonId, EconomyTier.PlayerId, amountUsd, [], "测试报价", "[Trade: test]"));
        TestRunner.True(reserve.Ok, "前置：应能落单：" + reserve.Check.Message);

        var status = TradeConfirmationGate.Confirm(ctx, reserve.OfferId!, TradeConfirmationSide.Npc, "模型提出");
        TestRunner.True(status.NpcAccepted, "前置：NPC 那一票要投上");
        return reserve.OfferId!;
    }

    /// <summary>在指定的开关状态下跑一段用例，结束后一定还原（否则会污染后面的套件）。</summary>
    private static void WithSettlement(bool wired, Action body)
    {
        bool before = TradeConfirmationGate.SettlementWired;
        TradeConfirmationGate.WireSettlement(wired);
        try { body(); }
        finally { TradeConfirmationGate.WireSettlement(before); }
    }

    // ── 前置闸门 ────────────────────────────────────────────────────

    private static void NotWiredRefusesAndMovesNothing()
    {
        WithSettlement(false, () =>
        {
            var npc = Sponsor();
            var world = ImportedWorld(npc);
            var ctx = ContextFor(world);
            string offerId = ReserveSponsored(ctx);
            TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "测试");

            string moneyBefore = Snapshot(ctx);
            bool persisted = false;
            var settlement = new TradeSettlement(Engine(), Silent, _ => { persisted = true; return true; });

            var outcome = settlement.Settle(ctx, offerId, "单元测试");

            TestRunner.Equal(SettlementStatus.NotWired, outcome.Status, "开关没开就必须拒绝");
            TestRunner.False(outcome.MoneyMoved);
            TestRunner.Equal(moneyBefore, Snapshot(ctx), "★开关没开时一分钱都不许动");
            TestRunner.False(persisted, "没结算就不该落盘");
            TestRunner.Equal(TradeOfferStates.Pending, world.Offers[offerId].State, "单子仍是待确认");
        });
    }

    private static void NotReadyWhenOnlyOneSideConfirmed()
    {
        WithSettlement(true, () =>
        {
            var npc = Sponsor();
            var world = ImportedWorld(npc);
            var ctx = ContextFor(world);
            string offerId = ReserveSponsored(ctx);         // 只有 NPC 那一票

            string moneyBefore = Snapshot(ctx);
            var outcome = new TradeSettlement(Engine(), Silent).Settle(ctx, offerId, "单元测试");

            TestRunner.Equal(SettlementStatus.NotReady, outcome.Status, "只有一方确认时不能结算");
            TestRunner.Contains(outcome.Detail, "还差", "原因要说清还差谁");
            TestRunner.Equal(moneyBefore, Snapshot(ctx), "★没到齐时一分钱都不许动");
        });
    }

    /// <summary>
    /// <b>★ 阶段 4 第五步 A 项（甲方 2026-10-12 拍板）：`player_to_npc` 现在真的动钱。</b>
    /// <para>
    /// 拍板内容：<b>1(a) 接受分叉</b>（影子是唯一真相，原版 <c>Credits</c> 仅供参考）、
    /// <b>2 单笔上限 = 影子余额</b>（不是新配置，就是余额闸的语义）、
    /// <b>3(a) 余额不足硬失败</b>。
    /// </para>
    /// <para>
    /// 这条钉的是"<b>玩家的钱真的减少、NPC 真的增加</b>"——本步唯一一处"玩家会掉钱"的行为，
    /// 所以四个数字（双方余额前后）逐个断言，而不是只看状态码。
    /// </para>
    /// </summary>
    private static void PlayerToNpcDirectionNowSettlesAndMovesMoney()
    {
        WithSettlement(true, () =>
        {
            var npc = Professional();                   // $90,000
            var world = ImportedWorld(npc);             // 玩家影子 $196,820
            var ctx = ContextFor(world);
            var engine = Engine();

            // 雇佣 = playerToNpc（玩家出钱）。第一步照旧允许它落单、也允许预览与双方确认。
            var reserve = engine.Submit(ctx, new TradeIntent(TradeKinds.Hire, TradeDirections.PlayerToNpc,
                EconomyTier.PlayerId, npc.PersonId, 10_000, [], "测试报价", "[Trade: test]"));
            TestRunner.True(reserve.Ok, "前置：反向交易应能照常落单：" + reserve.Check.Message);
            string offerId = reserve.OfferId!;

            TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Npc, "模型同意");
            var both = TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "玩家确认");
            TestRunner.True(both.ReadyToSettle, "前置：反向交易也能走到「双方已确认，待结算」");

            int persisted = 0;
            var settlement = new TradeSettlement(engine, Silent, _ => { persisted++; return true; });

            var outcome = settlement.Settle(ctx, offerId, "单元测试");

            TestRunner.Equal(SettlementStatus.Applied, outcome.Status,
                "★甲方拍板后 player_to_npc 必须能真的结算：" + outcome.Detail);
            TestRunner.True(outcome.MoneyMoved);

            // ★ 四个数字：玩家减少、NPC 增加，金额完全相同。
            TestRunner.Equal(196_820L - 10_000L, ctx.Wallet.BalanceUsd, "★玩家影子余额必须真的减少 $10,000");
            TestRunner.Equal(90_000L + 10_000L, world.Npcs[npc.PersonId].Balance, "★NPC 余额必须真的增加 $10,000");
            TestRunner.Equal(196_820L, outcome.PlayerBalanceBefore);
            TestRunner.Equal(186_820L, outcome.PlayerBalanceAfter);
            TestRunner.Equal(90_000L, outcome.NpcBalanceBefore);
            TestRunner.Equal(100_000L, outcome.NpcBalanceAfter);

            // 两个方向的账都记对了：NPC 收钱记正数、玩家流水也有一笔支出。
            TestRunner.Equal(1, world.Npcs[npc.PersonId].Ledger.Count, "NPC 侧应恰好记一笔流水");
            TestRunner.Equal(10_000L, world.Npcs[npc.PersonId].Ledger[0].Amount, "收款记为正数 +$10,000");
            TestRunner.Equal(EconomyTier.PlayerId, world.Npcs[npc.PersonId].Ledger[0].CounterpartyId,
                "NPC 流水的对手方必须是玩家（否则对账时看不出是谁给的钱）");
            TestRunner.True(world.Player.Ledger.Count >= 1, "玩家侧也要留一笔支出流水");
            TestRunner.Equal(-10_000L, world.Player.Ledger[0].Amount, "玩家支出记为负数 −$10,000");

            TestRunner.Equal(TradeOfferStates.Confirmed, world.Offers[offerId].State, "单子应置为已确认");
            TestRunner.Equal(1, persisted, "结算后必须落盘一次");
            TestRunner.True(world.Receipts.Contains(outcome.ReceiptKey), "幂等键要进收据本（否则会重复扣钱）");
        });
    }

    /// <summary>
    /// <b>★ 甲方拍板 3(a)：玩家余额不足时硬失败。</b>
    /// <para>
    /// 真机验收表 §6 的第 ② 条钉的就是它：<c>Failed</c> + <c>InsufficientFunds</c>、
    /// <b>两侧余额一字不动</b>、<c>audit</c> 不新增、<c>[Error]</c> 0。
    /// </para>
    /// <para>
    /// <b>为什么要在结算这一层测</b>：<see cref="BalanceValidator"/> 确实会在"落单"时先挡一道，
    /// 但余额是<b>会变的</b>——单子落下之后玩家完全可能先花掉别的钱。
    /// 那时结算这一层是<b>最后一道</b>闸，它必须自己守住（"我测过的那条路≠真机走的那条路"）。
    /// 所以这里刻意把余额抽走再造出那种局面。
    /// </para>
    /// <para>
    /// <b>为什么不能软失败</b>：<c>softFailOnBalance</c> 是给 NPC 用的（"囊中羞涩"的台词），
    /// 玩家不是 NPC，不该有这种台词；而且软失败不落 invalid、单子会留在 pending 上被反复重试。
    /// </para>
    /// </summary>
    private static void PlayerToNpcWithInsufficientFundsMovesNothing()
    {
        WithSettlement(true, () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "nse-settle-poor-" + Guid.NewGuid().ToString("N"));
            try
            {
                var npc = Professional();
                var world = ImportedWorld(npc);             // 玩家影子 $196,820
                var ctx = ContextFor(world);
                var engine = Engine();

                var reserve = engine.Submit(ctx, new TradeIntent(TradeKinds.Hire, TradeDirections.PlayerToNpc,
                    EconomyTier.PlayerId, npc.PersonId, 10_000, [], "测试报价", "[Trade: test]"));
                TestRunner.True(reserve.Ok, "前置：落单时应通过校验：" + reserve.Check.Message);
                string offerId = reserve.OfferId!;

                TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Npc, "模型同意");
                TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "玩家确认");

                // ★ 造出"单子落下之后钱被花掉"的局面：把影子余额抽到只剩 $200。
                long drained = world.Player.Balance - 200;
                var drain = new ShadowPlayerWallet(world).Apply(-drained,
                    new LedgerEntry { Day = 452, Season = 1, Kind = "测试抽干", Title = "测试：把余额降到 $200" });
                TestRunner.Equal(MoneyMoveStatus.Applied, drain.Status, "前置：应能把余额抽到 $200");
                TestRunner.Equal(200L, ctx.Wallet.BalanceUsd, "前置：玩家只剩 $200");

                var audit = new EconomyAudit(root, new FixedEconomyClock(T0), Silent);
                TestRunner.Equal(0, audit.CountLines(world.WorldId), "前置：此前一行审计都没有");

                string npcMoneyBefore = SnapshotNpc(ctx, npc.PersonId);
                int persisted = 0;
                var settlement = new TradeSettlement(engine, Silent,
                    _ => { persisted++; return true; }, audit);

                var outcome = settlement.Settle(ctx, offerId, "单元测试");

                TestRunner.Equal(SettlementStatus.Failed, outcome.Status, "★钱不够必须硬失败：" + outcome.Detail);
                TestRunner.False(outcome.MoneyMoved, "失败时 MoneyMoved 必须是 false");

                // ① 两侧余额一字不动（玩家侧没动、NPC 侧也不许动）。
                TestRunner.Equal(200L, ctx.Wallet.BalanceUsd, "★玩家余额一字不动");
                TestRunner.Equal(npcMoneyBefore, SnapshotNpc(ctx, npc.PersonId), "★NPC 余额也一字不动");
                TestRunner.Equal(0, world.Npcs[npc.PersonId].Ledger.Count, "NPC 侧不该留下任何流水");

                // ② ★ 真机验收表 §6 第 ② 条：audit **不新增**。
                //    审计的第一读者是"对账的人"与 V 段的回读（RecentSettlements），
                //    两者都假设"有 trade_settled 这一行 = 钱动过"。钱没动就不该有这一行。
                TestRunner.Equal(0, audit.CountLines(world.WorldId), "★钱没动时 audit 一行都不许新增");
                TestRunner.True(audit.Read(world.WorldId).Count == 0, "读回来也必须是空的");

                // ③ 幂等键必须被释放，否则玩家凑够钱之后这笔就再也做不成了。
                TestRunner.False(world.Receipts.Contains(outcome.ReceiptKey),
                    "★失败的尝试不许留下整笔的幂等键（否则凑够钱也无法重试）");

                // ④ 单子被置为终态 invalid，不会留在 pending 上被反复重试。
                TestRunner.Equal(TradeOfferStates.Invalid, world.Offers[offerId].State, "失败后单子应置为 invalid");

                // ⑤ 结论要落盘，且明确是"未成立"。
                TestRunner.True(persisted > 0, "失败的结论也要落盘（否则重启后又是一张 pending 单）");
                TestRunner.True(world.LastTrade != null, "要在影子存档上留下结论");
                TestRunner.False(world.LastTrade!.Settled, "它是「未成立」，绝不能算成已结算");
                TestRunner.Contains(world.LastTrade.Reason, "不足", "落盘的原因要能看出是钱不够");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
        });
    }

    /// <summary>★ 真机验收表 §6 的第 ③ 条：同一单再确认一次 ⇒ <c>AlreadyApplied</c>，一分不动。</summary>
    private static void PlayerToNpcSecondSettleIsIdempotent()
    {
        WithSettlement(true, () =>
        {
            var npc = Professional();
            var world = ImportedWorld(npc);
            var ctx = ContextFor(world);
            var engine = Engine();

            var reserve = engine.Submit(ctx, new TradeIntent(TradeKinds.Hire, TradeDirections.PlayerToNpc,
                EconomyTier.PlayerId, npc.PersonId, 10_000, [], "测试报价", "[Trade: test]"));
            TestRunner.True(reserve.Ok, "前置：应能落单：" + reserve.Check.Message);
            string offerId = reserve.OfferId!;
            TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Npc, "模型同意");
            TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "玩家确认");

            var settlement = new TradeSettlement(engine, Silent);
            TestRunner.Equal(SettlementStatus.Applied, settlement.Settle(ctx, offerId, "弹窗").Status,
                "前置：第一次应当真的结算");

            string moneyAfterFirst = Snapshot(ctx);
            int npcLedgerAfterFirst = world.Npcs[npc.PersonId].Ledger.Count;

            var second = settlement.Settle(ctx, offerId, "弹窗（再点一次）");

            TestRunner.Equal(SettlementStatus.AlreadyApplied, second.Status,
                "★重复结算必须报「已经结算过」，而不是再扣一次：" + second.Detail);
            TestRunner.Equal(moneyAfterFirst, Snapshot(ctx), "★重复结算一分钱都不许动");
            TestRunner.Equal(npcLedgerAfterFirst, world.Npcs[npc.PersonId].Ledger.Count, "不许再多记流水");
            TestRunner.Equal(1, settlement.AppliedCount, "只有第一次算真的动过钱");
        });
    }

    /// <summary>
    /// <b>★ 本步的边界：`npc_to_npc` 仍然不许过。</b>
    /// <para>
    /// 交接文档 §8 第 1 条点名过：不能"因为放开了 A 就顺手放开第三个方向"。
    /// 那条路径（转会费/工资/奖金）是另一套业务，没验收过。
    /// </para>
    /// <para>
    /// 这条测试的意义在于<b>它防的是"把方向闸整条删掉"这种改法</b>——
    /// 删掉之后这条会立刻变红，而 <c>player_to_npc</c> 那几条仍然是绿的。
    /// </para>
    /// </summary>
    private static void NpcToNpcDirectionIsStillNotWired()
    {
        WithSettlement(true, () =>
        {
            var npc = Professional();
            var world = ImportedWorld(npc);
            var ctx = ContextFor(world);

            // 直接落一张 npc_to_npc 的单（不经过 Submit：那条路在主挂载点上本就拒绝双 NPC 场景）。
            world.Offers["oNpcNpc"] = new TradeOffer
            {
                Id = "oNpcNpc", Kind = TradeKinds.Transfer, Direction = TradeDirections.NpcToNpc,
                InitiatorId = npc.PersonId, CounterpartyId = "other-npc", Amount = 5_000,
                State = TradeOfferStates.Pending, CreatedDay = 452, Season = 1, ExpiresDay = 466
            };

            string moneyBefore = Snapshot(ctx);
            bool persisted = false;
            var settlement = new TradeSettlement(Engine(), Silent, _ => { persisted = true; return true; });

            var outcome = settlement.Settle(ctx, "oNpcNpc", "单元测试");

            TestRunner.Equal(SettlementStatus.DirectionNotWired, outcome.Status, "★NPC↔NPC 必须仍被方向闸挡下");
            TestRunner.Contains(outcome.Detail, TradeDirections.NpcToNpc, "原因要点名是哪个方向");
            TestRunner.Contains(outcome.Detail, "尚未接线", "原因要说清是没接线，而不是校验失败");
            TestRunner.Equal(moneyBefore, Snapshot(ctx), "★NPC↔NPC 一分钱都不许动");
            TestRunner.False(world.Receipts.Contains(outcome.ReceiptKey), "被方向闸挡下时不该登记整笔幂等键");
            TestRunner.Equal(TradeOfferStates.Pending, world.Offers["oNpcNpc"].State, "单子应仍是待确认");

            // 与"没接线"那几条出口一致：结论要落盘（钱一分没动）。
            TestRunner.True(persisted, "方向被挡也要把「未成立」的结论落盘（钱依然一分没动）");
            TestRunner.True(world.LastTrade != null, "结论要写进世界");
            TestRunner.False(world.LastTrade!.Settled, "它是「未成立」，绝不能算成已结算");
            TestRunner.Contains(world.LastTrade.Reason, "尚未接线", "落盘的原因要能对上方向闸");
        });
    }

    /// <summary>
    /// <b>★ A 项的可见证据：回执与审计都必须说对方向。</b>
    /// <para>
    /// 原先回执把「（NPC → 你）」写死，放开 <c>player_to_npc</c> 之后那句话会变成<b>假话</b>
    /// （玩家出钱的交易被渲染成"NPC 给你钱"）。审计那一行同理：
    /// 真机验收表 §6 第 ① 条要看的就是 <c>Direction = player_to_npc</c>。
    /// </para>
    /// </summary>
    private static void PlayerToNpcReceiptAndAuditSayTheRightDirection()
    {
        WithSettlement(true, () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "nse-settle-dir-" + Guid.NewGuid().ToString("N"));
            try
            {
                var npc = Professional();
                var world = ImportedWorld(npc);
                var ctx = ContextFor(world);
                var engine = Engine();

                var reserve = engine.Submit(ctx, new TradeIntent(TradeKinds.Hire, TradeDirections.PlayerToNpc,
                    EconomyTier.PlayerId, npc.PersonId, 10_000, [], "测试报价", "[Trade: test]"));
                TestRunner.True(reserve.Ok, "前置：应能落单：" + reserve.Check.Message);
                string offerId = reserve.OfferId!;
                TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Npc, "模型同意");
                TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "玩家确认");

                var audit = new EconomyAudit(root, new FixedEconomyClock(T0), Silent);
                var outcome = new TradeSettlement(engine, Silent, audit: audit).Settle(ctx, offerId, "单元测试");
                TestRunner.Equal(SettlementStatus.Applied, outcome.Status, "前置：应当真的结算：" + outcome.Detail);

                // ① 回执：方向、减少、增加三件事都要说对。
                string receipt = world.LastTrade!.Receipt;
                TestRunner.Contains(receipt, "你 → " + npc.PersonId, "★回执必须说「你 → NPC」，不能说反");
                TestRunner.NotContains(receipt, npc.PersonId + " → 你", "★绝不能把玩家出钱渲染成 NPC 给钱");
                TestRunner.Contains(receipt, "减少", "玩家侧要标出「减少」（一眼看出钱少了）");
                TestRunner.Contains(receipt, "增加", "NPC 侧要标出「增加」");
                TestRunner.Contains(receipt, "$186,820", "要给出扣款后的玩家影子余额");

                // ② 结果弹窗正文与回执同源（否则界面与日志会分叉）。
                TestRunner.Contains(world.LastTrade.Detail, "你 → " + npc.PersonId,
                    "弹窗正文首行也要说对方向");

                // ③ 审计：Direction 必须是 player_to_npc，且两侧余额都在。
                var records = audit.Read(world.WorldId);
                TestRunner.Equal(1, records.Count, "每笔结算恰好一行审计");
                var line = records[0];
                TestRunner.Equal(TradeDirections.PlayerToNpc, line.Direction, "★审计行必须写明是玩家出钱");
                TestRunner.Equal(196_820L, line.PlayerBalanceBeforeUsd);
                TestRunner.Equal(186_820L, line.PlayerBalanceAfterUsd, "★审计要能看出玩家余额减少");
                TestRunner.Equal(90_000L, line.BalanceBeforeUsd);
                TestRunner.Equal(100_000L, line.BalanceAfterUsd, "★审计要能看出 NPC 余额增加");

                // ④ 审计回读那一段（V 段里给玩家看的）也要说对"支出"。
                string? recent = TradeNoteText.RecentSettlements(records);
                TestRunner.True(recent != null, "有结算记录时应能回读");
                TestRunner.Contains(recent!, "你支出", "回读行必须标出这笔是玩家支出");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
        });
    }

    /// <summary>
    /// <b>★ 第五步 D 项：老世界水位线为 0 时，审计行不能写 <c>Day: 0</c>。</b>
    /// <para>
    /// 真机 <c>audit\e35d0119.jsonl</c> 第 1 行就是这个形状（<c>Day: 0</c>），而
    /// <c>TradeNoteText.RecentSettlements</c> 要把它渲染成「第 N 天：…」——写 0 就没法看。
    /// 兜底顺序：调用方给的天数 → 交易单自己的 <c>CreatedDay</c> → 水位线。
    /// </para>
    /// <para>
    /// <b>为什么用两个世界而不是"同一张单结算两次"</b>：第二次结算会走
    /// <c>TradeOfferStates.Confirmed</c> 那条幂等出口（"这张单已经结算过"），
    /// 它在第 ③ 步就返回了，**根本不经过写审计的那一段**——用它来测这条兜底是测了个空。
    /// 所以这里老老实实造两次"真的结算"，分别带上 <c>Day = 452</c> 与 <c>Day = 0</c>。
    /// </para>
    /// <para><b>只对新行生效</b>：历史 <c>.jsonl</c> 一行都不改（审计"只追加、不修改"）。</para>
    /// </summary>
    private static void AuditDayFallsBackWhenWaterLineIsZero()
    {
        WithSettlement(true, () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "nse-settle-day-" + Guid.NewGuid().ToString("N"));
            try
            {
                var audit = new EconomyAudit(root, new FixedEconomyClock(T0), Silent);
                var settlement = new TradeSettlement(Engine(), Silent, audit: audit);

                // ① 正常情形：调用方给了天数 ⇒ 原样用它。
                var npcA = Sponsor();
                var worldA = ImportedWorld(npcA, worldId: "day-normal");
                var ctxA = ContextFor(worldA);
                string offerA = ReserveSponsored(ctxA, 500);
                TradeConfirmationGate.Confirm(ctxA, offerA, TradeConfirmationSide.Player, "弹窗");

                TestRunner.Equal(SettlementStatus.Applied, settlement.Settle(ctxA, offerA, "弹窗").Status);
                TestRunner.Equal(452, audit.Read(worldA.WorldId)[0].Day, "正常情形就该用调用方给的天数");

                // ② 老世界情形：水位线 0、调用方也给 0 ⇒ 兜底到交易单自己的 CreatedDay。
                var npcB = Sponsor();
                var worldB = ImportedWorld(npcB, worldId: "day-legacy");
                worldB.LastTickedDay = 0;                     // 老世界：水位线还没被推过
                var ctxB = ContextFor(worldB);
                string offerB = ReserveSponsored(ctxB, 500);
                TradeConfirmationGate.Confirm(ctxB, offerB, TradeConfirmationSide.Player, "弹窗");

                var legacy = new TradeContext
                {
                    World = worldB, Config = ctxB.Config, Personality = ctxB.Personality, Prompt = ctxB.Prompt,
                    Counterparty = ctxB.Counterparty, Favour = 0, Day = 0, Season = 1,
                    Wallet = new ShadowPlayerWallet(worldB)
                };

                var outcome = settlement.Settle(legacy, offerB, "老世界");

                TestRunner.Equal(SettlementStatus.Applied, outcome.Status,
                    "前置：老世界这一笔仍应真的结算：" + outcome.Detail);
                TestRunner.Equal(0, settlement.AuditFailures, "审计写入不该失败");

                var records = audit.Read(worldB.WorldId);
                TestRunner.Equal(1, records.Count, "每笔真的结算恰好一行审计");
                TestRunner.Equal(452, records[0].Day,
                    "★水位线为 0 时必须兜底到交易日，绝不能写 Day: 0");
                TestRunner.True(TradeNoteText.RecentSettlements(records)!.Contains("第 452 天"),
                    "回读行要能渲染出「第 452 天」");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
        });
    }

    /// <summary>一个"有钱、能雇"的 NPC（雇佣 = playerToNpc，玩家出钱）。</summary>
    private static NpcEconomy Professional(long balance = 90_000) => new()
    {
        PersonId = "pro-1",
        Balance = balance,
        TierId = "professional",
        ClubId = "c00",
        PersonalityKind = "热心同好",
        Season = 1
    };

    /// <summary>只看 NPC 那一侧的余额与流水条数（"一字不动"断言用）。</summary>
    private static string SnapshotNpc(TradeContext ctx, string npcId) =>
        ctx.World.Npcs.TryGetValue(npcId, out var npc) ? $"{npc.Balance}:{npc.Ledger.Count}" : "missing";

    private static void UnknownOfferIsRefused()
    {
        WithSettlement(true, () =>
        {
            var world = ImportedWorld(Sponsor());
            var ctx = ContextFor(world);

            var outcome = new TradeSettlement(Engine(), Silent).Settle(ctx, "o9999", "单元测试");

            TestRunner.Equal(SettlementStatus.NotReady, outcome.Status);
            TestRunner.Contains(outcome.Detail, "找不到交易单");
        });
    }

    private static void ExpiredOfferIsRefused()
    {
        WithSettlement(true, () =>
        {
            var npc = Sponsor();
            var world = ImportedWorld(npc);
            var ctx = ContextFor(world);
            string offerId = ReserveSponsored(ctx);
            TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "测试");

            // 把单子改成"早就过期"，再把 ctx 推到很后面的天数。
            world.Offers[offerId].ExpiresDay = 452;
            var later = new TradeContext
            {
                World = world, Config = ctx.Config, Personality = ctx.Personality, Prompt = ctx.Prompt,
                Counterparty = ctx.Counterparty, Favour = 0, Day = 500, Season = 3, Wallet = ctx.Wallet
            };

            string moneyBefore = Snapshot(later);
            var outcome = new TradeSettlement(Engine(), Silent).Settle(later, offerId, "单元测试");

            TestRunner.Equal(SettlementStatus.NotReady, outcome.Status, "过期单不能结算");
            TestRunner.Equal(moneyBefore, Snapshot(later), "★过期单不许动钱");
        });
    }

    // ── 真正动账 ────────────────────────────────────────────────────

    private static void SettlesNpcToPlayerAndMovesMoneyOnce()
    {
        WithSettlement(true, () =>
        {
            var npc = Sponsor();                        // $4,700
            var world = ImportedWorld(npc);             // 玩家影子 $196,820
            var ctx = ContextFor(world);
            string offerId = ReserveSponsored(ctx, 500);
            TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "弹窗");

            int persisted = 0;
            var settlement = new TradeSettlement(Engine(), Silent, _ => { persisted++; return true; });

            var outcome = settlement.Settle(ctx, offerId, "弹窗");

            TestRunner.Equal(SettlementStatus.Applied, outcome.Status, "应当真的结算：" + outcome.Detail);
            TestRunner.Equal(4_200L, world.Npcs[npc.PersonId].Balance, "NPC 应付出 $500");
            TestRunner.Equal(197_320L, ctx.Wallet.BalanceUsd, "玩家影子余额应收到 $500");
            TestRunner.Equal(4_700L, outcome.NpcBalanceBefore);
            TestRunner.Equal(4_200L, outcome.NpcBalanceAfter);
            TestRunner.Equal(196_820L, outcome.PlayerBalanceBefore);
            TestRunner.Equal(197_320L, outcome.PlayerBalanceAfter);
            TestRunner.Equal(TradeOfferStates.Confirmed, world.Offers[offerId].State, "单子应置为已确认");
            TestRunner.True(world.Offers[offerId].LedgerEntryId.Length > 0, "单子要记下流水 id");
            TestRunner.Equal(1, persisted, "结算后必须落盘一次");
            TestRunner.Equal(1, settlement.AppliedCount);
            TestRunner.True(world.Receipts.Contains(outcome.ReceiptKey), "幂等键要进收据本（否则会重复发钱）");
            TestRunner.Equal(1, world.Npcs[npc.PersonId].Ledger.Count, "NPC 侧应恰好记一笔流水");
            TestRunner.Equal(-500L, world.Npcs[npc.PersonId].Ledger[0].Amount, "支出记为负数 −$500");
        });
    }

    private static void SecondSettleIsIdempotentAndMovesNoMoney()
    {
        WithSettlement(true, () =>
        {
            var npc = Sponsor();
            var world = ImportedWorld(npc);
            var ctx = ContextFor(world);
            string offerId = ReserveSponsored(ctx, 500);
            TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "弹窗");

            var settlement = new TradeSettlement(Engine(), Silent);
            var first = settlement.Settle(ctx, offerId, "弹窗");
            TestRunner.Equal(SettlementStatus.Applied, first.Status);

            string afterFirst = Snapshot(ctx);
            int npcLedgerAfterFirst = world.Npcs[npc.PersonId].Ledger.Count;
            TestRunner.Equal(1, npcLedgerAfterFirst, "前置：第一次结算应恰好记一笔流水");

            var second = settlement.Settle(ctx, offerId, "弹窗（再点一次）");

            TestRunner.Equal(SettlementStatus.AlreadyApplied, second.Status,
                "重复结算必须报「已经结算过」，而不是又扣一次：" + second.Detail);
            TestRunner.Equal(afterFirst, Snapshot(ctx), "★重复结算一分钱都不许动");
            TestRunner.Equal(npcLedgerAfterFirst, world.Npcs[npc.PersonId].Ledger.Count,
                "重复结算不许再多记流水");
            TestRunner.Equal(1, settlement.AppliedCount, "只有第一次算真的动过钱");
        });
    }

    /// <summary>方案 §6 的加强版验收：<b>结算后的余额差必须等于预览推演的 delta</b>。</summary>
    private static void SettlementDeltaMatchesPreviewDelta()
    {
        WithSettlement(true, () =>
        {
            var npc = Sponsor();
            var world = ImportedWorld(npc);
            var ctx = ContextFor(world);
            var engine = Engine();

            var reserve = engine.Submit(ctx, new TradeIntent(TradeKinds.Sponsor, TradeDirections.NpcToPlayer,
                npc.PersonId, EconomyTier.PlayerId, 500, [], "测试报价", "[Trade: test]"));
            TestRunner.True(reserve.Ok);
            var offer = world.Offers[reserve.OfferId!];
            var preview = TradePreviewBuilder.ForOffer(ctx, offer, engine);

            long npcBefore = npc.Balance;
            long playerBefore = ctx.Wallet.BalanceUsd;

            TradeConfirmationGate.Confirm(ctx, offer.Id, TradeConfirmationSide.Npc, "模型提出");
            TradeConfirmationGate.Confirm(ctx, offer.Id, TradeConfirmationSide.Player, "弹窗");

            var outcome = new TradeSettlement(engine, Silent).Settle(ctx, offer.Id, "弹窗");
            TestRunner.Equal(SettlementStatus.Applied, outcome.Status);

            long npcDelta = npc.Balance - npcBefore;
            long playerDelta = ctx.Wallet.BalanceUsd - playerBefore;
            TestRunner.Equal(preview.Initiator.DeltaUsd, npcDelta, "NPC 侧余额差必须等于预览 delta");
            TestRunner.Equal(preview.Player.DeltaUsd, playerDelta, "玩家侧余额差必须等于预览 delta");
            TestRunner.Equal(-500L, npcDelta);
            TestRunner.Equal(500L, playerDelta);
        });
    }

    /// <summary>
    /// ★ §7.1 那条隐患的端到端版：玩家侧失败时 NPC 侧必须被回滚，
    /// 且整笔的幂等键<b>不许留下</b>（否则同一笔重放会被误判成"已经结算过"）。
    /// </summary>
    private static void PlayerSideFailureRollsBackAndLeavesNoReceipt()
    {
        WithSettlement(true, () =>
        {
            var npc = Sponsor();
            var world = new EconomyWorld { WorldId = "settle-fail", LastSeason = 1, LastTickedDay = 452 };
            world.Npcs[npc.PersonId] = npc;             // 没有导入玩家钱包 ⇒ 玩家侧必然被拒
            var ctx = ContextFor(world);
            string offerId = ReserveSponsored(ctx, 500);
            TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "弹窗");

            long npcBefore = npc.Balance;
            int ledgerBefore = npc.Ledger.Count;
            var settlement = new TradeSettlement(Engine(), Silent);

            var outcome = settlement.Settle(ctx, offerId, "弹窗");

            TestRunner.Equal(SettlementStatus.Failed, outcome.Status, "未导入的钱包必须让这一笔失败");
            TestRunner.Contains(outcome.Detail, "尚未导入", "失败原因要指出是钱包没导入");
            TestRunner.Equal(npcBefore, npc.Balance, "★玩家侧失败时 NPC 侧必须原额回滚");
            TestRunner.Equal(ledgerBefore, npc.Ledger.Count, "回滚后不该留下流水");
            TestRunner.False(world.Receipts.Contains(outcome.ReceiptKey),
                "★失败的尝试不许留下整笔的幂等键（否则重放会被误判成「已经结算过」）");
            TestRunner.Equal(TradeOfferStates.Invalid, world.Offers[offerId].State, "失败后单子应置为 invalid");
        });
    }

    // ── 审计与落盘 ──────────────────────────────────────────────────

    private static void AuditLineCarriesAmountsAndKeys()
    {
        WithSettlement(true, () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "nse-settle-audit-" + Guid.NewGuid().ToString("N"));
            try
            {
                var npc = Sponsor();
                var world = ImportedWorld(npc);
                var ctx = ContextFor(world);
                string offerId = ReserveSponsored(ctx, 500);
                TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "弹窗");

                var audit = new EconomyAudit(root, new FixedEconomyClock(T0), Silent);
                var settlement = new TradeSettlement(Engine(), Silent, audit: audit);

                var outcome = settlement.Settle(ctx, offerId, "弹窗");
                TestRunner.Equal(SettlementStatus.Applied, outcome.Status);
                TestRunner.Equal(0, settlement.AuditFailures);

                var records = audit.Read(world.WorldId);
                TestRunner.Equal(1, records.Count, "每笔结算恰好一行审计");
                var line = records[0];
                TestRunner.Equal(AuditKinds.TradeSettled, line.Kind);
                TestRunner.Equal(offerId, line.OfferId);
                TestRunner.Equal("赞助", line.Application);
                TestRunner.Equal(500L, line.AmountUsd);
                TestRunner.Equal(4_700L, line.BalanceBeforeUsd);
                TestRunner.Equal(4_200L, line.BalanceAfterUsd);
                TestRunner.Equal(196_820L, line.PlayerBalanceBeforeUsd);
                TestRunner.Equal(197_320L, line.PlayerBalanceAfterUsd);
                TestRunner.Equal(outcome.ReceiptKey, line.ReceiptKey);
                TestRunner.Equal(outcome.LedgerEntryId, line.LedgerEntryId);
                TestRunner.Equal("Applied", line.Status);
                TestRunner.True(File.Exists(audit.PathFor(world.WorldId)), "审计文件要真的落盘");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
        });
    }

    /// <summary>§7.3 验收：结算之后<b>重启（重新读盘）状态仍在</b>。</summary>
    private static void ForcedPersistSurvivesReload()
    {
        WithSettlement(true, () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "nse-settle-store-" + Guid.NewGuid().ToString("N"));
            try
            {
                var clock = new FixedEconomyClock(T0);
                var store = new EconomyStore(root, clock, Silent);
                var npc = Sponsor();
                var world = ImportedWorld(npc);
                var ctx = ContextFor(world);
                string offerId = ReserveSponsored(ctx, 500);
                TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "弹窗");

                var settlement = new TradeSettlement(Engine(), Silent, w => store.SaveWorld(w, force: true));
                var outcome = settlement.Settle(ctx, offerId, "弹窗");
                TestRunner.Equal(SettlementStatus.Applied, outcome.Status);

                var reloaded = new EconomyStore(root, clock, Silent).LoadWorld(world.WorldId).World;
                TestRunner.Equal(4_200L, reloaded.Npcs[npc.PersonId].Balance, "重启后 NPC 余额仍是结算后的");
                TestRunner.Equal(TradeOfferStates.Confirmed, reloaded.Offers[offerId].State, "重启后单子仍是已确认");
                TestRunner.Equal(outcome.LedgerEntryId, reloaded.Offers[offerId].LedgerEntryId, "流水 id 也要还在");
                TestRunner.True(reloaded.Receipts.Contains(outcome.ReceiptKey),
                    "幂等键必须落盘，否则重启后同一笔会再动一次钱");
                TestRunner.Equal(197_320L, reloaded.Player.Balance, "玩家影子余额也要落盘");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
        });
    }

    private static void NoPersistCallbackIsLoudButHarmless()
    {
        WithSettlement(true, () =>
        {
            var lines = new List<string>();
            var log = new DelegateEconomyLog((_, _, message, _) => lines.Add(message));

            var npc = Sponsor();
            var world = ImportedWorld(npc);
            var ctx = ContextFor(world);
            string offerId = ReserveSponsored(ctx, 500);
            TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "弹窗");

            var outcome = new TradeSettlement(Engine(), log).Settle(ctx, offerId, "弹窗");

            TestRunner.Equal(SettlementStatus.Applied, outcome.Status, "没有落盘回调也要照常结算（钱的事优先）");
            TestRunner.True(lines.Any(l => l.Contains("没有落盘回调")),
                "但必须明确警告状态活不过重启，不能静默。实际：" + string.Join(" / ", lines));
        });
    }

    private static string Snapshot(TradeContext ctx) =>
        $"{ctx.NpcsDebug()}|{ctx.Wallet.BalanceUsd}";

    // ── 动钱开关本身 ────────────────────────────────────────────────
    /// <summary>
    /// <b>默认必须是关的</b>：这条测试是"线上不会自己动钱"的最后一道保险，
    /// 任何把默认值改成 true 的改动都会在这里被拦下。
    /// </summary>
    private static void SettlementSwitchIsOffByDefault()
    {
        TestRunner.False(TradeConfirmationGate.SettlementWired,
            "★动钱开关默认必须关（否则一次普通确认就会真的动钱）");
        TestRunner.False(new EconomyConfig().Trade.SettlementWired,
            "配置结构的默认值也必须是 false");
        TestRunner.False(RealConfig().Economy.Trade.SettlementWired,
            "交付配置里 trade.settlementWired 必须是 false");
    }

    /// <summary>
    /// 开关能真的被配置打开（真机验证走 Steam 启动，拿不到环境变量，所以配置这条路必须通）。
    /// </summary>
    private static void ConfigSwitchCanTurnSettlementOn()
    {
        string root = Path.Combine(Path.GetTempPath(), "nse-settle-switch-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            foreach (string name in new[]
                     {
                         ConfigLoader.EconomyFileName, ConfigLoader.PersonalityFileName, ConfigLoader.PromptFileName
                     })
                File.Copy(Path.Combine(_configDir!, name), Path.Combine(root, name), overwrite: true);

            // 用真正的 JSON 改写而不是字符串替换：格式怎么变都不会让这条测试假失败。
            string path = Path.Combine(root, ConfigLoader.EconomyFileName);
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            node["trade"]!["settlementWired"] = true;
            File.WriteAllText(path, node.ToJsonString());

            var loaded = new ConfigLoader(root, Silent).Load();

            TestRunner.True(loaded.Economy.Trade.SettlementWired,
                "把 trade.settlementWired 改成 true 后，配置必须读出 true（真机就靠它开机）");
            // 这条只是"配置能打开开关"，绝不真的去改全局状态：
            TestRunner.False(TradeConfirmationGate.SettlementWired,
                "读配置本身不许顺手把全局开关打开（开关只在初始化时按配置设置一次）");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}

/// <summary>测试用的小工具：把整张账本的余额与流水条数压成一行，便于"一字不变"断言。</summary>
internal static class SettlementTestExtensions
{
    public static string NpcsDebug(this TradeContext ctx) =>
        string.Join(",", ctx.World.Npcs.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}:{pair.Value.Balance}:{pair.Value.Ledger.Count}"));
}
