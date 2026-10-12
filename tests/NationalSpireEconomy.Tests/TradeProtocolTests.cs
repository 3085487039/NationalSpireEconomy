using System.Text;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Trade;
using static NationalSpireEconomy.Trade.TradeIntent;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 协议解析：<c>[Trade: ...]</c> / <c>[Buy: ...]</c> 的增量流式解析与正文剔除。
/// <para>
/// 这一套是第 5 步的前置：原版的正则白名单不认 Trade，标记会被当作正文显示给玩家，
/// 所以我们必须在 <c>PrivateStreamParser.Feed</c> 之前把标记抽走。
/// </para>
/// </summary>
public static class TradeProtocolTests
{
    public static Action[] Cases =>
    [
        CompleteMarkerIsParsed,
        FullWidthColonIsAccepted,
        ReasonWithCommaIsKeptWhole,
        MultipleMarkersAreAllParsed,
        BuyMarkerCarriesItemLine,
        BuyMarkerWithoutItemIdIsRejected,
        AmountAcceptsChineseUnits,
        AmountAcceptsDollarAndThousands,
        NonNumericAmountIsReported,
        MissingAmountIsReported,
        MarkerSplitAcrossThreeChunksIsStillParsed,
        MarkerSplitOneCharAtATimeIsStillParsed,
        StripRemovesMarkerAndKeepsSurroundingText,
        UnknownMarkerAndNestedMarkerAreLeftAlone,
        UnclosedMarkerIsReleasedAndReported,
        EmptyChunkAdvancesPendingMarker,
        PlainTextPassesThroughUnchanged,
        ReasonMustBeLastFieldToKeepCommas
    ];

    private static string Clean(string text)
    {
        var builder = new StringBuilder();
        var protocol = new TradeProtocol();
        protocol.Feed(text, builder);
        protocol.Flush(builder);
        return builder.ToString();
    }

    private static TradeIntent Single(string text)
    {
        var intents = TradeProtocol.ParseAll(text);
        TestRunner.Equal(1, intents.Count, "应当解析出恰好 1 条意图：" + text);
        return intents[0];
    }

    // ── 基本解析 ─────────────────────────────────────────────────────

    private static void CompleteMarkerIsParsed()
    {
        var intent = Single("[Trade: 赞助, Amount: 50000, Reason: 这赛季刚拿工资]");

        TestRunner.Equal("赞助", intent.Kind, "Kind 不符");
        TestRunner.Equal(50_000L, intent.AmountUsd, "金额不符");
        TestRunner.Equal("这赛季刚拿工资", intent.Reason, "原因不符");
        // 无错误时必须恰好是 null：ParseFailed 与调用方都按 null 判定，
        // 空串会让"看起来没错误"的意图被判成解析失败。
        TestRunner.True(intent.ParseError is null, $"无错误时 ParseError 应为 null，实际 <{intent.ParseError ?? "null"}>");
        TestRunner.False(intent.ParseFailed, "不应判定为解析失败");
        TestRunner.False(intent.HasItems, "纯转账不应带物品行");
        TestRunner.Contains(intent.RawText, "[Trade:", "RawText 应保留原始标记供审计");
    }

    private static void FullWidthColonIsAccepted()
    {
        var intent = Single("[Trade：打赏，Amount：5000，Reason：你这局打得漂亮]");

        TestRunner.Equal("打赏", intent.Kind, "全角冒号/逗号也应能解析");
        TestRunner.Equal(5_000L, intent.AmountUsd);
        TestRunner.Equal("你这局打得漂亮", intent.Reason);
        TestRunner.True(intent.ParseError is null, $"全角写法不应报错，实际 <{intent.ParseError}>");
    }

    /// <summary>
    /// Reason 是最后一个字段，内部允许出现逗号。
    /// 若解析器对整段无脑按逗号切开，理由会被切成两半并丢掉后半句。
    /// </summary>
    private static void ReasonWithCommaIsKeptWhole()
    {
        var intent = Single("[Trade: 赞助, Amount: 50000, Reason: 这赛季刚拿工资，正好赞助你一点]");
        TestRunner.Equal("这赛季刚拿工资，正好赞助你一点", intent.Reason, "带逗号的理由被切断了");
        TestRunner.Equal(50_000L, intent.AmountUsd, "理由里的逗号不应影响金额解析");
        TestRunner.True(intent.ParseError is null, $"不应报错，实际 <{intent.ParseError}>");
    }

    private static void ReasonMustBeLastFieldToKeepCommas()
    {
        // Amount 放在 Reason 之后属于畸形写法：声明了"Reason 是最后一个字段"，
        // 那么之后的段落会被当成理由正文。这里钉住的是：它不会静默变成 0。
        var intent = Single("[Trade: 赞助, Reason: 先给一点，Amount: 50000]");
        TestRunner.True(intent.AmountUsd != 0 || intent.ParseError != null,
            "Amount 在 Reason 之后属于畸形写法，不应被静默接受为正确金额");
    }

