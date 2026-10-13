using System.Security.Cryptography;
using System.Text;
using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Economy;

public enum MoneyMoveStatus
{
    Applied,
    /// <summary>同一幂等键已经执行过，本次未产生任何变动。</summary>
    AlreadyApplied,
    /// <summary>付款方余额不足。NPC 侧应转成"软失败"，由模型用人物口吻说出来。</summary>
    InsufficientFunds,
    /// <summary>参数非法（金额、id、方向等）。</summary>
    Rejected
}

/// <summary>一次资金变动的结果。</summary>
public sealed record MoneyMoveResult(
    MoneyMoveStatus Status,
    long AmountUsd,
    long BalanceAfterUsd,
    string ReceiptKey = "",
    string LedgerEntryId = "",
    string? Detail = null)
{
    public bool Applied => Status == MoneyMoveStatus.Applied;
}

/// <summary>
/// 双向转账的结果。<paramref name="NpcSide"/> 与 <paramref name="PlayerSide"/> 各自独立，
/// 便于表达"NPC 付了但玩家那边被拒"这类部分失败的场景。
/// </summary>
public sealed record TransferResult(
    MoneyMoveResult NpcSide,
    MoneyMoveResult PlayerSide,
    string ReceiptKey)
{
    public bool BothApplied => NpcSide.Applied && PlayerSide.Applied;
}

/// <summary>玩家钱包。接线层负责把它接到 <c>CareerData.Credits</c> 与 <c>CreditFraction</c>。</summary>
public interface IPlayerWallet
{
    long BalanceUsd { get; }
    bool TryAdd(long deltaUsd, out string? error);

    /// <summary>
    /// 带流水明细的增减（阶段 4 第二步新增）。
    /// <para>
    /// 玩家侧的流水同样需要"天/类别/对手方/交易单号"，否则影子账本里只剩一句
    /// 「影子玩家钱包变动」，日后对账读不出这笔钱是什么业务。
    /// </para>
    /// <para>
    /// <b>默认实现直接忽略明细</b>，于是没有账本的实现（原版
    /// <c>CareerData</c> 钱包、单测用的内存钱包）一个字都不用改。
    /// </para>
    /// </summary>
    bool TryAdd(long deltaUsd, LedgerEntry template, out string? error) => TryAdd(deltaUsd, out error);
}

/// <summary>简单实现，供单测使用。</summary>
public sealed class InMemoryPlayerWallet : IPlayerWallet
{
    public long BalanceUsd { get; private set; }

    public InMemoryPlayerWallet(long balanceUsd = 0) => BalanceUsd = balanceUsd;

    public bool TryAdd(long deltaUsd, out string? error)
    {
        if (BalanceUsd + deltaUsd < 0)
        {
            error = "玩家余额不足。";
            return false;
        }
        BalanceUsd += deltaUsd;
        error = null;
        return true;
    }
}

/// <summary>
/// 记账器。<b>所有余额变动都必须经过这里</b>，不允许任何地方直接改 <c>NpcEconomy.Balance</c>。
/// <para>它保证三件事：</para>
/// <list type="number">
/// <item><b>幂等</b>：同一幂等键只生效一次；重复调用返回 <see cref="MoneyMoveStatus.AlreadyApplied"/>
/// 且不产生任何变动。</item>
/// <item><b>不产生负余额</b>：余额不足在源头被挡住（<see cref="MoneyMoveStatus.InsufficientFunds"/>），
/// 而不是先把余额扣成负数再去补救。</item>
/// <item><b>账实一致</b>：余额、流水、赛季统计、累计统计、收据五者在同一次调用里一起改完，
/// 不存在"钱扣了但没记账"的中间态。</item>
/// </list>
/// </summary>
public sealed class EconomyLedger
{
    /// <summary>冲正流水的类别名。它同时是"这条流水是冲正"的判定依据。</summary>
    public const string ReversalKind = "冲正";

    private readonly EconomyWorld _world;
    private readonly ReceiptBook _receipts;

    public EconomyLedger(EconomyWorld world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _receipts = new ReceiptBook(world.Receipts);
    }

