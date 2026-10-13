using System.Text;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Inject;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// <b>阶段 4 第三步：账本真相的措辞</b>——把"这笔交易到底怎么样了"渲染成
/// ① 玩家读得懂的回执、② 模型不敢编的依据。
/// <para>
/// <b>为什么单独一个零依赖文件</b>：这些话要同时进 V 段（模型与玩家都看得见）、
/// 官方弹窗（只有玩家看得见）、日志（只有我们看得见），三处必须<b>逐字一致</b>——
/// 各写各的措辞就会分叉，而真机排查最怕"日志说的和界面说的不是一件事"。
/// </para>
/// <para>
/// <b>纪律</b>：这些文本<b>一定含数字（金额）</b>，所以只能进易变段（V 段）或弹窗/日志，
/// <b>绝不能</b>进 S 段（稳定段零数字，见 <c>ConfigLoader.ValidatePrompt</c>）。
/// </para>
/// </summary>
public static class TradeNoteText
{
    /// <summary>
    /// <b>V 段里那条"账本口径"</b>（阶段 4 第三步 §7.3）。
    /// <para>
    /// 它必须是常量且每次都写：模型在正文里宣布"钱已经转过去了"是自由的，
    /// 而它的话还会被原版存进聊天历史，于是下一轮它把"我说过要给"当成"我已经给了"
    /// （真机三轮实测）。这条规则告诉它：<b>只有账本口径才算数</b>。
    /// </para>
    /// <para>
    /// 它没有金额、也没有天数，但依然留在 V 段而不是 S 段——它描述的是"账本口径"这件事，
    /// 与交易状态强相关；放进 S 段还会让 S 段的"零数字 + 逐字节稳定"纪律承担额外风险。
    /// </para>
    /// </summary>
    public const string LedgerRuleLine =
        "【账本口径】只有上面写着「已结算」的交易才算这笔钱真的动过；" +
        "你在正文里说「我已经转过去了」不会让钱动，账本也不会因此变化。";

    /// <summary>V 段里没有"待确认预览"也没有"最近一笔"时的兜底说明（避免模型以为交易系统坏了）。</summary>
    public const string NoRecentTradeLine = "【最近交易】还没有任何交易记录。";

    // ── 回执（结算成功 / 幂等命中）────────────────────────────────────

    /// <summary>
    /// 一行回执：给玩家看"成了没有、钱去哪了、现在剩多少"。
    /// <para>
    /// <b>阶段 4 第五步 A 项：方向必须说对。</b>原先这句把「（{who} → 你）」写死了——
    /// 那在"只开通 NPC→玩家"的首笔范围里是对的，而放开了 <c>player_to_npc</c> 之后
    /// 就变成假话：玩家出钱的交易会被渲染成"NPC 给你钱"。所以两个方向各有各的句式，
    /// 判据就用 <see cref="SettlementOutcome.Direction"/>（它本来就带着方向，不需要新参数）。
    /// </para>
    /// </summary>
    public static string ReceiptLine(SettlementOutcome outcome, string npcLabel)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        string who = string.IsNullOrWhiteSpace(npcLabel) ? outcome.NpcId : npcLabel;
        string id = IdPart(outcome.OfferId, outcome.LedgerEntryId);
        string kind = outcome.Kind.Length > 0 ? outcome.Kind : "交易";
        string amount = MoneyUnits.Format(outcome.AmountUsd);
        bool playerPaid = PlayerPaid(outcome.Direction);

