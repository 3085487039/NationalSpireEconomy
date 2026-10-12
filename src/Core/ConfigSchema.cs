using System.Text.Json.Serialization;

namespace NationalSpireEconomy.Core;

// ─────────────────────────────────────────────────────────────────────────────
// 配置文件 1：economy_config.json
// 只放数值与公式参数。任何「需要重编译才能改的业务规则」都不应出现在这里，
// 反过来，任何写死在 C# 里的数值都应当搬迁到此处。
// ─────────────────────────────────────────────────────────────────────────────

public sealed class EconomyConfig
{
    [JsonPropertyName("configVersion")] public int ConfigVersion { get; set; }

    public SeedingConfig Seeding { get; set; } = new();
    public SeasonConfig Season { get; set; } = new();
    public TradeConfig Trade { get; set; } = new();
    public InjectionConfig Injection { get; set; } = new();
}

public sealed class SeedingConfig
{
    /// <summary>绝对上界。任何档位的 max 都会被它夹住。</summary>
    [JsonPropertyName("maxWealth")] public long MaxWealth { get; set; } = 500_000;

    /// <summary>曲线：log-uniform（默认）或 uniform。</summary>
    [JsonPropertyName("curve")] public string Curve { get; set; } = "log-uniform";

    [JsonPropertyName("tiers")] public List<SeedTier> Tiers { get; set; } = [];
}

/// <summary>
/// 一个财富档位。匹配是「先 roles 精确命中，再 identitiesContains，最后 ratingAtLeast 兜底」，
/// 全部不命中时落到 <see cref="SeedingConfig.Tiers"/> 里 Role 为空的兜底项。
/// </summary>
public sealed class SeedTier
{
    public string Id { get; set; } = "";

    /// <summary>命中 CareerPerson.Role 的取值集合。空表示不按 Role 匹配。</summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>命中 CareerPerson.Identities 的取值集合（Role 未命中时的回退）。</summary>
    public List<string> IdentitiesContains { get; set; } = [];

    /// <summary>Rating 门槛，用于同一 Role 内部再分层。null 表示不限。</summary>
    public int? RatingAtLeast { get; set; }

    public long Min { get; set; }
    public long Max { get; set; }

    /// <summary>抽样权重，相对值，不需要归一化。</summary>
    public double Weight { get; set; } = 1;
}

public sealed class SeasonConfig
{
    /// <summary>由天数推导赛季号时使用的赛季长度。原版长赛季 84 天、短赛季 28 天。</summary>
    [JsonPropertyName("daysPerSeason")] public int DaysPerSeason { get; set; } = 84;

    /// <summary>工资发放时机：seasonStart（新赛季第一天）或 seasonEnd（赛季最后一天）。</summary>
    [JsonPropertyName("salaryPayoutMode")] public string SalaryPayoutMode { get; set; } = "seasonStart";

    /// <summary>每个财富档位对应的赛季工资（美元）。键是 tier id。</summary>
    [JsonPropertyName("salaryByTier")] public Dictionary<string, long> SalaryByTier { get; set; } = [];

    /// <summary>上限系数：实际上限 = balance × tierCapRatio × give × (1 - 0.4 × thrift)。</summary>
    [JsonPropertyName("tierCapRatio")] public Dictionary<string, double> TierCapRatio { get; set; } = [];

    /// <summary>下限基准系数，配合 minRatio × (0.5 + charitySensitivity × favorMultiplier)。</summary>
    [JsonPropertyName("minRatio")] public Dictionary<string, double> MinRatio { get; set; } = [];

    /// <summary>转会费 = Rating × ratingTimes，再夹到 transferFeeClamp。</summary>
    [JsonPropertyName("transferFeeRatingTimes")] public double TransferFeeRatingTimes { get; set; } = 5;

    [JsonPropertyName("transferFeeClamp")] public List<long> TransferFeeClamp { get; set; } = [40_000, 100_000];

    [JsonPropertyName("signingFeeRatio")] public double SigningFeeRatio { get; set; } = 0.2;