    public int ReceiptCount => _receipts.Count;

    public bool HasReceipt(string key) => _receipts.Contains(key);

    /// <summary>登记一个自定义幂等键（用于把"整笔转账已完成"这类复合事实也纳入防重）。</summary>
    public bool MarkReceipt(string key) => _receipts.Mark(key);

    /// <summary>幂等键的规范形态：<c>类别:世界:标识</c>。避免各处拼法不一致导致防重失效。</summary>
    public string Key(string category, params string[] parts) =>
        string.Join(":", new[] { category, _world.WorldId }.Concat(parts));

    /// <summary>
    /// 把收据本同步回存档。可在批量操作后调用一次；
    /// 注意：<b>真正的防重依赖内存中的收据本</b>，只有保存前同步即可。
    /// </summary>
    public void FlushReceipts()
    {
        _world.Receipts = _receipts.ToList();
    }

    /// <summary>
    /// 裁剪收据。仍被 Pending 交易引用的键会被保护，不会被裁掉。
    /// </summary>
    public IReadOnlyList<string> PruneReceipts(IEconomyClock? clock = null) =>
        _receipts.Prune(ProtectedKeys(), clock?.UtcNow);

    /// <summary>所有 Pending 交易关联的收据键。这些键一旦被裁，交易就可能被重复执行。</summary>
    public IReadOnlyList<string> ProtectedKeys()
        => _world.Offers.Values
            .Where(o => TradeOfferStates.IsPending(o.State))
            .SelectMany(o => new[] { o.Id, o.Id + ":apply", "offer:" + _world.WorldId + ":" + o.Id })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    // ─────────────────────────────────────────────────────────────────
    // 变动
    // ─────────────────────────────────────────────────────────────────

    /// <summary>给 NPC 加钱。</summary>
    public MoneyMoveResult Credit(string personId, long amountUsd, LedgerEntry entry, string? receiptKey = null)
        => Move(personId, Math.Abs(amountUsd), entry, receiptKey);

    /// <summary>从 NPC 扣钱。余额不足时返回 <see cref="MoneyMoveStatus.InsufficientFunds"/> 且不改动任何数据。</summary>
    public MoneyMoveResult Debit(string personId, long amountUsd, LedgerEntry entry, string? receiptKey = null)
        => Move(personId, -Math.Abs(amountUsd), entry, receiptKey);

    /// <summary>
    /// 通用变动。<paramref name="deltaUsd"/> 为正表示入账、为负表示出账。
    /// <paramref name="receiptKey"/> 为空表示不做幂等保护（仅用于确实不需要防重的内部调用）。
    /// </summary>
    public MoneyMoveResult Move(string personId, long deltaUsd, LedgerEntry entry, string? receiptKey = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(personId))
            return new MoneyMoveResult(MoneyMoveStatus.Rejected, deltaUsd, 0, Detail: "personId 为空。");
        if (personId == EconomyTier.PlayerId)
            return new MoneyMoveResult(MoneyMoveStatus.Rejected, deltaUsd, 0, Detail: "玩家余额不走 NPC 账本。");
        if (deltaUsd == 0)
            return new MoneyMoveResult(MoneyMoveStatus.Rejected, 0, 0, Detail: "金额为 0。");

        // 幂等闸：命中即整体短路，绝不重复改动。
        string key = receiptKey ?? "";
        if (key.Length > 0 && _receipts.Contains(key))
        {
            var existing = FindEntry(personId, key);
            return new MoneyMoveResult(MoneyMoveStatus.AlreadyApplied, deltaUsd,
                _world.Npcs.TryGetValue(personId, out var known) ? known.Balance : 0,
                key, existing?.Id ?? "", "该幂等键已经执行过。");
        }

        if (!_world.Npcs.TryGetValue(personId, out var npc))
            return new MoneyMoveResult(MoneyMoveStatus.Rejected, deltaUsd, 0, key, Detail: $"账本里没有 {personId}。");

