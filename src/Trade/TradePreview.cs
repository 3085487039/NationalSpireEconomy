using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// 交易的一侧（只读快照 + 假如成交会怎么变）。
/// <para>
/// 三个余额字段是<b>算出来的</b>，没有任何一个来自"真的动了账"：
/// <see cref="BalanceBeforeUsd"/> 读自当前存档，<see cref="DeltaUsd"/> 由方向决定，
/// <see cref="BalanceAfterUsd"/> 是两者之和。<b>本类型出现的任何地方都不允许写余额。</b>
/// </para>
/// </summary>
public sealed record TradePreviewSide(
    string PartyId,
    bool IsPlayer,
    long BalanceBeforeUsd,
    long DeltaUsd,
    long BalanceAfterUsd)
{
    /// <summary>假如成交，这一侧会变成负数吗（= 余额不足）。</summary>
    public bool WouldGoNegative => BalanceAfterUsd < 0;

    public string Describe() =>
        $"{PartyId}：{MoneyUnits.Format(BalanceBeforeUsd)} → {MoneyUnits.Format(BalanceAfterUsd)}" +
        $"（{(DeltaUsd >= 0 ? "+" : "−")}{MoneyUnits.Format(Math.Abs(DeltaUsd))}）";
}

/// <summary>预览里的一条校验结论（通过项也保留，便于"8 条全过"这种可核对的断言）。</summary>
public sealed record TradePreviewCheck(string Validator, bool Ok, string Code, string Message, bool Soft = false);

/// <summary>
/// <b>只读成交预览</b>（阶段 4 第一步的核心产物）。
/// <para>
/// 它回答"这笔交易如果成交，会是什么样子"：双方余额怎么变、八条校验器各自怎么说、
/// 幂等键是什么、有效期到哪天。<b>它自己一个字节都不写</b>——
/// 契约是 <see cref="MutatesLedger"/> 恒为 false，并由测试用"前后快照逐字段比对"钉住。
/// </para>
/// <para>
/// <b>纪律</b>：<see cref="ReadOnlyText"/> 里一定有金额（数字），所以它<b>只能</b>进 V 段
/// （易变段）或日志，<b>绝不能</b>进 S 段（稳定段零数字，见 <c>ConfigLoader.ValidatePrompt</c>）。
/// </para>
/// </summary>
public sealed record TradePreview(
    string OfferId,
    string WorldId,
    string Kind,
    string Direction,
    string Title,
    long AmountUsd,
    IReadOnlyList<TradeItemLine> Items,
    TradePreviewSide Initiator,
    TradePreviewSide Counterparty,
    TradePreviewSide Player,
    IReadOnlyList<TradePreviewCheck> Checks,
    bool WouldSucceed,
    string? BlockingCode,
    string? BlockingMessage,
    string IdempotencyKey,
    string IntentKey,
    int CurrentDay,
    int CreatedDay,
    int ExpiresDay,
    bool OfferExists,
    bool OfferPending,
    bool Expired,
    string ReadOnlyText)
{
    /// <summary>
    /// <b>只读契约</b>：预览绝不改余额、绝不写流水。恒为 false，且有测试钉着。
    /// </summary>
    public bool MutatesLedger => false;

    /// <summary>通过了几条校验器（形如 "8/8"）。</summary>
    public string CheckSummary =>
        $"{Checks.Count(c => c.Ok)}/{Checks.Count}";

    /// <summary>带上"双方确认到哪一步"的完整文案（供日志与 V 段注入）。</summary>
    public string TextWith(TradeConfirmationStatus? confirmation) =>
        confirmation == null ? ReadOnlyText : ReadOnlyText + Environment.NewLine + confirmation.DetailText;
}

