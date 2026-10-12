namespace NationalSpireEconomy.Game;

/// <summary>
/// 一次"正在组装的私信"的身份快照：世界 id、人物 id、会话 id、当天日期与赛季，
/// 以及原始 <c>CareerData</c> 引用（供 <c>BuildInput</c> 读好感等只读信息）。
/// </summary>
/// <param name="WorldId">影子存档的世界 id。</param>
/// <param name="PersonId">对话对象（NPC）的 id。</param>
/// <param name="ConversationId">上下文里的人物键（诊断用，与账本主键未必相同）。</param>
/// <param name="Day">组装时的生涯天数。</param>
/// <param name="Season">组装时的赛季。</param>
/// <param name="CapturedUtc">登记时刻（UTC）。</param>
/// <param name="ExpiresUtc">过期时刻（UTC）。</param>
/// <param name="Source">登记来源，便于排查"到底是谁写进去的"。</param>
/// <param name="Data">
/// 组装私信时那份 <c>CareerData</c> 的引用（<b>权威来源</b>）。
/// <para>
/// <b>为什么必须带着它</b>：方案 A 的读取端只有 <c>AiOptions</c>；而原版
/// <c>CareerEngine</c> 里<b>没有</b>任何"当前生涯"的静态入口
/// （已用元数据逐条扫描核对：只有 <c>Person(CareerData, string)</c>），
/// 所以这份引用是唯一能把"给谁注入、好感多少"补全的东西。
/// 拿不到它时我们<b>拒绝注入</b>，绝不拿空数据硬插。
/// </para>
/// </param>
public sealed record PrivateIdentity(
    string WorldId,
    string PersonId,
    string ConversationId,
    int Day,
    int Season,
    DateTime CapturedUtc,
    DateTime ExpiresUtc,
    string Source,
    NationalSpire.CareerData? Data);

/// <summary>
/// <b>方案 A 的身份槽</b>。
/// <para>
/// <b>为什么需要它</b>：方案 A 的主挂载点是
/// <c>AiService.RequestPrivateAsync(AiOptions, List, …)</c> —— 它<b>只有 <c>AiOptions</c>，
/// 拿不到 <c>CareerData</c> 与人物 id</b>。而"给谁注入"这件事必须准确，绝不能猜。
/// 于是我们用一个独立的后缀挂在 <c>PrivateMessagePrompts.Context(CareerData, PrivateConversation,
/// PrivateTurn, …)</c> 上：它在私信组装流程里<b>一定早于发送</b>被调用，把
/// (世界 id, 人物 id, 天数, 赛季) 记进这个槽；<c>RequestPrivateAsync</c> 的前缀再把槽读走。
/// </para>
/// <para>
/// <b>只接受账本里的人</b>：读取时会核对"这个 id 在不在影子账本里"，不在就拒绝注入。
/// 所以即便槽里被写进了真人或陌生人（理论上不会发生），也绝不会注入。
/// </para>
/// <para>
/// <b>过期即失效</b>：读走之后立即清空；超过 <see cref="DefaultTtl"/> 未使用也自动失效。
/// 私信从"组装上下文"到"真正发出"是同一个动作，正常远小于这个窗口；
/// 过期只可能出现在"组装了却没发出去"的情况，此时拒绝注入是保守且正确的选择。
/// </para>
/// </summary>
public static class PrivateIdentitySlot
{
    /// <summary>默认有效期。覆盖"组装完成 → 点发送"的整个交互过程。</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private static readonly object Gate = new();
    private static PrivateIdentity? _current;
    private static readonly List<PrivateIdentity> Candidates = [];

    /// <summary>登记成功次数。</summary>
    public static int Sets { get; private set; }

    /// <summary>兜底轨迹登记的次数（<c>CareerDayPatch</c> 每次推进日期登记一次）。</summary>
    public static int Tracked { get; private set; }

    /// <summary>被读取（未过期）次数。</summary>
    public static int Takes { get; private set; }

    /// <summary>因为过期而被丢弃的次数。</summary>
    public static int Expired { get; private set; }

    /// <summary>兜底轨迹的当前内容摘要（诊断用）。</summary>
    public static string TrackedSummary
    {
        get
        {
            lock (Gate)
            {
                return TrackedSummaryCache;
            }
        }
    }

    /// <summary>最近一次登记的摘要（诊断用，不随读取清空）。</summary>
    public static string LastCapture { get; private set; } = "(尚未捕获)";

    /// <summary>登记一次身份。人物 id 为空会被忽略（不覆盖已有值）。</summary>
    public static void Set(string? worldId, string? personId, string? conversationId, int day, int season,
        string source, NationalSpire.CareerData? data = null, TimeSpan? ttl = null)
    {
        if (string.IsNullOrWhiteSpace(personId)) return;

        var now = DateTime.UtcNow;
        var lifetime = ttl ?? DefaultTtl;
        var identity = new PrivateIdentity(
            worldId ?? "",
            personId!,
            conversationId ?? "",
            day,
            season,
            now,
            now + lifetime,
            source,
            data);

        lock (Gate)
        {
            _current = identity;
            Sets++;
            LastCapture = $"{now:HH:mm:ss.fff}｜世界 <{identity.WorldId}>｜人物 <{identity.PersonId}>" +
                          $"｜会话 <{identity.ConversationId}>｜第 {day} 天 / 第 {season} 赛季" +
                          $"｜来源 {source}｜有效期 {lifetime.TotalSeconds:0} 秒" +
                          $"｜生涯数据 {(data == null ? "缺失" : "在位")}";
        }
    }

