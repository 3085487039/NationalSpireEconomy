using System.Text;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using static NationalSpireEconomy.Trade.TradeIntent;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// <c>[Trade: ...]</c> / <c>[Buy: ...]</c> 标记的解析器，兼「正文剔除器」。
/// <para>
/// <b>为什么需要它</b>：原版 <c>PrivateMessages.Known</c> 是一张写死的正则白名单
/// （<c>PrivateMessages.cs:311</c>），<c>Trade</c> 不在其中，所以 <c>[Trade: ...]</c>
/// 现在会被当作正文直接显示给玩家。我们不能改原版，于是改用前缀补丁
/// <c>PrivateStreamParser.Feed</c>：先进我们的扫描器抽出标记，把剔除后的干净文本再交给原方法。
/// </para>
/// <para>
/// <b>必须增量</b>：原版的流式解析是按任意网络分片喂入的，一次 <see cref="Feed"/>
/// 可能只拿到 <c>"[Tra"</c>。因此本类是状态机，未闭合的标记一律留在待定缓冲里，
/// 绝不假设一次调用能拿到完整括号。
/// </para>
/// <para>
/// <b>保守原则</b>：只有"括号内第一个 <c>:</c> 之前的词"确实是 <c>Trade</c>/<c>Buy</c> 才剔除。
/// 不认识的标记（例如 <c>[Match: ...]</c>）原样放行，<b>且不把嵌套在内的 Trade 标记剥掉</b>——
/// 剥掉会破坏原版自己的解析器。
/// </para>
/// </summary>
public sealed class TradeProtocol
{
    /// <summary>
    /// 识别为交易标记的关键词（大小写不敏感）。
    /// <para>
    /// <b>★ 每一个"提示词让模型写"的标记都必须在这里登记</b>，否则玩家会在对话里看到它 ——
    /// 真机为"标记显示给玩家"这件事来回过 4 轮（见 HANDOFF-PHASE-4-STEP5.md §6.6），
    /// 而剥离器的关键词表就是这件事的最后一道闸。
    /// </para>
    /// <para>
    /// <c>Deal</c> 是 <c>[Deal: accept]</c>（提示词要求模型"同意对方提出的交易时再写一行"）。
    /// 它原先没在这张表里，于是**四个剥离出口全都漏掉它**，玩家会在私信里看到
    /// <c>[Deal: accept]</c> —— 与当初 <c>[Trade: …]</c> 泄漏是同一个形状。
    /// 它不需要被解析成意图（目前没有代码消费它，见 D1 决策），但必须被剥掉。
    /// </para>
    /// </summary>
    public static readonly string[] MarkerKeywords = ["Trade", "Buy", "Deal"];

    /// <summary>
    /// 上面那张表里<b>只剥掉、不当意图解析</b>的关键词。
    /// <para>
    /// 目前只有 <c>Deal</c>（<c>[Deal: accept]</c>）：提示词让模型写，但没有代码消费它，
    /// 而且它没有 <c>Kind</c>/<c>Amount</c> —— 拿去 <see cref="Parse"/> 只会产出一条
    /// 假的 <c>PARSE_ERROR</c>。
    /// </para>
    /// <para>
    /// 注意它与 <c>MarkerKeywords</c> 的关系：<b>剥掉是必须的，解析是可选的</b>。
    /// 将来若决定真的消费 <c>[Deal: accept]</c>（D1 决策的另一个分支），
    /// 就把它从这张表里移走并补上解析逻辑。
    /// </para>
    /// </summary>
    public static readonly string[] StripOnlyKeywords = ["Deal"];

    /// <summary>这个关键词是否属于"只剥不解析"。</summary>
    private const string StripOnlyKeyword = "Deal";

