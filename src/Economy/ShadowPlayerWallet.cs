using System.Globalization;
using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Economy;

/// <summary>首次导入的结果。<see cref="AlreadyImported"/> 与 <see cref="Imported"/> 互斥。</summary>
public sealed record PlayerImportOutcome(
    bool Imported,
    bool AlreadyImported,
    long BalanceUsd,
    string Detail)
{
    /// <summary>本次是否真的改动了影子钱包。</summary>
    public bool Changed => Imported;
}

/// <summary>
/// 玩家的<b>影子钱包</b>：可写的 <see cref="IPlayerWallet"/>，余额存在影子存档的
/// <see cref="EconomyWorld.Player"/> 节点里，<b>绝不碰原版存档</b>。
/// <para>
/// 它与只读的 <c>NationalSpireEconomy.Game.PlayerWallet</c>（读原版
/// <c>CareerData.Credits</c>）是互补关系：那个负责"把原版的数字读出来交给预览"，
/// 这个负责"钱由我们管辖之后的加减"。首次导入把前者读到的数字搬进来，之后两不相干。
/// </para>
/// <para><b>四条纪律：</b></para>
/// <list type="number">
/// <item><b>没导入就不许动钱</b>：<see cref="Apply"/> 在 <see cref="PlayerEconomy.Imported"/>
/// 为 false 时直接拒绝。先加钱再导入 = 凭空造钱，且会被导入覆盖。</item>
/// <item><b>幂等靠收据本</b>：带键的 <see cref="Apply"/> 重复调用返回
/// <see cref="MoneyMoveStatus.AlreadyApplied"/> 且不改任何数字；不带键的
/// <see cref="TryAdd"/> 只用于"整笔转账的幂等已由 <see cref="EconomyLedger.Transfer"/>
/// 顶层收据负责"的那条路径。</item>
/// <item><b>不产生负余额</b>：余额不足在源头拒绝，而不是先扣成负数再补救。</item>
/// <item><b>余额、流水、收据一次改完</b>：不存在"钱动了但没记账"的中间态。</item>
/// </list>
/// <para>
/// <b>收据键的命名坑（§7.2 接线时必须遵守）：</b>玩家侧那条腿的收据键要<b>区别于</b>
/// NPC 侧那条腿。NPC 侧走 <see cref="EconomyLedger.Move"/> 时会把传入的键标记掉，
/// 若玩家侧复用同一个键，<see cref="Apply"/> 会把它读成"已经做过"而跳过——钱就只出去不进来了。
/// 现有 <see cref="EconomyLedger.Transfer"/> 不受影响：它在两条腿之前只查一次键。
/// </para>
/// </summary>
public sealed class ShadowPlayerWallet : IPlayerWallet
{
    /// <summary>期初导入那笔流水的类别。</summary>
    public const string ImportKind = "期初导入";

    /// <summary>不带明细时的兜底类别。</summary>
    public const string DefaultKind = "转账";

    private const string DefaultTitle = "影子玩家钱包变动";

    private readonly EconomyWorld _world;
    private readonly EconomyLedger? _ledger;

    /// <param name="world">影子存档里的世界。</param>
    /// <param name="ledger">
    /// 本次结算使用的记账器。<b>传进来是为了共用同一本收据本</b>：
    /// 一次结算里 NPC 腿与玩家腿必须看同一份收据，否则两边会各记一份。
    /// 不传时退化为直接读写 <see cref="EconomyWorld.Receipts"/>（仅适合单测与只读用途）。
    /// </param>
    public ShadowPlayerWallet(EconomyWorld world, EconomyLedger? ledger = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _ledger = ledger;
    }

    /// <summary>影子存档里的玩家节点。</summary>
    public PlayerEconomy Node => _world.Player;

    /// <summary>当前可用余额（美元）。</summary>
    public long BalanceUsd => _world.Player.Balance;

    /// <summary>是否已从原版导入过初值。</summary>
    public bool Imported => _world.Player.Imported;

    /// <summary>玩家侧流水条数（诊断/测试用）。</summary>
    public int LedgerCount => _world.Player.Ledger.Count;

    // ─────────────────────────────────────────────────────────────────
    // 只读快照
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 给只读路径用的快照钱包。<b>预览/Reserve 一律只拿这个</b>，
    /// 于是"预览不可能动钱"是类型层面保证的，而不是靠"我记得没调 TryAdd"。
    /// </summary>
    public IPlayerWallet ReadOnlySnapshot() => new InMemoryPlayerWallet(BalanceUsd);

    /// <summary>把这次结算登记过的收据刷回存档。<b>保存前必须调用一次。</b></summary>
    public void FlushReceipts() => _ledger?.FlushReceipts();