/// <summary>
/// 预览构造器。它<b>只读</b> <see cref="TradeContext"/> 与存档：
/// 校验链本身是纯判定（见各 Validator 的注释），这里也不做任何记账。
/// </summary>
public static class TradePreviewBuilder
{
    /// <summary>对一个<b>还没落单</b>的意图做预览（"如果提交会怎样"）。</summary>
    public static TradePreview ForIntent(TradeContext ctx, TradeIntent intent, TradeEngine engine, string? offerIdHint = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(engine);

        // 提交阶段用完整八条（含幂等），因为"提交前"确实该看幂等怎么说。
        var checks = RunChecks(ctx, intent, engine.Validators);
        var (blocking, message, soft) = FirstBlock(checks);

        var ledger = new EconomyLedger(ctx.World);
        return new TradePreview(
            OfferId: offerIdHint ?? "(尚未落单)",
            WorldId: ctx.World.WorldId,
            Kind: intent.Kind,
            Direction: intent.Direction,
            Title: TitleFor(intent.Kind, intent.Direction),
            AmountUsd: intent.AmountUsd,
            Items: intent.Items,
            Initiator: SideOf(ctx, intent.InitiatorId, intent.Direction, intent.AmountUsd),
            Counterparty: SideOf(ctx, intent.CounterpartyId, intent.Direction, intent.AmountUsd),
            Player: PlayerSide(ctx, intent.Direction, intent.AmountUsd),
            Checks: checks,
            WouldSucceed: blocking == null,
            BlockingCode: blocking,
            BlockingMessage: message,
            IdempotencyKey: offerIdHint == null ? "(未落单)" : ledger.Key("trade", offerIdHint),
            IntentKey: IdempotencyValidator.IntentKey(ledger, intent, ctx.Day),
            CurrentDay: ctx.Day,
            CreatedDay: ctx.Day,
            ExpiresDay: ctx.Day + Math.Max(1, ctx.Config.Trade.OfferExpiryDays),
            OfferExists: false,
            OfferPending: true,
            Expired: false,
            ReadOnlyText: Compose(checks, ctx, intent.Kind, intent.Direction, intent.Title(),
                intent.InitiatorId, intent.CounterpartyId, intent.AmountUsd, intent.Items,
                offerIdHint ?? "(尚未落单)", ctx.Day, ctx.Day + Math.Max(1, ctx.Config.Trade.OfferExpiryDays), soft));
    }

    /// <summary>对一张<b>已存在的交易单</b>做预览（"确认它会发生什么"）。</summary>
    public static TradePreview ForOffer(TradeContext ctx, TradeOffer offer, TradeEngine engine)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(engine);

        var intent = TradeEngine.IntentOf(offer);

        // 确认阶段用去掉幂等的七条：与 TradeEngine.Confirm 完全一致，
        // 否则预览会说"这笔已提交过"，而真正的确认根本不会跑那一条。
        var checks = RunChecks(ctx, intent, engine.ConfirmValidators);

        bool pending = TradeOfferStates.IsPending(offer.State);
        bool expired = offer.IsExpired(ctx.Day);

        string? blocking = null;
        string? message = null;
        bool soft = false;
        if (!pending)
        {
            blocking = TradeCodes.OfferNotPending;
            message = $"交易单 {offer.Id} 已经处于 {offer.State} 状态。";
        }
        else if (expired)
        {
            blocking = TradeCodes.OfferExpired;
            message = $"交易单 {offer.Id} 已于第 {offer.ExpiresDay} 天过期。";
        }
        else
        {
            (blocking, message, soft) = FirstBlock(checks);
        }

