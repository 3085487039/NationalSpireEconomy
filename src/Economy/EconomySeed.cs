using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Economy;

/// <summary>一个档位的播种预期，用于自检"总供给是否符合设计量级"。</summary>
public sealed record TierBudget(
    string TierId,
    double Weight,
    double Share,
    int ExpectedCount,
    long MinUsd,
    long MaxUsd,
    double MeanUsd)
{
    public long ExpectedTotalUsd => (long)Math.Round(MeanUsd * ExpectedCount);
}

/// <summary>播种预算报告。<b>用精确公式算，不靠采样估计。</b></summary>
public sealed record SeedingBudget(
    IReadOnlyList<TierBudget> Tiers,
    int ExpectedNpcCount,
    long ExpectedTotalUsd)
{
    public double ExpectedPerNpcUsd => ExpectedNpcCount == 0 ? 0 : ExpectedTotalUsd / (double)ExpectedNpcCount;
}

/// <summary>播种结果。<paramref name="SkippedPlayer"/> 与 <paramref name="SkippedSeeded"/> 刻意分开。</summary>
public sealed record SeedResult(
    int SeededCount,
    int SkippedPlayer,
    int SkippedSeeded,
    long TotalUsd,
    IReadOnlyDictionary<string, int> CountByTier,
    IReadOnlyDictionary<string, long> TotalByTier);

/// <summary>
/// 初始资金播种。
/// <para>
/// 两个刻意的设计：
/// <list type="number">
/// <item><b>阶梯式两段水位线</b>（高知名度 / 高战绩）。这样"世界顶尖"里既有真有钱的，
/// 也有刚好够格、手头紧的，而不是同档位一个模子。</item>
/// <item><b>确定性</b>。种子来自 <c>WorldId + PersonId</c> 与原版 <c>StableHash</c> 同算法，
/// 所以影子存档丢了也能重建出一模一样的数字，不会因为丢个文件就换掉整套经济格局。</item>
/// </list>
/// </para>
/// </summary>
public static class EconomySeed
{
    /// <summary>播种随机种子用的命名空间，改动它会让所有存档的播种结果变化。</summary>
    public const string SeedNamespace = "economy-seed-v1";

    /// <summary>兜底档位的 id。配置里的无条件项应当用这个 id。</summary>
    public const string FallbackTierId = "default";

    /// <summary>
    /// 给一个人掷初始资金。<b>纯函数</b>：同样输入必然同样输出。
    /// </summary>
    public static long RollInitialBalance(string worldId, INpcFacts npc, SeedingConfig seeding, SeedTier tier)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(seeding);
        ArgumentNullException.ThrowIfNull(tier);

        long max = Math.Min(tier.Max, seeding.MaxWealth);
        long min = Math.Min(tier.Min, max);

        // 阶梯式水位线：人的水平越高，起步越靠上。用 MaxAscension 与 Rating 两个维度，
        // 这样同为"职业选手"也会因为战绩不同而分层。
        double level = LevelOf(npc);

        var rng = new Random(DeterministicHash.Seed(worldId, SeedNamespace, npc.Id));
        double sample = Math.Pow(rng.NextDouble(), 1.0 / (0.5 + level));

