namespace NationalSpireEconomy.Trade;

/// <summary>
/// 玩家在私信里下达的交易指令（「确认 &lt;单号&gt;」/「拒绝 &lt;单号&gt;」）的解析。
/// <para>
/// <b>为什么它必须容忍"包了壳"的文本（2026-10-11 22:12 真机事故）</b>：原版把私信里的每一句话
/// 都渲染成一条独立消息，正文前面顶着一层壳：
/// </para>
/// <code>第 6 赛季第 32 天，玩家发给苹果的消息\n确认 o0001</code>
/// <para>
/// 于是"文本以『确认』二字开头"这种判定永远不成立 —— 玩家打了指令，模组毫无反应，
/// 日志里<b>连一行都不留</b>（真机实测：玩家打了「确认 o0001」，`[Economy-Confirm]` 一条都没有，
/// 只有模型自己在回复里说"钱打过去了"）。壳的形状有两处独立证据：原版
/// <c>NationalSpire.dll</c> 里的模板串 <c>…赛季第…天，…发给…的消息\n</c>，
/// 以及本模组自己的组装自检（<c>InjectDiagnostics</c> 里按"system → 人物资料 → 历史记录 →
/// 上下文 → 玩家消息"的顺序构造，玩家那条正是这个形状）。
/// </para>
/// <para>
/// 所以这里按<b>行</b>匹配：壳在头一行，玩家的正文在换行之后。
/// </para>
/// <para>
/// <b>为什么只认行首、绝不"全文包含"</b>：我们的 V 段与成交预览里就写着
/// 「玩家如何确认：在私信里回一句『确认 o0001』」这样的字。一旦改成全文包含，
/// 这段话本身就会被当成玩家的指令 —— 变成"没人下令却自动确认"，那是直接动钱的口子。
/// <b>宁可漏，不可错。</b>
/// </para>
/// </summary>
public static class PlayerTradeCommand
{
    /// <summary>玩家确认指令的前缀（大小写/全半角都容忍）。</summary>
    public static readonly string[] ConfirmWords = ["确认", "接受", "[deal: confirm", "[deal:accept", "[deal: accept"];

    /// <summary>玩家拒绝指令的前缀。</summary>
    public static readonly string[] DeclineWords = ["拒绝", "不干", "[deal: decline", "[deal:decline", "[deal: reject"];

    /// <summary>原版那句壳的标识词（诊断用，不参与判定）。</summary>
    public const string WrapperMarker = "的消息";

    /// <summary>
    /// 解析一句（或一段）玩家文本。命中返回 <c>(是否确认, 单号)</c>；不成指令返回 null。
    /// <para>接受的形式：<c>确认 o0001</c>、<c>确认</c>、<c>拒绝 o0001</c>、<c>[Deal: confirm o0001]</c>，
    /// 以及被原版包了壳的 <c>第 6 赛季第 32 天，玩家发给苹果的消息\n确认 o0001</c>。</para>
    /// </summary>
    public static (bool Confirm, string OfferId)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // ① 整段：兼容"玩家就打了这一句"的裸文本（单测与聊天兜底都是这个形状）。
        var hit = MatchLine(text.Trim());
        if (hit != null) return hit;

        // ② 逐行：原版的壳在首行，玩家的正文在换行之后。从最后一行往前找——玩家的话通常在最末。
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            hit = MatchLine(line);
            if (hit != null) return hit;
        }

        return null;
    }

    /// <summary>
    /// 看起来像在打指令、却没解析出来（诊断用）。
    /// <para>
    /// 有了它，"我们没听懂"和"玩家压根没说话"在日志里就能一眼分开——
    /// 真机事故里正是这两者无法区分，才让一条静默失效藏了一整轮。
    /// </para>
    /// <para>
    /// <b>阶段 4 第五步 E 项：判据收紧成"行首"。</b>原先它是"全文包含『确认』/『拒绝』"，
    /// 于是玩家一句日常话也会被记成 Warning —— 真机实例（<c>12:28:08</c>）：
    /// 玩家正文写"<b>你再确认一下</b>"，日志里就多出一行
    /// <c>[Economy-Confirm] 玩家文本里出现确认/拒绝字样，但没能解析成指令</c>。
    /// 那行没有任何副作用，但它把"我们没听懂"这条诊断的<b>信噪比</b>拉低了：
    /// 真正该看的那种"打了『确认 o0001』却毫无反应"会淹在里面。
    /// </para>
    /// <para>
    /// 收紧后它<b>与 <see cref="Parse"/> 的判据完全同源</b>（都是 <see cref="MatchLine"/> 的"行首"规则）：
    /// 于是"这行 Warning 会不会出现"这个问题的答案可以直接读出来——
    /// <b>Warning 只在"解析本可以成功、却因为别的原因没成功"时才可能出现</b>，
    /// 而不是在玩家随口聊天时。宁可漏，不可错。
    /// </para>
    /// </summary>
    public static bool LooksLikeCommandAttempt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // 与 Parse 同一条遍历：最后一行往前（玩家的话通常在最末），壳在首行。
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (MatchLine(line) != null) return true;
        }

        return false;
    }

    /// <summary>剥掉原版那层壳，只留正文（诊断日志用；解析本身不依赖它）。</summary>
    public static string StripVanillaWrapper(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int newline = text.IndexOf('\n');
        return newline >= 0 ? text[(newline + 1)..].Trim() : text.Trim();
    }

    /// <summary>
    /// <b>唯一的"这一行是不是指令"判定</b>：<see cref="Parse"/> 与
    /// <see cref="LooksLikeCommandAttempt"/> 都走它，于是"能解析的"与"会被诊断记下的"
    /// 结构上不可能分叉。
    /// </summary>
    private static (bool Confirm, string OfferId)? MatchLine(string line)
    {
        foreach (string word in ConfirmWords)
            if (line.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                return (true, Tail(line, word));

        foreach (string word in DeclineWords)
            if (line.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                return (false, Tail(line, word));

        return null;
    }

    private static string Tail(string text, string word)
    {
        string rest = text[word.Length..].Trim().TrimStart(':', '：').Trim()
            .Trim('[', ']', '「', '」', '"', '“', '”').Trim();
        // 只取像一个单号的那一段（我们的单号形如 o0001）。
        // 按【任意空白】切断而不是只认空格：玩家可能写成「确认 o0001\n谢谢，房租交上了」，
        // 只认空格会让单号被换行后的正文粘成一串，白跑一次"找不到交易单"。
        int cut = rest.IndexOfAny([' ', '\t', '\n', '\r']);
        if (cut >= 0) rest = rest[..cut];
        return rest.Trim().Trim('[', ']', '。', '，', ',', '.');
    }
}
