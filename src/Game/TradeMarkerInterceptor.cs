using System.Runtime.CompilerServices;
using System.Text;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Trade;

namespace NationalSpireEconomy.Game;

/// <summary>
/// <c>[Trade: ...]</c> / <c>[Buy: ...]</c> 标记的拦截器：把标记从流式文本里抽走，正文原样放行。
/// <para>
/// <b>为什么需要它</b>：原版 <c>PrivateMessages.Known</c> 是一张写死的正则白名单
/// （<c>PrivateMessages.cs:311</c>），<c>Trade</c> 不在其中。于是 <c>[Trade: ...]</c>
/// 会被 <c>PrivateStreamParser</c> 当作<b>普通括号</b>原样塞进 <c>_visible</c>，
/// 玩家就会在私信里看到 `[Trade: 赞助, Amount: 50000]` 这种东西。
/// 我们不能改原版，所以在这里把它抽走。
/// </para>
/// <para>
/// <b>本阶段只做三件事</b>：喂 <see cref="TradeProtocol"/> → 剔除标记 → 记日志。
/// 不注入 Prompt、不动任何金额、不写影子存档。
/// </para>
/// <para>
/// <b>状态是每个解析器实例独立的</b>：原版给每一轮私信 <c>new</c> 一个
/// <c>PrivateStreamParser</c>（<c>AiPrivateMessages.cs:70</c>），
/// 所以未闭合的标记必须记在"实例 → 我们自己的解析器"的映射上，
/// 用静态字段会让两轮私信互相污染。
/// </para>
/// </summary>
public static class TradeMarkerInterceptor
{
    /// <summary>拦截器是否已经装配（记日志用）。</summary>
    public static bool Installed { get; private set; }

    /// <summary>日志出口。未装配时为 null，此时拦截器静默工作（单测友好）。</summary>
    public static IEconomyLog? Log { get; private set; }

    /// <summary>装上日志出口并标记为已装配。</summary>
    public static void Install(IEconomyLog? log)
    {
        Log = log;
        Installed = true;
    }

    /// <summary>卸载（卸载补丁时调用，保证状态不残留）。</summary>
    public static void Uninstall()
    {
        Log = null;
        Installed = false;
        lock (Gate) Tables.Clear();
    }

    // ─────────────────────────────────────────────────────────────────
    // 每个解析器实例的状态
    // ─────────────────────────────────────────────────────────────────

    private sealed class ParserState
    {
        public readonly TradeProtocol Protocol = new();

        /// <summary>可复用的正文缓冲，避免每个分片都 new 一个 StringBuilder。</summary>
        public readonly StringBuilder Clean = new();

        /// <summary>
        /// 本轮玩家<b>实际看到</b>的正文（标记已被抽走）。只在流结束时用来判断
        /// "模型是不是在正文里声称已经转账却没发标记"（见 <see cref="ReportTransferClaim"/>）。
        /// </summary>
        public readonly StringBuilder Visible = new();

        /// <summary>本实例累计识别出的意图数，仅用于日志。</summary>
        public int IntentCount;
    }

    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<object, ParserState> Tables = new();

    /// <summary>当前跟踪的解析器实例数（诊断用）。</summary>
    public static int TrackedInstances
    {
        get { lock (Gate) return Tables.Count(); }
    }

