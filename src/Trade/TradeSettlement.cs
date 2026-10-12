using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Inject;

namespace NationalSpireEconomy.Trade;

/// <summary>一次结算尝试的结论。</summary>
public enum SettlementStatus
{
    /// <summary>阶段 4 第二步的开关还没打开（<see cref="TradeConfirmationGate.SettlementWired"/> 为 false）。</summary>
    NotWired,

    /// <summary>还没到能结算的时候：双方没都确认、单子不在 pending、或已过期。<b>没有任何变动</b>。</summary>
    NotReady,

    /// <summary>
    /// 方向未接线：这个方向的结算路径刻意不接线。钱一分不动。
    /// <para>
    /// <b>2026-10-12 甲方拍板后只剩一个方向会被它挡下：`npc_to_npc`</b>
    /// （转会费/工资/奖金那套业务没验收过，见 <see cref="TradeSettlement.AllowedDirections"/>）。
    /// 首笔范围里那条"只开通 NPC→玩家"曾用它挡过 `player_to_npc`，本步已按拍板放开。
    /// </para>
    /// </summary>
    DirectionNotWired,

    /// <summary>真的动账了。</summary>
    Applied,

    /// <summary>这一笔之前已经结算过（幂等命中），<b>本次一个数字都没改</b>。</summary>
    AlreadyApplied,

    /// <summary>校验没过或动账失败；交易单已被引擎置为终态，钱没动（或已冲正）。</summary>
    Failed
}

/// <summary>
/// 一次结算的完整结果。<b>它是对账的最小单元</b>：金额、方向、双方、双方余额前后、幂等键、流水 id 全在。
/// </summary>
public sealed record SettlementOutcome(
    SettlementStatus Status,
    string OfferId,
    string Kind,
    string Direction,
    long AmountUsd,
    string NpcId,
    long NpcBalanceBefore,
    long NpcBalanceAfter,
    long PlayerBalanceBefore,
    long PlayerBalanceAfter,
    string ReceiptKey,
    string LedgerEntryId,
    string Detail)
{
    /// <summary>本次是否真的动了钱。</summary>
    public bool MoneyMoved => Status == SettlementStatus.Applied;

    /// <summary>这笔交易（含"之前已结算过"）是否处于已结算状态。</summary>
    public bool Settled => Status is SettlementStatus.Applied or SettlementStatus.AlreadyApplied;

    /// <summary>一句话回执，给日志与玩家看。</summary>
    public string ReceiptText => Status switch
    {
        SettlementStatus.Applied =>
            $"✅ 已结算：{Kind} {MoneyUnits.Format(AmountUsd)}（{NpcId} {MoneyUnits.Format(NpcBalanceAfter)}" +
            $"｜你的影子余额 {MoneyUnits.Format(PlayerBalanceAfter)}）",
        SettlementStatus.AlreadyApplied =>
            $"✅ 这笔已经结算过，本次未再动钱（你的影子余额 {MoneyUnits.Format(PlayerBalanceAfter)}）",
        SettlementStatus.NotWired => "⏸ 结算尚未接线：" + Detail,
        SettlementStatus.NotReady => "⏳ 还不能结算：" + Detail,
        SettlementStatus.DirectionNotWired => "⛔ 方向未接线：" + Detail,
        _ => "❌ 结算失败：" + Detail
    };
}