        var ledger = new EconomyLedger(ctx.World);
        return new TradePreview(
            OfferId: offer.Id,
            WorldId: ctx.World.WorldId,
            Kind: offer.Kind,
            Direction: offer.Direction,
            Title: TitleFor(offer.Kind, offer.Direction),
            AmountUsd: offer.Amount,
            Items: offer.Items,
            Initiator: SideOf(ctx, offer.InitiatorId, offer.Direction, offer.Amount),
            Counterparty: SideOf(ctx, offer.CounterpartyId, offer.Direction, offer.Amount),
            Player: PlayerSide(ctx, offer.Direction, offer.Amount),
            Checks: checks,
            WouldSucceed: blocking == null,
            BlockingCode: blocking,
            BlockingMessage: message,
            IdempotencyKey: ledger.Key("trade", offer.Id),
            IntentKey: IdempotencyValidator.IntentKey(ledger, intent, offer.CreatedDay),
            CurrentDay: ctx.Day,
            CreatedDay: offer.CreatedDay,
            ExpiresDay: offer.ExpiresDay,
            OfferExists: true,
            OfferPending: pending,
            Expired: expired,
            ReadOnlyText: Compose(checks, ctx, offer.Kind, offer.Direction, TitleFor(offer.Kind, offer.Direction),
                offer.InitiatorId, offer.CounterpartyId, offer.Amount, offer.Items, offer.Id,
                offer.CreatedDay, offer.ExpiresDay, soft));
    }

    // ── 内部 ─────────────────────────────────────────────────────────

    private static IReadOnlyList<TradePreviewCheck> RunChecks(TradeContext ctx, TradeIntent intent,
        IReadOnlyList<ITradeValidator> validators)
    {
        var checks = new List<TradePreviewCheck>();
        foreach (var validator in validators)
        {
            TradeCheck result;
            try
            {
                result = validator.Validate(ctx, intent);
            }
            catch (Exception e)
            {
                // 预览里的异常不能冒泡：它只是一张"会怎样"的说明。
                result = TradeCheck.Fail("PREVIEW_ERROR", validator.Name + " 抛异常：" + e.GetType().Name);
            }

            checks.Add(new TradePreviewCheck(validator.Name, result.Ok, result.Code, result.Message, result.Soft));
        }
        return checks;
    }

    private static (string? Code, string? Message, bool Soft) FirstBlock(IReadOnlyList<TradePreviewCheck> checks)
    {
        foreach (var check in checks)
            if (!check.Ok) return (check.Code, check.Message, check.Soft);
        return (null, null, false);
    }

    /// <summary>
    /// 某一侧会不会变、怎么变。<b>与 <c>TransferExecutor</c> 的规则逐条对齐</b>：
    /// <c>PlayerToNpc</c> ⇒ 玩家出钱；其余情况（<c>NpcToPlayer</c> / <c>NpcToNpc</c>）⇒ 发起方出钱。
    /// 这个对齐有一条测试钉着（预览的 delta 必须等于真跑一遍之后的余额差）。
    /// </summary>
    private static TradePreviewSide SideOf(TradeContext ctx, string partyId, string direction, long amountUsd)
    {
        bool isPlayer = ctx.IsPlayer(partyId);
        long before = isPlayer ? ctx.Wallet.BalanceUsd : ctx.NpcOf(partyId)?.Balance ?? 0;
        long delta = DeltaFor(ctx, partyId, direction, amountUsd);
        return new TradePreviewSide(partyId, isPlayer, before, delta, before + delta);
    }

    private static TradePreviewSide PlayerSide(TradeContext ctx, string direction, long amountUsd)
    {
        long before = ctx.Wallet.BalanceUsd;
        long delta = direction switch
        {
            TradeDirections.PlayerToNpc => -Math.Abs(amountUsd),
            TradeDirections.NpcToPlayer => Math.Abs(amountUsd),
            _ => 0
        };
        return new TradePreviewSide(ctx.LocalHumanId, true, before, delta, before + delta);
    }

    private static long DeltaFor(TradeContext ctx, string partyId, string direction, long amountUsd)
    {
        long amount = Math.Abs(amountUsd);
        if (ctx.IsPlayer(partyId))
            return direction == TradeDirections.NpcToPlayer ? amount : direction == TradeDirections.PlayerToNpc ? -amount : 0;

        // NPC 侧：PlayerToNpc 时对手方收钱；NpcToPlayer / NpcToNpc 时发起方出钱、对手方收钱。
        bool isInitiator = direction != TradeDirections.PlayerToNpc;
        return isInitiator ? -amount : amount;
    }

    private static string TitleFor(string kind, string direction) =>
        direction switch
        {
            TradeDirections.NpcToPlayer => $"{kind}（NPC → 你）",
            TradeDirections.PlayerToNpc => $"{kind}（你 → NPC）",
            TradeDirections.NpcToNpc => $"{kind}（NPC ↔ NPC）",
            _ => kind
        };

    private static string Compose(IReadOnlyList<TradePreviewCheck> checks, TradeContext ctx, string kind,
        string direction, string title, string initiatorId, string counterpartyId, long amountUsd,
        IReadOnlyList<TradeItemLine> items, string offerId, int createdDay, int expiresDay, bool soft)
    {
        var lines = new List<string>
        {
            $"【成交预览·只读】{title}｜交易单 {offerId}",
            $"  发起方：{initiatorId}｜对手方：{counterpartyId}" +
            (items.Count > 0 ? $"｜物品 {string.Join("、", items.Select(i => $"{i.ItemId}×{i.Quantity}"))}" : ""),
            $"  金额：{MoneyUnits.Format(Math.Abs(amountUsd))}",
            "  " + SideOf(ctx, initiatorId, direction, amountUsd).Describe() + "（发起方）",
            "  " + SideOf(ctx, counterpartyId, direction, amountUsd).Describe() + "（对手方）",
            "  " + PlayerSide(ctx, direction, amountUsd).Describe() + "（你）",
            $"  校验：{checks.Count(c => c.Ok)}/{checks.Count} 通过" +
            (checks.Any(c => !c.Ok)
                ? "｜未通过：" + string.Join("；", checks.Where(c => !c.Ok).Select(c => $"{c.Validator}={c.Code}"))
                : ""),
            $"  幂等键：{offerId}｜有效期：第 {expiresDay} 天（今天第 {ctx.Day} 天，创建于第 {createdDay} 天）",
            "  ★本预览不动任何余额；真正动钱要等「双人确认」都到齐之后的结算步骤。"
        };

        if (soft) lines.Add("  （该笔属于「软失败」：NPC 想给但给不起，会用自己的口吻说出来，不落交易单。）");

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>给 <see cref="TradeIntent"/> 起标题用的小扩展（避免两处各写一遍 switch）。</summary>
internal static class TradeTitleExtensions
{
    internal static string Title(this TradeIntent intent) =>
        intent.Direction switch
        {
            TradeDirections.NpcToPlayer => $"{intent.Kind}（NPC → 你）",
            TradeDirections.PlayerToNpc => $"{intent.Kind}（你 → NPC）",
            TradeDirections.NpcToNpc => $"{intent.Kind}（NPC ↔ NPC）",
            _ => intent.Kind
        };
}