    /// <summary>赛事奖金表（美元）。键是赛事类型，值是基础奖金；worldfinal 可以是名次数组。</summary>
    [JsonPropertyName("prizeUsd")] public Dictionary<string, long> PrizeUsd { get; set; } = [];

    /// <summary>高好感仍可拒绝的判据：金额占余额比例低于此值时，即使好感很高也会拒绝。</summary>
    [JsonPropertyName("extremeGapRatio")] public double ExtremeGapRatio { get; set; } = 0.0005;
}

public sealed class TradeConfig
{
    [JsonPropertyName("minAmount")] public long MinAmount { get; set; } = 100;
    [JsonPropertyName("maxSingleAmount")] public long MaxSingleAmount { get; set; } = 200_000;
    [JsonPropertyName("dailyLimitPerNpc")] public int DailyLimitPerNpc { get; set; } = 3;
    [JsonPropertyName("offerExpiryDays")] public int OfferExpiryDays { get; set; } = 14;

    /// <summary>NPC 向玩家出钱时是否必须校验自身余额。</summary>
    [JsonPropertyName("npcPayoutRequiresBalance")] public bool NpcPayoutRequiresBalance { get; set; } = true;

    [JsonPropertyName("requireFavourForNpcPayout")] public int RequireFavourForNpcPayout { get; set; } = 0;

    /// <summary>允许的 Kind 白名单。</summary>
    [JsonPropertyName("allowedKinds")] public List<string> AllowedKinds { get; set; } =
        ["赞助", "打赏", "雇佣", "奖金", "转账", "借款"];

    /// <summary>
    /// <b>Kind → 资金方向</b>的映射，用于"意图补全"。
    /// <para>
    /// <b>为什么需要它（真机实测 2026-10-11 20:41）</b>：模型发出的
    /// <c>[Trade: 赞助, Amount: 5000]</c> 只有 Kind 与金额，协议层不猜方向，
    /// 于是被 <c>IntentShapeValidator</c> 以 <c>BAD_SHAPE</c> 挡下——玩家看到"什么都没发生"。
    /// 方向必须有一个<b>可审计、可改配置</b>的来源，这里就是那个来源。
    /// </para>
    /// <para>
    /// 取值只能是 <c>npcToPlayer</c> / <c>playerToNpc</c> / <c>npcToNpc</c>；
    /// 标记里显式写了 <c>Direction:</c> 时以标记为准（见 <c>TradeIntentCompleter</c>）。
    /// <b>没配到的 Kind 一律拒绝</b>——绝不猜钱的方向。
    /// </para>
    /// </summary>
    [JsonPropertyName("kindDirections")] public Dictionary<string, string> KindDirections { get; set; } =
        new(StringComparer.Ordinal)
        {
            ["赞助"] = "npcToPlayer",
            ["打赏"] = "npcToPlayer",
            ["奖金"] = "npcToPlayer",
            ["雇佣"] = "playerToNpc",
            ["借款"] = "npcToPlayer",
            ["转账"] = "playerToNpc"
        };

    /// <summary>会产生余额变动的审计流水是否落盘。</summary>
    [JsonPropertyName("auditEnabled")] public bool AuditEnabled { get; set; } = true;

    /// <summary>
    /// <b>动钱开关（阶段 4 第二步）</b>：为 <c>true</c> 时「双方已确认」会<b>立即结算</b>
    /// （NPC 余额真的减少、玩家影子余额真的增加，并写一行审计）。
    /// <para>
    /// <b>默认 false</b>：不打开时行为与本阶段之前<b>完全一致</b>——停在"双方已确认，待结算"，一分钱不动。
    /// 它是"允许动钱"的唯一开关（环境变量 <c>NSE_SETTLEMENT=1</c> 同样能打开，两条路是"或"的关系）。
    /// </para>
    /// <para>
    /// <b>为什么用配置而不是只在代码里写死</b>：真机验证要走 Steam 启动，
    /// 而 Steam 启动拿不到我们临时设的环境变量（直接跑 exe 又过不了 Steamworks 初始化）。
    /// 改成"改这行配置 + 重启游戏"最稳，而且开关状态每次启动都会写进日志，可核对、可回退。
    /// </para>
    /// </summary>
    [JsonPropertyName("settlementWired")] public bool SettlementWired { get; set; }