    private static ParserState StateFor(object instance)
    {
        lock (Gate)
        {
            // 用 GetValue + 工厂而不是 GetOrCreateValue：后者要求值类型有
            // 可访问的无参构造函数，对私有嵌套类型会抛 MissingMethodException。
            return Tables.GetValue(instance, _ => new ParserState());
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Feed
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>PrivateStreamParser.Feed</c> 的前缀处理。
    /// </summary>
    /// <param name="instance">原版解析器实例（Harmony 按参数名 <c>__instance</c> 注入）。</param>
    /// <param name="text">即将喂进去的文本。</param>
    /// <param name="rewritten">改写后的文本；<paramref name="runOriginal"/> 为 false 时无意义。</param>
    /// <returns>是否继续执行原方法。</returns>
    public static bool RewriteFeed(object? instance, string? text, out string rewritten)
    {
        rewritten = "";

        // 拿不到实例（反射装配异常之类）时直接放行：拦截失败不该影响原版显示。
        if (instance == null) return true;
        if (string.IsNullOrEmpty(text)) return true;

        ParserState state = StateFor(instance);
        state.Clean.Clear();

        IReadOnlyList<TradeIntent> intents;
        try
        {
            // 用 StreamingFeed 而不是 Feed：分片停在半个标记上时，
            // "标记之前的正文"要立刻交给原版去显示，不能跟着标记一起押后。
            intents = state.Protocol.StreamingFeed(text, state.Clean);
        }
        catch (Exception e)
        {
            // 拦截器自己出错时放行原文——绝不因为我们的 bug 把玩家的话弄丢。
            Log?.Warn("trade", "标记扫描失败，已按原文放行：" + e.Message, e);
            return true;
        }

        if (intents.Count == 0)
        {
            // 没有识别出新标记。仍要把干净正文交回去：
            // 分片停在半个标记上时，clean 里是"标记之前"的那段文字，必须照常显示。
            rewritten = state.Clean.ToString();
            state.Visible.Append(rewritten);
            return true;
        }

        foreach (var intent in intents)
        {
            LogIntent(state, intent);

            // ★ 阶段 4 第一步：把模型提出的交易变成"只读预览 + 待确认单"（Reserve，不动钱）。
            //   放在这里是因为它天然是"模型刚说完一句交易标记"的时刻，身份槽还新鲜。
            //   ParseFailed 的意图会被 HandleModelIntent 自己跳过。
            TradePreviewWiring.HandleModelIntent(intent, Log);
        }
        rewritten = state.Clean.ToString();
        state.Visible.Append(rewritten);
        return true;
    }

    // ─────────────────────────────────────────────────────────────────
    // 显示出口的剥离（与 Feed 的职责分开）
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>只剥离标记、不产生任何意图</b>：给"真正被渲染/存档的那份正文"用。
    /// <para>
    /// <b>为什么必须有这个入口（2026-10-12 真机 + path-probe.log 实测）</b>：
    /// 我们原先只在 <c>PrivateStreamParser.Feed</c> 上剥标记，但真机的渲染链是
    /// </para>
    /// <code>LeadingReasoningStream.Feed("…")   ← 外层流，把原文【原样】累积
    ///     └→ PrivateStreamParser.Feed("…") ← 我们打补丁的地方（只影响解析器的 _visible）
    /// …
    /// PrivateTurn.set_Reply("…含标记的完整原文…")  ← ★ 界面渲染与存档用的是这一份
    /// </code>
    /// <para>
    /// 证据：`path-probe.log` 里同一时刻既有
    /// <c>Feed("[")</c> … <c>Feed("Trade")</c>（标记确实经过了 Feed，所以能被识别），
    /// 又有 <c>set_Reply("[0]\"哦，那就是又加一小时…\n\n[Trade: …\"")</c>（含标记的原文）。
    /// 两者互不影响 —— 于是标记照样出现在玩家眼前。
    /// </para>
    /// <para>
    /// <b>为什么这里不解析、不落单</b>：落单只由 <see cref="RewriteFeed"/> 负责。
    /// 若这里也解析，同一个标记会被处理两遍；虽然幂等键（world+双方+金额+当日）能挡住重复落单，
    /// 但"一条出口只干一件事"更不容易出错。这里<b>纯净化文本</b>。
    /// </para>
    /// <para>
    /// <b>为什么用 <see cref="TradeProtocol"/> 而不是简单的字符串替换</b>：
    /// 它已经把两个边界情形处理好了 —— 未闭合的 <c>[Trade: …</c>（原样放行，绝不吞正文）
    /// 与"括号里塞了别的标记"（原样放行，不破坏原版自己的解析）。这两点都有测试钉着。
    /// </para>
    /// </summary>
    /// <param name="text">即将被渲染/存档的正文。</param>
    /// <param name="cleaned">剥离后的正文；没有标记时与入参内容一致。</param>
    /// <returns>是否真的改动了文本（调用方据此决定要不要写日志）。</returns>
    public static bool StripForDisplay(string? text, out string cleaned)
    {
        cleaned = text ?? "";
        if (string.IsNullOrEmpty(text)) return false;

        // 没有左括号就必然没有标记：省掉一次完整扫描（私信每轮都会走到这里）。
        if (text.IndexOf('[') < 0) return false;

        try
        {
            string stripped = TradeProtocol.StripMarkers(text);
            if (string.Equals(stripped, text, StringComparison.Ordinal)) return false;

            // ★ 守住"绝不抹掉无内容片段"（2026-10-12 用例暴露的不一致）：
            //   TradeProtocol 对"残缺的标记"有两种结局 ——
            //     · `[Trade: 雇佣, Amount`（冒号后有内容）→ 原样放行（怕吞掉正文）；
            //     · `[Trade:`（冒号后什么都没有）→ 被 Flush 判成残缺标记并【丢弃】。
            //   后者会让我们把一个纯片段（甚至只是玩家正文里的半截方括号）静默抹掉，
            //   而它在渲染路径上是不可逆的。这里改成：只要剥完什么都不剩、
            //   而入参本身又不是"完整标记"，就退回归还原文 —— 宁可让玩家看到半截语法，
            //   也绝不悄悄删他的字。
            if (stripped.Trim().Length == 0 && !LooksLikeCompleteMarker(text))
            {
                Log?.Debug("trade",
                    $"显示出口剥离后为空，已退回归还原文（避免静默删文本）：<{text}>");
                return false;
            }

            cleaned = stripped;
            return true;
        }
        catch (Exception e)
        {
            // 纯净化失败时放行原文：绝不因为我们的 bug 把模型的话弄丢。
            Log?.Warn("trade", "显示出口剥离标记失败，已按原文放行：" + e.Message, e);
            return false;
        }
    }

    /// <summary>
    /// 这段文本是不是一个<b>完整</b>的交易标记（<c>[Trade: …]</c> / <c>[Buy: …]</c> 且括号闭合）。
    /// <para>
    /// 只用于上面那条"剥离后为空"的退让判断：完整的标记被剥掉是正常且期望的，
    /// 而残缺的片段被剥成空串就说明我们可能删了不该删的东西。
    /// </para>
    /// </summary>
    private static bool LooksLikeCompleteMarker(string text)
    {
        int open = text.IndexOf('[');
        if (open < 0) return false;
        int close = text.IndexOf(']', open);
        if (close < 0) return false;

        string group = text[open..(close + 1)];
        foreach (string keyword in TradeProtocol.MarkerKeywords)
        {
            if (group.StartsWith("[" + keyword, StringComparison.OrdinalIgnoreCase))
            {
                int colon = group.IndexOfAny([':', '：']);
                if (colon > 0) return true;
            }
        }
        return false;
    }

    private static void LogIntent(ParserState state, TradeIntent intent)
    {
        state.IntentCount++;

        if (intent.ParseFailed)
        {
            Log?.Warn("trade",
                $"识别出交易标记但解析失败（第 {state.IntentCount} 条）：{intent.ParseError}｜原文 {intent.RawText}");
            return;
        }

        string items = intent.HasItems
            ? "，物品 " + string.Join("、", intent.Items.Select(i => $"{i.ItemId}×{i.Quantity}"))
            : "";

        // 金额经 MoneyUnits 渲染：入参已经是美元，绝不再乘 10。
        Log?.Info("trade",
            $"识别出交易标记（第 {state.IntentCount} 条）：{intent.Kind} {MoneyUnits.Format(intent.AmountUsd)}" +
            $"{items}｜理由 {intent.Reason}");
    }

    // ─────────────────────────────────────────────────────────────────
    // Finish
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>PrivateStreamParser.Finish</c> 的前缀处理。
    /// <para>
    /// 两件事：把我们这边残留的待定标记丢掉（截断的标记不该让玩家看到
    /// <c>[Trade: 赞助, Amo</c> 这种半截语法），并保证原方法会被调用，
    /// 好让原版清掉它自己缓冲里那个因为标记被抽走而孤立下来的 <c>[</c>。
    /// </para>
    /// </summary>
    /// <returns>是否继续执行原方法。目前恒为 true。</returns>
    public static bool RewriteFinish(object? instance)
    {
        if (instance == null) return true;

        ParserState state = StateFor(instance);

        if (state.Protocol.HasPendingMarker)
        {
            string leftover = state.Protocol.Pending;

            // 丢掉残留：它已经在我们的缓冲里了，原版从没见过它，
            // 所以不交给原版就等于"从玩家视野里消失"。
            try
            {
                var discarded = new StringBuilder();
                state.Protocol.Flush(discarded);
            }
            catch (Exception e)
            {
                Log?.Warn("trade", "结束流时清理待定标记失败：" + e.Message, e);
            }

            Log?.Warn("trade",
                $"交易标记在流结束时仍未闭合，已丢弃 {leftover.Length} 个字符（多半是模型被截断）：{leftover}");
        }

        ReportTransferClaim(state);
        return true;
    }

    /// <summary>
    /// <b>真机哨兵（2026-10-11 22:12 与 22:31；2026-10-12 补账本口径）</b>：模型在正文里声称/承诺转账，
    /// 但本轮<b>一个标记都没发</b> ⇒ 这一轮不会产生任何交易单，账上也不会有任何变动。
    /// <para>
    /// 为什么必须留这一行：那两轮里模型都写了"钱打过去了/我再给你转五百"，而
    /// <c>识别出交易标记</c> 与预览、弹窗全都没有出现——玩家与我们都只能靠翻源码猜。
    /// 有了它，"模型嘴上说转了"与"系统真的动了账"在日志里就能一眼分开。
    /// </para>
    /// <para>
    /// <b>为什么还要补一句账本口径</b>：本行的判据只是"本轮没发标记"，而真机上完全可能是
    /// "上一轮钱就真的转过、这一轮它只是在复述"（第三步就是这么设计并验证的）。
    /// 两种情况处置完全不同，所以把 <c>LastTrade</c> 的结论摆在同一行里——
    /// 这也让第三步"这条哨兵应该变少"的验收可以直接在日志上判读：**有标记的那轮不再出现本行**，
    /// 而它出现时会立刻告诉你账本到底动没动。
    /// </para>
    /// <para>纯日志：不改变任何流程，更不碰钱。</para>
    /// </summary>
    private static void ReportTransferClaim(ParserState state)
    {
        if (Log == null) return;
        if (state.IntentCount > 0) return;          // 有标记就无需提醒（哪怕标记形状不对，也已另有警告）

        string visible = state.Visible.ToString();
        string? claim = TransferClaimDetector.FirstClaim(visible);
        if (claim == null) return;

        try
        {
            string flat = visible.Replace("\r", "").Replace("\n", "\\n").Trim();
            if (flat.Length > 70) flat = flat[..70] + "…";
            Log.Warn("trade",
                $"[Economy-Trade] 模型正文里说了「{claim}」，但本轮【没有】交易标记" +
                $"⇒ 本轮不会产生任何交易单" + LedgerClause() +
                $"（若它只是在承诺，可忽略本行）｜原文 <{flat}>");
        }
        catch (Exception)
        {
            // 诊断失败不影响原版显示。
        }
    }

    /// <summary>
    /// 给哨兵行补一句"账本到底怎么说"（读的是当前世界的 <c>LastTrade</c>）。
    /// 读不到（还没初始化 / 没有身份 / 没有记录）就返回空串，绝不因此影响那一行日志。
    /// </summary>
    private static string LedgerClause()
    {
        try
        {
            var world = LastTouchedWorld();
            string? summary = world == null ? null : TradeNoteText.LedgerSummary(world.LastTrade);
            return summary == null ? "、账上一分未动" : "｜" + summary;
        }
        catch (Exception)
        {
            return "、账上一分未动";
        }
    }

    /// <summary>
    /// 当前正在私信的那个世界（身份槽 → 运行时缓存）。<b>纯读</b>，取不到返回 null。
    /// <para>它只用于诊断行，所以刻意不按需载入世界、不播种、不落盘。</para>
    /// </summary>
    private static EconomyWorld? LastTouchedWorld()
    {
        var runtime = EconomyModEntry.Runtime;
        if (runtime == null) return null;
        string worldId = PrivateIdentitySlot.Peek()?.WorldId ?? "";
        return worldId.Length > 0 ? runtime.CachedWorld(worldId) : null;
    }

    /// <summary>把某个实例的解析器状态清掉。供测试与错误恢复使用。</summary>
    public static void Reset(object instance)
    {
        if (instance == null) return;
        lock (Gate) Tables.Remove(instance);
    }

    /// <summary>诊断：描述某个实例当前的待定缓冲。<b>仅供排查使用。</b></summary>
    public static string DescribeStateFor(object instance)
    {
        if (instance == null) return "(null)";
        lock (Gate)
        {
            if (!Tables.TryGetValue(instance, out var state)) return "(无状态)";
            return $"pending=<{state.Protocol.Pending}> hasPending={state.Protocol.HasPendingMarker} " +
                   $"intents={state.IntentCount}";
        }
    }
}
