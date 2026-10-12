namespace NationalSpireEconomy.Economy;

/// <summary>可注入的时钟。让裁剪规则可以在单测里确定性地推进"时间"，不依赖 DateTime.Now。</summary>
public interface IEconomyClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemEconomyClock : IEconomyClock
{
    public static readonly SystemEconomyClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>测试用固定时钟，可手动推进。</summary>
public sealed class FixedEconomyClock : IEconomyClock
{
    private DateTimeOffset _now;

    public FixedEconomyClock(DateTimeOffset start) => _now = start;

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>
/// 幂等收据本。每个收据键代表"这件事已经做过一次"，重复调用直接短路。
/// <para>
/// 两条裁剪规则，缺一不可：
/// <list type="number">
/// <item>数量上限 <see cref="MaxReceipts"/>，超出后按"先丢最旧"裁掉。</item>
/// <item><b>被 Pending 交易引用的键永不裁剪</b>——否则一笔还在等玩家确认的交易
/// 会因为在队列里蹲久了而丢掉防重保护，被重复执行。</item>
/// </list>
/// 另外支持按年龄裁剪：游戏里 512 次结算可能跨越很多个赛季，
/// 光靠数量上限会让收据长期滞留，年龄上限负责把它们清出去。
/// </para>
/// </summary>
public sealed class ReceiptBook
{
    /// <summary>与 EconomyWorld.MaxReceipts 保持一致。</summary>
    public const int DefaultMaxReceipts = EconomyWorld.MaxReceipts;

    /// <summary>收据的默认保鲜期：30 天。超过这个年龄且未被引用的收据会被清掉。</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(30);

    private readonly List<string> _keys;
    private readonly Dictionary<string, DateTimeOffset> _markedAt;
    private readonly List<string> _dirty = [];

    public ReceiptBook(IEnumerable<string>? keys = null, int maxReceipts = DefaultMaxReceipts,
        DateTimeOffset? now = null)
    {
        _keys = keys?.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().ToList() ?? [];
        MaxReceipts = maxReceipts;
        DateTimeOffset stamp = now ?? DateTimeOffset.UtcNow;
        _markedAt = _keys.ToDictionary(k => k, _ => stamp);
    }

    public int MaxReceipts { get; }

    public int Count => _keys.Count;

    public IReadOnlyList<string> Keys => _keys;

    /// <summary>自上一次 <see cref="TakeDirty"/> 以来新增的键，用于增量审计与调试。</summary>
    public IReadOnlyList<string> Dirty => _dirty;

    public bool Contains(string key) => _keys.Contains(key);

    /// <summary>登记一次已完成的操作。重复登记返回 false。</summary>
    public bool Mark(string key, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("收据键不能为空。", nameof(key));
        if (_keys.Contains(key)) return false;

        _keys.Add(key);
        _markedAt[key] = now ?? DateTimeOffset.UtcNow;
        _dirty.Add(key);
        return true;
    }

    public IReadOnlyList<string> TakeDirty()
    {
        var snapshot = _dirty.ToArray();
        _dirty.Clear();
        return snapshot;
    }

    /// <summary>
    /// 撤销一个收据键——"这件事最终没做成"。
    /// <para>
    /// <b>为什么必须有它（真机实测 2026-10-11 22:0x）</b>：转账是"NPC 先动、玩家后动"，
    /// 玩家那一侧失败时会把 NPC 侧<b>回滚</b>；但登记流水时收据键已经被 <see cref="Mark"/> 过了，
    /// 回滚不会撤销它。于是同一个键重放会返回"已经做过"，而<b>账上一分未动</b>——
    /// 调用方会把一次失败误当成"已结算"。释放键之后，失败的尝试<b>不留痕</b>，可以安全重试。
    /// </para>
    /// </summary>
    /// <returns>是否真的移除了（键不存在或为空时返回 false）。</returns>
    public bool Unmark(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (!_keys.Remove(key)) return false;
        _markedAt.Remove(key);
        _dirty.Remove(key);
        return true;
    }

    /// <summary>
    /// 裁剪。<paramref name="protectedKeys"/> 是必须保留的键（例如 Pending 交易关联的键）。
    /// 返回被移除的键，便于测试与日志。
    /// </summary>
    public IReadOnlyList<string> Prune(IEnumerable<string>? protectedKeys = null, DateTimeOffset? now = null,
        TimeSpan? maxAge = null)
    {
        var removed = new List<string>();
        var protect = new HashSet<string>(protectedKeys ?? [], StringComparer.Ordinal);
        DateTimeOffset stamp = now ?? DateTimeOffset.UtcNow;
        TimeSpan age = maxAge ?? DefaultMaxAge;

        // 第一轮：按年龄清理未被引用的陈旧收据。
        for (int i = _keys.Count - 1; i >= 0; i--)
        {
            string key = _keys[i];
            if (protect.Contains(key)) continue;
            if (!_markedAt.TryGetValue(key, out var marked)) continue;
            if (stamp - marked <= age) continue;
            _keys.RemoveAt(i);
            _markedAt.Remove(key);
            removed.Add(key);
        }

        // 第二轮：数量上限。被引用的键即使超限也不丢——防重优先于裁剪。
        for (int i = 0; i < _keys.Count && _keys.Count > MaxReceipts;)
        {
            if (protect.Contains(_keys[i])) { i++; continue; }
            string key = _keys[i];
            _keys.RemoveAt(i);
            _markedAt.Remove(key);
            removed.Add(key);
        }

        return removed;
    }

    /// <summary>导出回存档（按登记顺序）。</summary>
    public List<string> ToList() => _keys.ToList();
}
