namespace NationalSpireEconomy.Trade;

/// <summary>
/// 从模型回复的<b>正文</b>里判断"它是不是在声称/承诺已经转账"。
/// <para>
/// <b>为什么需要它（2026-10-11 22:12 与 22:31 两次真机）</b>：模型两次在正文里写
/// "五百我打过去了"/"我再给你转五百"，而<b>一次都没发 `[Trade:]` 标记</b>——
/// 于是账上一分未动，玩家却以为钱到了。更糟的是：游戏把回复的<b>正文</b>存进聊天历史
/// （标记会被剥掉），所以模型下一轮会把"我说过要给你钱"当成"钱已经给过了"，
/// <b>幻觉会自我强化</b>。
/// </para>
/// <para>
/// 它只用于<b>写一行警告日志</b>，不参与任何判定、更不碰钱：命中与否都不改变交易流程，
/// 只是让"模型嘴上说转了、系统这边什么都没发生"这件事在日志里可见。
/// </para>
/// </summary>
public static class TransferClaimDetector
{
    /// <summary>
    /// 声称/承诺转账的说法。刻意包含"我转给你"这类<b>承诺</b>：
    /// 我们的协议要求"愿意给钱就写标记"，所以只说不写正是要抓的那种情况。
    /// </summary>
    public static readonly string[] ClaimWords =
    [
        "已经转", "已经打", "转过去了", "打过去了", "已经到账", "到账了",
        "已经给", "我给了", "钱已经", "已汇",
        "转给你", "给你转", "打给你", "给你打", "给你凑", "我转", "我打"
    ];

    /// <summary>命中返回那个词（诊断日志要写出依据），没命中返回 null。</summary>
    public static string? FirstClaim(string? visibleText)
    {
        if (string.IsNullOrWhiteSpace(visibleText)) return null;
        foreach (string word in ClaimWords)
            if (visibleText.Contains(word, StringComparison.Ordinal))
                return word;
        return null;
    }

    /// <summary>是否在声称/承诺转账。</summary>
    public static bool LooksLikeTransferClaim(string? visibleText) => FirstClaim(visibleText) != null;
}
