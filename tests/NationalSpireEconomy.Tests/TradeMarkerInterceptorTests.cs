using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using NationalSpire;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Game;
using NationalSpireEconomy.Trade;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 阶段 2：<c>PrivateStreamParser</c> 前缀补丁。
/// <para>
/// 验收面有两条：① 标记被抽走，正文一字不差地留下来；
/// ② <b>原版自己的功能没被弄坏</b>——普通括号照常显示、已知指令照常解析。
/// 第二条比第一条更重要：补丁是在别人家的解析器上动刀。
/// </para>
/// <para>
/// <b>测试策略（踩过坑之后定下来的）</b>：
/// <list type="bullet">
/// <item><b>拦截器逻辑</b>用"手工调用前缀 + 手写替身"来验，完全确定、不依赖 Harmony。
/// 前缀本来就只是"改写入参再调用原方法"，手工走一遍就是它的全部行为。</item>
/// <item><b>Harmony 装配</b>单独用<b>真实</b> <c>PrivateStreamParser</c> 验一次：
/// 要求补丁装得上、并且自证（喂一个标记进去，<c>Text</c> 里不该还留着它）通过。</item>
/// <item>两条路径<b>不能混用</b>：给真实类型打上补丁之后，再手工调用一次前缀就是
/// <b>双重处理</b>——第一次把标记收进待定缓冲，第二次又把它连同前缀塞回原版，
/// 于是标记反而漏进正文（原版 pending 会出现 <c>[Trade: 赞前 </c> 这种顺序颠倒的串）。
/// 实测就是这么坑了一整轮。</item>
/// </list>
/// </para>
/// </summary>
public static class TradeMarkerInterceptorTests
{
    public static Action[] Cases =>
    [
        // 核心：标记抽取
        FeedStripsCompleteMarker,
        FeedKeepsSurroundingTextIntact,
        FeedReportsParsedIntentToLog,
        FeedReportsMalformedMarkerAsWarning,

        // 分片
        MarkerSplitAcrossChunksIsStrippedOnceComplete,
        PendingMarkerIsDroppedAtFinish,

        // 真机哨兵：正文里说"钱转了"但没发标记
        ClaimWithoutMarkerIsReportedAtFinish,
        ClaimWithMarkerIsNotReported,
        PlainChatIsNotReportedAtFinish,
        PendingMarkerIsNotLeakedToVisibleText,
        FinishOnCleanStreamIsHarmless,

        // 实例隔离
        StateIsIsolatedPerParserInstance,
        ResetClearsInstanceState,
        NullInstanceAndNullTextAreSafe,

        // 不弄坏原版
        VanillaDirectivesStillParse,
        PlainBracketsAreStillVisible,
        NestedUnknownMarkerIsLeftAlone,
        MultipleMarkersInOneChunk,

        // 系统：Harmony 装配与自证
        PatchAppliesToRealParserType,
        PatchUnpatchesCleanly,
        LoggingIsOptional
    ];

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    // ── 调用辅助 ────────────────────────────────────────────────────

    /// <summary>
    /// 走一遍"前缀改写 → 交给原版"，返回值是原版累积的可见正文。
    /// <para>
    /// 这正是补丁在运行时做的事：改写入参，再调用原方法。
    /// 替身<b>没有</b>被 Harmony 打补丁，所以这里不会双重处理。
    /// </para>
    /// </summary>
    private static string FeedThrough(StreamParserDouble parser, string chunk)
    {
        bool run = TradeMarkerInterceptor.RewriteFeed(parser, chunk, out string rewritten);
        TestRunner.True(run, "本阶段的前缀恒为放行（只改文本，不拦截原方法）");
        parser.Feed(rewritten);
        return parser.Text;
    }

    private static string FinishThrough(StreamParserDouble parser)
    {
        TradeMarkerInterceptor.RewriteFinish(parser);
        parser.Finish();
        return parser.Text;
    }

