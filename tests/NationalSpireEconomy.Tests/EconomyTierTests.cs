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

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    private static SeedingConfig Seeding()
    {
        return new ConfigLoader(TestConfig.Directory(), Silent).Load().Economy.Seeding;
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

    /// <summary>
    /// Role 认不出时，<b>IdentitiesContains 要能顶上</b>。
    /// <para>
    /// <b>这条用例原先只断言"结果非空"</b> —— 任何非空字符串都能通过，等于什么都没验，
    /// 而用例名承诺的是"Identities 被用到"。改成可证伪的写法后立刻查出真相：
    /// <b>交付配置里根本没有任何档位使用 <c>identitiesContains</c></b>，只有 <c>roles</c>，
    /// 所以那条判定分支在生产里是**死的**（代码支持、配置没用）。
    /// </para>
    /// <para>
    /// 因此这里用一个<b>自造的档位表</b>直接验"能力"（这也是 MoreSpecificTierWinsOverGeneric
    /// 等用例的既有做法），而不是假装交付配置会用上它。同时保留一条对照，
    /// 防止"解析器永远返回非兜底档"造成假通过。
    /// </para>
    /// </summary>
    private static void IdentitiesIsUsedWhenRoleMisses()
    {
        // 自造一张只用 IdentitiesContains 的档位表 —— 交付配置没有这种档位，
        // 所以只有造出来才能真正验到这条分支。
        var seeding = new SeedingConfig
        {
            Tiers =
            [
                new SeedTier { Id = "default", Min = 1, Max = 10, Weight = 1 },
                new SeedTier { Id = "byIdentity", IdentitiesContains = ["教练"], Min = 100, Max = 200, Weight = 1 }
            ]
        };

        // ① Role 认不出，但 Identities 带着"教练" ⇒ 必须按 Identities 命中。
        var withIdentity = new NpcFacts("x", "不明身份", Identities: ["教练", "退役选手"]);
        TestRunner.Equal("byIdentity", EconomyTier.ResolveId(withIdentity, seeding),
            "★Role 认不出时，IdentitiesContains 必须能命中");

        // ② 对照组：同样的未知 Role，但 Identities 不含"教练" ⇒ 必须落兜底档。
        //    没有它，第 ① 条可能因为"解析器压根不返回兜底档"而假通过。
        var otherIdentity = new NpcFacts("y", "不明身份", Identities: ["路人甲"]);
        TestRunner.Equal(EconomySeed.FallbackTierId, EconomyTier.ResolveId(otherIdentity, seeding),
            "对照组：Identities 对不上时必须落兜底档");

        // ③ 交付配置的事实：它只用 roles，没有任何 identitiesContains —— 把它钉住，
        //    免得有人以为"配置已经覆盖了身份匹配"。改配置时这条会提醒你一并更新文档。
        var shipped = Seeding();
        bool shippedUsesIdentities = shipped.Tiers.Any(t => t.IdentitiesContains.Count > 0);
        TestRunner.False(shippedUsesIdentities,
            "交付配置目前不使用 identitiesContains（若哪天用上了，请更新本用例与 docs 的说明）");
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