        // ★ 溢出必须显式检查（checked），与玩家侧 ShadowPlayerWallet 的写法保持一致。
        //
        // 曾经这里是裸的 `npc.Balance + deltaUsd`：接近 long.MaxValue 的入账会静默回绕成
        // 负数，紧接着下面的 `if (after < 0)` 会把它误报成 InsufficientFunds ——
        // 错误码与真实原因完全不符，排查时会被带偏。
        //
        // 经校验链的路径够不到这里（AmountBoundsValidator 上限 200,000），但
        // EconomyTick.PayPrize / Credit 是由接线层以配置驱动金额直接调用的，
        // 不在这条校验链后面 —— 所以这里的守卫是有实际意义的，不只是洁癖。
        long after;
        try
        {
            after = checked(npc.Balance + deltaUsd);
        }
        catch (OverflowException)
        {
            return new MoneyMoveResult(MoneyMoveStatus.Rejected, deltaUsd, npc.Balance, key,
                Detail: $"余额溢出：{MoneyUnits.Format(npc.Balance)} 加上 " +
                        $"{MoneyUnits.Format(deltaUsd)} 超出可表示范围，本次未改动任何数字。");
        }

        if (after < 0)
            return new MoneyMoveResult(MoneyMoveStatus.InsufficientFunds, deltaUsd, npc.Balance, key,
                Detail: $"余额 {MoneyUnits.Format(npc.Balance)} 不足以支出 {MoneyUnits.Format(-deltaUsd)}。");

        string entryId = key.Length > 0 ? IdFor(key) : Guid.NewGuid().ToString("N");
        var record = new LedgerEntry
        {
            Id = entryId,
            Day = entry.Day,
            Season = entry.Season,
            Kind = entry.Kind,
            Title = entry.Title,
            Amount = deltaUsd,
            BalanceAfter = after,
            CounterpartyId = entry.CounterpartyId,
            OfferId = entry.OfferId,
            Reason = entry.Reason,
            // 调用方可以显式指定"这条撤销的是哪个科目"；没指定时才默认等于自己的类别。
            // 少了这个回退，冲正流水会把自己的 Kind("冲正") 当成科目，统计就彻底走错。
            ApplicationKind = string.IsNullOrEmpty(entry.ApplicationKind) ? entry.Kind : entry.ApplicationKind
        };

        npc.Balance = after;
        npc.LastTouchedDay = Math.Max(npc.LastTouchedDay, entry.Day);
        npc.Ledger.Insert(0, record);
        if (npc.Ledger.Count > NpcEconomy.MaxLedgerEntries)
            npc.Ledger.RemoveRange(NpcEconomy.MaxLedgerEntries, npc.Ledger.Count - NpcEconomy.MaxLedgerEntries);

