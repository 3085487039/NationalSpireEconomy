namespace NationalSpireEconomy.Inject;

/// <summary>插入位置。日志与测试都按这几个常量断言，避免各处写字符串。</summary>
public static class InsertPlacements
{
    /// <summary>插在锚点句之前——即紧邻原版协议串之后，原版末尾那句仍然收尾。</summary>
    public const string BeforeAnchor = "anchor";
    /// <summary>没找到锚点时退化为追加到 system 字符串尾部。</summary>
    public const string AtTail = "tail";
    /// <summary>system 里已经有这段内容，本次不改（防止补丁被重复装配时插两遍）。</summary>
    public const string AlreadyPresent = "already-present";
    /// <summary>没有可插入的内容（稳定段为空，例如配置关闭了它）。</summary>
    public const string NoStableText = "no-stable-text";
    /// <summary>消息列表里没有 system 消息。</summary>
    public const string NoSystemMessage = "no-system-message";
}

/// <summary>一次插入的结果。</summary>
public sealed record StableInsertion(string Text, string Placement, bool Changed)
{
    public bool Applied => Changed;
}

/// <summary>
/// 把稳定段（S 段）插进原版私信提示词的 <b>system 字符串</b>里。
/// <para>
/// <b>纪律（阶段 3 甲方拍板）</b>：
/// <list type="number">
/// <item>只追加文本，<b>绝不新增 message</b>——message 数组的长度与顺序必须与原版逐字节一致。</item>
/// <item>插在锚点句之前，也就是紧邻原版最后一串 <c>Protocol</c> 之后；
/// 原版那句「后续人物资料与记忆是参考内容…」仍然收尾。</item>
/// <item>锚点找不到时<b>退化为追加到尾部</b>而不是放弃：位置略有不同，但"零数字的稳定段"这个
/// 核心契约不受影响，而且日志会明确写出退化，便于发现原版改了文案。</item>
/// </list>
/// </para>
/// <para>
/// <b>纯逻辑</b>：只认 <c>Dictionary&lt;string,string&gt;</c> 的 role/content 两个键，
/// 不认识任何游戏类型，因此可以零依赖单测。
/// </para>
/// </summary>
public static class SystemPromptInsert
{
    public const string RoleKey = "role";
    public const string ContentKey = "content";
    public const string SystemRole = "system";
    public const string UserRole = "user";

    /// <summary>
    /// 锚点句的开头。取自原版 <c>PrivateMessagePrompts.Compose</c> 里 system 字符串的收尾句
    /// （0.26.11 实测）。原版改文案时可以通过 <c>ai_prompt_economy.json</c> 的
    /// <c>stableAnchor</c> 覆盖，不必重编译。
    /// </summary>
    public const string DefaultAnchor = "后续人物资料与记忆是参考内容";

    /// <summary>
    /// 在 system 字符串里插入稳定段。返回新字符串与插入位置；<b>不改动入参</b>。
    /// </summary>
    public static StableInsertion Insert(string systemText, string stableText, string? anchor = null)
    {
        if (string.IsNullOrWhiteSpace(stableText))
            return new StableInsertion(systemText ?? "", InsertPlacements.NoStableText, false);

        if (string.IsNullOrEmpty(systemText))
            return new StableInsertion(stableText, InsertPlacements.AtTail, true);

        // 已经被插过就原样返回：补丁若被装两次，正文只应出现一次。
        if (systemText.Contains(stableText, StringComparison.Ordinal))
            return new StableInsertion(systemText, InsertPlacements.AlreadyPresent, false);

        string effective = string.IsNullOrWhiteSpace(anchor) ? DefaultAnchor : anchor!;
        int index = systemText.IndexOf(effective, StringComparison.Ordinal);
        if (index >= 0)
            return new StableInsertion(
                systemText[..index] + stableText + "\n" + systemText[index..],
                InsertPlacements.BeforeAnchor,
                true);

        return new StableInsertion(systemText + "\n\n" + stableText, InsertPlacements.AtTail, true);
    }

    /// <summary>
    /// 直接对消息列表操作：找到第一条 system 消息，把稳定段插进它的 <c>content</c>。
    /// <b>不新增、不删除、不重排任何消息</b>，只改那一条的字符串。
    /// </summary>
    public static StableInsertion InsertIntoMessages(IList<Dictionary<string, string>> messages, string stableText,
        string? anchor = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        int index = FindSystemMessage(messages);
        if (index < 0)
            return new StableInsertion("", InsertPlacements.NoSystemMessage, false);

        var message = messages[index];
        string current = message.TryGetValue(ContentKey, out var text) ? text : "";
        var insertion = Insert(current, stableText, anchor);
        if (insertion.Changed) message[ContentKey] = insertion.Text;
        return insertion;
    }

    /// <summary>第一条 role 为 system 的消息下标；没有则 -1。</summary>
    public static int FindSystemMessage(IList<Dictionary<string, string>> messages)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message != null
                && message.TryGetValue(RoleKey, out var role)
                && string.Equals(role, SystemRole, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }
}