    /// <summary>
    /// 待定缓冲的长度上限。正常情况下一个标记几十个字符就结束了；
    /// 超过这个长度说明遇到的是"只有 <c>[</c> 没有 <c>]</c>"的畸形正文，
    /// 必须放行，否则一次文本污染会让后续所有消息都卡在缓冲里。
    /// </summary>
    public const int MaxPendingLength = 4096;

    private readonly StringBuilder _pending = new();
    private readonly List<TradeIntent> _collected = [];

    /// <summary>当前是否有未闭合的候选标记卡在缓冲里（诊断用）。</summary>
    public bool HasPendingMarker => _pending.Length > 0;

    /// <summary>未闭合缓冲的内容（诊断用）。</summary>
    public string Pending => _pending.ToString();

    /// <summary>
    /// 增量喂入一段文本。返回本次新识别出的意图（可能是 0 个、1 个或多个），
    /// 同时把文本写进 <paramref name="cleanBody"/>——它已剔除交易标记，可直接交给原版解析器。
    /// </summary>
    public IReadOnlyList<TradeIntent> Feed(string? chunk, StringBuilder cleanBody)
    {
        ArgumentNullException.ThrowIfNull(cleanBody);
        _collected.Clear();
        if (string.IsNullOrEmpty(chunk))
        {
            // 空分片也必须推进一次：缓冲里可能已经攒了一个完整但尚未被察觉的标记。
            Drain(cleanBody);
            return Snapshot();
        }

        _pending.Append(chunk);
        Drain(cleanBody);
        return Snapshot();
    }

    /// <summary>增量喂入，只取意图、丢弃干净正文。</summary>
    public IReadOnlyList<TradeIntent> Feed(string? chunk)
    {
        var discard = new StringBuilder();
        return Feed(chunk, discard);
    }

    /// <summary>
    /// 流式喂入，<b>不等待未闭合的标记</b>：把标记<b>之前</b>的正文立刻写进
    /// <paramref name="cleanBody"/>，未闭合的标记继续留在待定缓冲里等下一个分片。
    /// <para>
    /// 与 <see cref="Feed"/> 的区别只在这一点上。原版私信是逐分片推进 UI 的
    /// （<c>AiPrivateMessages.cs:75</c>），如果用 <see cref="Feed"/>，
    /// 一个尚未闭合的标记会把该分片里"标记之前的文字"一起押后，
    /// 玩家就会看到正文延迟出现——极端情况下整段话要等标记补全才显示。
    /// </para>
    /// <para>
    /// 生命周期仍与 <see cref="Feed"/> 相同：每次调用前清空返回值，
    /// 结束时必须调 <see cref="Flush()"/>。
    /// </para>
    /// </summary>
    public IReadOnlyList<TradeIntent> StreamingFeed(string? chunk, StringBuilder cleanBody)
    {
        ArgumentNullException.ThrowIfNull(cleanBody);
        _collected.Clear();
        if (!string.IsNullOrEmpty(chunk)) _pending.Append(chunk);

        Drain(cleanBody, flushPrefixBeforeIncomplete: true);
        return Snapshot();
    }

    /// <summary>
    /// 结束本次流。<b>必须调用</b>：把缓冲里那把"迟迟等不到 <c>]</c>"的文本放行，
    /// 否则这段正文会永久消失。未闭合的标记会按"解析失败"产出一条带 <c>ParseError</c> 的意图。
    /// </summary>
    public IReadOnlyList<TradeIntent> Flush(StringBuilder cleanBody)
    {
        ArgumentNullException.ThrowIfNull(cleanBody);
        _collected.Clear();

        // 只要缓冲里存在一个完整的括号组，就一直推进到再也找不到为止。
        // 这样 "[Trade: a] [Trade: b" 这种"一个完整 + 一个残缺"的分片也能被正确拆开。
        while (TryFindCompleteGroup(_pending.ToString(), 0, out int open, out int length))
        {
            Emit(cleanBody, _pending.ToString(0, open));
            string group = _pending.ToString(open, length);
            Consume(cleanBody, group);
            _pending.Remove(0, open + length);
        }

        // 剩下的是真正残缺的尾巴：作为普通正文放行。
        if (_pending.Length > 0)
        {
            string tail = _pending.ToString();
            _pending.Clear();

            if (LooksLikeMarkerStart(tail, out _, out string? keyword))
            {
                _collected.Add(Parse(tail, keyword!, forcedError: "标记没有闭合（缺少 ]）。"));
            }
            else
            {
                cleanBody.Append(tail);
            }
        }

        return Snapshot();
    }