/// <summary>
/// <b>阶段 4 第二步的结算器</b>：把「双方已确认，待结算」变成「已结算」。
/// <para>
/// <b>它只做四件事</b>（动钱这件事本身仍然全部委托给 <see cref="TradeEngine.Confirm"/> → 执行器 → 记账器）：
/// <list type="number">
/// <item><b>前置闸门</b>：<see cref="TradeConfirmationGate.SettlementWired"/> 为 true（唯一动钱开关），
/// 且双方都已确认、单子仍是 pending、未过期。任一不满足就<b>原样返回、一个数字都不改</b>。</item>
/// <item><b>重跑校验链后动账</b>：直接调用 <see cref="TradeEngine.Confirm"/>——它本来就"取单 → 查状态与过期 →
/// 重跑完整校验链 → 幂等 → 动账 → 落状态"，钱从 <see cref="EconomyLedger"/> 与
/// <c>ShadowPlayerWallet</c> 走，我们不在这里另写一套。</item>
/// <item><b>强制落盘</b>：通过注入的 <c>persist</c> 回调把世界写盘（<c>force:true</c>，绕过节流）。
/// 这一步不能省：<b>钱动了而状态没写，重启后同一笔就能再动一次</b>——正是方案 §1 点名要避免的"半阴半阳"。</item>
/// <item><b>审计一行</b>：往 <c>audit/&lt;WorldId&gt;.jsonl</c> 追加一条，含双方余额前后、幂等键、流水 id。</item>
/// </list>
/// </para>
/// <para>
/// <b>幂等</b>：重复调用由两层挡——交易单状态（结算后是 <c>confirmed</c>，引擎会拒）与幂等键
/// <c>trade:{world}:{offerId}</c>（命中即 <see cref="SettlementStatus.AlreadyApplied"/>，不改任何数字）。
/// </para>
/// <para>
/// <b>为什么它不放在 <c>src/Game</c></b>：结算全是可以单测的经济逻辑；放 <c>src/Trade</c> 才能进
/// 零依赖基线。游戏侧只需要把"落盘怎么做、审计写哪儿"两个回调注入进来。
/// </para>
/// </summary>
public sealed class TradeSettlement
{
    /// <summary>
    /// <b>已接线的资金方向</b>。
    /// <para>
    /// <b>2026-10-12 甲方拍板（阶段 4 第五步）</b>：从"只开通 `npc_to_player`"扩成
    /// "开通 `npc_to_player` 与 `player_to_npc`"——即<b>两个方向都能真的动钱</b>，
    /// 玩家的影子余额会<b>真的减少</b>（甲方选的是"接受分叉"：影子是唯一真相，原版 <c>Credits</c> 仅供参考）。
    /// </para>
    /// <para>
    /// <b>`npc_to_npc` 仍然不在这里</b>：那条路径（转会费/工资/奖金）是另一套业务，
    /// 没有经过任何真机验收。所以下面那条判定是"白名单"，<b>绝不能整条删掉</b>——
    /// 删掉就等于同时放开了第三个方向（见交接文档 §8 第 1 条）。
    /// </para>
    /// <para>
    /// <b>单笔上限</b>：甲方拍板"单笔上限 = 影子余额"，它不是一个新配置，而是
    /// <see cref="EconomyLedger.Transfer"/> 里那条余额闸的语义——玩家不可能花超过自己有的钱。
    /// 单日上限沿用 NPC 侧既有配置 <c>trade.dailyLimitPerNpc</c>（按 NPC 计数，玩家侧不另立计数器）。
    /// </para>
    /// </summary>
    public static readonly string[] AllowedDirections =
        [TradeDirections.NpcToPlayer, TradeDirections.PlayerToNpc];

    private readonly TradeEngine _engine;
    private readonly IEconomyLog _log;
    private readonly Func<EconomyWorld, bool>? _persist;
    private readonly EconomyAudit? _audit;

    /// <param name="engine">交易引擎（<see cref="TradeEngine.Confirm"/> 里已经包含"重跑校验链 + 动账 + 落状态"）。</param>
    /// <param name="log">日志出口。</param>
    /// <param name="persist">强制落盘回调（游戏侧传 <c>world =&gt; runtime.SaveQuietly(world, "结算")</c>）。</param>
    /// <param name="audit">审计写入器（写 <c>audit/&lt;WorldId&gt;.jsonl</c>）。</param>
    public TradeSettlement(TradeEngine engine, IEconomyLog? log = null,
        Func<EconomyWorld, bool>? persist = null, EconomyAudit? audit = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _log = log ?? ConsoleEconomyLog.Instance;
        _persist = persist;
        _audit = audit;
    }

    /// <summary>本进程内真正动过账的结算次数（诊断用）。</summary>
    public int AppliedCount { get; private set; }

    /// <summary>本进程内审计写入失败的次数（诊断用；审计失败不阻塞结算）。</summary>
    public int AuditFailures { get; private set; }

    /// <summary>
    /// 尝试结算一张已双双确认的交易单。<b>永不抛出</b>——它会被游戏按钮回调直接调用。
    /// </summary>
    public SettlementOutcome Settle(TradeContext ctx, string offerId, string source = "")
    {
        ArgumentNullException.ThrowIfNull(ctx);

        try
        {
            return SettleCore(ctx, offerId, source);
        }
        catch (Exception e)
        {
            // 动钱路径上的异常一律吞掉并记 Error：崩掉比"这一笔没结算"严重得多。
            _log.Error("settle", $"结算 {offerId} 时抛异常（已忽略，钱未动）：{e.GetType().Name}: {e.Message}", e);
            return new SettlementOutcome(SettlementStatus.Failed, offerId, "", "", 0, "", 0, 0, 0, 0, "",
                "", e.GetType().Name + ": " + e.Message);
        }
    }