    /// <summary>读走当前身份（读走即清空）。没有、或已过期都返回 null。</summary>
    public static PrivateIdentity? Take()
    {
        lock (Gate)
        {
            var identity = _current;
            _current = null;
            if (identity == null) return null;

            if (identity.ExpiresUtc <= DateTime.UtcNow)
            {
                Expired++;
                return null;
            }

            Takes++;
            return identity;
        }
    }

    /// <summary>
    /// <b>只看不取</b>当前身份（<b>不清空</b>）。没有、或已过期都返回 null。
    /// <para>
    /// 给"旁路"用途：例如阶段 4 的交易预览要在模型流水里认出"这是哪个世界/哪个人"，
    /// 但它<b>不能</b>把身份读走——真正的私信注入还要用这一份（读走就没了）。
    /// </para>
    /// </summary>
    public static PrivateIdentity? Peek()
    {
        lock (Gate)
        {
            var identity = _current;
            if (identity == null) return null;
            if (identity.ExpiresUtc <= DateTime.UtcNow) return null;
            return identity;
        }
    }

    /// <summary>
    /// 登记<b>兜底轨迹</b>：这个世界的全部候选人物（<c>ConversationId</c> 是人物 id）。
    /// <para>
    /// 由 <see cref="CareerDayPatch"/> 在每次推进日期时调用。它存在的理由是
    /// <b>方案 A 不能出现"一次都不注入"的静默失败</b>：万一原版某条路径没经过
    /// <c>Compose</c>（于是 <see cref="Set"/> 不会被调用），我们还能靠这份轨迹 + 原版
    /// 私信上下文里的人物键把身份对上。真人与陌生人不在其中，所以不会注入错人。
    /// </para>
    /// </summary>
    public static void SetForWorld(string? worldId, IEnumerable<(string Id, string Handle)> people, int day,
        int season, string source, NationalSpire.CareerData? data = null, TimeSpan? ttl = null)
    {
        if (people == null) return;

        var now = DateTime.UtcNow;
        var lifetime = ttl ?? FallbackTtl;
        var list = new List<PrivateIdentity>();
        string world = worldId ?? "";

        foreach (var (id, handle) in people)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            list.Add(new PrivateIdentity(world, id, id, day, season, now, now + lifetime, source, data));
            if (!string.IsNullOrWhiteSpace(handle) && handle != id)
            {
                // 别名（Handle）也登记一份，库里存的键可能是它。
                list.Add(new PrivateIdentity(world, id, handle, day, season, now, now + lifetime,
                    source + "·别名", data));
            }
        }

        lock (Gate)
        {
            Candidates.Clear();
            Candidates.AddRange(list);
            Tracked++;
            TrackedSummaryCache = $"{list.Count} 条｜世界 <{world}>｜第 {day} 天" +
                                  (list.Count > 0 ? $"｜例：{string.Join("、", list.Take(3).Select(c => c.ConversationId))}" : "");
        }
    }

    /// <summary>兜底轨迹的有效期（比精确身份长得多：它描述的是"这个世界有谁"）。</summary>
    public static readonly TimeSpan FallbackTtl = TimeSpan.FromMinutes(30);

    private static string TrackedSummaryCache = "(尚未登记)";

    /// <summary>
    /// 按人物键在当前轨迹里找身份。<b>找不到返回 null；歧义（多个不同的人匹配同一个键）也返回 null。</b>
    /// </summary>
    /// <param name="key">原版私信上下文里的人物键（<c>private/&lt;key&gt;/…</c>）。</param>
    /// <param name="worldId">当前世界 id：轨迹里世界不一致的候选点直接作废。</param>
    public static PrivateIdentity? TryMatchTracked(string? key, string? worldId)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var now = DateTime.UtcNow;
        lock (Gate)
        {
            string? matchedId = null;
            PrivateIdentity? matched = null;

            foreach (var candidate in Candidates)
            {
                if (candidate.ExpiresUtc <= now) continue;
                if (worldId != null && worldId.Length > 0 && candidate.WorldId.Length > 0 &&
                    !string.Equals(candidate.WorldId, worldId, StringComparison.Ordinal))
                    continue;
                if (!string.Equals(candidate.ConversationId, key, StringComparison.Ordinal)) continue;

                if (matchedId != null && !string.Equals(matchedId, candidate.PersonId, StringComparison.Ordinal))
                    return null; // 歧义：宁可拒绝注入，也绝不猜。
                matchedId = candidate.PersonId;
                matched = candidate;
            }

            return matched;
        }
    }

    /// <summary>清空（测试与关闭模组时用）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            _current = null;
            Candidates.Clear();
            TrackedSummaryCache = "(已清空)";
        }
    }

    /// <summary>槽里当前是否有未过期的身份（不读走）。</summary>
    public static bool HasFresh
    {
        get
        {
            lock (Gate)
            {
                return _current != null && _current.ExpiresUtc > DateTime.UtcNow;
            }
        }
    }
}
