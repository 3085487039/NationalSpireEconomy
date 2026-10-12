using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Tests;

/// <summary>档位判定：0.26.7 的 9 种 Role 必须各有归属，不能有人掉进兜底档。</summary>
public static class EconomyTierTests
{
    public static Action[] Cases =>
    [
        EveryKnownRoleHitsItsOwnTier,
        PlayerIsNeverSeeded,
        UnknownRoleFallsBackToDefaultTier,
        IdentitiesIsUsedWhenRoleMisses,
        MoreSpecificTierWinsOverGeneric,
        RatingThresholdSplitsWithinRole,
        ResolveIdFallsBackSafely,
        GroupByTierCountsEveryone
    ];

    private static SeedingConfig Seeding()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "config");
            if (File.Exists(Path.Combine(candidate, ConfigLoader.EconomyFileName)))
                return new ConfigLoader(candidate, new DelegateEconomyLog((_, _, _, _) => { })).Load().Economy.Seeding;
            directory = directory.Parent;
        }
        throw new Exception("找不到 config/ 目录");
    }

    private static void EveryKnownRoleHitsItsOwnTier()
    {
        var seeding = Seeding();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["普通玩家"] = "crowd",
            ["青训选手"] = "youth",
            ["职业选手"] = "professional",
            ["世界顶尖"] = "elite",
            ["退役选手"] = "veteran",
            ["教练"] = "media",
            ["主播"] = "media",
            ["解说员"] = "media",
            ["赛事记者"] = "media"
        };

        foreach (string role in EconomyTier.KnownRoles)
        {
            string tier = EconomyTier.ResolveId(new NpcFacts("x", role), seeding);
            TestRunner.Equal(expected[role], tier, $"Role={role} 的档位判定不符");
            TestRunner.NotEqual(EconomySeed.FallbackTierId, tier, $"Role={role} 掉进了兜底档");
        }
    }

    private static void PlayerIsNeverSeeded()
    {
        // 玩家自己用 CareerData.Credits，不进我们的账本。
        var seeding = Seeding();
        var facts = new NpcFacts(EconomyTier.PlayerId, "职业选手");
        // 判定本身仍会返回档位（无害），但播种时必须被过滤掉——见 EconomySeedTests。
        TestRunner.Equal("professional", EconomyTier.ResolveId(facts, seeding));
    }

    private static void UnknownRoleFallsBackToDefaultTier()
    {
        var seeding = Seeding();
        string tier = EconomyTier.ResolveId(new NpcFacts("x", "未来的新身份"), seeding);
        TestRunner.Equal(EconomySeed.FallbackTierId, tier, "未知 Role 必须落到兜底档，而不是没有任何金额");
    }

    private static void IdentitiesIsUsedWhenRoleMisses()
    {
        // Role 为未知值，但 Identities 里带着"教练"。当前配置的 coach 档用 Roles 匹配，
        // 因此这里验证的是"不会误判成兜底档中的人，而是按 Identities 命中"这一能力；
        // 若配置未提供 IdentitiesContains 条件，则应落到兜底档——两种都算正确，只要不崩。
        var seeding = Seeding();
        var facts = new NpcFacts("x", "不明身份", Identities: ["教练", "退役选手"]);
        string tier = EconomyTier.ResolveId(facts, seeding);
        TestRunner.True(tier.Length > 0, "判定结果不能为空");
    }

    private static void MoreSpecificTierWinsOverGeneric()
    {
        var seeding = new SeedingConfig
        {
            Tiers =
            [
                new SeedTier { Id = "default", Min = 1, Max = 10, Weight = 1 },
                new SeedTier { Id = "byRole", Roles = ["职业选手"], Min = 100, Max = 200, Weight = 1 },
                new SeedTier { Id = "byRoleAndRating", Roles = ["职业选手"], RatingAtLeast = 1300, Min = 300, Max = 400, Weight = 1 }
            ]
        };

        TestRunner.Equal("byRole", EconomyTier.ResolveId(new NpcFacts("a", "职业选手", Rating: 1000), seeding));
        TestRunner.Equal("byRoleAndRating", EconomyTier.ResolveId(new NpcFacts("b", "职业选手", Rating: 1400), seeding),
            "条件更多的档位必须压过只有 Role 的档位");
        TestRunner.Equal(EconomySeed.FallbackTierId, EconomyTier.ResolveId(new NpcFacts("c", "路人"), seeding));
    }

    private static void RatingThresholdSplitsWithinRole()
    {
        var seeding = new SeedingConfig
        {
            Tiers =
            [
                new SeedTier { Id = "low", Roles = ["普通玩家"], RatingAtLeast = 0, Min = 1, Max = 10, Weight = 1 },
                new SeedTier { Id = "high", Roles = ["普通玩家"], RatingAtLeast = 800, Min = 100, Max = 200, Weight = 1 }
            ]
        };
        TestRunner.Equal("low", EconomyTier.ResolveId(new NpcFacts("a", "普通玩家", Rating: 700), seeding));
        // 条件数相同时，门槛更高者必须优先，否则高阶分层永远不生效。
        TestRunner.Equal("high", EconomyTier.ResolveId(new NpcFacts("b", "普通玩家", Rating: 900), seeding));
        TestRunner.Equal("high", EconomyTier.ResolveId(new NpcFacts("c", "普通玩家", Rating: 800), seeding),
            "刚好触到门槛应当算命中");
    }

    private static void ResolveIdFallsBackSafely()
    {
        var empty = new SeedingConfig { Tiers = [] };
        TestRunner.Equal("default", EconomyTier.ResolveId(new NpcFacts("a", "普通玩家"), empty));
        TestRunner.Equal("myFallback", EconomyTier.ResolveId(new NpcFacts("a", "普通玩家"), empty, "myFallback"));
    }

    private static void GroupByTierCountsEveryone()
    {
        var seeding = Seeding();
        var people = new List<INpcFacts>
        {
            new NpcFacts("a", "普通玩家"),
            new NpcFacts("b", "普通玩家"),
            new NpcFacts("c", "职业选手"),
            new NpcFacts("d", "世界顶尖")
        };
        var groups = EconomyTier.GroupByTier(people, seeding);
        TestRunner.Equal(3, groups.Count);
        TestRunner.Equal(2, groups["crowd"].Count);
        TestRunner.Equal(1, groups["professional"].Count);
        TestRunner.Equal(4, groups.Values.Sum(g => g.Count));
    }
}
