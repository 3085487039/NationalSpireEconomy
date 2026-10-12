using NationalSpireEconomy.Trade;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// "模型嘴上说转了"的检测（<see cref="TransferClaimDetector"/>）。
/// <para>
/// 它只喂日志哨兵，所以判据要<b>偏敏感</b>：宁可多写一行警告，也不要漏掉
/// "它说钱转了、账上一分未动"这种情况（真机 22:12 与 22:31 各一次）。
/// </para>
/// </summary>
public static class TransferClaimDetectorTests
{
    public static Action[] Cases =>
    [
        ClaimsAreDetectedInChineseProse,
        PromisesCountAsClaims,
        PlainChatIsNotAClaim,
        EmptyAndNullAreNotClaims,
        FirstClaimReportsTheEvidenceWord
    ];

    private static void ClaimsAreDetectedInChineseProse()
    {
        // 22:12 真机原文
        TestRunner.True(TransferClaimDetector.LooksLikeTransferClaim(
            "嗯，确认了，五百我打过去了，你查一下到没到。房租交了就行。"), "「打过去了」必须命中");
        // 22:31 真机原文
        TestRunner.True(TransferClaimDetector.LooksLikeTransferClaim(
            "行吧，我再给你转五百，这是我第二次掏钱了。"), "「给你转」必须命中");
    }

    private static void PromisesCountAsClaims()
    {
        // 协议要求"愿意给钱就写标记"，所以只承诺不写标记正是要抓的情况。
        TestRunner.True(TransferClaimDetector.LooksLikeTransferClaim("这钱我转给你，别急。"));
        TestRunner.True(TransferClaimDetector.LooksLikeTransferClaim("已经到账了，你查一下。"));
    }

    private static void PlainChatIsNotAClaim()
    {
        TestRunner.False(TransferClaimDetector.LooksLikeTransferClaim(
            "你先把今天对白开水那场打完，赢了通关我认。"), "普通对话不该被判成转账声明");
        TestRunner.False(TransferClaimDetector.LooksLikeTransferClaim(
            "房租？你一个顶着进阶10八分二十秒纪录的人跑来跟我借房租。"));
    }

    private static void EmptyAndNullAreNotClaims()
    {
        TestRunner.False(TransferClaimDetector.LooksLikeTransferClaim(null));
        TestRunner.False(TransferClaimDetector.LooksLikeTransferClaim(""));
        TestRunner.False(TransferClaimDetector.LooksLikeTransferClaim("   "));
    }

    private static void FirstClaimReportsTheEvidenceWord()
    {
        string? word = TransferClaimDetector.FirstClaim("钱已经到账了，别再催。");
        TestRunner.True(word != null, "应给出命中的那个词（日志里要写出依据）");
        TestRunner.Contains("钱已经到账了，别再催。", word!, "报告的词必须真的出现在原文里");
    }
}
