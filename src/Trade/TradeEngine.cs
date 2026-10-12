using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// 交易引擎：把"模型输出 → 解析 → 校验链 → 待确认交易单 → 玩家确认 → 动账"
/// 串成一条线。<b>它自己不动任何余额</b>，动账一律委托给执行器。
/// <para>
/// 两段式的核心纪律：<see cref="Submit"/> 只生成待确认单（<c>Reserve</c>，不动余额），
/// <see cref="Confirm"/> 在玩家点确认时<b>重新跑一遍完整校验链</b>才动账。
/// 这条纪律不能省——报价与确认之间可能隔着很久，余额、性格、归属都可能已经变了。
/// 原版 <c>PrivateContracts.Confirm</c> 就是这么做的。
/// </para>
/// </summary>
public sealed class TradeEngine
{
    private readonly TransferExecutor _transfers = new();
    private readonly PurchaseExecutor _purchases;
    private readonly IEconomyLog _log;

    public TradeEngine(EconomyConfig config, PersonalityConfig personality, PromptConfig prompt,
        IEconomyLog? log = null, ITradeCatalog? catalog = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Personality = personality ?? throw new ArgumentNullException(nameof(personality));
        Prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        Catalog = catalog ?? NullTradeCatalog.Instance;
        _log = log ?? ConsoleEconomyLog.Instance;
        _purchases = new PurchaseExecutor { Inventory = NullTradeInventory.Instance };

        Validators =
        [
            new IntentShapeValidator(),
            new AmountBoundsValidator(),
            new CatalogValidator(),
            new BalanceValidator(),
            new DailyLimitValidator(),
            new PersonalityFitValidator(),
            new FavourValidator(),
            new IdempotencyValidator()
        ];

        // 确认链 = 提交链去掉幂等那一条（见 ConfirmValidators 的说明）。
        ConfirmValidators = Validators.Where(v => v is not IdempotencyValidator).ToArray();
    }

    public EconomyConfig Config { get; }
    public PersonalityConfig Personality { get; }
    public PromptConfig Prompt { get; }
    public ITradeCatalog Catalog { get; }

    /// <summary>
    /// 提交阶段的校验链。共 8 条，<b>第一条失败即返回</b>。
    /// 顺序是刻意排的：先廉价的结构校验，再昂贵的性格计算。
    /// </summary>
    public IReadOnlyList<ITradeValidator> Validators { get; init; }

    /// <summary>
    /// 确认阶段的校验链：与 <see cref="Validators"/> 相同，但<b>去掉幂等校验器</b>。
    /// <para>
    /// 必须去掉：意图键在 <c>Reserve</c> 成功时就登记了，确认时再跑一遍会把自己判成"重复提交"。
    /// 确认阶段真正的防重有两道——"交易单状态必须仍是 pending"，
    /// 以及动账时的幂等键 <c>trade:{worldId}:{offerId}</c>。
    /// </para>
    /// </summary>
    public IReadOnlyList<ITradeValidator> ConfirmValidators { get; init; }

    // ─────────────────────────────────────────────────────────────────
    // Submit：模型输出 → 待确认交易单
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 提交一个交易意图。校验全部通过后生成 <c>pending</c> 交易单，<b>不动任何余额</b>。
    /// <para>
    /// 软失败（NPC 想给钱但余额不足）在这里被识别：<b>不落交易单、不生成失败卡片</b>，
    /// 只把素材写进 <c>PendingVoice</c>、记一条意向触发、累加拒绝次数。
    /// </para>
    /// </summary>
    public ReserveResult Submit(TradeContext ctx, TradeIntent intent)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(intent);

        var check = RunValidators(ctx, intent);

        if (check.Soft)
        {
            ApplySoftDecline(ctx, intent, check);
            return ReserveResult.Soft(check);
        }

        if (check.Failed)
        {
            // 性格上的拒绝要留下痕迹：NPC 会"记住"被人拿小钱试探或被开出不合理的价。
            // 这条只在硬失败时记 —— 软失败是余额不足，不是被小看（见 ApplySoftDecline）。
            if (PersonalityFitValidator.IsPersonalityRefusal(check.Code))
                RecordCharityRefusal(ctx, intent, check);

            _log.Info("trade", $"意图被拒（{check.Code}）：{check.Message}");
            return ReserveResult.Rejected(check);
        }

        var offer = BuildOffer(ctx, intent, check);

        // 幂等：把意图键登记下来，模型重试发同样的标记时会被校验器 8 挡下。
        var ledger = new EconomyLedger(ctx.World);
        ledger.MarkReceipt(IdempotencyValidator.IntentKey(ledger, intent, ctx.Day));
        ledger.FlushReceipts();