    private static void MultipleMarkersAreAllParsed()
    {
        var intents = TradeProtocol.ParseAll(
            "开头 [Trade: 打赏, Amount: 5000] 中间 [Trade: 赞助, Amount: 5万] 结尾");

        TestRunner.Equal(2, intents.Count);
        TestRunner.Equal("打赏", intents[0].Kind);
        TestRunner.Equal(5_000L, intents[0].AmountUsd);
        TestRunner.Equal("赞助", intents[1].Kind);
        TestRunner.Equal(50_000L, intents[1].AmountUsd);
    }

    // ── [Buy: ...] ───────────────────────────────────────────────────

    private static void BuyMarkerCarriesItemLine()
    {
        var intent = Single("[Buy: 礼物, ItemId: potion_energy, Amount: 3000, Reason: 送你个能量药水]");

        TestRunner.Equal(BuyKind, intent.Kind, "Buy 应映射到「购买」");
        TestRunner.Equal("购买", BuyKind, "BuyKind 的取值是协议契约的一部分");
        TestRunner.Equal(3_000L, intent.AmountUsd);
        TestRunner.True(intent.HasItems, "Buy 必须带物品行");
        TestRunner.Equal(1, intent.Items.Count);
        TestRunner.Equal("potion_energy", intent.Items[0].ItemId);
        TestRunner.Equal(1, intent.Items[0].Quantity, "未指定数量时默认为 1");
        TestRunner.True(intent.ParseError is null, $"不应报错，实际 <{intent.ParseError}>");
    }

    private static void BuyMarkerWithoutItemIdIsRejected()
    {
        var intent = Single("[Buy: 礼物, Amount: 3000]");
        TestRunner.True(intent.ParseFailed, "缺 ItemId 的 Buy 应当报错");
        TestRunner.Contains(intent.ParseError!, "ItemId");
    }

    // ── 金额解析 ─────────────────────────────────────────────────────

    private static void AmountAcceptsChineseUnits()
    {
        TestRunner.Equal(50_000L, Single("[Trade: 赞助, Amount: 5万]").AmountUsd, "5万 应为 50000");
        TestRunner.Equal(12_000L, Single("[Trade: 赞助, Amount: 1.2万]").AmountUsd, "1.2万 应为 12000");
        TestRunner.Equal(55_000L, Single("[Trade: 赞助, Amount: 5万5千]").AmountUsd, "5万5千 应为 55000");
    }

    /// <summary>
    /// 金额一律经 <c>MoneyUnits.TryParseUsd</c>，绝不在解析器里自己写换算。
    /// </summary>
    private static void AmountAcceptsDollarAndThousands()
    {
        TestRunner.Equal(50_000L, Single("[Trade: 赞助, Amount: $50,000]").AmountUsd, "$50,000 应为 50000");
        TestRunner.Equal(50_000L, Single("[Trade: 赞助, Amount: 50000]").AmountUsd, "50000 应为 50000");

        // 与唯一换算出口逐字对齐，防止某天有人"顺手优化"成自己解析。
        TestRunner.True(MoneyUnits.TryParseUsd("$50,000", out long reference));
        TestRunner.Equal(reference, Single("[Trade: 赞助, Amount: $50,000]").AmountUsd, "解析结果必须与 MoneyUnits 一致");
    }

    private static void NonNumericAmountIsReported()
    {
        var intent = Single("[Trade: 赞助, Amount: 很多钱]");
        TestRunner.True(intent.ParseError != null, "非数字金额应当报错而不是静默变成 0");
        TestRunner.Contains(intent.ParseError!, "Amount");
    }

    private static void MissingAmountIsReported()
    {
        var intent = Single("[Trade: 赞助, Reason: 就是想给]");
        TestRunner.True(intent.ParseError != null, "缺少 Amount 应当报错");
        TestRunner.Contains(intent.ParseError!, "Amount");
    }

    // ── 增量分片 ─────────────────────────────────────────────────────

    private static void MarkerSplitAcrossThreeChunksIsStillParsed()
    {
        var protocol = new TradeProtocol();
        var clean = new StringBuilder();
        var collected = new List<TradeIntent>();

        collected.AddRange(protocol.Feed("前面的话 [Trade: 赞", clean));
        TestRunner.Equal(0, collected.Count, "分片未闭合时不应产出意图");
        TestRunner.True(protocol.HasPendingMarker, "未闭合的标记应留在待定缓冲里");

        collected.AddRange(protocol.Feed("助, Amount: 50000, Rea", clean));
        TestRunner.Equal(0, collected.Count, "仍然未闭合，不应产出意图");

        collected.AddRange(protocol.Feed("son: 这赛季刚拿工资，正好赞助你一点] 后面的话", clean));
        collected.AddRange(protocol.Flush(clean));

        TestRunner.Equal(1, collected.Count, "跨 3 个分片仍应解析出 1 条意图");
        TestRunner.Equal("赞助", collected[0].Kind);
        TestRunner.Equal(50_000L, collected[0].AmountUsd);
        TestRunner.Equal("这赛季刚拿工资，正好赞助你一点", collected[0].Reason);
        TestRunner.Equal("前面的话  后面的话", clean.ToString(), "剔除标记后应保留前后正文");
    }

