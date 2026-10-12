using NationalSpireEconomy.Trade;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 玩家私信指令的解析（<see cref="PlayerTradeCommand"/>）。
/// <para>
/// 这一套的核心是两条互相拉扯的要求：
/// <b>① 要认得原版包了壳的消息</b>（真机事故：玩家打了「确认 o0001」，模组一声不吭）；
/// <b>② 又绝不能"全文包含"</b>——否则我们自己写在预览里的
/// 「在私信里回一句『确认 o0001』」就会被当成玩家的指令，变成没人下令却自动确认，
/// 那是直接动钱的口子。<b>宁可漏，不可错。</b>
/// </para>
/// </summary>
public static class PlayerTradeCommandTests
{
    public static Action[] Cases =>
    [
        BareConfirmIsParsed,
        BareDeclineIsParsed,
        WrappedConfirmIsParsed,
        WrappedDeclineIsParsed,
        CommandWithoutOfferIdIsParsed,
        DealMarkerFormIsParsed,
        MultiLineTakeTheOfferIdWithoutTheTail,
        MentionedButNotAtLineStartIsNotACommand,
        OurOwnPreviewTextIsNotACommand,
        EmptyAndBlankAreNotCommands,
        LooksLikeCommandAttemptTellsSilenceFromNotUnderstood,
        StripVanillaWrapperKeepsOnlyTheBody
    ];

    /// <summary>原版那条壳的真机形状（证据：NationalSpire.dll 的模板串 + InjectDiagnostics 的组装自检）。</summary>
    private const string Wrapper = "第 6 赛季第 32 天，玩家发给苹果的消息\n";

    /// <summary>断言能解析出来并给出结果，省掉满篇的 <c>!</c> 与重复断言。</summary>
    private static (bool Confirm, string OfferId) Parsed(string? text)
    {
        var hit = PlayerTradeCommand.Parse(text);
        TestRunner.True(hit != null, "应解析出指令，实际没解析出来：" + Flat(text));
        return hit!.Value;
    }

    private static string Flat(string? text) => (text ?? "<null>").Replace("\n", "\\n");

    private static void BareConfirmIsParsed()
    {
        var parsed = Parsed("确认 o0001");
        TestRunner.True(parsed.Confirm, "应判为确认");
        TestRunner.Equal("o0001", parsed.OfferId);
    }

    private static void BareDeclineIsParsed()
    {
        var parsed = Parsed("拒绝 o0001");
        TestRunner.False(parsed.Confirm, "应判为拒绝");
        TestRunner.Equal("o0001", parsed.OfferId);
    }

    /// <summary>★ 真机事故回归：原版把玩家的话包成「…发给…的消息\n&lt;正文&gt;」。</summary>
    private static void WrappedConfirmIsParsed()
    {
        var parsed = Parsed(Wrapper + "确认 o0001");
        TestRunner.True(parsed.Confirm);
        TestRunner.Equal("o0001", parsed.OfferId);
    }

    private static void WrappedDeclineIsParsed()
    {
        var parsed = Parsed(Wrapper + "拒绝 o0001");
        TestRunner.False(parsed.Confirm);
        TestRunner.Equal("o0001", parsed.OfferId);
    }

    /// <summary>不写单号：由调用方回退到"本世界当前待确认的那张单"。</summary>
    private static void CommandWithoutOfferIdIsParsed()
    {
        var parsed = Parsed(Wrapper + "确认");
        TestRunner.True(parsed.Confirm);
        TestRunner.Equal("", parsed.OfferId, "没写单号时应给空串，交给上层回退");
    }

    private static void DealMarkerFormIsParsed()
    {
        var parsed = Parsed("[Deal: accept] o0001");
        TestRunner.True(parsed.Confirm, "协议里写过的 [Deal: accept] 形式也要认");
        TestRunner.Equal("o0001", parsed.OfferId);
    }

    /// <summary>玩家可能多打一句：单号只取指令那一段，不能被后面的字粘住。</summary>
    private static void MultiLineTakeTheOfferIdWithoutTheTail()
    {
        var parsed = Parsed("确认 o0001\n谢谢，房租交上了");
        TestRunner.Equal("o0001", parsed.OfferId, "单号不能被换行后的正文粘成一串");
    }