    /// <summary>
    /// NPC 出钱时要求的最低情绪值。低于此值即"这点钱是在羞辱我吗"，即使金额没超性格上限也会拒绝。
    /// <para>
    /// 情绪值 = <c>amount / max(500, balance × 0.10)</c>，见 <c>PersonalityEconomy.Emotion</c>。
    /// 设为 0 表示关闭这条校验；设得越高，NPC 越"要面子"。
    /// </para>
    /// </summary>
    [JsonPropertyName("minEmotionToAccept")] public double MinEmotionToAccept { get; set; } = 1.0;

    /// <summary>
    /// NPC 出钱但余额不足时走软失败（不落 <c>TradeOffer</c>、不报错，只写一句素材进
    /// <c>PendingVoice</c>）。为 false 时退回硬失败，生成一张 <c>invalid</c> 交易单。
    /// </summary>
    [JsonPropertyName("softFailOnBalance")] public bool SoftFailOnBalance { get; set; } = true;
}

public sealed class InjectionConfig
{
    /// <summary>tier → wealthBand 措辞。必须零数字、定长，保护前缀缓存。</summary>
    [JsonPropertyName("stableTierLine")] public string StableTierLine { get; set; } = "";
    [JsonPropertyName("stableMoneyView")] public string StableMoneyView { get; set; } = "";
    [JsonPropertyName("stableIntentLine")] public string StableIntentLine { get; set; } = "";

    /// <summary>稳定段是否启用（关闭后只注入易变段，用于排查缓存问题）。</summary>
    [JsonPropertyName("stableEnabled")] public bool StableEnabled { get; set; } = true;

    /// <summary>
    /// 易变段（余额 / 本季收支 / 最近一笔流水 / 未竟之言）是否启用。
    /// <para>
    /// 与 <see cref="StableEnabled"/> 是<b>并列的两道闸</b>，不是主从关系：
    /// 只注入 S 段时把这里关掉即可，反之亦然。两段各自还有一份同名开关在
    /// <c>ai_prompt_economy.json</c> 里（那边的值是字符串），<b>两边都开才注入</b>。
    /// </para>
    /// </summary>
    [JsonPropertyName("volatileEnabled")] public bool VolatileEnabled { get; set; } = true;

    /// <summary>
    /// <b>挂载点体检</b>开关（方案 D）。打开时会在启动期打印程序集/类型副本清单、
    /// 诊断通道绑定结论，并给四个关键方法挂上"只观测不改数据"的探针。
    /// <para>
    /// 它是一次性排障工具：确认完"哪个方法真的被调用"之后可以关掉，回到只有注入的形态。
    /// </para>
    /// </summary>
    [JsonPropertyName("probeEnabled")] public bool ProbeEnabled { get; set; } = true;
}

// ─────────────────────────────────────────────────────────────────────────────
// 配置文件 2：personality_economy.json
// ─────────────────────────────────────────────────────────────────────────────

public sealed class PersonalityConfig
{
    [JsonPropertyName("configVersion")] public int ConfigVersion { get; set; }

    public EconomyProfile Defaults { get; set; } = new();

    [JsonPropertyName("favorTiers")] public FavorTierTable FavorTiers { get; set; } = new();

    /// <summary>Kind → 经济画像。必须覆盖 PersonalityLibrary.Kinds 的全部 24 项。</summary>
    public Dictionary<string, EconomyProfile> Kinds { get; set; } = [];

    public RefusalLineConfig RefusalLines { get; set; } = new();
}

/// <summary>
/// 一个性格的六个经济轴。<b>只影响「意愿与相对量级」</b>；
/// 绝对量级由 economy_config.json 的财富档位决定。两者相乘。
/// </summary>
public sealed class EconomyProfile
{
    /// <summary>给予倾向 0.2–1.4，直接乘进支出上限。</summary>
    public double Give { get; set; } = 1.0;

    /// <summary>施舍敏感度 0.05–0.90，越高越抗拒小额。</summary>
    public double CharitySensitivity { get; set; } = 0.5;