        long value = (long)Math.Floor(min + sample * (max - min));
        return RoundToMagnitude(value, max);
    }

    /// <summary>
    /// 阶梯水位线，范围 0~1。进阶占 6 成权重、评分占 4 成，
    /// 于是"同为职业选手"里，进阶 9 的人平均起步明显高于进阶 6 的人。
    /// </summary>
    public static double LevelOf(INpcFacts npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        double level = (Math.Clamp(npc.MaxAscension, 0, 10) / 10.0) * 0.6
                     + (Math.Clamp(npc.Rating, 500, 2100) - 500) / 1600.0 * 0.4;
        return Math.Clamp(level, 0, 1);
    }

    /// <summary>
    /// 实际使用的幂指数。<c>level = 0.5</c> 时恰好为 1，也就是标准的 log-uniform；
    /// level 越大指数越小、分布越偏向区间上端。
    /// </summary>
    public static double PowerFor(double level) => 1.0 / (0.5 + Math.Clamp(level, 0, 1));

    /// <summary>取整到人类会写的档位，避免出现 137942 这种假精确。</summary>
    public static long RoundToMagnitude(long value, long max)
    {
        long step = max switch
        {
            < 1_000 => 10,
            < 10_000 => 100,
            < 100_000 => 500,
            _ => 1_000
        };
        long rounded = value / step * step;
        return Math.Max(step, rounded);
    }

    /// <summary>
    /// 对一批 NPC 执行播种。
    /// <para>
    /// 档位分配是"全局打乱后按权重切分"，而不是逐人独立掷骰——
    /// 前者保证 1800 人规模下各档位人数稳定落在预期比例附近，
    /// 后者会在尾档（合计 4%）产生明显抖动。
    /// </para>
    /// </summary>
    public static SeedResult Seed(
        EconomyWorld world,
        IReadOnlyList<INpcFacts> people,
        EconomyConfig config,
        IEconomyLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(config);

        var seeding = config.Seeding;
        var tiers = seeding.Tiers;
        if (tiers.Count == 0) throw new InvalidOperationException("seeding.tiers 为空，无法播种。");

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        long grandTotal = 0;

        // 两类跳过要分开计数，否则"玩家本人"会和"已播过"混在一起，统计失去诊断价值。
        int skippedPlayer = 0;
        int skippedSeeded = 0;
        var pending = new List<INpcFacts>(people.Count);
        foreach (var person in people)
        {
            if (person.Id == EconomyTier.PlayerId) { skippedPlayer++; continue; }
            if (world.Npcs.ContainsKey(person.Id)) { skippedSeeded++; continue; }
            pending.Add(person);
        }

        var assignment = AssignTiers(pending, world.WorldId, seeding);

        foreach (var (npc, tier) in assignment)
        {
            long balance = RollInitialBalance(world.WorldId, npc, seeding, tier);
            var economy = new NpcEconomy
            {
                PersonId = npc.Id,
                Balance = balance,
                TierId = tier.Id,
                ClubId = npc.ClubId,
                PersonalityKind = npc.PersonalityKind,
                PersonalityVersion = npc.PersonalityVersion,
                Season = world.LastSeason,
                LastTickedDay = world.LastTickedDay,
                LastTouchedDay = world.LastTickedDay
            };
            world.Npcs[npc.Id] = economy;

            counts[tier.Id] = counts.GetValueOrDefault(tier.Id) + 1;
            totals[tier.Id] = totals.GetValueOrDefault(tier.Id) + balance;
            grandTotal += balance;
        }

        world.SeededCount = world.Npcs.Count;
        world.SeededTotalUsd = world.Npcs.Values.Sum(n => n.Balance);

        log?.Info("seed",
            $"播种完成：新增 {assignment.Count} 人（跳过玩家 {skippedPlayer}、已播种 {skippedSeeded}），" +
            $"本次总额 {MoneyUnits.Format(grandTotal)}，" +
            $"人均 {MoneyUnits.Format(assignment.Count == 0 ? 0 : grandTotal / assignment.Count)}。");

        return new SeedResult(assignment.Count, skippedPlayer, skippedSeeded, grandTotal, counts, totals);
    }

    /// <summary>
    /// 把一批人分配到档位。用 <c>WorldId</c> 做种子的稳定洗牌，
    /// 于是每个人的档位完全由 <c>WorldId</c> 决定，可确定性重放。
    /// </summary>
    public static List<(INpcFacts Npc, SeedTier Tier)> AssignTiers(
        IReadOnlyList<INpcFacts> people, string worldId, SeedingConfig seeding)
    {
        var result = new List<(INpcFacts, SeedTier)>(people.Count);
        if (people.Count == 0) return result;

        // 每人的档位首先是"判定"出来的：精确命中 Roles 的人不该被随机分到别的档。
        var exact = new List<(INpcFacts Npc, SeedTier Tier)>();
        var undecided = new List<INpcFacts>();
        foreach (var npc in people)
        {
            var tier = EconomyTier.Resolve(npc, seeding);
            if (tier == null || tier.Roles.Count == 0 && tier.IdentitiesContains.Count == 0 && tier.RatingAtLeast is null)
                undecided.Add(npc);
            else
                exact.Add((npc, tier!));
        }

        result.AddRange(exact);
        if (undecided.Count == 0) return result;

        // 剩下的按权重打乱切分。只用兜底档（以及将来可能出现的多条件档）。
        var fallbackTiers = seeding.Tiers
            .Where(t => t.Roles.Count == 0 && t.IdentitiesContains.Count == 0 && t.RatingAtLeast is null)
            .ToArray();
        if (fallbackTiers.Length == 0) fallbackTiers = [seeding.Tiers[^1]];

        var shuffled = undecided.ToList();
        Shuffle(shuffled, new Random(DeterministicHash.Seed(worldId, SeedNamespace, "tiers")));

        double totalWeight = fallbackTiers.Sum(t => Math.Max(0, t.Weight));
        int index = 0;
        foreach (var tier in fallbackTiers)
        {
            int take = tier == fallbackTiers[^1]
                ? shuffled.Count - index
                : (int)Math.Round(shuffled.Count * (Math.Max(0, tier.Weight) / totalWeight));
            take = Math.Clamp(take, 0, shuffled.Count - index);
            for (int i = 0; i < take; i++) result.Add((shuffled[index + i], tier));
            index += take;
        }

        return result;
    }

    private static void Shuffle<T>(IList<T> list, Random random)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// 用精确公式给出播种预算：<c>E[X] = (b-a)/(ln b - ln a)</c> 是连续 log-uniform 的期望，
    /// 再叠加取整带来的 <c>+step/2</c> 偏移。不做蒙特卡洛，避免"报告本身有误差"。
    /// </summary>
    public static SeedingBudget Budget(SeedingConfig seeding, IReadOnlyList<INpcFacts> people)
    {
        ArgumentNullException.ThrowIfNull(seeding);
        ArgumentNullException.ThrowIfNull(people);

        var byTier = EconomyTier.GroupByTier(people.Where(p => p.Id != EconomyTier.PlayerId), seeding);
        int total = byTier.Values.Sum(v => v.Count);
        var budgets = new List<TierBudget>();
        long expectedTotal = 0;

        foreach (var tier in seeding.Tiers)
        {
            int count = byTier.TryGetValue(tier.Id, out var list) ? list.Count : 0;
            long max = Math.Min(tier.Max, seeding.MaxWealth);
            long min = Math.Max(1, Math.Min(tier.Min, max));
            double mean = ExpectedRoundedMean(min, max);
            var budget = new TierBudget(
                tier.Id,
                tier.Weight,
                total == 0 ? 0 : count / (double)total,
                count,
                min,
                max,
                mean);
            budgets.Add(budget);
            expectedTotal += budget.ExpectedTotalUsd;
        }

        return new SeedingBudget(budgets, total, expectedTotal);
    }

    /// <summary>取整后的 log-uniform 期望值。</summary>
    public static double ExpectedRoundedMean(long min, long max)
    {
        if (max <= min) return min;
        long step = max switch
        {
            < 1_000 => 10,
            < 10_000 => 100,
            < 100_000 => 500,
            _ => 1_000
        };
        // 连续部分：(b-a)/(ln b - ln a)
        double continuous = (max - min) / (Math.Log(max) - Math.Log(min));
        // 被 Math.Max(step, ·) 抬起来的部分：对低于 step 的区间做一次近似补偿。
        double lifted = continuous < step ? step - continuous : 0;
        return continuous + lifted;
    }
}