    /// <summary>最极端的分片：一次只喂一个字符。</summary>
    private static void MarkerSplitOneCharAtATimeIsStillParsed()
    {
        const string text = "A[Trade: 打赏, Amount: 5000, Reason: 漂亮]B";
        var protocol = new TradeProtocol();
        var clean = new StringBuilder();
        var collected = new List<TradeIntent>();

        foreach (char c in text)
        {
            collected.AddRange(protocol.Feed(c.ToString(), clean));
            // 每个字符之后，缓冲里要么是空、要么是未闭合标记，绝不能丢字符。
            TestRunner.False(clean.Length > text.Length, "剔除后的正文不应比原文更长");
        }
        collected.AddRange(protocol.Flush(clean));

        TestRunner.Equal(1, collected.Count, "逐字符喂入也应解析出意图");
        TestRunner.Equal(5_000L, collected[0].AmountUsd);
        TestRunner.Equal("AB", clean.ToString(), "逐字符剔除后应剩 AB");
    }

    private static void EmptyChunkAdvancesPendingMarker()
    {
        var protocol = new TradeProtocol();
        var clean = new StringBuilder();

        // 模拟"分片刚好停在 ] 之前"，标记其实已经完整落在缓冲里，
        // 但因为 Drain 只在没有完整括号组时才留存，这里用空串再推一次确认不会重复产出。
        protocol.Feed("[Trade: 打赏, Amount: 5000]", clean);
        var again = protocol.Feed("", clean);

        TestRunner.Equal(0, again.Count, "空分片不应重复产出已经解析过的意图");
        TestRunner.Equal("", clean.ToString(), "标记应已被完全剔除");
    }

    // ── 剔除与保守放行 ───────────────────────────────────────────────

    private static void StripRemovesMarkerAndKeepsSurroundingText()
    {
        string clean = Clean("你好 [Trade: 赞助, Amount: 50000, Reason: 给你] 再见");
        TestRunner.NotContains(clean, "[Trade:", "剔除后不应残留 [Trade:");
        TestRunner.NotContains(clean, "赞助", "剔除后不应残留标记内容");
        TestRunner.Contains(clean, "你好", "应保留前文");
        TestRunner.Contains(clean, "再见", "应保留后文");
    }

    /// <summary>
    /// 不认识的标记原样放行，<b>且不剥掉嵌套在内的 Trade 标记</b>——
    /// 剥掉会让原版解析器看到一个残缺的 <c>[Profile: ...]</c>，属于破坏原版行为。
    /// </summary>
    private static void UnknownMarkerAndNestedMarkerAreLeftAlone()
    {
        const string text = "x [未知: [Trade: 赞助, Amount: 50000]] y";
        var intents = TradeProtocol.ParseAll(text);

        TestRunner.Equal(0, intents.Count, "嵌套在不认识标记里的 Trade 不应被抽出");
        TestRunner.Equal(text, Clean(text), "不认识的标记应原样放行");

        // 原版自己的标记同样不许动。
        const string vanilla = "[Profile: 骄傲, 要面子]";
        TestRunner.Equal(vanilla, Clean(vanilla), "原版标记必须原样保留");
        TestRunner.Equal(0, TradeProtocol.ParseAll(vanilla).Count);
    }

    /// <summary>
    /// 只有 <c>[</c> 没有 <c>]</c> 的畸形输入：正文必须被放行（不能永久吞掉），
    /// 同时产出一条带 ParseError 的意图供审计。
    /// </summary>
    private static void UnclosedMarkerIsReleasedAndReported()
    {
        var intents = TradeProtocol.ParseAll("[Trade: 赞助, Amount: 50000");

        TestRunner.Equal(1, intents.Count, "未闭合标记应产出 1 条解析失败记录");
        TestRunner.True(intents[0].ParseError != null, "未闭合标记必须带 ParseError");
        TestRunner.Contains(intents[0].ParseError!, "闭合");

        // 完全不像标记的未闭合方括号必须原样放行。
        TestRunner.Equal("[不是标记 后面的话", Clean("[不是标记 后面的话"), "普通方括号文本不应被吞掉");
    }

    private static void PlainTextPassesThroughUnchanged()
    {
        const string text = "这是一段没有任何标记的普通正文，带 [方括号] 和标点。";
        TestRunner.Equal(text, Clean(text), "无标记文本必须逐字不变");
        TestRunner.Equal(0, TradeProtocol.ParseAll(text).Count);
    }
}