    // ─────────────────────────────────────────────────────────────────
    // 动钱
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="IPlayerWallet"/> 那条路径：不带幂等键的增减。
    /// <para>
    /// 它服务于现有的 <see cref="EconomyLedger.Transfer"/>——那里面"整笔转账"的幂等
    /// 已经在顶层用收据键查过了，所以这里不必再带键。
    /// </para>
    /// </summary>
    public bool TryAdd(long deltaUsd, out string? error)
    {
        var result = Apply(deltaUsd, new LedgerEntry { Kind = DefaultKind, Title = DefaultTitle },
            receiptKey: null);
        error = result.Applied ? null : result.Detail;
        return result.Applied;
    }

    /// <summary>
    /// <see cref="IPlayerWallet"/> 的带明细重载：把整笔转账的业务信息（天、赛季、类别、
    /// 对手方、交易单号）写进玩家自己的流水。
    /// </summary>
    public bool TryAdd(long deltaUsd, LedgerEntry template, out string? error)
    {
        var result = Apply(deltaUsd, template, receiptKey: null);
        error = result.Applied ? null : result.Detail;
        return result.Applied;
    }

    /// <summary>不带明细的带键版本，等价于给一笔 <see cref="DefaultKind"/> 流水。</summary>
    public MoneyMoveResult Apply(long deltaUsd, string? receiptKey = null) =>
        Apply(deltaUsd, new LedgerEntry { Kind = DefaultKind, Title = DefaultTitle }, receiptKey);

    /// <summary>入账（正数）。</summary>
    public MoneyMoveResult Credit(long amountUsd, LedgerEntry template, string? receiptKey = null) =>
        Apply(Math.Abs(amountUsd), template, receiptKey);

    /// <summary>出账（负数）。余额不足时返回 <see cref="MoneyMoveStatus.InsufficientFunds"/> 且不改动任何数据。</summary>
    public MoneyMoveResult Debit(long amountUsd, LedgerEntry template, string? receiptKey = null) =>
        Apply(-Math.Abs(amountUsd), template, receiptKey);

    /// <summary>
    /// 通用变动。<paramref name="deltaUsd"/> 为正表示入账、为负表示出账。
    /// <paramref name="receiptKey"/> 为空表示不做幂等保护（仅用于确实不需要防重的内部调用）。
    /// </summary>
    public MoneyMoveResult Apply(long deltaUsd, LedgerEntry template, string? receiptKey = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        var node = _world.Player;

        // 纪律 1：没导入不许动钱。
        if (!node.Imported)
        {
            return new MoneyMoveResult(MoneyMoveStatus.Rejected, deltaUsd, 0, receiptKey ?? "",
                Detail: "玩家影子钱包尚未导入原版初值，拒绝动钱（先调用 ImportOnce）。");
        }

        if (deltaUsd == 0)
            return new MoneyMoveResult(MoneyMoveStatus.Rejected, 0, node.Balance, receiptKey ?? "",
                Detail: "金额为 0。");

        // 纪律 2：幂等闸，命中即整体短路。
        string key = receiptKey ?? "";
        if (key.Length > 0 && HasReceipt(key))
        {
            return new MoneyMoveResult(MoneyMoveStatus.AlreadyApplied, deltaUsd, node.Balance, key,
                FindEntryId(key), "该幂等键已经执行过。");
        }

        // 纪律 3：余额不足在源头拒绝。溢出不静默截断（checked 会抛 OverflowException）。
        long after = checked(node.Balance + deltaUsd);
        if (after < 0)
        {
            return new MoneyMoveResult(MoneyMoveStatus.InsufficientFunds, deltaUsd, node.Balance, key,
                Detail: $"玩家影子余额 {MoneyUnits.Format(node.Balance)} 不足以支出 {MoneyUnits.Format(-deltaUsd)}。");
        }

        var record = new LedgerEntry
        {
            // 与 EconomyLedger 一致：流水 id 由幂等键派生，"用键找流水"不需要额外索引。
            Id = key.Length > 0 ? EconomyLedger.IdFor(key) : Guid.NewGuid().ToString("N"),
            Day = template.Day,
            Season = template.Season,
            Kind = template.Kind,
            Title = template.Title,
            Amount = deltaUsd,
            BalanceAfter = after,
            CounterpartyId = template.CounterpartyId,
            OfferId = template.OfferId,
            Reason = template.Reason,
            ApplicationKind = string.IsNullOrEmpty(template.ApplicationKind) ? template.Kind : template.ApplicationKind
        };

        // 纪律 4：三者在同一次调用里一起改完。
        node.Balance = after;
        node.LastTouchedDay = Math.Max(node.LastTouchedDay, template.Day);
        node.AppendLedger(record);
        if (key.Length > 0) MarkReceipt(key);

        return new MoneyMoveResult(MoneyMoveStatus.Applied, deltaUsd, after, key, record.Id);
    }

