namespace NationalSpireEconomy.Economy;

/// <summary>交易意向的种类。白名单在 economy_config.json 的 trade.allowedKinds。</summary>
public static class TradeKinds
{
    public const string Sponsor = "赞助";
    public const string Tip = "打赏";
    public const string Hire = "雇佣";
    public const string Bonus = "奖金";
    public const string Transfer = "转账";
    public const string Loan = "借款";
}

/// <summary>
/// 一笔待确认交易的状态机。<see cref="State"/> 只能沿 Pending → 终态 单向迁移，
/// 这是幂等防重的第二道闸（第一道是 <c>receipts</c>）。
/// </summary>
public static class TradeOfferStates
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Declined = "declined";
    public const string Invalid = "invalid";
    public const string Expired = "expired";

    public static bool IsTerminal(string state) =>
        state is Confirmed or Declined or Invalid or Expired;

    public static bool IsPending(string state) => state == Pending;
}

/// <summary>谁掏钱。决定校验链走哪一支（余额方向、性格的施舍敏感度是否生效）。</summary>
public static class TradeDirections
{
    /// <summary>玩家出钱给 NPC。</summary>
    public const string PlayerToNpc = "player_to_npc";
    /// <summary>NPC 出钱给玩家。</summary>
    public const string NpcToPlayer = "npc_to_player";
    /// <summary>NPC 与 NPC 之间（转会费、工资、奖金、互相打赏）。</summary>
    public const string NpcToNpc = "npc_to_npc";
}

/// <summary>一笔流水。金额一律为美元，正数入账、负数出账。</summary>
public sealed class LedgerEntry
{
    /// <summary>稳定 id，用于去重与审计对账。</summary>
    public string Id { get; set; } = "";
    public int Day { get; set; }
    public int Season { get; set; }

    /// <summary>业务类别，例如 工资 / 转会费 / 赞助 / 打赏 / 奖金。</summary>
    public string Kind { get; set; } = "";

    /// <summary>
    /// 这条流水撤销的是哪一类业务。普通流水等于 <see cref="Kind"/>；
    /// 冲正流水的 <see cref="Kind"/> 是"冲正"，但本字段保留<b>被冲正那条</b>的类别，
    /// 这样统计才能正确地从原科目里扣掉，而不是把它错记成一笔新支出。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ApplicationKind { get; set; } = "";

    /// <summary>标题，给玩家看的一句话，例如「支付「雇佣玩家担任教练」」。</summary>
    public string Title { get; set; } = "";

    public long Amount { get; set; }

    /// <summary>记账后的余额快照，便于回溯"这笔之后剩多少"。</summary>
    public long BalanceAfter { get; set; }

    /// <summary>对手方 PersonId；玩家用 "player"，无对手方时为空。</summary>
    public string CounterpartyId { get; set; } = "";

    /// <summary>关联的交易单 id（若有）。</summary>
    public string OfferId { get; set; } = "";

    /// <summary>自由说明，会进入 Prompt 的「最近一笔」。</summary>
    public string Reason { get; set; } = "";
}

/// <summary>本赛季累计收支。跨赛季时清零，旧值进入 lifetime。</summary>
public sealed class SeasonStats
{
    public long SeasonIncome { get; set; }
    public long SeasonSpend { get; set; }
    public long SalaryIncome { get; set; }
    public long WinBonusIncome { get; set; }
    public long SponsorIncome { get; set; }
    public long TransferIncome { get; set; }
    public long TransferSpend { get; set; }
    public long PrizeIncome { get; set; }
    public long GiftIncome { get; set; }
    public long GiftSpend { get; set; }

    public long Net => SeasonIncome - SeasonSpend;

    public void Reset()
    {
        SeasonIncome = SeasonSpend = 0;
        SalaryIncome = WinBonusIncome = SponsorIncome = 0;
        TransferIncome = TransferSpend = 0;
        PrizeIncome = GiftIncome = GiftSpend = 0;
    }
}

/// <summary>累计统计，用于 "lifetime" 类展示与意向演进。</summary>
public sealed class LifetimeStats
{
    public long Income { get; set; }
    public long Spend { get; set; }
    public int TradeCount { get; set; }
    public int RefusedCount { get; set; }

