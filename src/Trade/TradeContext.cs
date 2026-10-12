using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Trade;

/// <summary>
/// 一次交易求值的全部输入。校验器<b>只读</b>它，不允许在这里改任何余额——
/// 唯一能改余额的地方是 <see cref="EconomyLedger"/>，且只有执行器能调用。
/// </summary>
public sealed class TradeContext
{
    public required EconomyWorld World { get; init; }
    public required EconomyConfig Config { get; init; }
    public required PersonalityConfig Personality { get; init; }
    public required PromptConfig Prompt { get; init; }

    /// <summary>NPC 侧事实（Role/Rating/性格快照）。缺省时性格适配会走"未知性格"的兜底路径。</summary>
    public INpcFacts? Counterparty { get; init; }

    /// <summary>玩家与这个 NPC 的好感度（原版 <c>PrivateMessages.Favour</c>，-100~100）。</summary>
    public int Favour { get; init; }

    public int Day { get; init; }

    /// <summary>缺省时由 <see cref="Day"/> 与配置里的赛季长度推导。</summary>
    public int Season { get; init; }

    public required IPlayerWallet Wallet { get; init; }

    /// <summary>多人模式下"本机玩家"的 id。默认就是 <see cref="EconomyTier.PlayerId"/>。</summary>
    public string LocalHumanId { get; init; } = EconomyTier.PlayerId;

    /// <summary>物品目录。未接线时用 <c>NullTradeCatalog</c>（任何 ItemId 都解析不出）。</summary>
    public ITradeCatalog? Catalog { get; init; }

    /// <summary>按 <paramref name="personId"/> 取 NPC 的经济状态；不存在返回 null。</summary>
    public NpcEconomy? NpcOf(string personId) =>
        !string.IsNullOrEmpty(personId) && World.Npcs.TryGetValue(personId, out var npc) ? npc : null;

    /// <summary>推导赛季号。<see cref="Season"/> 显式给出时以它为准。</summary>
    public int EffectiveSeason =>
        Season > 0 ? Season : EconomyTick.SeasonOf(Day, Math.Max(1, Config.Season.DaysPerSeason));

    /// <summary>判断某个 id 是否是"玩家"（而非 NPC）。</summary>
    public bool IsPlayer(string personId) =>
        string.Equals(personId, EconomyTier.PlayerId, StringComparison.Ordinal) ||
        string.Equals(personId, LocalHumanId, StringComparison.Ordinal);

    /// <summary>
    /// 某个参与方<b>给人看的名字</b>。
    /// <para>
    /// <b>为什么需要它（2026-10-12 真机截图暴露的观感问题）</b>：确认弹窗原先直接显示原始 id，
    /// 于是玩家看到的是 <c>发起方：player｜对手方：pro2_5</c>，而原版界面里这个人叫
    /// 「酸橙汽水」—— 一边是内部编号、一边是人名，看着像调试输出。
    /// </para>
    /// <para>
    /// 解析顺序：玩家侧用固定称呼 → <see cref="Counterparty"/> 的显示名（仅当它确实是这个人）
    /// → 退回原 id。<b>绝不返回空串</b>，否则文案里会缺一块。
    /// </para>
    /// <para>
    /// <b>已知局限</b>：影子存档里<b>不存人名</b>（人名是原版生涯数据，属于只读、且会变），
    /// 所以只要 <see cref="Counterparty"/> 为空或不是这个人（例如投票路径、跨重启后的补记路径），
    /// 就只能退回显示 id。要彻底解决就得把人名持久化进影子存档 —— 那是另一件事，本步不做。
    /// </para>
    /// </summary>
    public string DisplayNameOf(string personId)
    {
        if (IsPlayer(personId)) return "你";
        if (string.IsNullOrEmpty(personId)) return "对方";

        if (Counterparty != null && string.Equals(Counterparty.Id, personId, StringComparison.Ordinal))
        {
            string name = Counterparty.Name;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }

        return personId;
    }
}
