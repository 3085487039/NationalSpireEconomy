using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Inject;

/// <summary>
/// 稳定段（S 段）的合成。
/// <para>
/// <b>铁律：稳定段必须零数字。</b>
/// </para>
/// <para>
/// 原版 <c>PrivateMessagePrompts.OrderContext</c>（181-202 行）把
/// <c>profile/results/public</c> 归为 Stable、<c>current/topic/schedule</c> 归为 Current，
/// 并<b>分成两条独立 user message 发送</b>。前缀缓存的命中面就是"从头到 Stable 结束"这一段。
/// 私信单次 payload 在 1.1 万–5.3 万字符之间，把余额写进稳定段会让整段前缀每笔交易都失效。
/// </para>
/// <para>
/// 本类只读 <see cref="PromptInjectInput"/> 里<b>不随交易变化</b>的字段：
/// <c>TierId</c>、<c>Facts</c>、<c>Personality</c>、<c>Npc.Lifetime.DriftScore</c>、
/// <c>Npc.Lifetime.LastRefusedCharityDay</c>、<c>Npc.PersonalityKind</c>。
/// 它<b>绝不</b>读 <c>Balance</c>、<c>SeasonStats</c>、<c>Ledger</c>、<c>PendingVoice</c>——
/// 那几个字段每笔交易都会变，一旦被读进来，稳定段就会随交易抖动。
/// </para>
/// </summary>
public static class StableSegment
{
    /// <summary>稳定段的行序。改顺序等于让所有 NPC 的前缀缓存失效，所以要显式声明。</summary>
    public static readonly string[] LineKeys =
        ["stableTierLine", "stableMoneyView", "stableIntentLine"];

    /// <summary>本类不允许读取的易变字段名，供测试断言"输入隔离"。</summary>
    public static readonly string[] ForbiddenInputs =
        [nameof(NpcEconomy.Balance), nameof(NpcEconomy.SeasonStats), nameof(NpcEconomy.Ledger),
         nameof(NpcEconomy.PendingVoice), nameof(NpcEconomy.LastTouchedDay)];

    /// <summary>合成稳定段。稳定段是否启用由调用方按配置决定；这里总是产出文本。</summary>
    public static string Build(PromptInjectInput input, string? bandId = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = new List<string>();

        Add(lines, input.Prompt.Get("stableHeader"));

        Add(lines, PromptConfig.Render(input.Prompt.Get("stableTierLine"), new Dictionary<string, string>
        {
            ["wealthBand"] = WealthBandFor(input),
            ["incomeSource"] = IncomeSourceFor(input)
        }));

        string kind = KindOf(input);
        Add(lines, PromptConfig.Render(input.Prompt.Get("stableMoneyView"), new Dictionary<string, string>
        {
            ["tags"] = TagsFor(input, kind),
            ["kindTrait"] = kind,
            ["moneyAttitude"] = MoneyAttitudeFor(input, kind)
        }));

        string band = bandId ?? IntentEvolve.BandIdFor(input.Npc, input.Day);
        Add(lines, PromptConfig.Render(input.Prompt.Get("stableIntentLine"), new Dictionary<string, string>
        {
            ["intentPhrase"] = IntentEvolve.PhraseFor(input.Prompt, band)
        }));

        Add(lines, input.Prompt.Get("stableBlockSuffix"));

        return string.Join('\n', lines);
    }

    /// <summary>
    /// 只收非空行。模板缺失时 <c>PromptConfig.Get</c> 返回空串，
    /// 直接 <c>Add</c> 进去会留下空行——空行也算"内容"，会让指纹白白变化，
    /// 还会在 Prompt 里留下一段莫名其妙的空白。
    /// </summary>
    private static void Add(List<string> lines, string? line)
    {
        if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
    }

    /// <summary>
    /// 取量级档位的措辞。
    /// <para>
    /// 表 <c>_weightBands</c> 的键是<b>Role 名</b>（"职业选手"），不是档位 id（"professional"），
    /// 所以先按 Role 查；查不到再退回 <c>default</c>。
    /// </para>
    /// </summary>
    public static string WealthBandFor(PromptInjectInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var table = input.Prompt.Table("weightBands");
        string role = input.Facts?.Role ?? "";

        if (role.Length > 0 && table.TryGetValue(role, out var byRole) && byRole.Length > 0) return byRole;
        if (table.TryGetValue("default", out var fallback) && fallback.Length > 0) return fallback;

        // 表格缺项时的最后兜底：用零数字的人话档位，绝不能让数字漏进稳定段。
        return MoneyUnits.WealthBand(input.Npc.Balance);
    }

    /// <summary>收入来源措辞。同样先按 Role 查。</summary>
    public static string IncomeSourceFor(PromptInjectInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var table = input.Prompt.Table("incomeSource");
        string role = input.Facts?.Role ?? "";

        if (role.Length > 0 && table.TryGetValue(role, out var byRole) && byRole.Length > 0) return byRole;
        if (table.TryGetValue("default", out var fallback) && fallback.Length > 0) return fallback;
        return "比赛奖金和日常收入";
    }

    /// <summary>人话标签。空则用「普通」，与 <c>PersonalityEconomy.UnknownTag</c> 一致。</summary>
    public static string TagsFor(PromptInjectInput input, string? kind = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var profile = PersonalityEconomy.ProfileFor(input.Personality, kind ?? KindOf(input));
        return profile.Tags.Count > 0 ? string.Join("、", profile.Tags) : PersonalityEconomy.UnknownTag;
    }

    /// <summary>一句话金钱观。措辞来自 <c>ai_prompt_economy.json</c> 的 <c>moneyAttitude</c> 表。</summary>
    public static string MoneyAttitudeFor(PromptInjectInput input, string? kind = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        string key = kind ?? KindOf(input);
        return input.Prompt.Table("moneyAttitude").TryGetValue(key, out var value) ? value : "";
    }

    /// <summary>
    /// 这个 NPC 的性格名。优先用原版事实，缺省时回落到影子存档里的快照——
    /// 后者是第 5 步用来侦测 <c>[Profile: ...]</c> 改写性格的依据。
    /// </summary>
    public static string KindOf(PromptInjectInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Facts != null && input.Facts.PersonalityKind.Length > 0) return input.Facts.PersonalityKind;
        return input.Npc.PersonalityKind;
    }
}
