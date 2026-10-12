using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Economy;

/// <summary>
/// 判定一个 NPC 需要知道的全部事实。<b>刻意与游戏类型解耦</b>——
/// 接线层负责把 <c>NationalSpire.CareerPerson</c> 投影成这个接口，
/// 于是档位判定与播种都能在没有游戏的情况下单测。
/// </summary>
public interface INpcFacts
{
    /// <summary>原版 <c>CareerPerson.Id</c>。</summary>
    string Id { get; }

    /// <summary>原版 <c>CareerPerson.Role</c>。0.26.7 的实际取值见 <see cref="KnownRoles"/>。</summary>
    string Role { get; }

    /// <summary>原版 <c>CareerPerson.Identities</c>，Role 未命中时的回退依据。</summary>
    IReadOnlyList<string> Identities { get; }

    /// <summary>原版 <c>CareerPerson.Rating</c>。</summary>
    int Rating { get; }

    /// <summary>原版 <c>CareerPerson.MaxAscension</c>。</summary>
    int MaxAscension { get; }

    /// <summary>原版 <c>CareerPerson.ClubId</c>。</summary>
    string ClubId { get; }

    /// <summary>原版 <c>CareerPerson.Personality.Kind</c>。</summary>
    string PersonalityKind { get; }

    /// <summary>原版 <c>CareerPerson.Personality.Version</c>。</summary>
    int PersonalityVersion { get; }
}

/// <summary>方便的不可变实现，接线层与测试都用它。</summary>
public sealed record NpcFacts(
    string Id,
    string Role,
    int Rating = 0,
    int MaxAscension = 0,
    string ClubId = "",
    string PersonalityKind = "",
    int PersonalityVersion = 0,
    IReadOnlyList<string>? Identities = null,
    int SalaryTierFromConfig = 0) : INpcFacts
{
    public IReadOnlyList<string> Identities { get; init; } = Identities ?? [];
}

/// <summary>
/// 财富档位判定。判定顺序：<c>Roles</c> 精确命中 → <c>IdentitiesContains</c> → <c>RatingAtLeast</c> 分层
/// → 无条件兜底项。多个档位同时命中时取<b>最具体</b>的那个（条件多者优先），避免兜底项抢走精确命中。
/// </summary>
public static class EconomyTier
{
    /// <summary>0.26.7 源码里实际出现的全部 Role 取值。</summary>
    public static readonly string[] KnownRoles =
    [
        "普通玩家", "青训选手", "职业选手", "世界顶尖", "退役选手",
        "教练", "主播", "解说员", "赛事记者"
    ];

    /// <summary>原版里用于标识"玩家本人"的 Id。</summary>
    public const string PlayerId = "player";

    /// <summary>按配置选出档位。返回 null 表示连兜底项都没有（配置有问题，应由校验器拦下）。</summary>
    public static SeedTier? Resolve(INpcFacts npc, SeedingConfig seeding)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(seeding);
        return ResolveInternal(npc, seeding);
    }

    /// <summary>
    /// 命中打分。<b>负值表示未命中</b>；0 分是"完全无条件"的兜底项。
    /// <para>
    /// 两个设计点：
    /// <list type="bullet">
    /// <item>多条件同时满足会累加，因此"职业选手 + Rating≥1200"会压过只有 Role 的那一项。</item>
    /// <item>条件数相同时，<b>Rating 门槛更高者优先</b>。
    /// 否则"普通玩家(≥0)"会先于"普通玩家(≥800)"命中，高阶分层永远不生效。</item>
    /// </list>
    /// </para>
    /// </summary>
    private static (int Specificity, int Threshold) Rank(INpcFacts npc, SeedTier tier)
    {
        int specificity = 0;
        int threshold = 0;

        if (tier.Roles.Count > 0)
        {
            if (!tier.Roles.Contains(npc.Role, StringComparer.Ordinal)) return (-1, 0);
            specificity += 100;
        }

        if (tier.IdentitiesContains.Count > 0)
        {
            bool hit = npc.Identities.Any(id => tier.IdentitiesContains.Contains(id, StringComparer.Ordinal));
            if (!hit) return (-1, 0);
            specificity += 10;
        }

        if (tier.RatingAtLeast is int required)
        {
            if (npc.Rating < required) return (-1, 0);
            specificity += 1;
            threshold = required;
        }

        return (specificity, threshold);
    }

    private static SeedTier? ResolveInternal(INpcFacts npc, SeedingConfig seeding)
    {
        SeedTier? best = null;
        int bestSpecificity = -1;
        int bestThreshold = int.MinValue;

        foreach (var tier in seeding.Tiers)
        {
            var (specificity, threshold) = Rank(npc, tier);
            if (specificity < 0) continue;

            if (specificity > bestSpecificity
                || specificity == bestSpecificity && threshold > bestThreshold)
            {
                best = tier;
                bestSpecificity = specificity;
                bestThreshold = threshold;
            }
        }

        return best;
    }

    /// <summary>档位 id。判定失败时回退到 <paramref name="fallbackId"/>。</summary>
    public static string ResolveId(INpcFacts npc, SeedingConfig seeding, string fallbackId = "default") =>
        Resolve(npc, seeding)?.Id ?? fallbackId;

    /// <summary>把给定的人按档位分组，便于自检与人数量核对。</summary>
    public static Dictionary<string, List<INpcFacts>> GroupByTier(IEnumerable<INpcFacts> people, SeedingConfig seeding)
    {
        var groups = new Dictionary<string, List<INpcFacts>>(StringComparer.Ordinal);
        foreach (var npc in people)
        {
            string tier = ResolveId(npc, seeding);
            if (!groups.TryGetValue(tier, out var list)) groups[tier] = list = [];
            list.Add(npc);
        }
        return groups;
    }
}
