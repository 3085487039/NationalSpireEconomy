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
}