    private SettlementOutcome SettleCore(TradeContext ctx, string offerId, string source)
    {
        // ① 唯一动钱开关
        if (!TradeConfirmationGate.SettlementWired)
            return Finish(ctx, offerId, new SettlementOutcome(SettlementStatus.NotWired, offerId, "", "", 0, "", 0, 0, 0, 0, "",
                "", TradeConfirmationGate.SettlementIsNextPhase));

        if (!ctx.World.Offers.TryGetValue(offerId, out var offer))
            return Finish(ctx, offerId, new SettlementOutcome(SettlementStatus.NotReady, offerId, "", "", 0, "", 0, 0, 0, 0, "",
                "", $"找不到交易单 {offerId}。"));

        // ② 方向闸：**白名单**（见 <see cref="AllowedDirections"/>）。
        //    2026-10-12 甲方拍板前它只放行 `npc_to_player`（"玩家只会多钱、不会掉钱"）。
        //    本步按拍板放开 `player_to_npc`：玩家的影子余额会真的减少。
        //    `npc_to_npc` 仍然不许过——它没验收过，且这里的判定是白名单，
        //    所以"漏了某个新方向"只会更安全，不会更危险。
        if (!IsAllowedDirection(offer.Direction))
            return Finish(ctx, offerId, new SettlementOutcome(SettlementStatus.DirectionNotWired, offerId, offer.Kind,
                offer.Direction, offer.Amount, "", 0, 0, 0, 0, "", "",
                $"方向「{offer.Direction}」尚未接线（已开通：{string.Join(" / ", AllowedDirections)}）；" +
                "结算路径已写但按甲方决策尚未接线（钱不会动）。"));

        // ③ 双方都确认了吗（只读投影，不改任何东西）
        var status = TradeConfirmationGate.Status(ctx, offerId);
        if (!status.ReadyToSettle)
        {
            // 已经终态且是 confirmed ⇒ 这一笔之前结算过（或正在结算中）。
            // 按幂等回报：调用方（游戏/日志）不该把"重复结算"误读成"结算失败"。
            if (string.Equals(offer.State, TradeOfferStates.Confirmed, StringComparison.Ordinal))
                return Finish(ctx, offerId, AlreadySettled(ctx, offer));

            return Finish(ctx, offerId, new SettlementOutcome(SettlementStatus.NotReady, offerId, "", "", 0, "", 0, 0, 0, 0, "",
                "", status.DetailText));
        }

        // ④ 动账前的快照（审计与"余额差 == 预览 delta"都要它）
        string npcId = ctx.IsPlayer(offer.InitiatorId) ? offer.CounterpartyId : offer.InitiatorId;
        long npcBefore = ctx.NpcOf(npcId)?.Balance ?? 0;
        long playerBefore = ctx.Wallet.BalanceUsd;

        var result = _engine.Confirm(ctx, offerId);

        long npcAfter = ctx.NpcOf(npcId)?.Balance ?? 0;
        long playerAfter = ctx.Wallet.BalanceUsd;

        bool already = string.Equals(result.Check.Code, TradeCodes.AlreadyApplied, StringComparison.Ordinal);
        var outcomeStatus = result.Ok
            ? SettlementStatus.Applied
            : already
                ? SettlementStatus.AlreadyApplied
                : SettlementStatus.Failed;

        var ledger = new EconomyLedger(ctx.World);
        var outcome = new SettlementOutcome(
            outcomeStatus,
            offerId,
            offer.Kind,
            offer.Direction,
            offer.Amount,
            npcId,
            npcBefore,
            npcAfter,
            playerBefore,
            playerAfter,
            ledger.Key("trade", offerId),
            result.LedgerEntryId ?? offer.LedgerEntryId,
            result.Ok || already
                ? (string.IsNullOrWhiteSpace(source) ? "已结算" : "已结算（" + source + "）")
                : (result.Check.Message ?? "结算失败。"));

        if (outcomeStatus == SettlementStatus.Applied) AppliedCount++;

        // ④ 审计（先写审计再落盘：审计是旁路，落盘失败时它仍能留下证据）
        //    ★ 阶段 4 第五步：**只有钱真的动过的两条出口才写审计**（Applied / AlreadyApplied）。
        //      Failed 也有一行把它记下来过，但那会让"这一笔到底动没动钱"变得需要读 Status 才能判——
        //      而审计文件的第一读者是"对账的人"与 RecentSettlements 的回读，两者都假设
        //      "trade_settled 这一行 = 钱动过"。真机验收表 §6 第 ② 条的判据也正是
        //      「玩家钱不够时 audit 不新增」。失败的原因该由 LastTrade.Reason 与日志承担。
        if (outcomeStatus is SettlementStatus.Applied or SettlementStatus.AlreadyApplied)
            Audit(ctx, outcome);

        _log.Info("settle",
            $"[Economy-Settle] 交易单 {offerId}｜{outcome.Status}｜{outcome.Kind} {MoneyUnits.Format(outcome.AmountUsd)}" +
            $"｜{npcId} {MoneyUnits.Format(npcBefore)} → {MoneyUnits.Format(npcAfter)}" +
            $"｜玩家 {MoneyUnits.Format(playerBefore)} → {MoneyUnits.Format(playerAfter)}" +
            (string.IsNullOrWhiteSpace(source) ? "" : $"｜来源 {source}") +
            (outcomeStatus == SettlementStatus.Failed ? $"｜原因 {outcome.Detail}" : ""));

        if (outcomeStatus == SettlementStatus.Applied)
            _log.Info("settle",
                $"★首次真实结算规则生效：交易单 {offerId} 已按 {offer.Direction} 结算，" +
                $"幂等键 {outcome.ReceiptKey}，流水 {outcome.LedgerEntryId}。");

        // ⑤ 先把"这笔到底怎么样了"写进世界（Finish 只改内存），**再**落盘。
        //   顺序不能反：真机实测踩过一次——先落盘后写注记，于是存档里永远没有 LastTrade，
        //   "重启后仍能看到已结算回执"完全失效（存档文件时间戳纹丝不动、连键都没有）。
        var finished = Finish(ctx, offerId, outcome);

        // ⑥ 强制落盘（三种"尝试过结算"的结局都要落：Applied/AlreadyApplied 固化了钱，
        //    Failed 固化了"这张单已被置为 invalid"，不落就会出现"重启后又是一张 pending 单"）
        if (outcomeStatus is SettlementStatus.Applied or SettlementStatus.AlreadyApplied or SettlementStatus.Failed)
            Persist(ctx.World, finished);

        return finished;
    }