    /// <summary>议价强度 0.15–0.90，越高越会加价还价。</summary>
    public double Bargain { get; set; } = 0.5;

    /// <summary>消费谨慎度 0.15–0.90，反向压低上限。</summary>
    public double Thrift { get; set; } = 0.5;

    /// <summary>是否会向玩家借钱。默认全域关闭，只有明确自我贬低的性格才开。</summary>
    public bool Borrow { get; set; }

    /// <summary>主动发起交易的倾向 0.15–0.80。</summary>
    public double Initiative { get; set; } = 0.5;

    /// <summary>人话标签，用于 Prompt 注入与 UI 速读。例如 ["骄傲","要面子"]。</summary>
    public List<string> Tags { get; set; } = [];
}

public sealed class FavorTierTable
{
    [JsonPropertyName("tiers")] public List<FavorTier> Tiers { get; set; } = [];
}

public sealed class FavorTier
{
    public string Id { get; set; } = "";
    public int Min { get; set; }
    public int Max { get; set; }

    /// <summary>乘进 charitySensitivity 的系数。</summary>
    public double CharityMultiplier { get; set; } = 1.0;

    /// <summary>使用哪套拒绝台词：sharp 或 gentle。</summary>
    public string Line { get; set; } = "sharp";
}

public sealed class RefusalLineConfig
{
    public CharityLines Charity { get; set; } = new();
    public Dictionary<string, string> Cap { get; set; } = [];
    public SoftLineConfig Soft { get; set; } = new();
}

public sealed class CharityLines
{
    public Dictionary<string, string> Sharp { get; set; } = [];
    public Dictionary<string, string> Gentle { get; set; } = [];
}

public sealed class SoftLineConfig
{
    /// <summary>余额不足时的默认台词原文。会被注入下一轮的易变段。</summary>
    public string DefaultLine { get; set; } = "囊中羞涩，只能口头支持。";

    /// <summary>Kind → 语气方向（不是台词）。由模型自行措辞，避免机械复述。</summary>
    public Dictionary<string, string> ByKind { get; set; } = [];
}

// ─────────────────────────────────────────────────────────────────────────────
// 配置文件 3：ai_prompt_economy.json
// 只有措辞，没有逻辑。用字典承载，缺失的键在取值时回退到内置默认。
// ─────────────────────────────────────────────────────────────────────────────

public sealed class PromptConfig
{
    private readonly Dictionary<string, string> _values;

    public PromptConfig(Dictionary<string, string> values) => _values = values;

    public string this[string key] => Get(key, "");

    public string Get(string key, string fallback = "") =>
        _values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    public bool TryGet(string key, out string value)
    {
        if (_values.TryGetValue(key, out var found) && !string.IsNullOrWhiteSpace(found))
        {
            value = found;
            return true;
        }
        value = "";
        return false;
    }

    public IReadOnlyDictionary<string, string> Raw => _values;

    /// <summary>取一个嵌套对象下的全部字符串键值，用于 moneyAttitude / intentPhrase / wealthBands 这类表。</summary>
    public IReadOnlyDictionary<string, string> Table(string key)
    {
        if (!_tables.TryGetValue(key, out var table)) return EmptyTable;
        return table;
    }

    private static readonly Dictionary<string, string> EmptyTable = new();

    private readonly Dictionary<string, Dictionary<string, string>> _tables = new();

    internal void AddTable(string key, Dictionary<string, string> table) => _tables[key] = table;

    /// <summary>按模板替换占位符。缺失的占位符保留原样，便于在测试中立刻发现拼错。</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var builder = new System.Text.StringBuilder(template.Length + 64);
        int index = 0;
        while (index < template.Length)
        {
            int open = template.IndexOf('{', index);
            if (open < 0) { builder.Append(template, index, template.Length - index); break; }
            int close = template.IndexOf('}', open + 1);
            if (close < 0) { builder.Append(template, index, template.Length - index); break; }
            builder.Append(template, index, open - index);
            string name = template[(open + 1)..close];
            builder.Append(values.TryGetValue(name, out var value) ? value : "{" + name + "}");
            index = close + 1;
        }
        return builder.ToString();
    }
}