        // 冲正流水走 Revert、普通流水走 Accumulate：两条路的净额方向相反，
        // 走错就会出现"冲正一笔收入反而让收入翻倍"这类问题。
        if (record.Kind == ReversalKind) Revert(npc, record);
        else Accumulate(npc, record);
        if (key.Length > 0) _receipts.Mark(key);
        return new MoneyMoveResult(MoneyMoveStatus.Applied, deltaUsd, after, key, entryId);
    }

    /// <summary>
    /// 玩家 ↔ NPC 之间的转账。
    /// <para>
    /// 顺序刻意写成"NPC 先动、玩家后动"：NPC 的钱在我们手里，失败了可以直接回滚；
    /// 玩家的钱在游戏本体里，一旦动过就不好撤。NPC 失败则整笔作废，玩家侧不会被执行。
    /// </para>
    /// </summary>
    public TransferResult Transfer(long amountUsd, string npcId, bool fromPlayer, IPlayerWallet wallet,
        LedgerEntry entry, string? receiptKey = null)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentNullException.ThrowIfNull(entry);

        long amount = Math.Abs(amountUsd);
        if (amount == 0)
        {
            var rejected = new MoneyMoveResult(MoneyMoveStatus.Rejected, 0, 0, Detail: "金额为 0。");
            return new TransferResult(rejected, rejected, receiptKey ?? "");
        }

        string key = receiptKey ?? "";
        if (key.Length > 0 && _receipts.Contains(key))
        {
            long npcBalance = _world.Npcs.TryGetValue(npcId, out var known) ? known.Balance : 0;
            var already = new MoneyMoveResult(MoneyMoveStatus.AlreadyApplied, amount, npcBalance, key,
                FindEntry(npcId, key)?.Id ?? "", "该幂等键已经执行过。");
            var alreadyPlayer = new MoneyMoveResult(MoneyMoveStatus.AlreadyApplied, amount, wallet.BalanceUsd, key);
            return new TransferResult(already, alreadyPlayer, key);
        }

        // 玩家余额不足时直接拒绝，不动 NPC 侧。
        if (fromPlayer && wallet.BalanceUsd < amount)
        {
            var playerShort = new MoneyMoveResult(MoneyMoveStatus.InsufficientFunds, amount, wallet.BalanceUsd, key,
                Detail: "玩家余额不足。");
            var npcUntouched = new MoneyMoveResult(MoneyMoveStatus.Rejected, amount,
                _world.Npcs.TryGetValue(npcId, out var n) ? n.Balance : 0, key, Detail: "玩家侧未通过。");
            return new TransferResult(npcUntouched, playerShort, key);
        }

        long delta = fromPlayer ? amount : -amount;
        var npcSide = Move(npcId, delta, entry, key);
        if (npcSide.Status != MoneyMoveStatus.Applied)
        {
            var playerSkipped = new MoneyMoveResult(npcSide.Status == MoneyMoveStatus.AlreadyApplied
                ? MoneyMoveStatus.AlreadyApplied
                : MoneyMoveStatus.Rejected, amount, wallet.BalanceUsd, key, Detail: "NPC 侧未通过，玩家侧未执行。");
            return new TransferResult(npcSide, playerSkipped, key);
        }

        long playerDelta = fromPlayer ? -amount : amount;
        if (!wallet.TryAdd(playerDelta, entry, out string? error))
        {
            // 玩家侧失败 → 回滚 NPC 侧，保证不会凭空多出或少掉钱。
            RollbackEntry(npcId, npcSide.LedgerEntryId);

            // ★ 真机实测隐患（2026-10-11 22:0x，探针实测）：
            //   Move 在登记 NPC 流水时已经把【整笔】的键记进了收据本，而回滚不会撤销它。
            //   于是"收了又退回去"这个状态会被幂等闸吞掉——同一个键重放返回 AlreadyApplied，
            //   账上一分未动，调用方却会以为"这笔已经结算过"（状态与幂等不一致）。
            //   这里把整笔键与回滚键一起释放，让失败的尝试【不留痕】，可以安全重试；
            //   RollbackEntry 已经删掉了那笔流水，所以重试不会造成重复入账。
            _receipts.Unmark(key);
            _receipts.Unmark(Key("rollback", npcId, npcSide.LedgerEntryId));

            var playerFailed = new MoneyMoveResult(MoneyMoveStatus.Rejected, amount, wallet.BalanceUsd, key,
                Detail: error ?? "玩家钱包拒绝本次变动，NPC 侧已回滚。");
            return new TransferResult(
                new MoneyMoveResult(MoneyMoveStatus.Rejected, amount, _world.Npcs[npcId].Balance, key,
                    // 把钱包给出的真实原因带上：执行器优先取 NPC 侧的 Detail 作为对外说明，
                    // 若只写"玩家侧失败"，日志与玩家就永远看不到"其实是因为钱包没导入"。
                    Detail: "玩家侧失败，已回滚" + (string.IsNullOrWhiteSpace(error) ? "。" : "：" + error)),
                playerFailed,
                key);
        }

        var playerSide = new MoneyMoveResult(MoneyMoveStatus.Applied, amount, wallet.BalanceUsd, key);
        return new TransferResult(npcSide, playerSide, key);
    }

    /// <summary>
    /// 按具体流水 id 冲正。<b>用流水而非"最后一笔"来定位</b>，避免并发或后续操作导致冲错账。
    /// 冲正本身也走幂等键，重复调用不会重复退款。
    /// </summary>
    public MoneyMoveResult Reverse(string personId, string ledgerEntryId, string reason, int day, int season)
    {
        if (!_world.Npcs.TryGetValue(personId, out var npc))
            return new MoneyMoveResult(MoneyMoveStatus.Rejected, 0, 0, Detail: $"账本里没有 {personId}。");

        var original = npc.Ledger.FirstOrDefault(e => e.Id == ledgerEntryId);
        if (original == null)
            return new MoneyMoveResult(MoneyMoveStatus.Rejected, 0, npc.Balance, Detail: "找不到要冲正的流水。");

        string key = Key("reverse", personId, ledgerEntryId);
        if (_receipts.Contains(key))
            return new MoneyMoveResult(MoneyMoveStatus.AlreadyApplied, original.Amount, npc.Balance, key,
                Detail: "该流水已经冲正过。");

        return Move(personId, -original.Amount, new LedgerEntry
        {
            Day = day,
            Season = season,
            Kind = ReversalKind,
            Title = "冲正：" + original.Title,
            CounterpartyId = original.CounterpartyId,
            OfferId = original.OfferId,
            Reason = reason,
            // 关键：告诉记账器"我撤销的是 original.ApplicationKind 那个科目"，
            // 否则统计会把冲正记成一笔新支出（这是实测出来的缺陷）。
            ApplicationKind = original.ApplicationKind.Length > 0 ? original.ApplicationKind : original.Kind
        }, key);
    }

    // ─────────────────────────────────────────────────────────────────
    // 赛季统计
    // ─────────────────────────────────────────────────────────────────

    /// <summary>跨赛季滚动：把本赛季累计清零，准备接收新赛季数据。</summary>
    public static void RollSeason(NpcEconomy npc, int newSeason)
    {
        ArgumentNullException.ThrowIfNull(npc);
        if (npc.Season == newSeason) return;
        npc.Season = newSeason;
        npc.SeasonStats.Reset();
    }

    /// <summary>
    /// 累计统计。冲正流水的金额符号与原流水相反，所以<b>不能按符号归桶</b>：
    /// 冲正一笔收入若按符号处理，会变成一笔支出，统计彻底失真。
    /// 这里统一按"原流水的方向 + 是新增还是冲正"决定净额。
    /// </summary>
    private static void Accumulate(NpcEconomy npc, LedgerEntry entry)
    {
        string kind = string.IsNullOrEmpty(entry.ApplicationKind) ? entry.Kind : entry.ApplicationKind;
        ApplyStats(npc, kind, entry.Amount, direction: +1);
        npc.Lifetime.TradeCount++;
    }

    /// <summary>
    /// 统计增减的唯一出口。<paramref name="direction"/> 为 <c>+1</c> 表示新增、<c>-1</c> 表示冲正。
    /// <para>
    /// 净额规则（这套规则是<b>实测</b>出来的，不是推导出来的）：
    /// <list type="bullet">
    /// <item>原流水是收入：新增给它加收入桶，冲正从收入桶里减。</item>
    /// <item>原流水是支出：新增给它加支出桶，冲正从支出桶里减。</item>
    /// </list>
    /// 冲正绝不能"反向记一笔"，否则支出会被冲成收入。
    /// </para>
    /// </summary>
    private static void ApplyStats(NpcEconomy npc, string kind, long amount, int direction)
    {
        long magnitude = Math.Abs(amount);
        if (magnitude == 0 || direction == 0) return;

        bool wasIncome = amount > 0;
        var season = npc.SeasonStats;

        if (wasIncome)
        {
            long delta = magnitude * direction;
            season.SeasonIncome = Math.Max(0, season.SeasonIncome + delta);
            npc.Lifetime.Income = Math.Max(0, npc.Lifetime.Income + delta);
        }
        else
        {
            long delta = magnitude * direction;
            season.SeasonSpend = Math.Max(0, season.SeasonSpend + delta);
            npc.Lifetime.Spend = Math.Max(0, npc.Lifetime.Spend + delta);
        }

        // 科目细分同样按原流水方向定位科目，再按 direction 决定加减。
        long signed = magnitude * direction;
        switch (kind)
        {
            case "工资":
            case "薪资":
                if (wasIncome) season.SalaryIncome = Math.Max(0, season.SalaryIncome + signed);
                break;
            case "胜场奖金":
            case "赛事奖金":
            case "奖金":
                if (wasIncome) season.WinBonusIncome = Math.Max(0, season.WinBonusIncome + signed);
                break;
            case "赞助":
                if (wasIncome) season.SponsorIncome = Math.Max(0, season.SponsorIncome + signed);
                break;
            case "转会费":
                if (wasIncome) season.TransferIncome = Math.Max(0, season.TransferIncome + signed);
                else season.TransferSpend = Math.Max(0, season.TransferSpend + signed);
                break;
            case "名次奖金":
                if (wasIncome) season.PrizeIncome = Math.Max(0, season.PrizeIncome + signed);
                break;
            case "打赏":
            case "礼物":
                if (wasIncome) season.GiftIncome = Math.Max(0, season.GiftIncome + signed);
                else season.GiftSpend = Math.Max(0, season.GiftSpend + signed);
                break;
        }
    }

    /// <summary>内部回滚：把某一笔流水原地撤销。走与 <see cref="Reverse"/> 相同的幂等路径。</summary>
    private MoneyMoveResult RollbackEntry(string personId, string ledgerEntryId)
    {
        if (!_world.Npcs.TryGetValue(personId, out var npc))
            return new MoneyMoveResult(MoneyMoveStatus.Rejected, 0, 0, Detail: $"账本里没有 {personId}。");

        var original = npc.Ledger.FirstOrDefault(e => e.Id == ledgerEntryId);
        if (original == null)
            return new MoneyMoveResult(MoneyMoveStatus.Rejected, 0, npc.Balance, Detail: "找不到要回滚的流水。");

        string key = Key("rollback", personId, ledgerEntryId);
        if (_receipts.Contains(key))
            return new MoneyMoveResult(MoneyMoveStatus.AlreadyApplied, original.Amount, npc.Balance, key);

        if (!npc.Ledger.Remove(original))
            return new MoneyMoveResult(MoneyMoveStatus.Rejected, 0, npc.Balance, Detail: "回滚时未能移除原流水。");

        long restored = npc.Balance - original.Amount;
        if (restored < 0)
        {
            // 不该发生：回滚就是把刚加的减去。真出现说明账本被外力改过。
            npc.Ledger.Insert(0, original);
            return new MoneyMoveResult(MoneyMoveStatus.Rejected, 0, npc.Balance, key,
                Detail: "回滚会导致负余额，已放弃。");
        }

        npc.Balance = restored;
        Revert(npc, original);
        _receipts.Mark(key);
        return new MoneyMoveResult(MoneyMoveStatus.Applied, -original.Amount, restored, key, original.Id);
    }

    /// <summary>
    /// 撤销一条流水对赛季与终生统计的影响。
    /// <para>
    /// 注意必须传<b>绝对值</b>：冲正流水的金额本身带着相反的符号（这是账务记录的需要），
    /// 但"撤销的方向"已经由 <c>direction: -1</c> 表达。若把负号一起传进去，
    /// 一笔收入的冲正会被重新判定成支出，统计会再次走错桶。
    /// </para>
    /// </summary>
    private static void Revert(NpcEconomy npc, LedgerEntry entry)
    {
        string kind = string.IsNullOrEmpty(entry.ApplicationKind) ? entry.Kind : entry.ApplicationKind;
        ApplyStats(npc, kind, Math.Abs(entry.Amount), direction: -1);
    }

    private LedgerEntry? FindEntry(string personId, string receiptKey)
    {
        if (!_world.Npcs.TryGetValue(personId, out var npc)) return null;
        string id = IdFor(receiptKey);
        return npc.Ledger.FirstOrDefault(e => e.Id == id);
    }

    /// <summary>流水 id 由幂等键派生，于是"用键找流水"不需要额外索引。</summary>
    public static string IdFor(string receiptKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(receiptKey));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }
}
