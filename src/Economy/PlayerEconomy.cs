namespace NationalSpireEconomy.Economy;

/// <summary>
/// 玩家自己的经济状态——阶段 4 第二步拍板的「影子钱包」（方案 A）。
/// <para>
/// <b>为什么玩家的钱要有一份影子副本：</b>铁律 1 写着"不写原版存档"
/// （<c>national_spire_career.json</c> 只读）。而"给玩家加钱"若写进原版的
/// <c>CareerData.Credits</c>，游戏自己会把它存进原版存档 ⇒ 与铁律冲突。
/// 于是玩家余额成为影子存档的一等数据：<b>首次从原版只读导入一次</b>，
/// 之后由我们的经济系统管辖。
/// </para>
/// <para>
/// <b>代价（甲方已确认接受）：</b>游戏内自带的金钱数字不再跟着我们的账变动。
/// 因此这个数字是"我们说话算数"的那一份，日志与将来的界面都读它。
/// </para>
/// <para>
/// <b>它不是 <see cref="NpcEconomy"/>。</b>玩家没有档位、性格、赛季统计这些概念，
/// 硬塞进 NPC 那张表会污染 V 段注入（玩家会出现在"对手方"列表里）。
/// </para>
/// </summary>
public sealed class PlayerEconomy
{
    /// <summary>
    /// 当前可用余额（美元）。<b>这是玩家侧唯一真相。</b>
    /// 原版 <c>Credits + CreditFraction</c> 只在首次导入时读过一次，之后不再同步。
    /// </summary>
    public long Balance { get; set; }

    // ── 首次导入的水位线 ─────────────────────────────────────────────

    /// <summary>
    /// 是否已经导入过原版初值。<b>为 false 时一律拒绝动钱</b>——
    /// 没导入就先加钱，等于凭空造钱，而且下一次导入会把它覆盖掉。
    /// </summary>
    public bool Imported { get; set; }

    /// <summary>导入那一刻原版 <c>CareerData.Credits</c> 的快照（内部单位，1 单位 = 10 美元）。</summary>
    public long LastImportedCredits { get; set; }

    /// <summary>导入那一刻原版 <c>CareerLifeState.CreditFraction</c> 的快照（不足一个内部单位的小数部分）。</summary>
    public decimal LastImportedFraction { get; set; }

    /// <summary>导入时折算出的美元余额，便于日后对账时不必再算一遍。</summary>
    public long LastImportedBalanceUsd { get; set; }

    /// <summary>导入发生在第几天。</summary>
    public int ImportedDay { get; set; }

    /// <summary>导入发生的 UTC 时间戳（ISO 8601）。</summary>
    public string ImportedAt { get; set; } = "";

    // ── 流水 ────────────────────────────────────────────────────────

    /// <summary>
    /// 玩家侧流水，环形，最多 <see cref="MaxLedgerEntries"/> 条（最新在前）。
    /// <para>
    /// 第一笔恒为「期初导入」，于是"钱是怎么来的"永远能从流水里读出来。
    /// 金额一律为美元，正数入账、负数出账。
    /// </para>
    /// </summary>
    public List<LedgerEntry> Ledger { get; set; } = [];

    /// <summary>最后一次变动的游戏日。</summary>
    public int LastTouchedDay { get; set; }

    public const int MaxLedgerEntries = 64;

    /// <summary>追加一条流水并按水位裁掉最旧的。金额为负表示支出。</summary>
    public void AppendLedger(LedgerEntry entry)
    {
        Ledger.Insert(0, entry);
        if (Ledger.Count > MaxLedgerEntries)
            Ledger.RemoveRange(MaxLedgerEntries, Ledger.Count - MaxLedgerEntries);
    }
}