    /// <summary>结束本次流，只取意图、丢弃干净正文。</summary>
    public IReadOnlyList<TradeIntent> Flush()
    {
        var discard = new StringBuilder();
        return Flush(discard);
    }

    /// <summary>
    /// 一次性解析整段文本（供测试与非流式场景使用）。等价于 <c>Feed(text)</c> + <c>Flush()</c>。
    /// </summary>
    public static IReadOnlyList<TradeIntent> ParseAll(string? text)
    {
        var protocol = new TradeProtocol();
        var results = new List<TradeIntent>();
        var discarded = new StringBuilder();

        results.AddRange(protocol.Feed(text, discarded));
        results.AddRange(protocol.Flush(discarded));
        return results;
    }

    /// <summary>把文本里所有交易标记剔除，返回干净正文。</summary>
    public static string StripMarkers(string? text)
    {
        var protocol = new TradeProtocol();
        var clean = new StringBuilder();
        protocol.Feed(text, clean);
        protocol.Flush(clean);
        return clean.ToString();
    }

    // ─────────────────────────────────────────────────────────────────
    // 增量扫描
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 把缓冲里所有"已经完整"的部分推进到 <paramref name="cleanBody"/>。
    /// 只处理到最后一个完整的括号组为止，剩下的留给下一次调用。
    /// </summary>
    /// <param name="flushPrefixBeforeIncomplete">
    /// 缓冲里只剩一个<b>未闭合</b>的标记时，是否把该标记<b>之前</b>的正文立刻放行。
    /// <list type="bullet">
    /// <item><c>false</c>（<see cref="Feed"/>）：押后处理。整段解析完再一起给出，
    /// 语义简单，但"标记之前的文字"要等标记补全才出现。</item>
    /// <item><c>true</c>（<see cref="StreamingFeed"/>）：立刻放行。逐分片推进 UI 的场景必须这样，
    /// 否则玩家会看到正文延迟显示。</item>
    /// </list>
    /// </param>
    private void Drain(StringBuilder cleanBody, bool flushPrefixBeforeIncomplete = false)
    {
        while (true)
        {
            string text = _pending.ToString();
            if (!TryFindCompleteGroup(text, 0, out int open, out int length))
            {
                if (text.IndexOf('[') < 0)
                {
                    // 连左括号都没有：整段都是普通正文，直接放行。
                    Emit(cleanBody, text);
                    _pending.Clear();
                }
                else if (_pending.Length > MaxPendingLength)
                {
                    // 畸形正文（只有左括号且迟迟不闭合）。放行，避免永久卡住后续消息。
                    Emit(cleanBody, text);
                    _pending.Clear();
                }
                else if (flushPrefixBeforeIncomplete && TryFindOpenBracket(text, out int bracket))
                {
                    // 只留下一个未闭合的标记：把它之前的正文放行，缓冲从那个 '[' 开始重记。
                    // 少了这一步，"前面的话 [Trade: ..." 里的"前面的话"会被一起押后。
                    Emit(cleanBody, text[..bracket]);
                    _pending.Remove(0, bracket);
                }
                return;
            }

            Emit(cleanBody, text[..open]);
            string group = text.Substring(open, length);
            Consume(cleanBody, group);
            _pending.Remove(0, open + length);
        }
    }

    /// <summary>找出文本里第一个未配对的左括号位置。没有则返回 false。</summary>
    private static bool TryFindOpenBracket(string text, out int index)
    {
        index = text.IndexOf('[');
        return index >= 0;
    }