    /// <summary>
    /// <b>阶段 4 第三步：把这一次结算的结论写进影子存档</b>（<see cref="EconomyWorld.LastTrade"/>），
    /// 然后原样返回它。
    /// <para>
    /// <b>为什么写在这里而不是游戏侧</b>：结算的每一条出口（开关没开 / 方向未接线 / 还没到齐 /
    /// 找不到单子 / 真的结算了）都会经过这里，于是"未成立"与"已结算"这两种真相
    /// <b>在无头单测里也能被验证</b>，不必起游戏。
    /// </para>
    /// <para>
    /// <b>它不落盘</b>：真正动账那几条出口本来就会强制落盘（同一个对象一起写下去）；
    /// 而"没动钱"的出口一次盘都不该写——真机教训是"没结算就别碰存储"。
    /// </para>
    /// <para>
    /// <b>它绝不覆盖一条"已结算"的回执</b>：那笔钱已经动过，是终局事实；
    /// 之后同一张单再来一次会命中幂等闸并走到这里，若让"本单没再动钱"把回执盖掉，
    /// 玩家就会看到"刚才说成了、现在说没成"（见 <c>TradePreviewWiring.RecordNote</c> 的说明）。
    /// </para>
    /// </summary>
    private SettlementOutcome Finish(TradeContext ctx, string offerId, SettlementOutcome outcome)
    {
        try
        {
            // 有些出口（开关没开、方向未接线、找不到单子）的结果里没有方向与对手方，
            // 但它们恰恰是最需要被玩家看见的"未成立"——所以从单子上补齐。
            ctx.World.Offers.TryGetValue(offerId, out var offer);
            string npcId = outcome.NpcId.Length > 0
                ? outcome.NpcId
                : offer == null
                    ? ""
                    : ctx.IsPlayer(offer.InitiatorId) ? offer.CounterpartyId : offer.InitiatorId;
            string direction = outcome.Direction.Length > 0
                ? outcome.Direction
                : offer?.Direction ?? "";
            long amount = outcome.AmountUsd != 0 ? outcome.AmountUsd : offer?.Amount ?? 0;
            string kind = outcome.Kind.Length > 0 ? outcome.Kind : offer?.Kind ?? "";
            var labeled = outcome with { NpcId = npcId, Direction = direction, AmountUsd = amount, Kind = kind };

            // 已经有一条"已结算"的回执时，只有再一次"已结算"才允许覆盖它。
            bool settledNow = labeled.Settled;
            if (!settledNow && ctx.World.LastTrade is { } previous && previous.Settled)
            {
                _log.Debug("settle",
                    $"交易单 {offerId} 本次是 {labeled.Status}，但最近一笔仍是一条已结算回执，不覆盖它。");
                return outcome;
            }

            ctx.World.LastTrade = new LastTradeNote
            {
                Day = ctx.Day,
                OfferId = labeled.OfferId.Length > 0 ? labeled.OfferId : offerId,
                Kind = kind,
                Direction = direction,
                AmountUsd = Math.Abs(amount),
                NpcId = npcId,
                Status = NoteStatusOf(labeled.Status),
                Reason = labeled.Detail,
                Receipt = TradeNoteText.ReceiptLine(labeled, npcId),
                Detail = TradeNoteText.ReceiptDetail(labeled, npcId),
                LedgerEntryId = labeled.LedgerEntryId
            };

            // ★ 2026-10-12 真机实测补上的落盘：**"这一笔没成"的几条出口原先不落盘**。
            //   真机现象：重复确认 o0001 被幂等闸挡下（内存里写好了"已结算"结论），
            //   但影子存档的文件时间戳纹丝不动、存档里连 LastTrade 这个键都没有——
            //   §7.4 要的"重启后仍能看到已结算回执"于是完全失效。
            //
            //   这里只补"上面没管"的那几条出口，避免既有的"结算恰好落盘一次"被改成两次：
            //   · Applied / AlreadyApplied —— 已经由 SettleCore 的 ⑤ 负责落盘；
            //   · NotWired（开关没开）—— 它什么都没改变，一次盘都不该写（有测试钉着）；
            //   · Failed —— 也已经由 ⑤ 落盘（它固化了"这张单被置为 invalid"）。
            if (labeled.Status is SettlementStatus.NotReady or SettlementStatus.DirectionNotWired)
                Persist(ctx.World, labeled);
        }
        catch (Exception e)
        {
            // 记真相这件事失败不该影响已经动过的钱。
            _log.Warn("settle", $"写入最近一笔交易结论失败（已忽略）：{e.GetType().Name}: {e.Message}");
        }

        return outcome;
    }

