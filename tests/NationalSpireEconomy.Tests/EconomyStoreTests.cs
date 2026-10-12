using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 影子存档的读写层。这一层的每个失败模式都必须有测试钉住，
/// 因为它决定了交易系统会不会把玩家的存档搞坏。
/// </summary>
public static class EconomyStoreTests
{
    public static Action[] Cases =>
    [
        SaveCreatesFileAndLeavesNoTemp,
        RoundTripPreservesNpcsAndOffers,
        SaveRotatesBackupAndRecoversFromIt,
        CorruptFileIsPreservedNotDeleted,
        CorruptFileWithNoBackupRebuildsWithoutCrash,
        MissingFileFallsBackToBackup,
        WriteIntervalIsThrottledButForceBypasses,
        DifferentWorldIdsDoNotBleedIntoEachOther,
        ForeignWorldIdInFileIsRejected,
        NewerSchemaIsRejected,
        IndexTracksWorldsAndArchive,
        ValidationRejectsNegativeBalance,
        ValidationRejectsKeyMismatch,
        ValidationRejectsDanglingOfferReference,
        ValidationRejectsOverlongLedger,
        SaveRefusesFailedSelfCheck,
        SavePrunesBloatedReceiptsInsteadOfRefusing,
        SafeWorldIdSanitizesTraversal,
        ClockStampAdvances
    ];

    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static DelegateEconomyLog Silent => new((_, _, _, _) => { });

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }
        public FixedEconomyClock Clock { get; }

        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "nse-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Clock = new FixedEconomyClock(T0);
        }

        public EconomyStore NewStore() => new(Root, Clock, Silent);

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* 测试清理失败不应让测试失败 */ }
        }
    }

    private static EconomyWorld SampleWorld(string worldId = "abcdef01")
    {
        var world = new EconomyWorld { WorldId = worldId, CreatedAt = T0.ToString("O"), LastWrittenAt = T0.ToString("O") };
        world.Npcs["p0"] = new NpcEconomy
        {
            PersonId = "p0",
            Balance = 120_000,
            TierId = "professional",
            ClubId = "c00",
            PersonalityKind = "战绩踩头者",
            PersonalityVersion = 3,
            Season = 10,
            LastTouchedDay = 700
        };
        world.Npcs["p1"] = new NpcEconomy { PersonId = "p1", Balance = 3_000, TierId = "crowd", Season = 10 };
        world.Offers["off1"] = new TradeOffer
        {
            Id = "off1",
            Kind = TradeKinds.Sponsor,
            Direction = TradeDirections.NpcToPlayer,
            InitiatorId = "p0",
            CounterpartyId = "player",
            Amount = 50_000,
            State = TradeOfferStates.Pending,
            CreatedDay = 700,
            Season = 10,
            ExpiresDay = 714
        };
        world.Receipts.Add("tick:" + worldId + ":700");
        return world;
    }

    private static void SaveCreatesFileAndLeavesNoTemp()
    {
        using var sandbox = new Sandbox();
        var store = sandbox.NewStore();
        var world = SampleWorld();

        TestRunner.True(store.SaveWorld(world), "首次保存应真正写盘");
        string path = store.WorldPath(world.WorldId);
        TestRunner.True(File.Exists(path), "主文件应存在");
        TestRunner.False(File.Exists(path + EconomyStore.TempSuffix), "原子写不应残留 .tmp");
        TestRunner.True(File.Exists(store.IndexPath), "索引应被创建");
    }

    private static void RoundTripPreservesNpcsAndOffers()
    {
        using var sandbox = new Sandbox();
        var store = sandbox.NewStore();
        store.SaveWorld(SampleWorld());

        var reopened = sandbox.NewStore();
        var report = reopened.LoadWorld("abcdef01");
        TestRunner.Equal(WorldLoadStatus.Loaded, report.Status);
        TestRunner.Equal(2, report.World.Npcs.Count);
        TestRunner.Equal(120_000L, report.World.Npcs["p0"].Balance);
        TestRunner.Equal("战绩踩头者", report.World.Npcs["p0"].PersonalityKind);
        TestRunner.Equal(50_000L, report.World.Offers["off1"].Amount);
        TestRunner.Equal(1, report.World.Receipts.Count);
    }

    private static void SaveRotatesBackupAndRecoversFromIt()
    {
        using var sandbox = new Sandbox();
        var store = sandbox.NewStore();
        var world = SampleWorld();
        store.SaveWorld(world, force: true);

        // 第二次保存会把第一份轮换成 .bak
        world.Npcs["p0"].Balance = 999_999;
        store.SaveWorld(world, force: true);
        TestRunner.True(File.Exists(store.WorldBackupPath(world.WorldId)), "第二次保存后应存在备份");

        // 破坏主文件，应从备份恢复，且恢复的金额是"第一次"的那份
        File.WriteAllText(store.WorldPath(world.WorldId), "{ this is not json");

        var reopened = sandbox.NewStore();
        var report = reopened.LoadWorld(world.WorldId);
        TestRunner.Equal(WorldLoadStatus.RecoveredFromBackup, report.Status);
        TestRunner.Equal(120_000L, report.World.Npcs["p0"].Balance);
        TestRunner.True(report.Detail != null && report.Detail.Contains("备份"), "应给出恢复说明");
    }

    private static void CorruptFileIsPreservedNotDeleted()
    {
        using var sandbox = new Sandbox();
        var store = sandbox.NewStore();
        store.SaveWorld(SampleWorld());
        File.WriteAllText(store.WorldPath("abcdef01"), "totally broken");

        var reopened = sandbox.NewStore();
        reopened.LoadWorld("abcdef01");

        var preserved = Directory.GetFiles(store.WorldsDirectory, "*.broken-*");
        TestRunner.Equal(1, preserved.Length, "坏文件必须改名保留，供事后排查");
        TestRunner.Contains(File.ReadAllText(preserved[0]), "totally broken");
    }

    private static void CorruptFileWithNoBackupRebuildsWithoutCrash()
    {
        using var sandbox = new Sandbox();
        var store = sandbox.NewStore();
        // 手写一个坏文件，不经过 SaveWorld，因此没有备份。
        Directory.CreateDirectory(store.WorldsDirectory);
        File.WriteAllText(store.WorldPath("abcdef01"), "{ broken");

        var report = store.LoadWorld("abcdef01");
        TestRunner.Equal(WorldLoadStatus.RebuiltFromScratch, report.Status);
        TestRunner.True(report.DataWasLost, "重建应被标记为数据丢失，供 UI 提示");
        TestRunner.Equal(0, report.World.Npcs.Count);
        TestRunner.Equal("abcdef01", report.World.WorldId);
    }

    private static void MissingFileFallsBackToBackup()
    {
        using var sandbox = new Sandbox();
        var store = sandbox.NewStore();
        store.SaveWorld(SampleWorld(), force: true);
        store.SaveWorld(SampleWorld(), force: true);

        File.Delete(store.WorldPath("abcdef01"));
        var reopened = sandbox.NewStore();
        var report = reopened.LoadWorld("abcdef01");
        TestRunner.Equal(WorldLoadStatus.RecoveredFromBackup, report.Status);
    }

    private static void WriteIntervalIsThrottledButForceBypasses()
    {
        using var sandbox = new Sandbox();
        var store = sandbox.NewStore();
        store.MinimumWriteInterval = TimeSpan.FromSeconds(10);
        var world = SampleWorld();

        TestRunner.True(store.SaveWorld(world), "第一次应写盘");
        TestRunner.False(store.SaveWorld(world), "间隔内的第二次应被节流");

        sandbox.Clock.Advance(TimeSpan.FromSeconds(11));
        TestRunner.True(store.SaveWorld(world), "超过间隔后应恢复写盘");

        sandbox.Clock.Advance(TimeSpan.FromSeconds(-11));
        TestRunner.True(store.SaveWorld(world, force: true), "force 应绕过节流");
    }

    private static void DifferentWorldIdsDoNotBleedIntoEachOther()
    {
        using var sandbox = new Sandbox();
        var store = sandbox.NewStore();

        var first = SampleWorld("worldaaa");
        first.Npcs["p0"].Balance = 111;
        store.SaveWorld(first, force: true);

        var second = SampleWorld("worldbbb");
        second.Npcs["p0"].Balance = 222;
        store.SaveWorld(second, force: true);

        var reopened = sandbox.NewStore();
        TestRunner.Equal(111L, reopened.LoadWorld("worldaaa").World.Npcs["p0"].Balance);
        TestRunner.Equal(222L, reopened.LoadWorld("worldbbb").World.Npcs["p0"].Balance);
        TestRunner.NotEqual(reopened.WorldPath("worldaaa"), reopened.WorldPath("worldbbb"));
    }

    private static void ForeignWorldIdInFileIsRejected()
    {
        using var sandbox = new Sandbox();
        var store = sandbox.NewStore();
        // 把 A 的账本内容塞进 B 的文件名里，模拟手工拷错文件。
        string json = System.Text.Json.JsonSerializer.Serialize(SampleWorld("worldaaa"));
        Directory.CreateDirectory(store.WorldsDirectory);
        File.WriteAllText(store.WorldPath("worldbbb"), json);

        var report = store.LoadWorld("worldbbb");
        TestRunner.Equal(WorldLoadStatus.RebuiltFromScratch, report.Status);
        TestRunner.Equal(0, report.World.Npcs.Count);
        TestRunner.Equal("worldbbb", report.World.WorldId);
    }

    private static void NewerSchemaIsRejected()
    {
        using var sandbox = new Sandbox();
        var store = sandbox.NewStore();
        var world = SampleWorld();
        world.SchemaVersion = EconomyWorld.CurrentSchemaVersion + 1;
        string json = System.Text.Json.JsonSerializer.Serialize(world);

        Directory.CreateDirectory(store.WorldsDirectory);
        File.WriteAllText(store.WorldPath(world.WorldId), json);

        var report = store.LoadWorld(world.WorldId);
        TestRunner.Equal(WorldLoadStatus.RebuiltFromScratch, report.Status);
    }

    private static void IndexTracksWorldsAndArchive()
    {
        using var sandbox = new Sandbox();
        var store = sandbox.NewStore();
        store.SaveWorld(SampleWorld("worldaaa"), force: true);
        store.SetActiveWorld("worldaaa");

        var index = store.LoadIndex();
        TestRunner.Equal("worldaaa", index.ActiveWorldId);
        TestRunner.True(index.Find("worldaaa") != null);

        store.MarkArchived("worldaaa");
        var after = store.LoadIndex();
        TestRunner.True(after.Find("worldaaa")!.Archived, "归档标记应写入索引");
        TestRunner.True(string.IsNullOrEmpty(after.ActiveWorldId), "归档后不应再是现役世界");
    }

    private static void ValidationRejectsNegativeBalance()
    {
        var world = SampleWorld();
        world.Npcs["p0"].Balance = -1;
        var problems = EconomyStore.Validate(world);
        TestRunner.True(problems.Any(p => p.Contains("余额为负")), "负余额必须被拒绝：" + string.Join("；", problems));
    }

    private static void ValidationRejectsKeyMismatch()
    {
        var world = SampleWorld();
        world.Npcs["wrongkey"] = new NpcEconomy { PersonId = "pX", Balance = 10 };
        var problems = EconomyStore.Validate(world);
        TestRunner.True(problems.Any(p => p.Contains("与字典键不一致")), "键值不一致必须被拒绝");
    }

    private static void ValidationRejectsDanglingOfferReference()
    {
        var world = SampleWorld();
        world.Offers["off1"].CounterpartyId = "ghost";
        var problems = EconomyStore.Validate(world);
        TestRunner.True(problems.Any(p => p.Contains("不存在的人物 ghost")), "悬空引用必须被拒绝");
    }

    private static void ValidationRejectsOverlongLedger()
    {
        var world = SampleWorld();
        for (int i = 0; i < NpcEconomy.MaxLedgerEntries + 5; i++)
            world.Npcs["p0"].Ledger.Add(new LedgerEntry { Id = "l" + i, Amount = 1 });
        var problems = EconomyStore.Validate(world);
        TestRunner.True(problems.Any(p => p.Contains("超过上限")), "超长流水必须被拒绝");
    }

    private static void SaveRefusesFailedSelfCheck()
    {
        using var sandbox = new Sandbox();
        var store = sandbox.NewStore();
        var world = SampleWorld();
        world.Npcs["p0"].Balance = -500;

        TestRunner.Throws<InvalidDataException>(() => store.SaveWorld(world, force: true));
        TestRunner.False(File.Exists(store.WorldPath(world.WorldId)),
            "自检失败时不应留下任何文件，原档必须保持原样");
    }

    /// <summary>
    /// <b>真机事故回归（2026-10-11 20:0x）</b>：收据膨胀到 8000+ 时自检会拒绝写入，
    /// 而真机后果是"影子存档永远停在播种那天"（内存里已到第 398 天，磁盘 lastTickedDay 还是 0）。
    /// <para>
    /// 现在 <c>SaveWorld</c> 必须在自检<b>之前</b>兜底裁剪：任何来源的收据膨胀都不该
    /// 让整个世界写不出去。裁剪只动幂等收据，不碰余额与水位线。
    /// </para>
    /// </summary>
    private static void SavePrunesBloatedReceiptsInsteadOfRefusing()
    {
        using var sandbox = new Sandbox();
        var store = sandbox.NewStore();
        var world = SampleWorld();

        // 造出真机那种膨胀量（远高于自检阈值 512×2）。
        for (int i = 0; i < EconomyWorld.MaxReceipts * 4; i++) world.Receipts.Add("bloat:" + i);
        TestRunner.True(EconomyStore.Validate(world).Count > 0,
            "前置：不裁剪时自检必须报出收据膨胀（否则这条用例没测到东西）");

        bool saved = store.SaveWorld(world, force: true);
        TestRunner.True(saved, "收据膨胀必须被兜底裁剪后写出，而不是拒绝写入");
        TestRunner.True(world.Receipts.Count <= EconomyWorld.MaxReceipts,
            $"落盘前必须裁到上限内，实际 {world.Receipts.Count}");
        TestRunner.Equal(0, EconomyStore.Validate(world).Count, "裁剪之后必须通过自检");

        // 磁盘上的那一份也必须是收敛的（换一个 store 从文件读回来验证）。
        var reopened = sandbox.NewStore();
        var report = reopened.LoadWorld(world.WorldId);
        TestRunner.Equal(WorldLoadStatus.Loaded, report.Status);
        TestRunner.True(report.World.Receipts.Count <= EconomyWorld.MaxReceipts,
            $"磁盘上的收据也必须在上限内，实际 {report.World.Receipts.Count}");
        Console.WriteLine($"      · 兜底裁剪后收据 {world.Receipts.Count} 条（上限 {EconomyWorld.MaxReceipts}），落盘成功");
    }

    private static void SafeWorldIdSanitizesTraversal()
    {
        // "../../evil" 共 6 个非法字符（3 个点 + 3 个斜杠），全部换成下划线。
        // 这一条是防目录穿越的关键：结果里不能再出现 '.' 或路径分隔符。
        string sanitized = EconomyStore.SafeWorldId("../../evil");
        TestRunner.Equal("______evil", sanitized);
        TestRunner.NotContains(sanitized, ".");
        TestRunner.NotContains(sanitized, "/");
        TestRunner.NotContains(sanitized, "\\");

        TestRunner.Equal("abc123", EconomyStore.SafeWorldId("abc123"));
        TestRunner.Equal("a-b_c", EconomyStore.SafeWorldId("a-b_c"));
        TestRunner.Throws<ArgumentException>(() => EconomyStore.SafeWorldId("  "));
    }

    private static void ClockStampAdvances()
    {
        var clock = new FixedEconomyClock(T0);
        TestRunner.Equal(T0, clock.UtcNow);
        clock.Advance(TimeSpan.FromHours(3));
        TestRunner.Equal(T0.AddHours(3), clock.UtcNow);
    }
}