    /// <summary>
    /// 意向漂移分数，负值趋于谨慎/拮据，正值趋于大方。
    /// 由性格起点叠加历史行为累积，注入层据此选 intentPhrase 文案。
    /// </summary>
    public double DriftScore { get; set; }

    /// <summary>最近一次被"小钱试探"的日子。用于性格记仇类的文案。</summary>
    public int LastRefusedCharityDay { get; set; }
}

/// <summary>意向演进的最近触发记录。环形，最多 8 条。</summary>
public sealed class IntentTrigger
{
    public int Day { get; set; }
    public string Kind { get; set; } = "";
    public string Detail { get; set; } = "";
    public double Delta { get; set; }
}

/// <summary>
/// 一个 NPC 的经济状态。<b>这是我们自己的数据，绝不写回原版存档。</b>
/// 金额一律为美元整数；与原版内部单位的换算只在 <c>MoneyUnits</c> 里发生。
/// </summary>
public sealed class NpcEconomy
{
    public string PersonId { get; set; } = "";

    /// <summary>当前可用余额（美元）。唯一真相。</summary>
    public long Balance { get; set; }

    /// <summary>播种时定下的财富档位 id，对应 economy_config.json 的 seeding.tiers[].id。</summary>
    public string TierId { get; set; } = "";

    /// <summary>所属俱乐部 id 的快照，便于按俱乐部聚合与校验。</summary>
    public string ClubId { get; set; } = "";

    // ── 性格快照：用于侦测原版性格被 AI 改写，从而重算经济画像 ──
    /// <summary>NationalSpire 的 PersonalityProfile.Kind 快照。</summary>
    public string PersonalityKind { get; set; } = "";
    /// <summary>PersonalityLibrary.Version 快照。</summary>
    public int PersonalityVersion { get; set; }

    // ── 赛季收支 ──
    public int Season { get; set; }
    public SeasonStats SeasonStats { get; set; } = new();
    public LifetimeStats Lifetime { get; set; } = new();

    // ── 幂等水位线：这些字段本身就是防重的第一道闸 ──
    /// <summary>每日结算水位线，<c>day &lt;= LastTickedDay</c> 一律跳过。</summary>
    public int LastTickedDay { get; set; }
    /// <summary>已发放工资的赛季，防止重复发薪。</summary>
    public int LastSalaryPaidSeason { get; set; }
    /// <summary>已发放赞助款的赛季。</summary>
    public int LastSponsorPaidSeason { get; set; }

    /// <summary>最近流水，环形，最多 <see cref="MaxLedgerEntries"/> 条（最新在前）。</summary>
    public List<LedgerEntry> Ledger { get; set; } = [];

    /// <summary>意向演进触发记录，环形，最多 <see cref="MaxIntentTriggers"/> 条。</summary>
    public List<IntentTrigger> IntentTriggers { get; set; } = [];

    /// <summary>软失败留下的"未竟之言"，下一轮注入后清空。</summary>
    public string PendingVoice { get; set; } = "";

    /// <summary>是否已从原版 People 列表移除。余额保留，人回来了还能接上。</summary>
    public bool Retired { get; set; }

    /// <summary>最后一次变动的游戏日，用于陈旧数据清理与展示。</summary>
    public int LastTouchedDay { get; set; }

    public const int MaxLedgerEntries = 64;
    public const int MaxIntentTriggers = 8;

    /// <summary>追加一条流水并按水位裁掉最旧的。金额为负表示支出。</summary>
    public void AppendLedger(LedgerEntry entry)
    {
        Ledger.Insert(0, entry);
        if (Ledger.Count > MaxLedgerEntries)
            Ledger.RemoveRange(MaxLedgerEntries, Ledger.Count - MaxLedgerEntries);
    }

    public void AppendTrigger(IntentTrigger trigger)
    {
        IntentTriggers.Insert(0, trigger);
        if (IntentTriggers.Count > MaxIntentTriggers)
            IntentTriggers.RemoveRange(MaxIntentTriggers, IntentTriggers.Count - MaxIntentTriggers);
    }
}

/// <summary>交易单里的物品行。本期恒为空数组，为"购买物品"预留。</summary>
public sealed class TradeItemLine
{
    public string ItemId { get; set; } = "";
    public int Quantity { get; set; } = 1;
}

