using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Trade;

/// <summary>确认方：NPC 与玩家各算一票。</summary>
public enum TradeConfirmationSide
{
    /// <summary>NPC 侧（在私信里答应了这笔）。</summary>
    Npc,

    /// <summary>玩家侧（在界面上点了确认）。</summary>
    Player
}

/// <summary>
/// 双人确认的当前状态（<b>只读投影</b>，由收据本上的两个幂等键算出来）。
/// </summary>
public sealed record TradeConfirmationStatus(
    string OfferId,
    string State,
    bool NpcAccepted,
    bool PlayerConfirmed,
    bool ReadyToSettle,
    bool Expired,
    string? BlockingCode,
    string? BlockingMessage,
    string DetailText)
{
    /// <summary>只读契约：确认只登记幂等收据，<b>绝不改余额</b>。</summary>
    public bool MutatesLedger => false;

    /// <summary>双方都到齐了没有。</summary>
    public bool BothConfirmed => NpcAccepted && PlayerConfirmed;
}

/// <summary>
/// <b>双人确认闸门</b>（阶段 4 第一步）。
/// <para>
/// 一笔交易要动钱，必须<b>两边都明确同意</b>：NPC 在私信里答应、玩家在界面上确认。
/// 本闸门只把"谁同意了"登记成<b>幂等收据</b>（键 <c>confirm:{worldId}:{offerId}:npc|player</c>），
/// 并据此算出"能不能结算"——<b>它自己一个字节的余额都不动</b>。
/// </para>
/// <para>
/// <b>为什么用收据本而不是新增状态字段</b>：收据本天生幂等（<c>Mark</c> 重复返回 false），
/// 于是"两边各点十次也只算一票"是<b>结构上</b>成立的，不需要额外写状态机代码或改存档 schema；
/// 而且它与动账时用的幂等键（<c>trade:{worldId}:{offerId}</c>）在同一套机制里，便于对账。
/// </para>
/// <para>
/// <b>阶段边界</b>：结算（真正扣款/加款 + 审计落盘）在<b>第二步</b>接线，入口是
/// <see cref="TradeSettlement"/>。<b>动钱的唯一开关是本类的 <see cref="SettlementWired"/></b>，
/// 它默认 <c>false</c>（由 <c>SettlementIsDeliberatelyNotWired</c> 这条测试钉住），
/// 只有游戏侧接线在显式开关下才会打开。
/// </para>
/// </summary>
public static class TradeConfirmationGate
{
    /// <summary>收据键后缀：NPC 那一票。</summary>
    public const string NpcSuffix = ":npc";

    /// <summary>收据键后缀：玩家那一票。</summary>
    public const string PlayerSuffix = ":player";

    /// <summary>新增的失败码（沿用 <see cref="TradeCodes"/> 的风格，集中在这里便于日志检索）。</summary>
    public const string ConfirmPendingCode = "CONFIRM_PENDING";

    /// <summary>
    /// <b>动钱的唯一开关</b>：结算是否已接线。<b>默认 false</b>（阶段 4 第一步的行为）。
    /// <para>
    /// 为什么不是 <c>const bool</c>：<c>const false</c> 会让所有结算分支变成"不可达代码"（CS0162），
    /// 而本项目对构建警告零容忍；改成"静态属性 + 唯一写入口"语义不变，
    /// 还能让真机验证通过环境变量临时打开（<c>NSE_SETTLEMENT=1</c>），不必为"试一次"重新编译。
    /// </para>
    /// </summary>
    public static bool SettlementWired { get; private set; }

    /// <summary>
    /// 打开/关闭结算。<b>只有阶段 4 第二步的接线层才允许调用</b>（游戏侧 <c>EconomyModEntry</c>
    /// 依据 <c>NSE_SETTLEMENT</c> 调用；测试在自己的用例里显式开关并在 finally 里还原）。
    /// </summary>
    public static void WireSettlement(bool wired) => SettlementWired = wired;

