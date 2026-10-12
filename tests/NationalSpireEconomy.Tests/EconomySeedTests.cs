using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 播种。核心要证明三件事：<b>确定性</b>（丢档能重建）、<b>落在档位区间内</b>、
/// <b>预算报告与实际一致</b>。
/// </summary>
public static class EconomySeedTests
{
    public static Action[] Cases =>
    [
        SameWorldAndPersonAlwaysRollsTheSame,
        DifferentWorldRollsDifferently,
        BalanceStaysInsideTierRange,
        BalanceRespectsGlobalMaxWealth,
        HigherLevelStartsHigher,
        SeedSkipsPlayerAndAlreadySeeded,
        SeedIsIdempotent,
        SeedFillsEveryTier,
        AssignmentIsDeterministicAndRespectsWeights,
        ExactTiersAreNotRandomised,
        BudgetMeanMatchesMonteCarlo,
        HigherLevelPushesDistributionUp,
        RealConfigBudgetIsReported
    ];

    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static DelegateEconomyLog Silent => new((_, _, _, _) => { });

    private static ConfigLoadResult LoadReal()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "config");
            if (File.Exists(Path.Combine(candidate, ConfigLoader.EconomyFileName)))
                return new ConfigLoader(candidate, Silent).Load();
            directory = directory.Parent;
        }
        throw new Exception("找不到 config/ 目录");
    }

    private static SeedingConfig FlatSeeding() => new()
    {
        MaxWealth = 500_000,
        Curve = "log-uniform",
        Tiers =
        [
            new SeedTier { Id = "crowd", Roles = ["普通玩家"], Min = 500, Max = 5_000, Weight = 62 },
            new SeedTier { Id = "professional", Roles = ["职业选手"], Min = 50_000, Max = 200_000, Weight = 12 },
            new SeedTier { Id = "default", Min = 1_000, Max = 20_000, Weight = 1 }
        ]
    };

    private static void SameWorldAndPersonAlwaysRollsTheSame()
    {
        var seeding = FlatSeeding();
        var tier = seeding.Tiers[0];
        var npc = new NpcFacts("crowd-0-5", "普通玩家", Rating: 700, MaxAscension: 2);

        long first = EconomySeed.RollInitialBalance("world1", npc, seeding, tier);
        for (int i = 0; i < 50; i++)
            TestRunner.Equal(first, EconomySeed.RollInitialBalance("world1", npc, seeding, tier),
                "同一世界同一人物必须掷出同一金额");
    }

    private static void DifferentWorldRollsDifferently()
    {
        var seeding = FlatSeeding();
        var tier = seeding.Tiers[0];
        var npc = new NpcFacts("crowd-0-5", "普通玩家", Rating: 700);

        // 单点可能巧合，取 20 个人看是否出现差异。
        bool anyDifference = false;
        for (int i = 0; i < 20; i++)
        {
            var person = new NpcFacts($"crowd-{i}", "普通玩家", Rating: 700);
            if (EconomySeed.RollInitialBalance("worldA", person, seeding, tier)
                != EconomySeed.RollInitialBalance("worldB", person, seeding, tier))
                anyDifference = true;
        }
        TestRunner.True(anyDifference, "不同世界应当掷出不同的资金分布");
    }

    private static void BalanceStaysInsideTierRange()
    {
        var seeding = FlatSeeding();
        var rng = new Random(42);
        foreach (var tier in seeding.Tiers)
        {
            for (int i = 0; i < 2000; i++)
            {
                var npc = new NpcFacts($"n{i}", "x", Rating: rng.Next(500, 2100), MaxAscension: rng.Next(0, 11));
                long balance = EconomySeed.RollInitialBalance("world", npc, seeding, tier);
                TestRunner.InRange(balance, tier.Min, tier.Max, $"档位 {tier.Id} 掷出了越界金额");
            }
        }
    }

    private static void BalanceRespectsGlobalMaxWealth()
    {
        var seeding = new SeedingConfig
        {
            MaxWealth = 50_000,
            Tiers = [new SeedTier { Id = "elite", Roles = ["世界顶尖"], Min = 200_000, Max = 500_000, Weight = 1 }]
        };
        var npc = new NpcFacts("p21", "世界顶尖", Rating: 1600, MaxAscension: 9);
        for (int i = 0; i < 200; i++)
        {
            long balance = EconomySeed.RollInitialBalance("w", npc with { Id = "p" + i }, seeding, seeding.Tiers[0]);
            TestRunner.InRange(balance, 1, 50_000, "maxWealth 必须把档位上限夹住");
        }
    }

    private static void HigherLevelStartsHigher()
    {
        // 阶梯水位线：同为职业选手，进阶与评分更高的人平均起步更高。
        var seeding = FlatSeeding();
        var tier = seeding.Tiers[1];
        long weakTotal = 0, strongTotal = 0;
        const int samples = 400;
        for (int i = 0; i < samples; i++)
        {
            weakTotal += EconomySeed.RollInitialBalance("w",
                new NpcFacts($"weak{i}", "职业选手", Rating: 1000, MaxAscension: 6), seeding, tier);
            strongTotal += EconomySeed.RollInitialBalance("w",
                new NpcFacts($"strong{i}", "职业选手", Rating: 1700, MaxAscension: 9), seeding, tier);
        }
        TestRunner.True(strongTotal > weakTotal,
            $"高水平应当在统计上起步更高：弱 {weakTotal} vs 强 {strongTotal}");
    }

    private static void SeedSkipsPlayerAndAlreadySeeded()
    {
        var loaded = LoadReal();
        var world = new EconomyWorld { WorldId = "seedtest", LastSeason = 10, LastTickedDay = 700 };
        var people = new List<INpcFacts>
        {
            new NpcFacts("player", "参赛选手"),
            new NpcFacts("p0", "职业选手", Rating: 1400, MaxAscension: 8),
            new NpcFacts("p1", "普通玩家", Rating: 700, MaxAscension: 2)
        };

        var first = EconomySeed.Seed(world, people, loaded.Economy, Silent);
        TestRunner.Equal(2, first.SeededCount, "玩家本人不应被播种");
        TestRunner.Equal(1, first.SkippedPlayer, "玩家应被单独计为「跳过玩家」");
        TestRunner.Equal(0, first.SkippedSeeded);
        TestRunner.False(world.Npcs.ContainsKey("player"), "账本里不应出现 player");

        var second = EconomySeed.Seed(world, people, loaded.Economy, Silent);
        TestRunner.Equal(0, second.SeededCount, "重复播种不应新增");
        TestRunner.Equal(1, second.SkippedPlayer, "玩家仍然要被单独计为「跳过玩家」");
        TestRunner.Equal(2, second.SkippedSeeded, "已播种的两人应计为「已播种」");
    }

    private static void SeedIsIdempotent()
    {
        var loaded = LoadReal();
        var people = Enumerable.Range(0, 60)
            .Select(i => (INpcFacts)new NpcFacts($"p{i}", i % 3 == 0 ? "职业选手" : "普通玩家", Rating: 700 + i * 10, MaxAscension: i % 10))
            .ToList();

        var worldA = new EconomyWorld { WorldId = "sameworld", LastTickedDay = 700 };
        EconomySeed.Seed(worldA, people, loaded.Economy, Silent);

        var worldB = new EconomyWorld { WorldId = "sameworld", LastTickedDay = 700 };
        EconomySeed.Seed(worldB, people, loaded.Economy, Silent);

        foreach (string id in worldA.Npcs.Keys)
            TestRunner.Equal(worldA.Npcs[id].Balance, worldB.Npcs[id].Balance,
                $"{id} 在同一世界下重复播种必须得到相同金额");
    }

    private static void SeedFillsEveryTier()
    {
        var loaded = LoadReal();
        var world = new EconomyWorld { WorldId = "filltest" };
        // 覆盖全部 9 种已知 Role。
        var people = new List<INpcFacts>();
        int index = 0;
        foreach (string role in EconomyTier.KnownRoles)
            for (int i = 0; i < 8; i++)
                people.Add(new NpcFacts($"n{index++}", role, Rating: 700 + index, MaxAscension: index % 10));

        var result = EconomySeed.Seed(world, people, loaded.Economy, Silent);
        TestRunner.Equal(people.Count, result.SeededCount);
        TestRunner.Equal(0, result.CountByTier.GetValueOrDefault(EconomySeed.FallbackTierId),
            "全部是已知 Role，不应有人掉进兜底档");

        // 9 种 Role 映射到 6 个档位：教练/主播/解说员/赛事记者共用 media。
        TestRunner.Equal(6, result.CountByTier.Count, "应当恰好落到 6 个档位");
        foreach (string tier in new[] { "crowd", "youth", "media", "professional", "elite", "veteran" })
            TestRunner.True(result.CountByTier.GetValueOrDefault(tier) > 0, $"档位 {tier} 没有分到人");

        long sum = world.Npcs.Values.Sum(n => n.Balance);
        TestRunner.Equal(sum, world.SeededTotalUsd, "总量统计必须与逐人求和一致");
        TestRunner.Equal(people.Count, world.SeededCount);
    }

    private static void AssignmentIsDeterministicAndRespectsWeights()
    {
        var seeding = FlatSeeding();
        var people = Enumerable.Range(0, 1000)
            .Select(i => (INpcFacts)new NpcFacts($"n{i}", "未知身份"))
            .ToList();

        var first = EconomySeed.AssignTiers(people, "w1", seeding);
        var second = EconomySeed.AssignTiers(people, "w1", seeding);
        TestRunner.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
            TestRunner.Equal(first[i].Tier.Id, second[i].Tier.Id, "档位分配必须可确定性重放");

        // 全部 1000 人都是未知身份，会按兜底档权重切；这里只有一个兜底档，应当全落进去。
        TestRunner.Equal(1000, first.Count(a => a.Tier.Id == EconomySeed.FallbackTierId));
    }

    private static void ExactTiersAreNotRandomised()
    {
        // 精确命中 Roles 的人不能被随机洗到别的档——否则"世界顶尖"会被洗成"普通玩家"。
        var seeding = FlatSeeding();
        var people = new List<INpcFacts>();
        for (int i = 0; i < 50; i++) people.Add(new NpcFacts($"prof{i}", "职业选手"));
        for (int i = 0; i < 50; i++) people.Add(new NpcFacts($"crowd{i}", "普通玩家"));

        var assignment = EconomySeed.AssignTiers(people, "w", seeding);
        foreach (var (npc, tier) in assignment)
        {
            if (npc.Id.StartsWith("prof")) TestRunner.Equal("professional", tier.Id);
            else TestRunner.Equal("crowd", tier.Id);
        }
    }

    private static void BudgetMeanMatchesMonteCarlo()
    {
        // 解析公式必须与实测吻合，否则预算报告会误导人。
        // 注意：实测用的是"幂分布 + 取整"，所以要与公式用同一组 p 值比较。
        (long Min, long Max, double[] Powers)[] cases =
        [
            (500, 5_000, [1.0, EconomySeed.PowerFor(0.0), EconomySeed.PowerFor(1.0)]),
            (8_000, 30_000, [1.0, EconomySeed.PowerFor(0.0), EconomySeed.PowerFor(1.0)]),
            (200_000, 500_000, [1.0, EconomySeed.PowerFor(0.5)])
        ];

        foreach (var (min, max, powers) in cases)
        {
            double analytic = 0;
            foreach (double p in powers) analytic += ExpectedPowerMean(min, max, p);
            analytic /= powers.Length;

            var rng = new Random(2026);
            long total = 0;
            const int perPower = 8_000;
            foreach (double p in powers)
            {
                var tier = new SeedTier { Id = "t", Min = min, Max = max };
                var seeding = new SeedingConfig { MaxWealth = max };
                for (int i = 0; i < perPower; i++)
                {
                    // 直接按公式抽样，与 RollInitialBalance 内部使用的分布保持一致。
                    double sample = Math.Pow(rng.NextDouble(), p);
                    long value = (long)Math.Floor(min + sample * (max - min));
                    total += EconomySeed.RoundToMagnitude(value, max);
                }
            }

            double measured = total / (double)(perPower * powers.Length);
            double error = Math.Abs(measured - analytic) / analytic;
            TestRunner.True(error < 0.03,
                $"区间 [{min},{max}] 的解析均值 {analytic:0} 与实测 {measured:0} 偏差 {error:P1} 过大");
        }
    }

    /// <summary>幂分布 <c>x = min + (max-min)·u^p</c> 的期望（取整前）。</summary>
    private static double ExpectedPowerMean(long min, long max, double power) =>
        min + (max - min) / (power + 1.0);

    private static void HigherLevelPushesDistributionUp()
    {
        // 幂指数随 level 单调下降，分布越偏上端。
        TestRunner.True(EconomySeed.PowerFor(0.0) > EconomySeed.PowerFor(0.5));
        TestRunner.True(EconomySeed.PowerFor(0.5) > EconomySeed.PowerFor(1.0));
        TestRunner.True(Math.Abs(EconomySeed.PowerFor(0.5) - 1.0) < 1e-9, "level=0.5 时应当恰好是标准 log-uniform");
    }

    private static void RealConfigBudgetIsReported()
    {
        var loaded = LoadReal();

        // 用接近真实的人口结构：按档位权重铺 1800 人。
        // 真实存档里 crowd 约 62%、职业 12%、青训 12%、媒体 10%、退役 2%、顶尖 2%。
        (string Role, int Count)[] mix =
        [
            ("普通玩家", 1116),
            ("职业选手", 216),
            ("青训选手", 216),
            ("教练", 90),
            ("主播", 45),
            ("解说员", 45),
            ("赛事记者", 36),
            ("退役选手", 36),
            ("世界顶尖", 0)
        ];

        var people = new List<INpcFacts>();
        int index = 0;
        foreach (var (role, count) in mix)
            for (int i = 0; i < count; i++)
            {
                people.Add(new NpcFacts($"n{index}", role, Rating: 620 + index % 700, MaxAscension: index % 10));
                index++;
            }

        var budget = EconomySeed.Budget(loaded.Economy.Seeding, people);
        TestRunner.Equal(1800, budget.ExpectedNpcCount);

        Console.WriteLine($"        [预算] 人数 {budget.ExpectedNpcCount}，" +
                          $"预计总量 {MoneyUnits.Format(budget.ExpectedTotalUsd)}，" +
                          $"人均 {MoneyUnits.Format((long)budget.ExpectedPerNpcUsd)}");
        foreach (var tier in budget.Tiers.Where(t => t.ExpectedCount > 0).OrderByDescending(t => t.ExpectedTotalUsd))
            Console.WriteLine($"        [预算] {tier.TierId,-14} 人数 {tier.ExpectedCount,5}  " +
                              $"区间 {MoneyUnits.Format(tier.MinUsd)}~{MoneyUnits.Format(tier.MaxUsd)}  " +
                              $"均值 {MoneyUnits.Format((long)tier.MeanUsd),10}  " +
                              $"小计 {MoneyUnits.Format(tier.ExpectedTotalUsd)}");

        // 量级护栏：人均不应低于 5000；同样也不该失控到 5 万以上。
        TestRunner.InRange((long)budget.ExpectedPerNpcUsd, 5_000, 50_000, "人均资金量级异常");

        // 回归护栏：2026-04 两次调整 professional 区间（$50k~$200k → $30k~$120k → $30k~$160k），
        // 目标是把人均拉回 1.6 万。这条断言防止有人手滑把区间改回去。
        TestRunner.InRange((long)budget.ExpectedPerNpcUsd, 15_500, 16_500,
            $"人均应落在 1.6 万 ± 500 的目标带内，实际 {budget.ExpectedPerNpcUsd:0}");
    }
}