/// <summary>
/// 一笔交易单。由模型输出 <c>[Trade: ...]</c> 或 UI 发起，<b>必须玩家确认后</b>才由程序执行。
/// 模型只决定 <see cref="Amount"/> 的提议值，任何余额变动都不由模型决定。
/// </summary>
public sealed class TradeOffer
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Direction { get; set; } = TradeDirections.PlayerToNpc;

    /// <summary>发起方 PersonId，玩家用 "player"。</summary>
    public string InitiatorId { get; set; } = "";
    /// <summary>对手方 PersonId。</summary>
    public string CounterpartyId { get; set; } = "";

    /// <summary>提议金额（美元）。</summary>
    public long Amount { get; set; }

    /// <summary>物品行。本期恒为空；"购买物品"启用后与此字段并列，不是二选一。</summary>
    public List<TradeItemLine> Items { get; set; } = [];

    /// <summary>
    /// 是否带物品行。它与 <see cref="Amount"/> 是<b>并列</b>关系：
    /// 将来"用 3 万加一件道具换某人加盟"这类复合交易不需要重构数据结构，只是本属性变 true。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasItems => Items.Count > 0;

    public string Reason { get; set; } = "";
    public string State { get; set; } = TradeOfferStates.Pending;

    public int CreatedDay { get; set; }
    public int Season { get; set; }
    public int ExpiresDay { get; set; }

    /// <summary>失败原因，会回灌到后续 Prompt（对齐原版 <c>PrivateOffer.Detail</c> 的用法）。</summary>
    public string Report { get; set; } = "";

    /// <summary>确认执行后的流水 id，用于对账。</summary>
    public string LedgerEntryId { get; set; } = "";

    /// <summary>发起时计算的校验码。确认时要<b>重新</b>校验，这里仅用于诊断差异。</summary>
    public string ValidationStamp { get; set; } = "";

    public bool IsExpired(int day) => ExpiresDay > 0 && day > ExpiresDay;
}

/// <summary>
/// <b>"最近一笔交易到底怎么样了"这一行真相</b>（阶段 4 第三步 §7.1/§7.2）。
/// <para>
/// 它存在的唯一理由是：<b>模型嘴里的交易与账本上的交易必须有一处对得上</b>。
/// 真机三轮里模型都说"钱转过去了"而账上一分没动，玩家也毫不知情——
/// 只写日志等于默认让模型继续编，因为<b>模型只看得见 Prompt（V 段），看不见日志</b>。
/// </para>
/// <para>
/// 它同时服务两边：玩家读它是回执，模型读它是"不许编"的依据。
/// 因为带金额，它只能渲染进 V 段（易变段），绝不能进 S 段。
/// </para>
/// <para>
/// <b>为什么落盘而不是只放内存</b>：重启即丢就等于没写——玩家下次进来只看到模型
/// 上一轮说的"我转给你了"，而我们的注记已经消失，两边又对不上。
/// </para>
/// </summary>
public sealed class LastTradeNote
{
    /// <summary>记下它的那一天（游戏内天数）。</summary>
    public int Day { get; set; }

    /// <summary>交易单 id；还没有单号时为空（例如补全就失败了）。</summary>
    public string OfferId { get; set; } = "";

    /// <summary>业务科目（赞助 / 打赏 / …）。</summary>
    public string Kind { get; set; } = "";

    /// <summary>方向（<see cref="TradeDirections"/>）。</summary>
    public string Direction { get; set; } = "";

    /// <summary>
    /// 对手方（NPC）的 PersonId。文案里要写"谁给谁"，只有 <see cref="OfferId"/> 是说不清的
    /// （而弹窗与 V 段都必须能指名道姓）。
    /// </summary>
    public string NpcId { get; set; } = "";

    /// <summary>涉及金额（美元）。</summary>
    public long AmountUsd { get; set; }

    /// <summary>结论码，见 <see cref="TradeNoteStatuses"/>。</summary>
    public string Status { get; set; } = "";

    /// <summary>人类可读的原因（拒绝码、失败原因、方向未接线…）。</summary>
    public string Reason { get; set; } = "";