    // ─────────────────────────────────────────────────────────────────
    // 首次导入
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 首次导入：把原版 <c>CareerData.Credits</c> + <c>CareerLifeState.CreditFraction</c>
    /// 折算成美元作为影子余额初值。<b>只读原版，绝不写回。</b>
    /// <para>
    /// 水位线是 <see cref="PlayerEconomy.Imported"/>：已经导入过就什么都不做，
    /// 于是本方法可以随便反复调用（每次载入世界时调一次也没关系）。
    /// </para>
    /// <para>
    /// <b>它不要求原版余额必须与上一次相同</b>：导入之后原版自己怎么变（游戏发薪、
    /// 玩家在游戏里花钱）我们不再同步——这是决策 A 的既定代价，不是缺陷。
    /// </para>
    /// <para>
    /// 传进来的值算出来为负时<b>拒绝导入且不置位水位线</b>：宁可让钱包保持"未导入"
    /// （一动钱就被拒），也不要让一个负数变成玩家余额。
    /// </para>
    /// </summary>
    public static PlayerImportOutcome ImportOnce(EconomyWorld world, long creditsInternal, decimal creditFraction,
        int day, int season = 0, IEconomyClock? clock = null, IEconomyLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        var node = world.Player;

        if (node.Imported)
        {
            string already =
                $"已经导入过（第 {node.ImportedDay} 天，原版 {node.LastImportedCredits} + " +
                $"{Invariant(node.LastImportedFraction)} 内部单位 = {MoneyUnits.Format(node.LastImportedBalanceUsd)}），" +
                "本次未改动任何数字。";
            log?.Debug("wallet", "玩家影子钱包：" + already);
            return new PlayerImportOutcome(false, true, node.Balance, already);
        }

        long balance = (long)MoneyUnits.BalanceUsd(creditsInternal, creditFraction);
        if (balance < 0)
        {
            string bad =
                $"原版生涯余额为负（{creditsInternal} + {Invariant(creditFraction)} 内部单位 = " +
                $"{MoneyUnits.Format(balance)}），拒绝导入，水位线保持未导入。";
            log?.Error("wallet", "玩家影子钱包：" + bad);
            return new PlayerImportOutcome(false, false, node.Balance, bad);
        }

        node.Imported = true;
        node.Balance = balance;
        node.LastImportedCredits = creditsInternal;
        node.LastImportedFraction = creditFraction;
        node.LastImportedBalanceUsd = balance;
        node.ImportedDay = day;
        node.ImportedAt = (clock ?? SystemEconomyClock.Instance).UtcNow.ToString("O");
        node.LastTouchedDay = day;

        // 第一笔流水就是"钱从哪来"，于是日后对账不必猜。
        string reason = $"Credits {creditsInternal} + CreditFraction {Invariant(creditFraction)}";
        node.AppendLedger(new LedgerEntry
        {
            Id = ImportEntryId(world.WorldId),
            Day = day,
            Season = season,
            Kind = ImportKind,
            Title = "从原版生涯导入初始余额（一次性）",
            Amount = balance,
            BalanceAfter = balance,
            CounterpartyId = "",
            OfferId = "",
            Reason = reason
        });

        string detail =
            $"首次导入：原版 {MoneyUnits.Format(balance)}（{reason}）→ 影子余额 " +
            $"{MoneyUnits.Format(node.Balance)}｜第 {day} 天｜此后玩家余额由影子存档管辖。";
        log?.Info("wallet", "玩家影子钱包" + detail);
        return new PlayerImportOutcome(true, false, node.Balance, detail);
    }

    /// <summary>期初导入那笔流水的稳定 id。</summary>
    public static string ImportEntryId(string worldId) => EconomyLedger.IdFor("wallet:import:" + worldId);

    // ─────────────────────────────────────────────────────────────────
    // 内部
    // ─────────────────────────────────────────────────────────────────

    private static string Invariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 收据查重：优先问 <see cref="EconomyLedger"/>（一次结算里两侧共用同一本收据），
    /// 没有记账器时才直接看存档里的收据列表。
    /// </summary>
    private bool HasReceipt(string key) => _ledger != null
        ? _ledger.HasReceipt(key)
        : _world.Receipts.Contains(key, StringComparer.Ordinal);

    private void MarkReceipt(string key)
    {
        if (_ledger != null) { _ledger.MarkReceipt(key); return; }
        if (!_world.Receipts.Contains(key, StringComparer.Ordinal)) _world.Receipts.Add(key);
    }

    private string FindEntryId(string key)
    {
        string derived = EconomyLedger.IdFor(key);
        return _world.Player.Ledger.FirstOrDefault(e => string.Equals(e.Id, derived, StringComparison.Ordinal))?.Id
               ?? derived;
    }
}