    /// <summary>第二步的说明（日志与文档里引用同一句话，避免说法分叉）。</summary>
    public const string SettlementIsNextPhase =
        "阶段 4 第二步：结算（扣款/加款 + 审计落盘）尚未接线；本阶段到「双方已确认，待结算」为止。";

    /// <summary>某一方的幂等键。</summary>
    public static string ConfirmKey(EconomyLedger ledger, string offerId, TradeConfirmationSide side)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return ledger.Key("confirm", offerId) + (side == TradeConfirmationSide.Npc ? NpcSuffix : PlayerSuffix);
    }

    /// <summary>只读查询当前状态，不登记任何东西。</summary>
    public static TradeConfirmationStatus Status(TradeContext ctx, string offerId)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (!ctx.World.Offers.TryGetValue(offerId, out var offer))
            return Blocked(offerId, TradeOfferStates.Pending, false, false,
                TradeCodes.OfferNotFound, $"找不到交易单 {offerId}。");

        var ledger = new EconomyLedger(ctx.World);
        bool npc = ledger.HasReceipt(ConfirmKey(ledger, offerId, TradeConfirmationSide.Npc));
        bool player = ledger.HasReceipt(ConfirmKey(ledger, offerId, TradeConfirmationSide.Player));
        return Compose(ctx, offer, npc, player, note: null);
    }

    /// <summary>
    /// 登记某一方的确认。<b>幂等</b>：同一方重复确认返回 false 变化（不重复登记、不重复写日志）。
    /// <b>不动任何余额</b>。
    /// </summary>
    /// <param name="ctx">交易上下文（只读账本 + 存档）。</param>
    /// <param name="offerId">交易单 id。</param>
    /// <param name="side">确认方。</param>
    /// <param name="note">可选的来源说明（例如"私信里答应"/"界面点击"），只进日志。</param>
    public static TradeConfirmationStatus Confirm(TradeContext ctx, string offerId, TradeConfirmationSide side,
        string? note = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (!ctx.World.Offers.TryGetValue(offerId, out var offer))
            return Blocked(offerId, TradeOfferStates.Pending, false, false,
                TradeCodes.OfferNotFound, $"找不到交易单 {offerId}。");

        var ledger = new EconomyLedger(ctx.World);
        bool npc = ledger.HasReceipt(ConfirmKey(ledger, offerId, TradeConfirmationSide.Npc));
        bool player = ledger.HasReceipt(ConfirmKey(ledger, offerId, TradeConfirmationSide.Player));

        // 状态与过期：与 TradeEngine.Confirm 的前两道闸一致（这里只改状态，不动钱）。
        if (!TradeOfferStates.IsPending(offer.State))
            return Compose(ctx, offer, npc, player,
                $"交易单 {offerId} 已是 {offer.State} 状态，确认被拒绝。");

        if (offer.IsExpired(ctx.Day))
        {
            offer.State = TradeOfferStates.Expired;
            offer.Report = "交易已过期。";
            return Blocked(offerId, offer.State, npc, player, TradeCodes.OfferExpired,
                $"交易单 {offerId} 已于第 {offer.ExpiresDay} 天过期。");
        }

        string key = ConfirmKey(ledger, offerId, side);
        bool first = ledger.MarkReceipt(key);
        ledger.FlushReceipts();

        if (side == TradeConfirmationSide.Npc) npc = true; else player = true;

        string? notice = first
            ? $"（{SideName(side)} 首次确认{(string.IsNullOrWhiteSpace(note) ? "" : "｜来源 " + note)}）"
            : $"（{SideName(side)} 重复确认，已按幂等忽略）";

        return Compose(ctx, offer, npc, player, notice);
    }

    /// <summary>
    /// 某一方拒绝。只落状态与说明，<b>不动钱</b>；已有确认记录不会被抹掉（便于审计"谁先答应过"）。
    /// </summary>
    public static TradeConfirmationStatus Decline(TradeContext ctx, string offerId, TradeConfirmationSide side,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (!ctx.World.Offers.TryGetValue(offerId, out var offer))
            return Blocked(offerId, TradeOfferStates.Pending, false, false,
                TradeCodes.OfferNotFound, $"找不到交易单 {offerId}。");

        if (!TradeOfferStates.IsPending(offer.State))
            return Blocked(offerId, offer.State, false, false, TradeCodes.OfferNotPending,
                $"交易单 {offerId} 已是 {offer.State} 状态，不能再拒绝。");

        offer.State = TradeOfferStates.Declined;
        offer.Report = $"{SideName(side)} 拒绝：{reason}";

        var ledger = new EconomyLedger(ctx.World);
        bool npc = ledger.HasReceipt(ConfirmKey(ledger, offerId, TradeConfirmationSide.Npc));
        bool player = ledger.HasReceipt(ConfirmKey(ledger, offerId, TradeConfirmationSide.Player));

        return Compose(ctx, offer, npc, player, $"（{SideName(side)} 拒绝：{reason}）");
    }

    /// <summary>状态说明里用的人类可读名称。</summary>
    public static string SideName(TradeConfirmationSide side) =>
        side == TradeConfirmationSide.Npc ? "NPC" : "玩家";

    /// <summary>把 <c>"npc"</c> / <c>"player"</c> 之类的文本解析成确认方；解析不出来返回 null。</summary>
    public static TradeConfirmationSide? ParseSide(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.Trim();
        if (t.Equals("npc", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("NPC", StringComparison.Ordinal) ||
            t.Contains("npc", StringComparison.OrdinalIgnoreCase)) return TradeConfirmationSide.Npc;
        if (t.Contains("player", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("玩家", StringComparison.Ordinal) ||
            t.Contains("我", StringComparison.Ordinal)) return TradeConfirmationSide.Player;
        return null;
    }

    // ── 内部 ─────────────────────────────────────────────────────────

    private static TradeConfirmationStatus Compose(TradeContext ctx, TradeOffer offer, bool npc, bool player,
        string? note)
    {
        bool pending = TradeOfferStates.IsPending(offer.State);
        bool expired = offer.IsExpired(ctx.Day);
        bool declined = string.Equals(offer.State, TradeOfferStates.Declined, StringComparison.Ordinal);

        string? code = null, message = null;
        if (!pending && declined)
        {
            code = TradeCodes.OfferNotPending;
            message = "该交易已被拒绝。";
        }
        else if (!pending)
        {
            code = TradeCodes.OfferNotPending;
            message = $"交易单已是 {offer.State} 状态。";
        }
        else if (expired)
        {
            code = TradeCodes.OfferExpired;
            message = $"交易单已于第 {offer.ExpiresDay} 天过期。";
        }
        else if (!(npc && player))
        {
            code = ConfirmPendingCode;
            message = $"还差 {MissingText(npc, player)} 的确认。";
        }

        bool ready = pending && !expired && npc && player;

        string detail = $"【双人确认】交易单 {offer.Id}｜NPC {(npc ? "已确认" : "未确认")}｜" +
                        $"玩家 {(player ? "已确认" : "未确认")}｜" +
                        (ready ? "✅ 双方已确认，待结算" : "⏳ 尚未就绪" +
                            (message == null ? "" : "（" + message + "）")) +
                        (note == null ? "" : "｜" + note);

        return new TradeConfirmationStatus(offer.Id, offer.State, npc, player, ready, expired, code, message, detail);
    }

    private static string MissingText(bool npc, bool player) => (npc, player) switch
    {
        (false, false) => "NPC 与玩家",
        (false, true) => "NPC",
        (true, false) => "玩家",
        _ => "（无）"
    };

    private static TradeConfirmationStatus Blocked(string offerId, string state, bool npc, bool player,
        string code, string message) =>
        new(offerId, state, npc, player, false, false, code, message,
            $"【双人确认】交易单 {offerId}｜NPC {(npc ? "已确认" : "未确认")}｜" +
            $"玩家 {(player ? "已确认" : "未确认")}｜❌ {message}");
}