    /// <summary>
    /// 原版 <c>PrivateStreamParser</c> 的最小替身，保留我们依赖的全部形状：
    /// <list type="bullet">
    /// <item><c>Feed(string)</c> 逐字符扫描，<c>[...]</c> 命中 <c>Known</c> 就当指令，否则当正文。</item>
    /// <item><c>Text</c> / <c>Directives</c> / <c>Error</c> 与原版同名。</item>
    /// <item>未闭合的 <c>[</c> 留在待定缓冲里，由 <c>Finish()</c> 放行——原版就是这个行为。</item>
    /// </list>
    /// <para>
    /// 刻意做成"能编译的普通类型"而不是泛型/反射构造：它是测试内的实现细节，
    /// 编译期检查比运行期反射可靠得多。
    /// </para>
    /// </summary>
    private sealed class StreamParserDouble(bool known)
    {
        private readonly StringBuilder _visible = new();
        private readonly StringBuilder _pending = new();
        private int _depth;

        public List<Dictionary<string, string>> Directives { get; } = [];
        public string Error { get; private set; } = "";
        public string Text => _visible.ToString();

        /// <summary>替身版的 Known：只看"方括号 + 单个大写字母 + 冒号"。</summary>
        private bool Known(string block) => known && Regex.IsMatch(block, @"^\[\s*[A-Z]\s*[:：]");

