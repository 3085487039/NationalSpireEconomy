using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Inject;

/// <summary>
/// 易变段（V 段）的合成。
/// <para>
/// <b>所有数字只允许出现在这里。</b>它进 user 的 <c>ordered.Current</c> 段内
/// （与日期/日程/状态同组），所以它每笔交易都变也无所谓——前缀缓存只关心
/// "从头到稳定段结束"那一段是否逐字节一致。
/// </para>
/// <para>金额一律经 <see cref="MoneyUnits.Format"/> 渲染：入参<b>已经是美元</b>，绝不再乘 10。</para>
/// </summary>
public static class VolatileSegment
{
    /// <summary>易变段的行序。</summary>
    public static readonly string[] LineKeys =
        ["volatileCurrent", "volatileLastEntry", "volatilePendingVoice"];

    /// <summary>
    /// 合成易变段。
    /// <para>
    /// <paramref name="pendingVoice"/> 缺省时取 <c>Npc.PendingVoice</c>——
    /// 后者是软失败留下的"未竟之言"，读进 Prompt 之后由调用方用
    /// <see cref="PromptInjector"/> 清空。
    /// </para>
    /// </summary>
    public static string Build(PromptInjectInput input, string? pendingVoice = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = new List<string>();
        var npc = input.Npc;
        var season = npc.SeasonStats;

        Add(lines, input.Prompt.Get("volatileHeader"));

        Add(lines, PromptConfig.Render(input.Prompt.Get("volatileCurrent"), new Dictionary<string, string>
        {
            ["balance"] = MoneyUnits.Format(npc.Balance),
            ["season"] = input.Season.ToString(),
            ["seasonIncome"] = MoneyUnits.Format(season.SeasonIncome),
            ["salaryIncome"] = MoneyUnits.Format(season.SalaryIncome),
            ["winBonusIncome"] = MoneyUnits.Format(season.WinBonusIncome),
            ["sponsorIncome"] = MoneyUnits.Format(season.SponsorIncome)
        }));

        var last = LastRealEntry(npc);
        if (last != null)
            Add(lines, PromptConfig.Render(input.Prompt.Get("volatileLastEntry"), new Dictionary<string, string>
            {
                ["day"] = last.Day.ToString(),
                ["title"] = last.Title,
                ["amount"] = MoneyUnits.Format(Math.Abs(last.Amount)),
                ["reason"] = last.Reason.Length > 0 ? "（" + last.Reason + "）" : ""
            }));

        string voice = pendingVoice ?? npc.PendingVoice;
        if (!string.IsNullOrWhiteSpace(voice))
            Add(lines, PromptConfig.Render(input.Prompt.Get("volatilePendingVoice"),
                new Dictionary<string, string> { ["line"] = voice }));

        // 额外只读注记（阶段 4 第一步：待确认交易的只读预览）。
        // 它天然带金额，所以只能落在这里；内容由调用方保证只读。
        if (!string.IsNullOrWhiteSpace(input.ExtraVolatileNote))
            Add(lines, input.ExtraVolatileNote);

        return string.Join('\n', lines);
    }

    /// <summary>只收非空行，避免模板缺失时留下空行（空行也会改变指纹）。</summary>
    private static void Add(List<string> lines, string? line)
    {
        if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
    }

    /// <summary>本次合成是否会带上 <c>PendingVoice</c>。调用方据此决定要不要清空它。</summary>
    public static bool WillIncludePendingVoice(PromptInjectInput input, string? pendingVoice = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        string voice = pendingVoice ?? input.Npc.PendingVoice;
        return !string.IsNullOrWhiteSpace(voice);
    }

    /// <summary>
    /// 易变段的指纹。
    /// <para>
    /// <b>只用于诊断</b>（日志与启动期自证），<b>绝不</b>写进存档：
    /// 易变段每笔交易都会变，根本不存在"缓存命中"这回事，把它的指纹持久化只会误导后人
    /// 以为它和 <c>EconomyWorld.StableSegmentFingerprint</c> 是一类东西。
    /// </para>
    /// </summary>
    public static string FingerprintOf(string volatileText) =>
        DeterministicHash.StableHash(volatileText ?? "").ToString("x8");

    /// <summary>
    /// 取"最近一笔"真实流水。
    /// <para>
    /// 冲正流水要跳过：给模型看一行"冲正：支付转会费"没有信息量，
    /// 反而会让它以为发生了一笔新交易。同理也跳过金额为 0 的记录。
    /// </para>
    /// </summary>
    public static LedgerEntry? LastRealEntry(NpcEconomy npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        foreach (var entry in npc.Ledger)
        {
            if (entry.Kind == EconomyLedger.ReversalKind) continue;
            if (entry.Amount == 0) continue;
            return entry;
        }
        return null;
    }
}