    /// <summary>★ 钱的安全阀：指令必须出现在<b>行首</b>，正文里提到不算。</summary>
    private static void MentionedButNotAtLineStartIsNotACommand()
    {
        TestRunner.True(PlayerTradeCommand.Parse("玩家如何确认：在私信里回一句「确认 o0001」") == null,
            "正文里提到「确认」不能当成玩家下令");
        TestRunner.True(PlayerTradeCommand.Parse("我确认一下再答复你") == null,
            "「我确认一下」这种日常说法绝不能触发确认");
        TestRunner.True(PlayerTradeCommand.Parse("对方说拒绝 o0001 也没关系") == null,
            "拒绝也要行首才算");
    }

    /// <summary>我们自己写进 V 段/预览的说明文字，绝不能变成指令（否则会自动确认交易）。</summary>
    private static void OurOwnPreviewTextIsNotACommand()
    {
        const string preview =
            "【成交预览·只读】赞助（NPC → 你）｜交易单 o0001\n" +
            "  玩家如何确认：在私信里回一句「确认 o0001」（或「拒绝 o0001」），或直接在游戏弹窗上点确认。";

        TestRunner.True(PlayerTradeCommand.Parse(preview) == null,
            "预览文案里含着「确认 o0001」，但它不是玩家的指令——全文包含式解析会在这里自动确认交易");
    }

    private static void EmptyAndBlankAreNotCommands()
    {
        TestRunner.True(PlayerTradeCommand.Parse(null) == null);
        TestRunner.True(PlayerTradeCommand.Parse("") == null);
        TestRunner.True(PlayerTradeCommand.Parse("   \n  \n") == null);
    }

    /// <summary>
    /// 诊断用：区分"玩家压根没提确认"（Debug）与"提了但我们没听懂"（Warning）。
    /// 真机事故里正是这两者分不开，才让一条静默失效藏了一整轮。
    /// <para>
    /// <b>阶段 4 第五步 E 项</b>：判据收紧成<b>行首</b>之后，这条诊断只会在
    /// "长得就像一条指令、但没解析成"时出现。真机 <c>12:28:08</c> 的
    /// "<b>你再确认一下</b>"不再命中——它不是指令形状，只是普通聊天。
    /// </para>
    /// </summary>
    private static void LooksLikeCommandAttemptTellsSilenceFromNotUnderstood()
    {
        TestRunner.False(PlayerTradeCommand.LooksLikeCommandAttempt("你好，最近怎么样"), "闲聊不该被当成尝试下指令");
        TestRunner.True(PlayerTradeCommand.LooksLikeCommandAttempt(Wrapper + "确认 o0001"));
        TestRunner.True(PlayerTradeCommand.LooksLikeCommandAttempt("确认"),
            "裸的「确认」也是指令形状（上层会回退到当前待确认的那张单）");

        // ★ E 项的真机回归：这三句都是普通聊天，都**不该**再刷 Warning。
        TestRunner.False(PlayerTradeCommand.LooksLikeCommandAttempt("你再确认一下"),
            "★真机 12:28:08：「你再确认一下」是聊天，不是指令形状（E 项收紧点）");
        TestRunner.False(PlayerTradeCommand.LooksLikeCommandAttempt("这单我确认了但先不执行"),
            "「我确认了」不在行首，按 Parse 的判据它本来就解析不出来");
        TestRunner.False(PlayerTradeCommand.LooksLikeCommandAttempt("我拒绝了这个报价"),
            "提到「拒绝」但不是行首，同样不算指令形状");

        // 反过来：真的打了指令却没解析成（例如单号写错），必须仍然报出来。
        TestRunner.True(PlayerTradeCommand.LooksLikeCommandAttempt(Wrapper + "确认"),
            "包了壳的指令仍然要能被诊断认出来");

        // 判据同源：能被诊断认出的，Parse 必然也认（反之亦然）——这条是结构性的。
        TestRunner.Equal(PlayerTradeCommand.Parse("确认 o0001") != null,
            PlayerTradeCommand.LooksLikeCommandAttempt("确认 o0001"),
            "★两者必须同源：能解析 ⇒ 命中诊断形状");
    }

    private static void StripVanillaWrapperKeepsOnlyTheBody()
    {
        TestRunner.Equal("确认 o0001", PlayerTradeCommand.StripVanillaWrapper(Wrapper + "确认 o0001"));
        TestRunner.Equal("确认 o0001", PlayerTradeCommand.StripVanillaWrapper("确认 o0001"),
            "没有壳的裸文本原样返回");
    }
}