        public void Feed(string text)
        {
            foreach (char ch in text)
            {
                if (_pending.Length == 0)
                {
                    if (ch == '[') { _pending.Append(ch); _depth = 1; }
                    else _visible.Append(ch);
                    continue;
                }

                _pending.Append(ch);
                if (ch == '[') _depth++;
                if (ch != ']' || --_depth != 0) continue;

                string block = _pending.ToString();
                _pending.Clear();
                if (!Known(block)) { _visible.Append(block); continue; }
                Directives.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["cmd"] = block });
            }
        }

        public void Finish()
        {
            if (_pending.Length == 0) return;
            if (Known(_pending.ToString())) Error = "交互标记没有结束，未执行。";
            else _visible.Append(_pending);
            _pending.Clear();
            _depth = 0;
        }
    }

    // ── 核心：标记抽取 ──────────────────────────────────────────────

    private static void FeedStripsCompleteMarker()
    {
        var parser = new StreamParserDouble(known: true);
        string visible = FeedThrough(parser,
            "这局打得漂亮。[Trade: 赞助, Amount: 50000, Reason: 刚拿工资] 收着吧。");

        TestRunner.NotContains(visible, "[Trade:", "标记必须从可见正文里消失");
        TestRunner.NotContains(visible, "Amount", "标记内容不该漏进正文");
        TestRunner.NotContains(visible, "50000", "金额不该漏进正文");
        TestRunner.Contains(visible, "这局打得漂亮。", "前文要保留");
        TestRunner.Contains(visible, "收着吧。", "后文要保留");
    }

    private static void FeedKeepsSurroundingTextIntact()
    {
        var parser = new StreamParserDouble(known: true);
        string visible = FeedThrough(parser, "A[Trade: 打赏, Amount: 5000]B");

        TestRunner.Contains(visible, "A", "标记前的字符要在");
        TestRunner.Contains(visible, "B", "标记后的字符要在");
        TestRunner.NotContains(visible, "$", "不该凭空多出货币符号");
        TestRunner.NotContains(visible, "]", "不该残留右括号");
    }

    private static void FeedReportsParsedIntentToLog()
    {
        var lines = new List<(LogLevel Level, string Message)>();
        var log = new DelegateEconomyLog((level, _, message, _) => lines.Add((level, message)));
        TradeMarkerInterceptor.Install(log);
        try
        {
            var parser = new StreamParserDouble(known: true);
            FeedThrough(parser, "[Trade: 赞助, Amount: 50000, Reason: 刚拿工资]");

            TestRunner.Equal(1, lines.Count, "应恰好记一条日志");
            TestRunner.Equal(LogLevel.Info, lines[0].Level, "正常识别应记 Info");
            TestRunner.Contains(lines[0].Message, "赞助", "日志要带 Kind");
            // 金额必须经 MoneyUnits 渲染（$50,000），而不是原样打印 50000。
            TestRunner.Contains(lines[0].Message, MoneyUnits.Format(50_000), "金额应走 MoneyUnits 渲染");
            TestRunner.Contains(lines[0].Message, "刚拿工资", "日志要带理由");
        }
        finally
        {
            TradeMarkerInterceptor.Install(Silent);
        }
    }

    private static void FeedReportsMalformedMarkerAsWarning()
    {
        var lines = new List<(LogLevel Level, string Message)>();
        var log = new DelegateEconomyLog((level, _, message, _) => lines.Add((level, message)));
        TradeMarkerInterceptor.Install(log);
        try
        {
            var parser = new StreamParserDouble(known: true);
            // Amount 不是数字 → 解析失败，但仍应被抽走（不能把畸形标记显示给玩家）。
            string visible = FeedThrough(parser, "[Trade: 赞助, Amount: 很多钱]");

            TestRunner.NotContains(visible, "[Trade:", "畸形标记同样要被抽走");
            TestRunner.Equal(1, lines.Count, "应记一条日志");
            TestRunner.Equal(LogLevel.Warning, lines[0].Level, "解析失败应记 Warning");
            TestRunner.Contains(lines[0].Message, "失败", "日志要说明是解析失败");
        }
        finally
        {
            TradeMarkerInterceptor.Install(Silent);
        }
    }

    // ── 分片 ────────────────────────────────────────────────────────

    private static void MarkerSplitAcrossChunksIsStrippedOnceComplete()
    {
        var parser = new StreamParserDouble(known: true);

        FeedThrough(parser, "前 [Trade: 赞");
        FeedThrough(parser, "助, Amount: 50000, Rea");
        string visible = FeedThrough(parser, "son: 刚拿工资] 后");

        TestRunner.NotContains(visible, "[Trade:", "跨分片也必须抽干净");
        TestRunner.NotContains(visible, "50000", "金额不该漏出来");
        TestRunner.Contains(visible, "前", "前文要在。实际 <" + visible + ">");
        TestRunner.Contains(visible, "后", "后文要在");
    }

    private static void PendingMarkerIsDroppedAtFinish()
    {
        var lines = new List<(LogLevel Level, string Message)>();
        var log = new DelegateEconomyLog((level, _, message, _) => lines.Add((level, message)));
        TradeMarkerInterceptor.Install(log);
        try
        {
            var parser = new StreamParserDouble(known: true);
            FeedThrough(parser, "开头的话 [Trade: 赞助, Amo");   // 模型被截断
            string visible = FinishThrough(parser);

            TestRunner.NotContains(visible, "[Trade:", "截断的标记不能显示成正文");
            TestRunner.Contains(visible, "开头的话", "标记之前的正常文字要保留。实际 <" + visible + ">");
            TestRunner.True(lines.Any(l => l.Level == LogLevel.Warning), "应记一条警告");
        }
        finally
        {
            TradeMarkerInterceptor.Install(Silent);
        }
    }

    // ── 真机哨兵：正文里说"钱转了"但没发标记 ────────────────────────

    /// <summary>
    /// ★ 真机事故回归（2026-10-11 22:12 与 22:31）：模型正文写着"五百我打过去了"/"我再给你转五百"，
    /// 而<b>一个标记都没发</b> —— 于是账上一分未动，玩家却以为钱到了。
    /// 这条钉住：流结束时必须留下<b>一行可检索的警告</b>，把"嘴上说转了"与"真的动账"分开。
    /// </summary>
    private static void ClaimWithoutMarkerIsReportedAtFinish()
    {
        var lines = new List<(LogLevel Level, string Message)>();
        var log = new DelegateEconomyLog((level, _, message, _) => lines.Add((level, message)));
        TradeMarkerInterceptor.Install(log);
        try
        {
            var parser = new StreamParserDouble(known: true);
            FeedThrough(parser, "嗯，确认了，五百我打过去了，你查一下到没到。");
            FinishThrough(parser);

            TestRunner.True(lines.Any(l => l.Level == LogLevel.Warning && l.Message.Contains("账上一分未动")),
                "没有标记却声称转账时必须留警告。实际：" + string.Join(" / ", lines.Select(l => l.Message)));
            TestRunner.True(lines.Any(l => l.Message.Contains("打过去了")),
                "警告里要写出命中的依据词，便于定位");
        }
        finally
        {
            TradeMarkerInterceptor.Install(Silent);
        }
    }

    /// <summary>真发了标记就不该再报"嘴上说转了"（标记已经产生了交易单，语义完全不同）。</summary>
    private static void ClaimWithMarkerIsNotReported()
    {
        var lines = new List<(LogLevel Level, string Message)>();
        var log = new DelegateEconomyLog((level, _, message, _) => lines.Add((level, message)));
        TradeMarkerInterceptor.Install(log);
        try
        {
            var parser = new StreamParserDouble(known: true);
            FeedThrough(parser, "行，我转给你 [Trade: 赞助, Amount: 500, Direction: npcToPlayer]");
            FinishThrough(parser);

            TestRunner.True(lines.Any(l => l.Message.Contains("识别出交易标记")), "标记必须被认出来");
            TestRunner.False(lines.Any(l => l.Message.Contains("账上一分未动")),
                "有标记时不该再报'嘴上说转了'");
        }
        finally
        {
            TradeMarkerInterceptor.Install(Silent);
        }
    }

    /// <summary>普通对话（不含转账说法）不该被误报，否则日志会被噪音淹没。</summary>
    private static void PlainChatIsNotReportedAtFinish()
    {
        var lines = new List<(LogLevel Level, string Message)>();
        var log = new DelegateEconomyLog((level, _, message, _) => lines.Add((level, message)));
        TradeMarkerInterceptor.Install(log);
        try
        {
            var parser = new StreamParserDouble(known: true);
            FeedThrough(parser, "你先把今天对白开水那场打完，赢了通关我认。");
            FinishThrough(parser);

            TestRunner.False(lines.Any(l => l.Message.Contains("账上一分未动")),
                "普通对话不该触发转账哨兵。实际：" + string.Join(" / ", lines.Select(l => l.Message)));
        }
        finally
        {
            TradeMarkerInterceptor.Install(Silent);
        }
    }

    /// <summary>
    /// 标记被抽走之后，原版可能已经收下了一个孤立的 <c>[</c>（分片恰好停在标记中间时）。
    /// 它必须被清理掉，而不是作为垃圾字符留在正文里。
    /// </summary>
    private static void PendingMarkerIsNotLeakedToVisibleText()
    {
        var parser = new StreamParserDouble(known: true);

        FeedThrough(parser, "开始 ");
        FeedThrough(parser, "[Tra");                       // 孤立左括号进了原版
        FeedThrough(parser, "de: 赞助, Amount: 5000] 结束");  // 标记闭合，被抽走
        string visible = FinishThrough(parser);

        TestRunner.Contains(visible, "开始", "标记前的正文要在");
        TestRunner.Contains(visible, "结束", "标记后的正文要在");
        TestRunner.NotContains(visible, "[Trade:", "不得漏出标记");
        TestRunner.NotContains(visible, "]", "不得漏出右括号");
        TestRunner.NotContains(visible, "][", "不该出现拼接出来的括号对");
    }

    private static void FinishOnCleanStreamIsHarmless()
    {
        var parser = new StreamParserDouble(known: true);
        FeedThrough(parser, "完全正常的正文，没有任何标记。");
        string before = parser.Text;

        FinishThrough(parser);

        TestRunner.Equal(before, parser.Text, "没有待定标记时 Finish 不该改任何东西");
        TestRunner.Equal("", parser.Error, "不该误置原版的 Error");
    }

    // ── 实例隔离 ────────────────────────────────────────────────────

    /// <summary>
    /// 原版每一轮私信都 new 一个解析器。若拦截器的状态是静态的，
    /// 上一轮没闭合的标记会污染下一轮的文本。
    /// </summary>
    private static void StateIsIsolatedPerParserInstance()
    {
        var first = new StreamParserDouble(known: true);
        var second = new StreamParserDouble(known: true);

        // 第一轮停在半个标记上。
        FeedThrough(first, "第一轮 [Trade: 赞");

        // 第二轮是干净文本：不该因为第一轮的残留而被吃掉任何字。
        string secondVisible = FeedThrough(second, "第二轮的完整正文");
        TestRunner.Contains(secondVisible, "第二轮的完整正文", "两轮之间必须互不影响");

        // 第一轮继续补完，仍能被正确识别。
        string firstVisible = FeedThrough(first, "助, Amount: 5000] 完了");
        TestRunner.NotContains(firstVisible, "[Trade:", "第一轮自己的标记仍要被抽走");
        TestRunner.Contains(second.Text, "第二轮的完整正文", "第二轮的内容不该被改写");
    }

    private static void ResetClearsInstanceState()
    {
        var parser = new StreamParserDouble(known: true);
        FeedThrough(parser, "残留 [Trade: 赞");
        TestRunner.True(TradeMarkerInterceptor.TrackedInstances > 0, "应已跟踪到实例");

        TradeMarkerInterceptor.Reset(parser);
        // 重置之后再喂完整标记，仍然要能识别（等于重新开始一轮流）。
        string visible = FeedThrough(parser, "[Trade: 打赏, Amount: 5000]");
        TestRunner.NotContains(visible, "[Trade:", "重置后仍应正常工作");
    }

    private static void NullInstanceAndNullTextAreSafe()
    {
        TestRunner.True(TradeMarkerInterceptor.RewriteFeed(null, "[Trade: 赞助, Amount: 5000]", out _),
            "拿不到实例时应放行，而不是抛异常");
        TestRunner.True(TradeMarkerInterceptor.RewriteFinish(null), "Finish 同理");

        var parser = new StreamParserDouble(known: true);
        TestRunner.True(TradeMarkerInterceptor.RewriteFeed(parser, null, out _), "null 文本应放行");
        TestRunner.True(TradeMarkerInterceptor.RewriteFeed(parser, "", out _), "空文本应放行");
        TestRunner.Equal("", parser.Text, "不该产生任何可见文本");
    }

    // ── 不弄坏原版 ──────────────────────────────────────────────────

    /// <summary>
    /// 原版自己的指令解析必须照常工作——这是补丁最大的风险点。
    /// 替身模型：<c>[X: ...]</c> 命中 <c>Known</c> 时算一条原版指令，不进正文且计数 +1。
    /// </summary>
    private static void VanillaDirectivesStillParse()
    {
        var parser = new StreamParserDouble(known: true);

        FeedThrough(parser, "[X: 骄傲] ");
        FeedThrough(parser, "[Y: 10] ");
        FeedThrough(parser, "[Trade: 赞助, Amount: 5000] ");
        string visible = FeedThrough(parser, "正文。");

        TestRunner.Equal(2, parser.Directives.Count, "两条原版指令都应被解析出来");
        TestRunner.NotContains(visible, "[X:", "原版指令不进可见正文");
        TestRunner.NotContains(visible, "[Trade:", "我们的标记也不进可见正文");
        TestRunner.Contains(visible, "正文。", "普通正文要留下");
    }

    /// <summary>原版把"普通括号"当正文显示。我们必须保持这个行为。</summary>
    private static void PlainBracketsAreStillVisible()
    {
        var parser = new StreamParserDouble(known: true);
        string visible = FeedThrough(parser, "他说【[重要]】这件事。");

        TestRunner.Contains(visible, "[重要]", "普通方括号必须照常显示给玩家");
    }

    private static void NestedUnknownMarkerIsLeftAlone()
    {
        var parser = new StreamParserDouble(known: true);
        // 与第 5 步协议层一致：不认识的括号组整组放行，不剥里面的 Trade。
        string visible = FeedThrough(parser, "x [未知: [Trade: 赞助, Amount: 5000]] y");

        TestRunner.Contains(visible, "[未知:", "不认识的括号组要原样显示");
        TestRunner.Contains(visible, "y", "后文要在");
    }

    private static void MultipleMarkersInOneChunk()
    {
        var parser = new StreamParserDouble(known: true);
        string visible = FeedThrough(parser,
            "先 [Trade: 打赏, Amount: 5000] 再 [Trade: 赞助, Amount: 5万] 结束");

        TestRunner.NotContains(visible, "[Trade:", "一个分片里的多个标记都要抽走");
        TestRunner.NotContains(visible, "5000", "第一个金额不该泄漏");
        TestRunner.NotContains(visible, "5万", "第二个金额不该泄漏");
        TestRunner.Contains(visible, "先", "正文要在");
        TestRunner.Contains(visible, "结束", "结尾要在");
    }

    // ── 系统：Harmony 装配与自证 ────────────────────────────────────

    /// <summary>
    /// 补丁必须能装到<b>真实的</b> <c>PrivateStreamParser</c> 上，并且自证通过。
    /// <para>
    /// 这正是生产环境启动时那行强制诊断做的事：造一个真实解析器、喂一个标记进去、
    /// 读它的 <c>Text</c>——标记消失了才算真的生效。这条<b>不依赖 AI 配合</b>。
    /// </para>
    /// </summary>
    private static void PatchAppliesToRealParserType()
    {
        var target = typeof(PrivateStreamParser);
        TestRunner.Equal(PrivateStreamPatch.TargetTypeName, target.FullName, "目标类型名必须与原版一致");

        var feed = target.GetMethod(PrivateStreamPatch.FeedMethodName, [typeof(string)]);
        var finish = target.GetMethod(PrivateStreamPatch.FinishMethodName, Type.EmptyTypes);
        TestRunner.True(feed != null, "PrivateStreamParser.Feed(string) 必须存在");
        TestRunner.True(finish != null, "PrivateStreamParser.Finish() 必须存在");

        var harmony = new Harmony("nationalspire.economy.tests.real." + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            TestRunner.True(PrivateStreamPatch.ApplyTo(harmony, target, feed, finish, Silent),
                "补丁应能装到真实解析器上：" + PrivateStreamPatch.Detail);
            TestRunner.True(PrivateStreamPatch.Applied, "应标记为已装配");
            TestRunner.True(PrivateStreamPatch.PatchedMethods.Contains(PrivateStreamPatch.FeedMethodName), "Feed 必须被拦上");
            TestRunner.True(PrivateStreamPatch.PatchedMethods.Contains(PrivateStreamPatch.FinishMethodName), "Finish 也应被拦上");

            bool? selfTest = PatchDiagnostics.SelfTest(target);
            TestRunner.True(selfTest == true,
                "自证应通过。实际=" + selfTest + "，错误=" + (PatchDiagnostics.SelfTestError ?? "(无)"));
        }
        finally
        {
            PrivateStreamPatch.Unapply(harmony, Silent);
        }

        TestRunner.False(PrivateStreamPatch.Applied, "卸载后应还原为未装配");
        TestRunner.Equal(0, PrivateStreamPatch.PatchedMethods.Count, "卸载后补丁列表应为空");
    }

    /// <summary>
    /// 卸载之后，真实解析器必须恢复"标记会漏进正文"的原版行为。
    /// 这条是 <see cref="PatchDiagnostics.SelfTest"/> 的<b>反向验证</b>：
    /// 证明自证确实能区分"补丁生效"与"补丁没生效"，而不是永远返回 true。
    /// </summary>
    private static void PatchUnpatchesCleanly()
    {
        var target = typeof(PrivateStreamParser);

        // 卸载状态下自证必须失败（标记应该留在正文里）。
        bool? unpatched = PatchDiagnostics.SelfTest(target);
        TestRunner.True(unpatched == false,
            "卸载状态下自证必须报未通过（标记会漏出来），否则这个自证没有意义。实际=" + unpatched);
        TestRunner.True(PatchDiagnostics.SelfTestError != null, "应给出失败原因");
    }

    private static void LoggingIsOptional()
    {
        // 没有日志出口时拦截器必须静默工作，不能抛异常。
        TradeMarkerInterceptor.Install(null);
        try
        {
            var parser = new StreamParserDouble(known: true);
            string visible = FeedThrough(parser, "[Trade: 赞助, Amount: 5000] 正文");

            TestRunner.NotContains(visible, "[Trade:", "没有日志出口时也要照常剥离");
            TestRunner.Contains(visible, "正文", "正文照常保留");
        }
        finally
        {
            TradeMarkerInterceptor.Install(Silent);
        }
    }
}