        _log.Info("trade",
            $"生成待确认交易单 {offer.Id}：{offer.Kind} {MoneyUnits.Format(offer.Amount)}（{offer.Direction}）。");

        return new ReserveResult(true, offer.Id, check);
    }

    private TradeOffer BuildOffer(TradeContext ctx, TradeIntent intent, TradeCheck check) =>
        StoreOffer(ctx, new TradeOffer
        {
            Id = NextOfferId(ctx.World, intent),
            Kind = intent.Kind,
            Direction = intent.Direction,
            InitiatorId = intent.InitiatorId,
            CounterpartyId = intent.CounterpartyId,
            Amount = intent.AmountUsd,
            Items = intent.Items.ToList(),
            Reason = intent.Reason,
            State = TradeOfferStates.Pending,
            CreatedDay = ctx.Day,
            Season = ctx.EffectiveSeason,
            ExpiresDay = ctx.Day + Math.Max(1, Config.Trade.OfferExpiryDays),
            ValidationStamp = check.Code
        });

    /// <summary>
    /// 软失败的落地：写 <c>PendingVoice</c>、记意向触发、累加拒绝次数。
    /// <b>不落交易单</b>，所以玩家侧完全看不到硬报错。
    /// </summary>
    private void ApplySoftDecline(TradeContext ctx, TradeIntent intent, TradeCheck check)
    {
        string npcId = ctx.IsPlayer(intent.InitiatorId) ? intent.CounterpartyId : intent.InitiatorId;
        var npc = ctx.NpcOf(npcId);
        if (npc == null)
        {
            _log.Warn("trade", $"软失败找不到 NPC {npcId}，已忽略。");
            return;
        }

        npc.PendingVoice = check.InjectLine;
        npc.AppendTrigger(new IntentTrigger
        {
            Day = ctx.Day,
            Kind = "trade_soft_declined",
            Detail = intent.Kind,
            Delta = -0.05
        });
        npc.Lifetime.RefusedCount++;
        npc.LastTouchedDay = Math.Max(npc.LastTouchedDay, ctx.Day);

        _log.Info("trade", $"{npcId} 余额不足，转为软失败（不落交易单）。");
    }

    // ─────────────────────────────────────────────────────────────────
    // Confirm：玩家确认 → 重新校验 → 动账
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 玩家确认。步骤顺序不可颠倒：取单 → 查状态与过期 → <b>重新校验</b> → 幂等 → 动账 → 落状态。
    /// </summary>
    public CommitResult Confirm(TradeContext ctx, string offerId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (string.IsNullOrWhiteSpace(offerId))
            return CommitResult.Failed(TradeCheck.Fail(TradeCodes.OfferNotFound, "交易单 id 为空。"));

        if (!ctx.World.Offers.TryGetValue(offerId, out var offer))
            return CommitResult.Failed(TradeCheck.Fail(TradeCodes.OfferNotFound, $"找不到交易单 {offerId}。"));

        long npcBefore = ctx.NpcOf(offer.CounterpartyId)?.Balance ?? 0;

        // 1) 状态与过期
        if (!TradeOfferStates.IsPending(offer.State))
            return CommitResult.Failed(TradeCheck.Fail(TradeCodes.OfferNotPending,
                $"交易单 {offerId} 已经处于 {offer.State} 状态，不能重复执行。"), npcBefore, ctx.Wallet.BalanceUsd);

        if (offer.IsExpired(ctx.Day))
        {
            offer.State = TradeOfferStates.Expired;
            offer.Report = "交易已过期。";
            return CommitResult.Failed(TradeCheck.Fail(TradeCodes.OfferExpired,
                $"交易单 {offerId} 已于第 {offer.ExpiresDay} 天过期。"), npcBefore, ctx.Wallet.BalanceUsd);
        }

        // 2) 重新校验。注意：失败时【不静默改金额】——原版是重新报错让玩家重谈。
        var intent = IntentOf(offer);
        var check = RunValidators(ctx, intent, ConfirmValidators);

        if (check.Soft)
        {
            ApplySoftDecline(ctx, intent, check);
            offer.State = TradeOfferStates.Invalid;
            offer.Report = "";
            return CommitResult.Failed(check, npcBefore, ctx.Wallet.BalanceUsd);
        }

        if (check.Failed)
        {
            offer.State = TradeOfferStates.Invalid;
            offer.Report = check.Message;

            // 与 Submit 对称：确认阶段被性格拒了，同样要记仇。
            if (PersonalityFitValidator.IsPersonalityRefusal(check.Code))
                RecordCharityRefusal(ctx, intent, check);

            _log.Info("trade", $"确认时重新校验失败（{check.Code}）：{check.Message}");
            return CommitResult.Failed(check, npcBefore, ctx.Wallet.BalanceUsd);
        }

        // 3) 动账。幂等键 = "trade:{worldId}:{offerId}"
        var ledger = new EconomyLedger(ctx.World);
        string key = ledger.Key("trade", offerId);
        string title = TitleFor(offer);

        var outcome = offer.HasItems
            ? _purchases.Execute(ctx, offer, key, title)
            : _transfers.Execute(ctx, offer, key, title);

        if (outcome.Status == ExecStatus.AlreadyApplied)
        {
            offer.State = TradeOfferStates.Confirmed;
            offer.LedgerEntryId = outcome.LedgerEntryId;
            offer.Report = "该交易单已经执行过。";
            return CommitResult.Failed(
                TradeCheck.Fail(TradeCodes.AlreadyApplied, "该交易单已经执行过，余额未再变动。"),
                npcBefore, ctx.Wallet.BalanceUsd);
        }

        if (!outcome.Applied)
        {
            offer.State = TradeOfferStates.Invalid;
            offer.Report = outcome.Detail ?? "执行失败。";
            return CommitResult.Failed(
                TradeCheck.Fail(TradeCodes.ExecuteFailed, offer.Report), npcBefore, ctx.Wallet.BalanceUsd);
        }

        // 4) 成功：落状态、记流水、给意向演进留数据。
        offer.State = TradeOfferStates.Confirmed;
        offer.LedgerEntryId = outcome.LedgerEntryId;
        offer.Report = "";
        RecordTradeTrigger(ctx, offer);

        _log.Info("trade",
            $"交易单 {offerId} 已执行：{offer.Kind} {MoneyUnits.Format(offer.Amount)}，流水 {outcome.LedgerEntryId}。");

        return new CommitResult(true, TradeCheck.Pass, outcome.LedgerEntryId,
            outcome.NpcBalanceAfter, outcome.PlayerBalanceAfter);
    }

    /// <summary>
    /// 记一次"性格上的拒绝"，让 NPC 记住这件事。
    /// <para>
    /// 这是 <c>resentful</c> 档的全部数据来源：<see cref="Inject.IntentEvolve"/> 会把
    /// <c>LastRefusedCharityDay</c> 写进影子存档，之后 7 天内这个人的意向措辞都会偏向
    /// "最近有点介意被人拿小钱试探过"。
    /// </para>
    /// <para>
    /// <b>只在硬失败时调用</b>（<c>PERSONALITY_REFUSES_CHARITY</c> / <c>PERSONALITY_CAP</c>）。
    /// 软失败 <c>NPC_SOFT_DECLINE</c> 是"想给但给不起"，与"被小看"是两件事，不能记仇。
    /// </para>
    /// </summary>
    private void RecordCharityRefusal(TradeContext ctx, TradeIntent intent, TradeCheck check)
    {
        string npcId = ctx.IsPlayer(intent.InitiatorId) ? intent.CounterpartyId : intent.InitiatorId;
        var npc = ctx.NpcOf(npcId);
        if (npc == null) return;

        Inject.IntentEvolve.Record(npc, Inject.DriftTriggers.CharityRefused,
            check.Code + "/" + intent.Kind, ctx.Day);

        _log.Info("trade",
            $"{npcId} 因性格拒绝了「{intent.Kind}」（{check.Code}），已记入意向漂移。");
    }

    /// <summary>玩家拒绝一张待确认交易单。不产生任何金额变动。</summary>
    public void Decline(TradeContext ctx, string offerId, string reason)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!ctx.World.Offers.TryGetValue(offerId, out var offer)) return;
        if (!TradeOfferStates.IsPending(offer.State)) return;

        offer.State = TradeOfferStates.Declined;
        offer.Report = reason;
        _log.Info("trade", $"交易单 {offerId} 被玩家拒绝：{reason}");
    }

    /// <summary>
    /// 把超期未确认的交易单标记为 <c>expired</c>。由 <see cref="EconomyTick"/> 在推进时间时调用。
    /// 返回本次标记的张数。
    /// </summary>
    public int ExpireStale(EconomyWorld world, int day)
    {
        ArgumentNullException.ThrowIfNull(world);

        int count = 0;
        foreach (var offer in world.Offers.Values)
        {
            if (!TradeOfferStates.IsPending(offer.State)) continue;
            if (!offer.IsExpired(day)) continue;

            offer.State = TradeOfferStates.Expired;
            offer.Report = $"超过 {Config.Trade.OfferExpiryDays} 天未确认，已自动失效。";
            count++;
        }

        if (count > 0) _log.Info("trade", $"第 {day} 天清理了 {count} 张过期交易单。");
        return count;
    }

    // ─────────────────────────────────────────────────────────────────
    // 内部
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 按顺序跑校验链，<b>第一条失败即返回</b>。
    /// 顺序是刻意排的：先廉价的结构校验，再昂贵的性格计算。
    /// </summary>
    /// <param name="chain">要跑的链。缺省为提交链；确认阶段必须显式传 <see cref="ConfirmValidators"/>。</param>
    public TradeCheck RunValidators(TradeContext ctx, TradeIntent intent, IReadOnlyList<ITradeValidator>? chain = null)
    {
        foreach (var validator in chain ?? Validators)
        {
            var check = validator.Validate(ctx, intent);
            if (!check.Ok) return check;
        }
        return TradeCheck.Pass;
    }

    /// <summary>把交易单还原成一个意图，用于确认时重新校验。</summary>
    public static TradeIntent IntentOf(TradeOffer offer) => new(
        offer.Kind,
        offer.Direction,
        offer.InitiatorId,
        offer.CounterpartyId,
        offer.Amount,
        offer.Items,
        offer.Reason,
        RawText: "");

    /// <summary>
    /// 生成交易单 id。用<b>世界内的递增序号</b>而不是随机数：
    /// 一是幂等键 <c>trade:{worldId}:{offerId}</c> 因此可复现，
    /// 二是"同一张单"在存档里永远指向同一笔流水，重启后也能对上账。
    /// </summary>
    private static string NextOfferId(EconomyWorld world, TradeIntent intent)
    {
        int next = world.Offers.Count + 1;
        string candidate;
        do
        {
            candidate = "o" + next.ToString("D4");
            next++;
        }
        while (world.Offers.ContainsKey(candidate));

        return candidate;
    }

    /// <summary>
    /// 落盘一张交易单。超过 <see cref="EconomyWorld.MaxOffers"/> 时<b>只裁终态单</b>，
    /// 绝不裁还在等玩家确认的那张——这与幂等收据"保护优先于上限"是同一条纪律。
    /// </summary>
    private static TradeOffer StoreOffer(TradeContext ctx, TradeOffer offer)
    {
        ctx.World.Offers[offer.Id] = offer;
        TrimOffers(ctx.World);
        return offer;
    }

    private static void TrimOffers(EconomyWorld world)
    {
        if (world.Offers.Count <= EconomyWorld.MaxOffers) return;

        // 只淘汰终态单，最旧的先走。若全是待确认单，那就一张都不裁：
        // 裁掉待确认单会让玩家点确认时"单子凭空消失"，比存档略大严重得多。
        var evictable = world.Offers
            .Where(pair => TradeOfferStates.IsTerminal(pair.Value.State))
            .OrderBy(pair => pair.Value.CreatedDay)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (string id in evictable)
        {
            if (world.Offers.Count <= EconomyWorld.MaxOffers) break;
            world.Offers.Remove(id);
        }
    }

    private static string TitleFor(TradeOffer offer) => offer.Kind switch
    {
        TradeKinds.Sponsor => "赞助",
        TradeKinds.Tip => "收到打赏",
        TradeKinds.Hire => "雇佣",
        TradeKinds.Bonus => "发放奖金",
        TradeKinds.Transfer => "转账",
        TradeKinds.Loan => "借款",
        _ => offer.Kind
    };

    /// <summary>
    /// 成交后记录一条意向触发，供第 6 步的意向演进使用。
    /// <b>不在这里累加 <c>Lifetime.TradeCount</c></b>——那一笔由 <see cref="EconomyLedger"/> 内部记，
    /// 重复加会让统计翻倍。
    /// </summary>
    private static void RecordTradeTrigger(TradeContext ctx, TradeOffer offer)
    {
        string npcId = ctx.IsPlayer(offer.InitiatorId) ? offer.CounterpartyId : offer.InitiatorId;
        var npc = ctx.NpcOf(npcId);
        if (npc == null) return;

        npc.AppendTrigger(new IntentTrigger
        {
            Day = ctx.Day,
            Kind = offer.Direction == TradeDirections.PlayerToNpc ? "trade_received" : "trade_paid",
            Detail = offer.Kind,
            Delta = offer.Direction == TradeDirections.PlayerToNpc ? 0.05 : 0.02
        });
    }
}