        return outcome.Status switch
        {
            SettlementStatus.Applied when playerPaid =>
                $"【已结算】{kind} {amount}（你 → {who}）｜你的影子余额 " +
                $"{MoneyUnits.Format(outcome.PlayerBalanceBefore)} → {MoneyUnits.Format(outcome.PlayerBalanceAfter)}" +
                $"（减少）｜{who} {MoneyUnits.Format(outcome.NpcBalanceBefore)} → {MoneyUnits.Format(outcome.NpcBalanceAfter)}" +
                $"（增加）｜{id}",
            SettlementStatus.Applied =>
                $"【已结算】{kind} {amount}（{who} → 你）｜你的影子余额 " +
                $"{MoneyUnits.Format(outcome.PlayerBalanceBefore)} → {MoneyUnits.Format(outcome.PlayerBalanceAfter)}" +
                $"｜{who} {MoneyUnits.Format(outcome.NpcBalanceBefore)} → {MoneyUnits.Format(outcome.NpcBalanceAfter)}" +
                $"｜{id}",
            SettlementStatus.AlreadyApplied =>
                $"【已结算】这笔之前就结算过了，本次没有再动钱｜你的影子余额 " +
                $"{MoneyUnits.Format(outcome.PlayerBalanceAfter)}｜{who} {MoneyUnits.Format(outcome.NpcBalanceAfter)}" +
                $"｜{id}",
            _ => "【交易】" + outcome.ReceiptText
        };
    }

    /// <summary>
    /// 多行回执正文：给结果弹窗看（玩家点完按钮当场就要知道成没成）。
    /// 第一行与 <see cref="ReceiptLine"/> 同源，弹窗与 V 段因此不会分叉。
    /// </summary>
    public static string ReceiptDetail(SettlementOutcome outcome, string npcLabel)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        string who = string.IsNullOrWhiteSpace(npcLabel) ? outcome.NpcId : npcLabel;
        var lines = new List<string> { ReceiptLine(outcome, npcLabel) };

        if (outcome.Settled)
        {
            lines.Add($"  双方余额：{who} {MoneyUnits.Format(outcome.NpcBalanceBefore)} → " +
                      $"{MoneyUnits.Format(outcome.NpcBalanceAfter)}；你的影子余额 " +
                      $"{MoneyUnits.Format(outcome.PlayerBalanceBefore)} → " +
                      $"{MoneyUnits.Format(outcome.PlayerBalanceAfter)}");
            if (outcome.LedgerEntryId.Length > 0)
                lines.Add($"  流水号：{outcome.LedgerEntryId}");
            if (outcome.ReceiptKey.Length > 0)
                lines.Add($"  幂等键：{outcome.ReceiptKey}（同一笔重复结算不会再动钱）");
            lines.Add("  这笔已写进审计文件 audit/<WorldId>.jsonl，重启后依然有效。");
        }
        else
        {
            if (outcome.Detail.Length > 0) lines.Add("  原因：" + outcome.Detail);
            lines.Add("  ⚠ 钱一分都没有动。上面这条会写进你的私信经济段，NPC 那边也看得到。");
        }

        return string.Join('\n', lines);
    }

    /// <summary>未成立（被拒 / 没能落单 / 结算被挡）时的一句话。</summary>
    public static string NotSettledLine(LastTradeNote note, string npcLabel)
    {
        ArgumentNullException.ThrowIfNull(note);
        string who = string.IsNullOrWhiteSpace(npcLabel) ? "对方" : npcLabel;
        string kind = note.Kind.Length > 0 ? note.Kind : "交易";
        string amount = note.AmountUsd > 0 ? MoneyUnits.Format(note.AmountUsd) : "（金额未定）";
        string reason = note.Reason.Length > 0 ? note.Reason : "没有通过校验";
        string id = note.OfferId.Length > 0 ? $"｜交易单 {note.OfferId}" : "";
        // 阶段 4 第五步：`player_to_npc` 放开之后，"谁给谁"不再总是"NPC → 你"。
        string flow = PlayerPaid(note.Direction) ? $"你 → {who}" : $"{who} → 你";

        return note.Status switch
        {
            TradeNoteStatuses.Declined =>
                $"【未成立】{kind} {amount}（{flow}）被你拒绝了，钱一分没动{id}。",
            TradeNoteStatuses.Incomplete =>
                $"【未成立】上一笔与{who}的{kind}意图没说清（{Trim(reason)}），没有落单，钱一分没动{id}。",
            // 刻意【不说】"已结算"，也不说"钱一分没动"——我们真的不知道。
            // 只凭一个 Confirmed 状态位就宣布其中任何一种，都是在替账本撒谎。
            TradeNoteStatuses.Unverified =>
                $"【无法核实】{kind} {amount}（{flow}）单子上写着已确认，但账本里找不到对应流水{id}。" +
                $"它可能很久以前真的结算过（流水已被裁剪），也可能根本没结算——{Trim(reason)}",
            TradeNoteStatuses.Failed =>
                $"【未成立】{kind} {amount}（{flow}）没能结算：{Trim(reason)}｜钱一分没动{id}。",
            _ =>
                $"【未成立】{kind} {amount}（{flow}）没有落单：{Trim(reason)}｜钱一分没动{id}。"
        };
    }

    // ── 审计回读（§7.4）──────────────────────────────────────────────

    /// <summary>
    /// 把"最近一笔交易"压成一句<b>日志用</b>的账本口径，供"嘴上说转了"那条哨兵引用。
    /// <para>
    /// <b>为什么要它</b>：那条哨兵原先只写"账上一分未动"，可它的判据只是"本轮模型没发标记"——
    /// 而真机上完全可能是"钱上一轮就真的转过、这一轮它只是在复述"。
    /// 两种情况的处置完全不同（前者是模型在编，后者是模型在讲已经发生的事），
    /// 所以必须把账本真相摆在同一行里，让看日志的人一眼分开。
    /// </para>
    /// <para><b>纯函数</b>：不写任何东西，读不到就返回 null（调用方据此不追加）。</para>
    /// </summary>
    public static string? LedgerSummary(LastTradeNote? note)
    {
        if (note == null || note.Status.Length == 0) return null;

        string who = note.NpcId.Length > 0 ? note.NpcId : "对方";
        string id = note.OfferId.Length > 0 ? "｜" + note.OfferId : "";
        // 阶段 4 第五步：方向放开后，"谁给谁"必须按 note.Direction 说，不能写死成 NPC 给你。
        bool playerPaid = PlayerPaid(note.Direction);
        string settledPhrase = playerPaid
            ? $"你已给过 {who} {MoneyUnits.Format(note.AmountUsd)}"
            : $"{who} 已给过你 {MoneyUnits.Format(note.AmountUsd)}";
        string againPhrase = playerPaid
            ? $"你给 {who} 的那一笔 {MoneyUnits.Format(note.AmountUsd)} 之前就已结算过"
            : $"{who} 那一笔 {MoneyUnits.Format(note.AmountUsd)} 之前就已结算过";

        return note.Status switch
        {
            TradeNoteStatuses.Settled =>
                $"账本：{settledPhrase}（{note.OfferId}，钱确实动过）",
            TradeNoteStatuses.AlreadySettled =>
                $"账本：{againPhrase}（本次未再动钱）",
            TradeNoteStatuses.Declined =>
                $"账本：最近一笔 {who} 的 {MoneyUnits.Format(note.AmountUsd)} 被你拒绝，钱没动{id}",
            TradeNoteStatuses.Failed or TradeNoteStatuses.Incomplete or TradeNoteStatuses.Refused =>
                $"账本：最近一笔 {MoneyUnits.Format(note.AmountUsd)}【未成立】（{Trim(note.Reason)}），钱没动{id}",
            _ => $"账本：最近一笔的状态是 {note.Status}，钱没动{id}"
        };
    }
    /// <summary>把审计记录里的最近几笔结算渲染成一段说明。没有记录时返回 null。</summary>
    public static string? RecentSettlements(IEnumerable<AuditRecord>? records, int take = 3)
    {
        if (records == null || take <= 0) return null;

        var settled = records.Where(r => r.Kind == AuditKinds.TradeSettled).ToList();
        if (settled.Count == 0) return null;

        var lines = new List<string> { $"【最近结算】共 {settled.Count} 笔，最近 {Math.Min(take, settled.Count)} 笔：" };
        foreach (var record in settled.AsEnumerable().Reverse().Take(take))
        {
            string who = record.PersonId.Length > 0 ? record.PersonId : "对方";
            string id = record.OfferId.Length > 0 ? $"｜{record.OfferId}" : "";
            string flow = string.IsNullOrWhiteSpace(record.Direction) ? "" : $"｜{record.Direction}";
            // 阶段 4 第五步：老审计行没有 Direction（空串）时按"NPC → 你"渲染——
            // 那不是猜：老行全部出自只开通 NPC→玩家 的那个阶段。
            bool playerPaid = PlayerPaid(record.Direction);
            string income = playerPaid ? "你支出" : "你收入";
            lines.Add($"  第 {record.Day} 天：{who} {MoneyUnits.Format(record.AmountUsd)}" +
                      $"（{who} 余额 {MoneyUnits.Format(record.BalanceAfterUsd)}｜你的影子余额 " +
                      $"{MoneyUnits.Format(record.PlayerBalanceAfterUsd)}｜{income}）{id}{flow}");
        }
        return string.Join('\n', lines);
    }

    // ── 内部 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 这笔钱是不是<b>玩家出的</b>（<c>player_to_npc</c>）。
    /// <para>
    /// 只有这一个方向会让玩家的影子余额减少，所以所有"谁给谁"的措辞都该只问它。
    /// 方向为空串（老审计行、老存档）时返回 false ⇒ 按"NPC → 你"渲染，
    /// 这与"那些行确实出自只开通 NPC→玩家 的阶段"这一事实一致。
    /// </para>
    /// </summary>
    private static bool PlayerPaid(string? direction) =>
        string.Equals(direction, TradeDirections.PlayerToNpc, StringComparison.Ordinal);

    private static string IdPart(string offerId, string ledgerEntryId)
    {
        var parts = new List<string>();
        if (offerId.Length > 0) parts.Add("单号 " + offerId);
        if (ledgerEntryId.Length > 0) parts.Add("流水 " + Short(ledgerEntryId));
        return parts.Count > 0 ? string.Join("｜", parts) : "（无单号）";
    }

    /// <summary>流水 id 很长，日志与界面里只留前 8 位，够对账了。</summary>
    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    /// <summary>把原因压成一句（拒绝原因里有换行时不能把一行拆成两行）。</summary>
    private static string Trim(string reason)
    {
        var flat = new StringBuilder(reason.Length);
        foreach (char c in reason)
            flat.Append(c is '\r' or '\n' ? ' ' : c);
        string text = flat.ToString().Trim();
        return text.Length > 120 ? text[..120] + "…" : text;
    }
}
