namespace NationalSpireEconomy.Inject;

/// <summary>易变段（V 段）的落点。日志与测试都按这几个常量断言。</summary>
public static class VolatilePlacements
{
    /// <summary>追加到 <c>ordered.Current</c> 那条 user 消息的末尾（甲方拍板的方案 a）。</summary>
    public const string Appended = "appended";
    /// <summary>那条消息里已经有同一段内容，本次不改（防止补丁被重复装配时插两遍）。</summary>
    public const string AlreadyPresent = "already-present";
    /// <summary>没有可插入的内容（易变段为空，例如配置关闭了它）。</summary>
    public const string NoVolatileText = "no-volatile-text";
    /// <summary>找不到带锚点的那条消息。</summary>
    public const string NoCurrentMessage = "no-current-message";
}

/// <summary>一次 V 段插入的结果。<see cref="MessageIndex"/> 为 -1 表示没找到目标消息。</summary>
public sealed record VolatileInsertion(string Text, string Placement, int MessageIndex, bool Changed)
{
    public bool Applied => Changed;
}

/// <summary>
/// 把易变段（V 段）追加进原版私信里承载 <c>ordered.Current</c> 的那一条 <b>user</b> 消息。
/// <para>
/// <b>为什么不改 system</b>：所有数字只允许出现在易变段，而稳定段（system 尾部）必须零数字——
/// 把余额写进 system 会让"从头到稳定段结束"这段前缀缓存每笔交易都失效。
/// 按甲方拍板的方案 a：V 段落进 <c>ordered.Current</c> 段内（与日期/赛程/状态同一条消息），
/// <b>不新增 message</b>。
/// </para>
/// <para>
/// <b>怎么认出那条消息</b>：原版 <c>PrivateMessagePrompts.Context</c> 生成的上下文正文以
/// <c>当前日期：…</c> 开头，而 <c>OrderContext</c> 把这段正文归进 <c>Current</c> 组，
/// 所以"以锚点开头的最后一条 user 消息"就是它（历史记录在自己之后还会再追加吗？不会——
/// <c>Compose</c> 里 Current 是在历史记录之后才 Add 的）。
/// </para>
/// <para>
/// <b>找不到锚点就放弃</b>，绝不退化成"随便挑一条 user 消息追加"：那有可能把经济信息
/// 塞进玩家自己说的话里，属于污染原版数据，比不注入严重得多。放弃时会把原因写进日志。
/// </para>
/// </summary>
public static class CurrentSegmentInsert
{
    /// <summary>
    /// 锚点：原版上下文正文的开头。原版改文案时可用 <c>ai_prompt_economy.json</c> 的
    /// <c>volatileAnchor</c> 覆盖，不必重编译。
    /// </summary>
    public const string DefaultAnchor = "当前日期：";

    /// <summary>
    /// 次要标记：原版 <c>Context</c> 在日期之后紧接着写
    /// <c>{NPC}对{玩家}的关系\n好感X，关系为…</c>。
    /// <para>
    /// 它<b>只用来在多个候选之间取舍</b>（见 <see cref="FindCurrentMessage"/>），
    /// 不是命中条件：万一原版改掉这句话，注入仍然照常发生，只是退回到"取最后一条候选"。
    /// 这样既不会因为文案变动而静默失效，也不会把经济信息塞进玩家自己打的那条消息里。
    /// </para>
    /// </summary>
    public const string RelationMarker = "关系为";

    /// <summary>把易变段追加到一段正文末尾。返回新字符串，<b>不改动入参</b>。</summary>
    public static VolatileInsertion Insert(string currentText, string volatileText, int messageIndex = -1)
    {
        if (string.IsNullOrWhiteSpace(volatileText))
            return new VolatileInsertion(currentText ?? "", VolatilePlacements.NoVolatileText, messageIndex, false);

        string text = currentText ?? "";
        if (text.Contains(volatileText, StringComparison.Ordinal))
            return new VolatileInsertion(text, VolatilePlacements.AlreadyPresent, messageIndex, false);

        return new VolatileInsertion(text + "\n" + volatileText, VolatilePlacements.Appended, messageIndex, true);
    }

    /// <summary>
    /// 对消息列表操作：找到承载 <c>ordered.Current</c> 的那条 user 消息，把易变段追加进去。
    /// <b>不新增、不删除、不重排任何消息。</b>
    /// </summary>
    public static VolatileInsertion InsertIntoMessages(IList<Dictionary<string, string>> messages, string volatileText,
        string? anchor = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (string.IsNullOrWhiteSpace(volatileText))
            return new VolatileInsertion("", VolatilePlacements.NoVolatileText, -1, false);

        int index = FindCurrentMessage(messages, anchor);
        if (index < 0)
            return new VolatileInsertion("", VolatilePlacements.NoCurrentMessage, -1, false);

        var message = messages[index];
        string current = message.TryGetValue(SystemPromptInsert.ContentKey, out var text) ? text : "";
        var insertion = Insert(current, volatileText, index);
        if (insertion.Changed) message[SystemPromptInsert.ContentKey] = insertion.Text;
        return insertion;
    }

    /// <summary>
    /// 以锚点开头的 user 消息下标；没有则 -1。
    /// <para>
    /// <b>为什么取"最后一条"</b>：<c>Compose</c> 先把历史记录 Add 进去，最后才 Add Current，
    /// 所以越靠后的越可能是本次的上下文；取第一条反而可能命中历史里的旧上下文。
    /// </para>
    /// <para>
    /// <b>为什么还要次要标记来取舍</b>：玩家完全可能在私信正文里以「当前日期：」开头打字，
    /// 而玩家那条消息正好排在 Current 之后，于是"最后一条"会挑错。
    /// 候选里只要有一条带次要标记（原版的关系行），就优先挑它——这类误判在实践中因此不会发生。
    /// 一个候选都不带时退回"最后一条"，<b>绝不因此拒绝注入</b>。
    /// </para>
    /// </summary>
    public static int FindCurrentMessage(IList<Dictionary<string, string>> messages, string? anchor = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        string effective = string.IsNullOrWhiteSpace(anchor) ? DefaultAnchor : anchor!;
        int lastCandidate = -1;
        int lastWithRelation = -1;

        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message == null) continue;
            if (!message.TryGetValue(SystemPromptInsert.RoleKey, out var role)) continue;
            if (!string.Equals(role, SystemPromptInsert.UserRole, StringComparison.Ordinal)) continue;
            if (!message.TryGetValue(SystemPromptInsert.ContentKey, out var content)) continue;
            if (!content.TrimStart().StartsWith(effective, StringComparison.Ordinal)) continue;

            lastCandidate = i;
            if (content.Contains(RelationMarker, StringComparison.Ordinal)) lastWithRelation = i;
        }

        return lastWithRelation >= 0 ? lastWithRelation : lastCandidate;
    }
}