    /// <summary>这个方向接没接线（见 <see cref="AllowedDirections"/>）。</summary>
    public static bool IsAllowedDirection(string? direction) =>
        !string.IsNullOrEmpty(direction) &&
        Array.Exists(AllowedDirections, d => string.Equals(d, direction, StringComparison.Ordinal));

    /// <summary>把结算结论映射成落盘用的状态常量（见 <see cref="TradeNoteStatuses"/>）。</summary>
    private static string NoteStatusOf(SettlementStatus status) => status switch
    {
        SettlementStatus.Applied => TradeNoteStatuses.Settled,
        SettlementStatus.AlreadyApplied => TradeNoteStatuses.AlreadySettled,
        _ => TradeNoteStatuses.Failed
    };

    /// <summary>
    /// 已经结算过（单子处于 <c>confirmed</c>）时的幂等回报。
    /// <b>不碰任何数字</b>，只把当时的余额与流水 id 原样报出来，供调用方对账与显示。
    /// </summary>
    private static SettlementOutcome AlreadySettled(TradeContext ctx, TradeOffer settled)
    {
        string npcId = ctx.IsPlayer(settled.InitiatorId) ? settled.CounterpartyId : settled.InitiatorId;
        long npcBalance = ctx.NpcOf(npcId)?.Balance ?? 0;
        long playerBalance = ctx.Wallet.BalanceUsd;

        return new SettlementOutcome(
            SettlementStatus.AlreadyApplied,
            settled.Id,
            settled.Kind,
            settled.Direction,
            settled.Amount,
            npcId,
            npcBalance,
            npcBalance,
            playerBalance,
            playerBalance,
            new EconomyLedger(ctx.World).Key("trade", settled.Id),
            settled.LedgerEntryId,
            "这张单已经处于 confirmed 状态（此前结算过），本次未再动钱。");
    }