    /// <summary>一行回执（给玩家与模型看的"已成/未成"那句话）。</summary>
    public string Receipt { get; set; } = "";

    /// <summary>回执正文（多行；只有结算成功/幂等命中才有，弹窗与 V 段"最近结算"用它）。</summary>
    public string Detail { get; set; } = "";

    /// <summary>流水 id（结算成功时有）。</summary>
    public string LedgerEntryId { get; set; } = "";

    /// <summary>是否已经"成"了（只有它算真的给过钱）。</summary>
    public bool Settled =>
        Status is TradeNoteStatuses.Settled or TradeNoteStatuses.AlreadySettled;
}

/// <summary>
/// <see cref="LastTradeNote.Status"/> 的取值。<b>用常量而不是枚举</b>：
/// 它直接落进影子存档的 JSON，可读字符串比数字好排查。
/// <para>
/// 只有 <see cref="Settled"/> 与 <see cref="AlreadySettled"/> 代表"钱真的动过"——
/// 这一条同时写给玩家与模型看（见 V 段的账本口径行）。
/// </para>
/// </summary>
public static class TradeNoteStatuses
{
    /// <summary>已经结算，钱真的动了。</summary>
    public const string Settled = "settled";

    /// <summary>这一笔之前已经结算过（幂等命中），本次没再动钱。</summary>
    public const string AlreadySettled = "already_settled";

    /// <summary>交易标记没能补全（方向/双方/金额说不清）。</summary>
    public const string Incomplete = "incomplete";

    /// <summary>没有落单（校验链拒绝、软失败、无可用身份…）。</summary>
    public const string Refused = "refused";

    /// <summary>结算被拒（方向未接线、还没到齐、过期…），钱一分没动。</summary>
    public const string Failed = "failed";

    /// <summary>玩家自己点了拒绝 / 回了「拒绝 &lt;单号&gt;」。</summary>
    public const string Declined = "declined";
}

/// <summary>单个生涯（WorldId）的完整经济数据。</summary>
public sealed class EconomyWorld
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string WorldId { get; set; } = "";

    public string CreatedAt { get; set; } = "";
    public string LastWrittenAt { get; set; } = "";

    /// <summary>最近一次推进到的游戏日，用于跨存档恢复。</summary>
    public int LastTickedDay { get; set; }
    public int LastSeason { get; set; }

    /// <summary>PersonId → 经济状态。</summary>
    public Dictionary<string, NpcEconomy> Npcs { get; set; } = [];

    /// <summary>
    /// 玩家自己的影子钱包（阶段 4 第二步）。<b>它与 <see cref="Npcs"/> 是并列的两块</b>：
    /// 玩家不进 NPC 表，否则会被当成"N 人之一"注入进 Prompt。
    /// 老存档里没有这个字段，反序列化后是本类型的默认实例（未导入、余额 0）。
    /// </summary>
    public PlayerEconomy Player { get; set; } = new();

    /// <summary>OfferId → 交易单（含已终结的，按数量裁剪）。</summary>
    public Dictionary<string, TradeOffer> Offers { get; set; } = [];

    /// <summary>
    /// 幂等收据集合。每个键代表"这件事已经做过"，重复调用直接短路。
    /// 保留最近 <see cref="MaxReceipts"/> 条，且永不裁掉仍被 Pending 交易引用的键。
    /// </summary>
    public List<string> Receipts { get; set; } = [];

    /// <summary>播种用量统计，便于自检"总供给是否符合设计量级"。</summary>
    public long SeededTotalUsd { get; set; }
    public int SeededCount { get; set; }

    /// <summary>最后一次写入的注入摘要指纹，用于 CacheGuard 判断稳定段是否需要重写。</summary>
    public string StableSegmentFingerprint { get; set; } = "";

    /// <summary>
    /// <b>最近一笔交易的结论</b>（阶段 4 第三步）：未成立时写原因，结算后写回执。
    /// 它是"模型的话必须与账本一致"的唯一权威来源（见 <see cref="LastTradeNote"/>）。
    /// 老存档里没有它，反序列化后是空对象（相当于"还没有过交易"）。
    /// </summary>
    public LastTradeNote? LastTrade { get; set; }

    public const int MaxReceipts = 512;
    public const int MaxOffers = 256;
}