    /// <summary>
    /// 定位第一个完整的 <c>[...]</c> 组。<paramref name="length"/> 含两侧方括号。
    /// <para>
    /// <b>按深度配对，不是找第一个 <c>]</c></b>：<c>"[未知: [Trade: 打赏]] ..."</c> 里
    /// 内层的 <c>]</c> 不能当作外层的结束，否则外层会被切成两半，
    /// 属于原版的标记就被我们撕开了。
    /// </para>
    /// </summary>
    private static bool TryFindCompleteGroup(string text, int from, out int open, out int length)
    {
        open = -1;
        length = 0;

        int start = text.IndexOf('[', from);
        if (start < 0) return false;

        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            if (text[i] == '[') depth++;
            else if (text[i] == ']' && --depth == 0)
            {
                open = start;
                length = i - start + 1;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 处理一个完整括号组（按深度配对）。
    /// <para>
    /// 是交易标记就把内容抽走并解析；不是交易标记就<b>整组逐字放行</b>。
    /// </para>
    /// <para>
    /// 关键：<b>绝不进入不认识的组</b>。像 <c>[未知: [Trade: 打赏]]</c> 这样的输入，
    /// 该组原样保留——一旦我们把内层的 <c>[Trade: 打赏]</c> 剥掉，外层就只剩下
    /// <c>[未知: ]</c>，那是原版解析器看到的一个残缺标记，属于破坏原版行为。
    /// </para>
    /// </summary>
    private void Consume(StringBuilder cleanBody, string group)
    {
        if (!LooksLikeMarkerStart(group, out _, out string keyword))
        {
            cleanBody.Append(group);
            return;
        }

        // ★ 只剥不解析的标记：`[Deal: accept]`。
        //
        // 提示词要求模型"同意对方提出的交易时再写一行 [Deal: accept]"，
        // 但目前**没有代码消费它**（NPC 那一票来自 [Trade: …] 本身）。
        // 它必须被剥掉（否则玩家在私信里看得到机器语法，与 [Trade:] 泄漏是同一个形状），
        // 但不能当交易意图去解析 —— 它没有 Kind/Amount，硬解会产出一条假的 PARSE_ERROR，
        // 在日志里刷出一堆"标记解析失败"，把真正的故障淹掉。
        if (keyword.Equals(StripOnlyKeyword, StringComparison.OrdinalIgnoreCase)) return;

        _collected.Add(Parse(group, keyword));
    }

    /// <summary>
    /// 判断一个括号组是否是交易标记。判据是<b>字面</b>的：括号内第一个冒号之前的词
    /// 必须正好是 <c>Trade</c> 或 <c>Buy</c>。
    /// <para>
    /// 这一条同时挡掉了 <c>[未知: [Trade: ...]]</c>：此时第一个词是"未知"，整组原样放行，
    /// 不会把原版自己标记的内容剥掉（剥掉会让原版解析器看到一个残缺的标记）。
    /// </para>
    /// </summary>
    private static bool LooksLikeMarkerStart(string text, out int colon, out string keyword)
    {
        colon = -1;
        keyword = "";

        if (text.Length < 2 || text[0] != '[') return false;

        int stop = text.IndexOf(']');
        if (stop < 0) stop = text.Length;

        int firstColon = FirstColon(text, 1, stop);
        if (firstColon < 0) return false;

        // 关键词必须是"单个词"：一旦冒号前出现空格、方括号或第二个冒号，
        // 说明这层括号不是标记层（典型例子 "[未知: [Trade: ...]]"），必须原样放行。
        string head = text[1..firstColon];
        if (head.Length == 0) return false;
        if (head.AsSpan().IndexOf('[') >= 0) return false;

        foreach (string candidate in MarkerKeywords)
            if (head.Equals(candidate, StringComparison.Ordinal))
            {
                colon = firstColon;
                keyword = candidate;
                return true;
            }

        return false;
    }

    private static int FirstColon(string text, int from, int to)
    {
        for (int i = from; i < to; i++)
            if (text[i] is ':' or '：') return i;
        return -1;
    }

    // ─────────────────────────────────────────────────────────────────
    // 字段解析
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 解析一个已经确认是交易标记的括号组。
    /// </summary>
    /// <param name="group">含两侧方括号的原始标记，例如 <c>[Trade: 赞助, Amount: 50000]</c>。</param>
    /// <param name="keyword">已经识别出来的关键词（Trade / Buy）。</param>
    /// <param name="rawText">写进 <see cref="TradeIntent.RawText"/> 的原文，供审计。</param>
    /// <param name="forcedError">调用方已知的失败原因（例如"标记没有闭合"）。</param>
    private TradeIntent Parse(string group, string keyword, string? rawText = null, string? forcedError = null)
    {
        bool isBuy = keyword.Equals("Buy", StringComparison.OrdinalIgnoreCase);
        string kindText = "";
        string directionText = "";
        long amount = 0;
        string? amountError = null;
        string itemId = "";
        int quantity = 1;
        string? quantityError = null;
        bool hasAmountField = false;
        var reason = new StringBuilder();
        bool reasonStarted = false;
        bool sawKindSegment = false;

        // 先把关键词本身剥掉：group = "[Trade: 赞助, Amount: 50000, Reason: ...]"，
        // 冒号之后才是数据段。少了这一步，"Trade: 赞助" 会被当成一个字段，
        // 于是 Kind 永远取不到值（这是实测出来的，不要凭直觉"顺手"改回去）。
        int head = FirstColon(group, 1, group.Length);
        string content = head >= 0 && head + 1 <= group.Length ? group[(head + 1)..] : group;
        if (content.EndsWith(']')) content = content[..^1];

        // 段落约定：以 `,` 分栏，每栏是 `键: 值`。
        // Kind 是【第一栏且没有冒号】的那一个裸值——判定必须落在栏的位置上。
        // 另外 Reason 视为最后一个字段：它之后的一切（含被逗号切开的尾巴）都拼回理由。
        foreach (string field in SplitTopLevel(content))
        {
            int colon = FirstColon(field, 0, field.Length);

            if (colon < 0)
            {
                // 已经在理由里：这个裸段落是理由被逗号切开的尾巴，必须拼回去。
                if (reasonStarted)
                {
                    reason.Append('，').Append(field.Trim());
                    continue;
                }

                // 只有第一栏可能是无键名的 Kind；之后再出现裸值就忽略，避免噪声污染。
                if (!sawKindSegment)
                {
                    sawKindSegment = true;
                    if (!isBuy) kindText = field.Trim();
                }
                continue;
            }

            string key = field[..colon].Trim();
            string value = field[(colon + 1)..].Trim();

            if (reasonStarted)
            {
                // 已经在理由里，后续段落一律拼回理由。
                // 这里<b>必须补回那个分隔用的逗号</b>：少了它，
                // "这赛季刚拿工资，正好赞助你一点" 会被拼成 "这赛季刚拿工资正好赞助你一点"，
                // 理由被悄悄改写，而且没有任何报错。
                reason.Append('，').Append(field.Trim());
                continue;
            }

            if (key.Equals("Amount", StringComparison.OrdinalIgnoreCase))
            {
                hasAmountField = true;
                if (value.Length == 0) amountError = "Amount 为空。";
                else if (!MoneyUnits.TryParseUsd(value, out amount)) amountError = $"Amount 无法解析：{value}";
                else if (amount <= 0) amountError = $"Amount 必须为正：{value}";
            }
            else if (key.Equals("Direction", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("方向", StringComparison.OrdinalIgnoreCase))
            {
                // 显式方向：模型（或玩家手打）可以直接指定，最可靠。
                // 认不出来就留着空串，交给 TradeIntentCompleter 按配置映射兜底/拒绝。
                directionText = value;
            }
            else if (key.Equals("Reason", StringComparison.OrdinalIgnoreCase))
            {
                reasonStarted = true;
                reason.Append(value);
            }
            else if (key.Equals("ItemId", StringComparison.OrdinalIgnoreCase))
            {
                itemId = value;
            }
            else if (key.Equals("Quantity", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, out quantity)) quantityError = $"Quantity 无法解析：{value}";
                else if (quantity <= 0) quantityError = $"Quantity 必须为正：{value}";
            }
            // 其余键（例如未来的 Target/Due）忽略，保持向前兼容。
        }

        IReadOnlyList<TradeItemLine> items = isBuy
            ? [new TradeItemLine { ItemId = itemId, Quantity = quantity }]
            : [];

        string kind = isBuy ? BuyKind : kindText.Trim();
        string? error = amountError ?? quantityError;

        if (error == null && isBuy)
        {
            if (itemId.Length == 0) error = "[Buy: ...] 必须带 ItemId。";
            else if (quantity <= 0) error = $"购买数量必须为正：{quantity}";
        }

        if (error == null && kind.Length == 0) error = "标记里没有 Kind。";
        if (error == null && !hasAmountField) error = "标记里没有 Amount。";

        // 没有错误时必须返回 null 而不是空串：ParseFailed 与调用方都按 null 判定。
        string? finalError = forcedError ?? error;
        if (string.IsNullOrWhiteSpace(finalError)) finalError = null;

        return new TradeIntent(
            kind,
            // 方向可以是标记里显式给的（"Direction: npcToPlayer"）；没给就留空，
            // 由 TradeIntentCompleter 按配置的 kindDirections 映射补全（或拒绝）。
            Direction: TradeIntentCompleter.NormalizeDirection(directionText),
            InitiatorId: "",
            CounterpartyId: "",
            AmountUsd: amount,
            Items: items,
            Reason: reason.ToString(),
            RawText: rawText ?? group,
            ParseError: finalError);
    }

    /// <summary>
    /// 按顶层逗号切分（全角/半角都认），但<b>不动 <c>$50,000</c> 这类金额里的千分位逗号</b>。
    /// 少了这个豁免，<c>Amount: $50,000</c> 会被切成 <c>$50</c> 和 <c>000</c>，
    /// 结果是一笔五万美元的交易被记成五十美元——金额类缺陷里最难发现的一种。
    /// </summary>
    private static List<string> SplitTopLevel(string content)
    {
        var parts = new List<string>();
        int start = 0;
        int i = 0;
        while (i < content.Length)
        {
            if (content[i] is not (',' or '，'))
            {
                i++;
                continue;
            }

            // 逗号后紧跟 3 位数字 + 边界，且本段里出现过 '$'，则判定为千分位，不切。
            if (LooksLikeThousandsSeparator(content, start, i))
            {
                i++;
                continue;
            }

            parts.Add(content[start..i].Trim());
            start = i + 1;
            i = start;
        }
        parts.Add(content[start..].Trim());
        return parts;
    }

    private static bool LooksLikeThousandsSeparator(string content, int segmentStart, int comma)
    {
        if (content.AsSpan(segmentStart, comma - segmentStart).IndexOf('$') < 0) return false;

        int digits = 0;
        int j = comma + 1;
        while (j < content.Length && char.IsAsciiDigit(content[j]) && digits < 3)
        {
            digits++;
            j++;
        }

        return digits == 3 && (j >= content.Length || !char.IsAsciiDigit(content[j]));
    }

    private static void Emit(StringBuilder cleanBody, string text)
    {
        if (text.Length > 0) cleanBody.Append(text);
    }

    private IReadOnlyList<TradeIntent> Snapshot() => _collected.Count == 0 ? [] : _collected.ToArray();
}