    private void Audit(TradeContext ctx, SettlementOutcome outcome)
    {
        if (_audit == null) return;

        try
        {
            _audit.Append(ctx.World.WorldId, new AuditRecord
            {
                Kind = AuditKinds.TradeSettled,
                Day = DayForAudit(ctx, outcome.OfferId),
                Season = ctx.EffectiveSeason,
                PersonId = outcome.NpcId,
                CounterpartyId = EconomyTier.PlayerId,
                ReceiptKey = outcome.ReceiptKey,
                LedgerEntryId = outcome.LedgerEntryId,
                OfferId = outcome.OfferId,
                Application = outcome.Kind,
                Direction = outcome.Direction,
                AmountUsd = outcome.AmountUsd,
                BalanceBeforeUsd = outcome.NpcBalanceBefore,
                BalanceAfterUsd = outcome.NpcBalanceAfter,
                PlayerBalanceBeforeUsd = outcome.PlayerBalanceBefore,
                PlayerBalanceAfterUsd = outcome.PlayerBalanceAfter,
                Status = outcome.Status.ToString(),
                Detail = outcome.Detail
            });
        }
        catch (Exception e)
        {
            AuditFailures++;
            _log.Warn("settle", $"结算审计写入失败（已忽略，不影响钱）：{e.GetType().Name}: {e.Message}", e);
        }
    }

    /// <summary>
    /// <b>阶段 4 第五步 D 项：审计行的天数兜底。</b>
    /// <para>
    /// <b>为什么需要它</b>：审计行的 <c>Day</c> 一直取自 <see cref="TradeContext.Day"/>，
    /// 而那个值来自影子存档的水位线 <c>LastTickedDay</c>；<b>老世界的水位线是 0</b>
    /// （真机 <c>audit\e35d0119.jsonl</c> 第 1 行就写着 <c>Day: 0</c>）——
    /// 于是这条审计行没法回答"这是第几天的事"，而 <c>TradeNoteText.RecentSettlements</c>
    /// 要把它渲染成「第 N 天：…」给玩家看。
    /// </para>
    /// <para>
    /// <b>兜底顺序</b>：调用方给的天数（&gt; 0 才信）→ 交易单自己的 <c>CreatedDay</c> → 水位线。
    /// 只用<b>已经存在的事实</b>，绝不编一个数字——三个来源都是 0 时才写 0，
    /// 那时它是诚实的"不知道"，而不是一个假的天数。
    /// </para>
    /// <para>
    /// <b>只对新行生效</b>（甲方拍板）：历史 <c>.jsonl</c> 一行都不改 —— 审计是"只追加、不修改"的文件，
    /// 拿脚本回填会把"当时到底记了什么"改掉，那正是审计最不能失去的东西。
    /// </para>
    /// </summary>
    private static int DayForAudit(TradeContext ctx, string offerId)
    {
        if (ctx.Day > 0) return ctx.Day;

        if (ctx.World.Offers.TryGetValue(offerId, out var offer) && offer.CreatedDay > 0)
            return offer.CreatedDay;

        return ctx.World.LastTickedDay;
    }

    private void Persist(EconomyWorld world, SettlementOutcome outcome)
    {
        if (_persist == null)
        {
            // 没有落盘回调 = 这次的结算活不过重启。宁可吵一句，也不要静默。
            _log.Warn("settle",
                $"交易单 {outcome.OfferId} 结算后【没有落盘回调】，状态只在内存里（重启即丢）。");
            return;
        }

        try
        {
            bool written = _persist(world);
            if (!written) _log.Warn("settle", $"交易单 {outcome.OfferId} 结算后落盘返回失败（状态可能只在内存里）。");
        }
        catch (Exception e)
        {
            _log.Error("settle",
                $"交易单 {outcome.OfferId} 结算后落盘抛异常（钱已动，但状态可能没写盘）：" +
                $"{e.GetType().Name}: {e.Message}", e);
        }
    }
}
