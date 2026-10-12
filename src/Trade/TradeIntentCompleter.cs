using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// <b>意图补全</b>：把 <see cref="TradeProtocol"/> 解析出来的<b>上下文无关</b>标记
/// 补成一条完整、可校验的 <see cref="TradeIntent"/>（方向 + 发起方 + 对手方）。
/// <para>
/// <b>为什么需要它（真机实测 2026-10-11 20:41）</b>：模型发出了
/// <c>[Trade: 赞助, Amount: 5000]</c>，协议层正确地解析出了 Kind 与 Amount，
/// 但 <c>Direction</c>/<c>InitiatorId</c>/<c>CounterpartyId</c> 都是空串
/// （协议层刻意与对话上下文解耦），于是被 <see cref="IntentShapeValidator"/> 以
/// <c>BAD_SHAPE：未知的交易方向：</c> 挡下——玩家看到的是一次"什么都没发生"。
/// </para>
/// <para>
/// <b>方向从哪来（按优先级）</b>：
/// <list type="number">
/// <item>标记里显式写了 <c>Direction:</c>（模型或玩家可以直接指定，最可靠）；</item>
/// <item>配置 <c>trade.kindDirections</c> 里的 <c>Kind → 方向</c> 映射（可审计、可改配置）；</item>
/// <item>都没有 ⇒ <b>拒绝</b>，并在日志里点名是哪个 Kind 没配方向（绝不猜钱的方向）。</item>
/// </list>
/// </para>
/// <para>
/// <b>它只补字段、不动钱</b>：补完的意图仍然要过八条校验器、再由玩家与 NPC 双方确认。
/// </para>
/// </summary>
public static class TradeIntentCompleter
{
    /// <summary>标记里显式方向字段的名字（大小写不敏感）。</summary>
    public static readonly string[] DirectionFieldNames = ["Direction", "方向", "From", "To"];

    /// <summary>
    /// 补全一条意图。
    /// </summary>
    /// <param name="raw">协议层解析出来的意图（方向/双方多半是空的）。</param>
    /// <param name="npcId">本次私信对面的 NPC。</param>
    /// <param name="playerId">玩家 id（通常是 <c>player</c>）。</param>
    /// <param name="config">交易配置（提供 <c>kindDirections</c> 映射）。</param>
    /// <returns>补全后的意图，或 <c>(null, 原因)</c>——原因会原样进日志。</returns>
    public static (TradeIntent? Intent, string? Reason) Complete(TradeIntent raw, string npcId, string playerId,
        TradeConfig config)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(npcId))
            return (null, "拿不到本次私信的 NPC id，无法补全交易双方");

        // ① 方向：显式 > 配置映射。（来源会体现在日志的 `方向 …` 字段里，
        //    这里不再单独留一个只写不读的变量——曾经因此吃了一个 CS0219 警告。）
        string direction = NormalizeDirection(raw.Direction);
        if (!IntentShapeValidator.IsKnownDirection(direction))
            direction = DirectionForKind(config, raw.Kind);

        if (!IntentShapeValidator.IsKnownDirection(direction))
            return (null, $"Kind「{raw.Kind}」既没在标记里写 Direction，也没在 trade.kindDirections 里配置方向" +
                          "——已拒绝（绝不猜钱的方向）");

        // ② 双方：NpcToNpc 在私信场景里拿不到"第二个 NPC"的可靠身份，本步直接拒绝。
        string initiator, counterparty;
        switch (direction)
        {
            case TradeDirections.NpcToPlayer:
                initiator = npcId;
                counterparty = playerId;
                break;
            case TradeDirections.PlayerToNpc:
                initiator = playerId;
                counterparty = npcId;
                break;
            default:
                return (null, "NPC↔NPC 交易在主挂载点是私信时拿不到第二个 NPC 的可靠身份，" +
                              "本步拒绝（等有明确的双 NPC 上下文再接）");
        }

        var completed = raw with
        {
            Direction = direction,
            InitiatorId = initiator,
            CounterpartyId = counterparty
        };
        return (completed, null);
    }

    /// <summary>把标记里写的方向文本规范化成三个常量之一；认不出来返回空串。</summary>
    public static string NormalizeDirection(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string t = text.Trim().Replace(" ", "").Replace("→", "->").Replace("－", "-").ToLowerInvariant();

        if (t is "npctoplayer" or "npc->player" or "npc给玩家" or "npc给钱" or "npc付出" or "npc_to_player")
            return TradeDirections.NpcToPlayer;
        if (t is "playertonpc" or "player->npc" or "玩家给npc" or "我给npc" or "player_to_npc")
            return TradeDirections.PlayerToNpc;
        if (t is "npctonpc" or "npc->npc" or "npc_to_npc")
            return TradeDirections.NpcToNpc;

        // 直接就是常量本身（大小写不敏感）。
        if (string.Equals(t, TradeDirections.NpcToPlayer, StringComparison.OrdinalIgnoreCase))
            return TradeDirections.NpcToPlayer;
        if (string.Equals(t, TradeDirections.PlayerToNpc, StringComparison.OrdinalIgnoreCase))
            return TradeDirections.PlayerToNpc;
        if (string.Equals(t, TradeDirections.NpcToNpc, StringComparison.OrdinalIgnoreCase))
            return TradeDirections.NpcToNpc;

        return "";
    }

    /// <summary>按配置映射取某个 Kind 的方向；没配返回空串。</summary>
    public static string DirectionForKind(TradeConfig config, string? kind)
    {
        if (config == null || string.IsNullOrWhiteSpace(kind)) return "";
        if (!config.KindDirections.TryGetValue(kind.Trim(), out string? mapped)) return "";
        return NormalizeDirection(mapped);
    }

    /// <summary>
    /// <b>回声判定</b>：模型给出的标记是否只是<b>在引用玩家刚打的字</b>。
    /// <para>
    /// <b>为什么必须查（真机实测 2026-10-11 20:41）</b>：那一轮玩家自己把
    /// <c>[Trade: 赞助, Amount: 5000]</c> 打进消息里做测试，NPC 的回复原样引用了它
    /// （原话："这行字你从哪抄来的"）。如果我们把这种"引用"当成 NPC 的报价，
    /// 就等于<b>凭空替 NPC 同意了一笔它没同意的交易</b>——在"不动钱"阶段只是显示错，
    /// 到第二步就会变成真实的错误扣款。
    /// </para>
    /// <para>判据是确定性的：把标记原文与玩家本轮说的话各自压掉空白后比 substring。</para>
    /// </summary>
    public static bool LooksLikePlayerEcho(TradeIntent intent, IEnumerable<string>? playerTexts)
    {
        if (intent == null || playerTexts == null) return false;

        string marker = Compact(intent.RawText);
        if (marker.Length < 6) return false;   // 太短的不判（避免误伤）

        foreach (string text in playerTexts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (Compact(text).Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>压掉所有空白与常见全角空格，便于比对。</summary>
    private static string Compact(string text) =>
        new(text.Where(c => !char.IsWhiteSpace(c) && c != '\u3000').ToArray());
}
