using NationalSpire;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Game;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 适配层：<c>CareerPerson</c> → <see cref="NpcFacts"/> 的投影。
/// <para>
/// 这是整个经济系统唯一能看到原版类型的地方。测试要钉住两件事：
/// ① 字段映射逐项正确（漏一个就等于档位判错）；
/// ② 投影结果能真的驱动档位判定（"能编译"不等于"接对了"）。
/// </para>
/// </summary>
public static class NpcFactsProjectionTests
{
    public static Action[] Cases =>
    [
        ProjectsEveryField,
        ProjectsNullAsNull,
        MissingPersonalityKindBecomesEmptyNotCrash,
        ProjectAllSkipsNullAndDuplicates,
        ProjectAllSkipsPeopleWithoutId,
        HumansAreExcludedFromLedger,
        LedgerPeopleExcludesHumans,
        DeletedPeopleAreStillVisible,
        ProjectionActuallyDrivesTierResolution,
        ProjectionIsNotCachedSoPersonalityEditsShowUp
    ];

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    private static string? _configDir;

    private static ConfigLoadResult RealConfig()
    {
        if (_configDir == null)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "config");
                if (File.Exists(Path.Combine(candidate, ConfigLoader.EconomyFileName)))
                {
                    _configDir = candidate;
                    break;
                }
                directory = directory.Parent;
            }
            if (_configDir == null) throw new Exception("找不到 config/ 目录");
        }
        return new ConfigLoader(_configDir, Silent).Load();
    }

    private static CareerPerson Person(
        string id = "p1",
        string role = "职业选手",
        int rating = 1200,
        int maxAscension = 10,
        string clubId = "c01",
        string kind = "热心同好",
        int kindVersion = 3,
        params string[] identities) => new()
    {
        Id = id,
        Name = "测试选手",
        Role = role,
        Rating = rating,
        MaxAscension = maxAscension,
        ClubId = clubId,
        Identities = [.. identities],
        Personality = new PersonalityProfile { Kind = kind, Version = kindVersion }
    };

    // ── 字段映射 ────────────────────────────────────────────────────

    private static void ProjectsEveryField()
    {
        var facts = NpcFactsProjection.Project(
            Person(id: "p9", role: "世界顶尖", rating: 2100, maxAscension: 20,
                   clubId: "c07", kind: "战绩踩头者", kindVersion: 3, identities: ["前冠军", "主播"]));

        TestRunner.True(facts != null, "不该返回 null");
        TestRunner.Equal("p9", facts!.Id);
        TestRunner.Equal("世界顶尖", facts.Role, "Role 必须透传，档位靠它精确命中");
        TestRunner.Equal(2100, facts.Rating, "Rating 参与分层");
        TestRunner.Equal(20, facts.MaxAscension, "MaxAscension 参与水位线计算");
        TestRunner.Equal("c07", facts.ClubId);
        TestRunner.Equal("战绩踩头者", facts.PersonalityKind, "性格决定经济画像");
        TestRunner.Equal(3, facts.PersonalityVersion, "版本号用于侦测性格被改写");
        TestRunner.Equal(2, facts.Identities.Count, "Identities 是 Role 未命中时的回退依据");
        TestRunner.Contains(string.Join(",", facts.Identities), "前冠军");
    }

    private static void ProjectsNullAsNull()
    {
        TestRunner.True(NpcFactsProjection.Project(null) == null, "null 人应投影成 null");
    }

    private static void MissingPersonalityKindBecomesEmptyNotCrash()
    {
        var person = Person();
        person.Personality = null!;                     // 旧档可能没有这一段
        var facts = NpcFactsProjection.Project(person);

        TestRunner.True(facts != null, "缺少性格段不该让投影失败");
        TestRunner.Equal("", facts!.PersonalityKind, "应回退成空串，由下游走 defaults 画像");
        TestRunner.Equal(0, facts.PersonalityVersion);
    }

    private static void ProjectAllSkipsNullAndDuplicates()
    {
        var people = new List<CareerPerson?>
        {
            Person(id: "a"),
            null,
            Person(id: "b"),
            Person(id: "a")                             // 同 id 重复
        };

        var facts = NpcFactsProjection.ProjectAll(people);
        TestRunner.Equal(2, facts.Count, "应跳过 null 与重复 id");
        TestRunner.Equal("a", facts[0].Id);
        TestRunner.Equal("b", facts[1].Id);
    }

    private static void ProjectAllSkipsPeopleWithoutId()
    {
        var facts = NpcFactsProjection.ProjectAll([Person(id: ""), Person(id: "ok")]);
        TestRunner.Equal(1, facts.Count, "没有 id 的人不能进账本——账本键是 PersonId");
        TestRunner.Equal("ok", facts[0].Id);
    }

    // ── 真人排除 ────────────────────────────────────────────────────

    private static void HumansAreExcludedFromLedger()
    {
        var data = new CareerData { LocalHumanId = "human0", HumanIds = ["human1"] };

        TestRunner.True(NpcFactsProjection.IsHuman(data, "human0"), "本机玩家算真人");
        TestRunner.True(NpcFactsProjection.IsHuman(data, "human1"), "HumanIds 里的人算真人");
        TestRunner.False(NpcFactsProjection.IsHuman(data, "npc1"), "普通 NPC 不是真人");
        TestRunner.False(NpcFactsProjection.IsHuman(data, ""), "空 id 不是真人");
    }

    /// <summary>
    /// 真人<b>不进我们的账本</b>：他们的钱在原版 <c>CareerData.Credits</c> 里，
    /// 由 <see cref="PlayerWallet"/> 管。混进来会出现"同一个人的钱被记两遍"。
    /// </summary>
    private static void LedgerPeopleExcludesHumans()
    {
        var data = new CareerData { LocalHumanId = "human0", HumanIds = ["human1"] };
        data.People =
        [
            Person(id: "human0"),
            Person(id: "human1"),
            Person(id: "npc1"),
            Person(id: "npc2")
        ];

        var facts = NpcFactsProjection.ProjectLedgerPeople(data);
        TestRunner.Equal(2, facts.Count, "只应保留两个 NPC");
        TestRunner.False(facts.Any(f => f.Id.StartsWith("human", StringComparison.Ordinal)),
            "真人绝不能进账本：" + string.Join(",", facts.Select(f => f.Id)));
    }

    private static void DeletedPeopleAreStillVisible()
    {
        var data = new CareerData();
        data.People = [Person(id: "npc1")];
        data.DeletedPeople["npc2"] = Person(id: "npc2");

        var deleted = NpcFactsProjection.ProjectDeletedPeople(data);
        TestRunner.Equal(1, deleted.Count, "被删/退役的人也要能投影出来");
        TestRunner.Equal("npc2", deleted[0].Id);
    }

    // ── 端到端：投影真的能驱动档位判定 ──────────────────────────────

    /// <summary>
    /// "能编译"不等于"接对了"。这条把投影结果喂进真实的档位判定，
    /// 确认字段名/类型都对得上——例如 Role 写错成 Name 就会在这里掉进 default 档。
    /// </summary>
    private static void ProjectionActuallyDrivesTierResolution()
    {
        var seeding = RealConfig().Economy.Seeding;

        var pro = NpcFactsProjection.Project(Person(role: "职业选手"))!;
        var elite = NpcFactsProjection.Project(Person(role: "世界顶尖"))!;
        var unknown = NpcFactsProjection.Project(Person(role: "观众"))!;

        TestRunner.Equal("professional", EconomyTier.ResolveId(pro, seeding),
            "职业选手必须命中 professional 档");
        TestRunner.Equal("elite", EconomyTier.ResolveId(elite, seeding),
            "世界顶尖必须命中 elite 档");
        TestRunner.Equal("default", EconomyTier.ResolveId(unknown, seeding),
            "未收录的 Role 应落到无条件兜底档，而不是抛异常");
    }

    /// <summary>
    /// 投影刻意不做缓存：原版会通过 <c>[Profile: ...]</c> 改写性格，
    /// 缓存一份就会埋下"性格变了但档位没重算"的 bug。
    /// </summary>
    private static void ProjectionIsNotCachedSoPersonalityEditsShowUp()
    {
        var person = Person(kind: "热心同好");
        var before = NpcFactsProjection.Project(person)!;

        person.Personality.Kind = "尖刻挑刺者";
        person.Rating = 9999;
        var after = NpcFactsProjection.Project(person)!;

        TestRunner.Equal("热心同好", before.PersonalityKind, "前置：改动前是原来的性格");
        TestRunner.Equal("尖刻挑刺者", after.PersonalityKind, "性格被改写后投影必须立刻反映出来");
        TestRunner.Equal(9999, after.Rating, "其他字段同样要实时");

        // 与第 3 步的侦测函数串起来：快照过时判定依赖这个实时性。
        var npc = new NpcEconomy { PersonId = "p1", PersonalityKind = "热心同好" };
        TestRunner.False(PersonalityEconomy.IsSnapshotCurrent(npc, after),
            "性格已被改写，快照应判定为过时");
    }
}
