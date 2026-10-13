using System.Reflection;
using System.Text.RegularExpressions;
using NationalSpire;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Game;
using NationalSpireEconomy.Inject;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 阶段 3 的接线层契约：影子存档的生命周期（播种 / 推进 / 落盘）、
/// S 段注入补丁的挂载点与行为、以及启动期自证<b>真的会失败</b>（反向控制）。
/// <para>
/// 这里刻意走"离线单测"路线：不诱导游戏里的 AI 输出任何标记，
/// 所有断言都是确定性的输入输出。
/// </para>
/// </summary>
public static class InjectWiringTests
{
    /// <summary>
    /// <b>顺序有讲究</b>：前两条与"进程内第一次调用"有关，必须排在最前面——
    /// <c>PrivateMessagePatch</c> 的"首次一定说话"是<b>一次性</b>状态，
    /// 一旦被别的用例消耗掉，这两条就再也验证不到了。
    /// </summary>
    public static Action[] Cases =>
    [
        FirstBailPathSpeaksOnFirstCall,
        ComposeHintOnlyBeforeAnyComposeCall,
        RetiredComposeHintNoLongerCriesWolf,
        ComposePostfixInjectsStableBlock,
        ComposePostfixInjectsVolatileSegment,
        ComposePostfixKeepsMessageCountAndOrder,
        ComposePostfixSkipsHumans,
        ComposePostfixToleratesNullArguments,
        ComposePostfixHonorsMasterSwitch,
        RuntimeSeedsAndPersists,
        RuntimeAdvancePaysSalaryOnceAndIsIdempotent,
        RuntimeAdvanceEmitsTickLine,
        RuntimeSkipsHumansAndUnknownPeople,
        DayPatchTargetIsMoveToNextDay,
        AdvancePostfixToleratesNullData,
        InjectDiagnosticsSelfProofPasses,
        InjectDiagnosticsVolatileSelfProofPasses,
        MountPointsResolveInDeployedBinary,
        HookProbeResolvesAllFourTargets,
        HookProbeSummaryFormatIsStable,
        StableProofActuallyFailsOnDigitTemplate,
        VolatileProofFailsWithoutBalancePlaceholder,
        DisabledInitLeavesRuntimeNull
    ];

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    /// <summary>被捕获的控制台行。前两条用例会把它接到补丁上。</summary>
    private static readonly List<string> ConsoleLines = [];

    private const string NpcId = "npc-1";
    private const string ProbeWorldId = "wire-world";

    // ── 影子存档生命周期 ────────────────────────────────────────────

    private static void RuntimeSeedsAndPersists()
    {
        var config = LoadReal();
        string root = TempRoot();
        var store = new EconomyStore(root, null, Silent);
        var runtime = new ShadowEconomyRuntime(store, config, Silent);

        var world = runtime.EnsureWorld(ProbeWorldId, Facts());

        TestRunner.True(world != null, "应能建立自证世界");
        TestRunner.Equal(2, world!.Npcs.Count, "两个人应各得到一份初始资金");
        TestRunner.True(world.Npcs.Values.All(n => n.Balance > 0), "播种金额必须为正");
        TestRunner.True(world.Npcs.Values.All(n => n.TierId.Length > 0), "每个人都应有档位 id");
        TestRunner.True(File.Exists(store.WorldPath(ProbeWorldId)), "播种之后应立刻落盘");

        // 再问一次：不该重复播种，也不该换掉已有的钱。
        long before = world.Npcs[NpcId].Balance;
        runtime.EnsureWorld(ProbeWorldId, Facts());
        TestRunner.Equal(2, world.Npcs.Count, "重复 EnsureWorld 不该多出人来");
        TestRunner.Equal(before, world.Npcs[NpcId].Balance, "重复播种不该改动已有余额");
    }

    private static void RuntimeAdvancePaysSalaryOnceAndIsIdempotent()
    {
        var config = LoadReal();
        var store = new EconomyStore(TempRoot(), null, Silent);
        var runtime = new ShadowEconomyRuntime(store, config, Silent);

        var world = runtime.EnsureWorld(ProbeWorldId, Facts())!;
        long before = world.Npcs[NpcId].Balance;
        int dueDay = EconomyTick.SalaryDueDay(config.Economy, EconomyTick.FirstSeason);
        long expected = EconomyTick.SalaryFor(config.Economy, "professional");

        // 第 1 赛季的工资在【赛季末】结算：结算日之前一分钱都不发。
        var early = runtime.AdvanceTo(ProbeWorldId, Facts(), dueDay - 1);
        TestRunner.Equal(0, early.SalariesPaid, "第 1 赛季结算日之前不该发薪");
        TestRunner.Equal(before, world.Npcs[NpcId].Balance, "结算日之前余额必须原封不动");

        // 结算日当天：两个人各发一次工资。
        var settle = runtime.AdvanceTo(ProbeWorldId, Facts(), dueDay);
        TestRunner.Equal(dueDay, world.LastTickedDay, "水位线应推进到第 1 赛季结算日");
        TestRunner.Equal(2, settle.SalariesPaid, "两个人各发一次工资");
        TestRunner.Equal(before + expected, world.Npcs[NpcId].Balance, "工资应经记账器入账");

        // 幂等：同一天、更早的日期都不该再动一分钱。
        var repeat = runtime.AdvanceTo(ProbeWorldId, Facts(), dueDay);
        var earlier = runtime.AdvanceTo(ProbeWorldId, Facts(), 2);

        TestRunner.Equal(0, repeat.SalariesPaid, "重复推进同一天不该再发薪");
        TestRunner.Equal(0, earlier.SalariesPaid, "推进到更早的日期不该发生任何事");
        TestRunner.Equal(before + expected, world.Npcs[NpcId].Balance, "余额必须保持不变");

        // 跨赛季：结算日的次日就是第 2 赛季第 1 天，应触发赛季滚动并按赛季初发薪。
        var season = runtime.AdvanceTo(ProbeWorldId, Facts(), dueDay + 1);
        TestRunner.True(season.SeasonChanged, "推进过赛季边界应触发跨赛季");
        TestRunner.Equal(dueDay + 1, world.LastTickedDay);
    }

    private static void RuntimeAdvanceEmitsTickLine()
    {
        var config = LoadReal();
        var store = new EconomyStore(TempRoot(), null, Silent);
        var lines = new List<string>();
        var runtime = new ShadowEconomyRuntime(store, config, Silent, (line, _) => lines.Add(line));
        int dueDay = EconomyTick.SalaryDueDay(config.Economy, EconomyTick.FirstSeason);

        runtime.EnsureWorld(ProbeWorldId, Facts());
        runtime.AdvanceTo(ProbeWorldId, Facts(), dueDay);

        string tick = lines.FirstOrDefault(l => l.StartsWith("[Economy-Tick]", StringComparison.Ordinal)) ?? "";
        TestRunner.True(tick.Length > 0, "推进必须留下 [Economy-Tick] 记录，实际日志：" + string.Join(" / ", lines));
        TestRunner.Contains(tick, $"第 0 天 → 第 {dueDay} 天", "要写清从哪一天推进到哪一天");
        TestRunner.Contains(tick, $"推进 {dueDay} 天", "要写清推进了几天");
        TestRunner.Contains(tick, "发薪 2 人", "发薪人数要出现在同一行里");
        TestRunner.NotContains(tick, "跨赛季",
            "新世界的第一次推进不是跨赛季（水位线从 0 初始化到第 1 赛季），不该报跨赛季");

        // 真的跨赛季时必须报出来：第 2 赛季第 1 天紧跟在第 1 赛季末结算日之后。
        runtime.AdvanceTo(ProbeWorldId, Facts(), dueDay + 1);
        string rollover = lines.LastOrDefault(l => l.Contains("跨赛季", StringComparison.Ordinal)) ?? "";
        TestRunner.True(rollover.Length > 0, "越过赛季边界必须打出跨赛季记录，实际：" + string.Join(" / ", lines));
        TestRunner.Contains(rollover, "跨赛季 第 1 → 第 2 赛季", "要写清是从哪个赛季跨到哪个赛季");
    }

    private static void RuntimeSkipsHumansAndUnknownPeople()
    {
        var config = LoadReal();
        var store = new EconomyStore(TempRoot(), null, Silent);
        var runtime = new ShadowEconomyRuntime(store, config, Silent);

        var data = new CareerData { WorldId = "wire-humans", Day = 1, Season = 1 };
        data.HumanIds.Add("human-1");
        data.LocalHumanId = "human-1";

        var human = new CareerPerson { Id = "human-1", Name = "玩家本人", Role = "职业选手" };
        // 刻意【不】放进 data.People：账本里没有的人也不该被注入。
        var outsider = new CareerPerson { Id = "nobody", Name = "不在账本里的人", Role = "普通玩家" };
        var npc = NewPerson(NpcId, "职业选手", "热心同好");
        data.People.AddRange([human, npc]);

        var world = runtime.WorldFor(data)!;

        TestRunner.True(!world.Npcs.ContainsKey("human-1"), "真人绝不能进我们的账本（他的钱在 Credits 里）");
        TestRunner.True(world.Npcs.ContainsKey(NpcId), "普通 NPC 应被播种");

        TestRunner.True(runtime.BuildInput(data, human, world) == null, "真人不应被注入经济信息");
        TestRunner.True(runtime.BuildInput(data, outsider, world) == null, "不在账本里的人生成输入时必须为 null");

        var input = runtime.BuildInput(data, npc, world);
        TestRunner.True(input != null, "账本里的人应能生成注入输入");
        TestRunner.Equal(NpcId, input!.Npc.PersonId, "注入输入必须指向正确的人");
        TestRunner.True(input.TierId.Length > 0, "注入输入必须带档位 id");
    }

    // ── S 段注入补丁 ────────────────────────────────────────────────

    private static void ComposePostfixInjectsStableBlock()
    {
        var state = SetupMod(out _);
        var (data, conversation) = ConversationFixture("wire-inject");

        var messages = OriginalMessages();
        int before = messages.Count;

        PrivateMessagePatch.ComposePostfix(messages, data, conversation);

        TestRunner.Equal(before, messages.Count, "绝不能新增 message");
        string system = messages[0]["content"];
        TestRunner.Contains(system, "【经济状况】", "system 里应出现稳定段标题");
        TestRunner.Contains(system, SystemPromptInsert.DefaultAnchor, "原版收尾句必须保留");
        TestRunner.True(
            system.IndexOf("【经济状况】", StringComparison.Ordinal)
            < system.IndexOf(SystemPromptInsert.DefaultAnchor, StringComparison.Ordinal),
            "稳定段必须插在收尾句之前");
        TestRunner.False(Regex.IsMatch(system, @"\d"), "system 正文不得出现数字（含稳定段）：<" + system + ">");
        TestRunner.Equal(InsertPlacements.BeforeAnchor, PrivateMessagePatch.LastPlacement);
        TestRunner.True(PrivateMessagePatch.LastMessageCountUnchanged, "补丁应自证 message 条数不变");
        TestRunner.True(PrivateMessagePatch.LastStableLength > 0, "应记下稳定段长度");
        TestRunner.Equal(8, PrivateMessagePatch.LastFingerprint.Length, "应记下 8 位指纹");
        TestRunner.True(state.Enabled, "前置：模组应处于启用状态");
    }

    private static void ComposePostfixKeepsMessageCountAndOrder()
    {
        SetupMod(out _);
        var (data, conversation) = ConversationFixture("wire-order");

        var messages = OriginalMessages();
        string[] roles = messages.Select(m => m["role"]).ToArray();

        PrivateMessagePatch.ComposePostfix(messages, data, conversation);

        TestRunner.Equal(string.Join(",", roles), string.Join(",", messages.Select(m => m["role"])),
            "message 顺序必须与原版一致");
        TestRunner.NotContains(messages[1]["content"], "【经济状况】", "不能碰 user 消息");
        TestRunner.True(messages.Count == roles.Length, "条数不变");
    }

    private static void ComposePostfixSkipsHumans()
    {
        SetupMod(out _);

        var data = new CareerData { WorldId = "wire-human-inject", Day = 5, Season = 1 };
        data.HumanIds.Add("human-1");
        data.LocalHumanId = "human-1";
        data.People.Add(new CareerPerson { Id = "human-1", Name = "玩家本人", Role = "职业选手" });

        var conversation = new PrivateConversation { PersonId = "human-1" };
        var messages = OriginalMessages();
        string systemBefore = messages[0]["content"];

        PrivateMessagePatch.ComposePostfix(messages, data, conversation);

        TestRunner.Equal(systemBefore, messages[0]["content"], "真人聊天不该被注入任何经济信息");
    }

    private static void ComposePostfixToleratesNullArguments()
    {
        SetupMod(out _);
        var messages = OriginalMessages();
        string before = messages[0]["content"];

        // Harmony 参数按位注入，注入不到时会是 null——那时必须原样返回，而不是抛异常。
        PrivateMessagePatch.ComposePostfix(null, null, null);
        PrivateMessagePatch.ComposePostfix(messages, null, null);
        PrivateMessagePatch.ComposePostfix(messages, new CareerData(), null);

        TestRunner.Equal(before, messages[0]["content"], "参数缺失时一个字节都不该改");
    }

    private static void ComposePostfixHonorsMasterSwitch()
    {
        // 总开关关掉：injection.stableEnabled = false
        string config = CopyRealConfig(TempRoot());
        string economyPath = Path.Combine(config, ConfigLoader.EconomyFileName);
        string text = File.ReadAllText(economyPath);
        text = text.Replace("\"stableEnabled\": true", "\"stableEnabled\": false");
        File.WriteAllText(economyPath, text);

        var state = EconomyModEntry.Initialize(Silent, TempRoot(), config);
        TestRunner.True(state.Enabled, "关掉注入不该让模组无法启用：" + state.Describe());
        TestRunner.False(state.Economy!.Injection.StableEnabled, "前置：总开关应为 false");

        var (data, conversation) = ConversationFixture("wire-switch");
        var messages = OriginalMessages();
        string before = messages[0]["content"];

        PrivateMessagePatch.ComposePostfix(messages, data, conversation);

        TestRunner.Equal(before, messages[0]["content"], "总开关关闭时不该注入稳定段");
    }

    // ── 打点：首次调用一定说话 ──────────────────────────────────────

    /// <summary>
    /// <b>阶段 3-A 的实际问题</b>：补丁装上了，但所有提前返回都是静默的，于是日志里查不到任何动态痕迹，
    /// 分不清"补丁没被调用"和"被调用了但条件不满足"。这条钉住"第一次调用无论走到哪一步都要说话，并带原因"。
    /// </summary>
    private static void FirstBailPathSpeaksOnFirstCall()
    {
        SetupMod(out _);   // 它会把补丁的控制台通道接到 ConsoleLines

        // 真人：BuildInput 会返回 null，正好走"提前返回"这条最容易静默的路。
        var data = new CareerData { WorldId = "wire-bail", Day = 12, Season = 1 };
        data.HumanIds.Add("human-1");
        data.LocalHumanId = "human-1";
        data.People.Add(new CareerPerson { Id = "human-1", Name = "玩家本人", Role = "职业选手" });

        int before = PrivateMessagePatch.Invocations;
        var messages = OriginalMessages();
        string snapshot = string.Join("\u0001", messages.Select(m => m["content"]));

        PrivateMessagePatch.ComposePostfix(messages, data, new PrivateConversation { PersonId = "human-1" });

        TestRunner.Equal(before + 1, PrivateMessagePatch.Invocations, "补丁必须被调用到");
        TestRunner.Equal(snapshot, string.Join("\u0001", messages.Select(m => m["content"])),
            "提前返回时一个字节都不该改");

        string line = ConsoleLines.FirstOrDefault(l => l.Contains("首次实测注入", StringComparison.Ordinal)) ?? "";
        TestRunner.True(line.Length > 0, "第一次调用必须留下痕迹，实际捕获：" + string.Join(" / ", ConsoleLines));
        TestRunner.Contains(line, "未注入", "要说清这次没有注入");
        TestRunner.Contains(line, "原因", "要带原因，否则还是分不清为什么");
        TestRunner.Contains(line, "human-1", "原因里要点出具体的人");
    }

    private static void ComposeHintOnlyBeforeAnyComposeCall()
    {
        // 纯函数：不依赖"进程里还没发生过注入"这种顺序前提。
        // patchRetired 显式给 false，所以这条断言与进程状态无关。
        TestRunner.Contains(
            ShadowEconomyRuntime.ComposeHintFor(ShadowEconomyRuntime.TickSourceGame, 0,
                patchApplied: true, patchRetired: false),
            "私信组装补丁 0 次", "装了却一次没被调用 ⇒ 才是真的该提醒（阶段 3-A 那个事故的形状）");
        TestRunner.Equal("",
            ShadowEconomyRuntime.ComposeHintFor(ShadowEconomyRuntime.TickSourceGame, 1,
                patchApplied: true, patchRetired: false),
            "命中过一次之后就该闭嘴，不能长期刷日志");
        TestRunner.Equal("",
            ShadowEconomyRuntime.ComposeHintFor(ShadowEconomyRuntime.TickSourceSelfCheck, 0,
                patchApplied: true, patchRetired: false),
            "启动自证不带这句噪音");
    }

    /// <summary>
    /// <b>补丁按设计退场之后，那句"挂载点没被调用"就是永久的假话。</b>
    /// <para>
    /// 方案 A 接管后旧的 Compose 注入补丁被 <c>MarkRetired</c> 停用，
    /// 而 <c>Invocations</c> 只由它递增 ⇒ 计数恒为 0，于是这句提醒会**每推进一天刷一次**，
    /// 把接手者往"挂载点坏了"的方向引（真相是它已按设计退场）。
    /// </para>
    /// <para>
    /// 这条用例直接钉住"退场后闭嘴"，并保留"没退场 + 零命中 ⇒ 仍然要吵"作为对照 ——
    /// 否则把整句删掉也能让本用例通过，那会丢掉真正的告警能力。
    /// </para>
    /// </summary>
    private static void RetiredComposeHintNoLongerCriesWolf()
    {
        // 已退场（IsRetired=true）⇒ 不该再出现。
        TestRunner.Equal("",
            ShadowEconomyRuntime.ComposeHintFor(ShadowEconomyRuntime.TickSourceGame, 0,
                patchApplied: false, patchRetired: true),
            "★退场之后这句提醒必须消失 —— 它是永真的假话，会把排查带偏");

        // 对照：补丁还挂着、却一次都没命中 ⇒ 仍然必须吵（这才是阶段 3-A 的真实故障形状）。
        TestRunner.Contains(
            ShadowEconomyRuntime.ComposeHintFor(ShadowEconomyRuntime.TickSourceGame, 0,
                patchApplied: true, patchRetired: false),
            "挂载点没被调用", "对照组：装了没命中仍然要提醒");
    }

    // ── V 段（易变段） ──────────────────────────────────────────────

    /// <summary>
    /// V 段落进 <c>ordered.Current</c> 那条 user 消息：<b>不新增 message</b>、
    /// system 保持零数字、玩家那条消息一字不动。
    /// </summary>
    private static void ComposePostfixInjectsVolatileSegment()
    {
        SetupMod(out _);
        var (data, conversation) = ConversationFixture("wire-volatile");

        var messages = OriginalMessages();
        int before = messages.Count;
        string profileBefore = messages[1]["content"];
        string playerBefore = messages[3]["content"];

        PrivateMessagePatch.ComposePostfix(messages, data, conversation);

        TestRunner.Equal(before, messages.Count, "绝不能新增 message");
        TestRunner.Equal(VolatilePlacements.Appended, PrivateMessagePatch.LastVolatilePlacement, "落点应是「追加」");
        TestRunner.Equal(2, PrivateMessagePatch.LastVolatileMessageIndex, "应命中承载 ordered.Current 的那条消息");
        TestRunner.Equal(8, PrivateMessagePatch.LastVolatileFingerprint.Length, "应记下 8 位 V 段指纹");
        TestRunner.True(PrivateMessagePatch.LastVolatileLength > 0, "应记下 V 段长度");

        string current = messages[2]["content"];
        TestRunner.Contains(current, "【当前经济】", "V 段要落在 Current 消息里");
        TestRunner.Contains(current, "$", "V 段要写出精确金额（带 $ 与千分位）");
        TestRunner.True(Regex.IsMatch(current, @"\d"), "V 段必须承载数字");
        TestRunner.NotContains(current, "【经济状况】", "S 段不该跑到 user 消息里");
        TestRunner.Equal(playerBefore, messages[3]["content"], "玩家自己那条消息必须一字不动");
        TestRunner.Equal(profileBefore, messages[1]["content"], "人物资料那条消息不该被动");

        // 两段隔离的端到端证据：数字全在 user 的 Current 里，system 仍然零数字。
        TestRunner.False(Regex.IsMatch(messages[0]["content"], @"\d"),
            "system 必须保持零数字：<" + messages[0]["content"] + ">");

        string line = ConsoleLines.FirstOrDefault(l => l.Contains("[Economy-Inject-V]", StringComparison.Ordinal)) ?? "";
        TestRunner.True(line.Length > 0,
            "进程内第一次成功的 V 段注入必须留下痕迹（由前面那条 S 段用例触发），实际捕获：" +
            string.Join(" / ", ConsoleLines));
        TestRunner.Contains(line, "首次实测注入", "关键词要保持可搜");
    }

    private static void InjectDiagnosticsVolatileSelfProofPasses()
    {
        SetupMod(out var config);

        var proof = InjectDiagnostics.LastVolatileProof;
        var insertion = InjectDiagnostics.LastVolatileInsertionProof;

        TestRunner.True(proof != null && proof.Passed, "V 段自证必须通过：" + (proof?.Error ?? ""));
        TestRunner.True(proof!.HasDigits, "V 段必须承载数字");
        TestRunner.True(proof.HasHeader, "V 段要含 volatileHeader");
        TestRunner.True(proof.BalancePresent, "V 段要含余额");
        TestRunner.True(proof.PendingVoicePresent, "V 段要含未竟之言");
        TestRunner.True(proof.Length > 0, "V 段不该为空");
        TestRunner.Equal(8, proof.Fingerprint.Length, "V 段指纹应是 8 位十六进制");
        TestRunner.Equal(CurrentSegmentInsert.DefaultAnchor,
            config.Prompt.Get("volatileAnchor", CurrentSegmentInsert.DefaultAnchor), "锚点应与原版文案一致");

        TestRunner.True(insertion != null && insertion.Passed, "V 段插入自证必须通过：" + (insertion?.Error ?? ""));
        TestRunner.Equal(VolatilePlacements.Appended, insertion!.Placement, "落点应是「追加」");
        TestRunner.True(insertion.MessageCountUnchanged, "V 段插入不该改变 message 条数");
        TestRunner.True(insertion.Idempotent, "V 段重复插入必须幂等");
        TestRunner.True(insertion.PlayerMessageUntouched, "V 段绝不能动玩家那条消息");
    }

    /// <summary>
    /// <b>反向控制</b>：把余额占位符从模板里拿掉，V 段自证必须报失败。
    /// 没有这条，"V 段自证通过"就可能只是恒真断言。
    /// </summary>
    private static void VolatileProofFailsWithoutBalancePlaceholder()
    {
        string configDir = CopyRealConfig(TempRoot());
        string promptPath = Path.Combine(configDir, ConfigLoader.PromptFileName);
        string text = File.ReadAllText(promptPath);
        text = text.Replace(
            "\"volatileCurrent\": \"你可用余额 {balance}。本赛季收入 {seasonIncome}（工资 {salaryIncome}、胜场奖金 {winBonusIncome}、赞助 {sponsorIncome}）。\"",
            "\"volatileCurrent\": \"你的余额情况不便细说。\"");
        text = text.Replace("\"volatileLastEntry\": \"最近一笔：第 {day} 天 {title} {amount}{reason}。\"",
            "\"volatileLastEntry\": \"\"");
        File.WriteAllText(promptPath, text);

        TestRunner.NotContains(text, "{balance}", "前置：模板替换必须真的生效");
        TestRunner.NotContains(text, "{amount}", "前置：模板替换必须真的生效");

        var broken = new ConfigLoader(configDir, Silent).Load();
        var proof = InjectDiagnostics.ProveVolatileSegment(broken);

        TestRunner.False(proof.Passed, "没有余额占位符时 V 段自证必须失败");
        TestRunner.False(proof.HasDigits, "此时易变段里应当没有数字");
        TestRunner.False(proof.BalancePresent, "应识别出余额缺失");
        TestRunner.True(proof.Error != null, "应给出失败原因");
    }

    // ── 挂载点体检（方案 D） ────────────────────────────────────────

    /// <summary>
    /// 把四个挂载点在<b>实际引用的那份 NationalSpire</b> 上解析出来，并把真实签名打到测试输出。
    /// <para>
    /// 这条测试的价值不只是"存在"，而是把签名**写进可复核的输出**：阶段 3 的事故正是
    /// "挂载点看起来对、但游戏跑的不是它"，而运行中的 NationalSpire 是 v0.26.10，
    /// 我们参考的源码是 0.26.7——版本差必须靠这种断言暴露出来。
    /// </para>
    /// </summary>
    private static void MountPointsResolveInDeployedBinary()
    {
        var copies = HookProbe.NationalSpireCopies();
        TestRunner.True(copies.Count >= 1, "至少要有一份 NationalSpire 程序集");
        Console.WriteLine("      · NationalSpire 程序集副本：" + copies.Count);
        foreach (var copy in copies) Console.WriteLine("        - " + HookProbe.Describe(copy));

        // ① PrivateMessagePrompts.Compose：S 段原挂载点
        var prompts = HookProbe.AssembliesDefining("NationalSpire.PrivateMessagePrompts");
        TestRunner.True(prompts.Count >= 1, "应有程序集定义 NationalSpire.PrivateMessagePrompts");
        Console.WriteLine("      · PrivateMessagePrompts 副本：" + prompts.Count);

        var compose = PrivateMessagePatch.FindComposeMethod();
        TestRunner.True(compose != null, "应能解析到 PrivateMessagePrompts.Compose");
        Console.WriteLine("      · Compose 签名：" + DescribeSignature(compose!));

        // ② PrivateMessagePrompts.Context：末端方案 A 要用的"身份捕获"点
        var contextFound = FindMethod(prompts, "NationalSpire.PrivateMessagePrompts", "Context");
        TestRunner.True(contextFound != null, "应能解析到 PrivateMessagePrompts.Context");
        Console.WriteLine("      · Context 签名：" + DescribeSignature(contextFound!));

        // ③ AiService.RequestPrivateAsync：方案 A 的主挂载点（私信上线前最后一站）
        var request = FindRequestPrivateMethods();
        TestRunner.True(request.Count >= 1, "应能解析到 RequestPrivateAsync(AiOptions, …)");
        foreach (var method in request) Console.WriteLine("      · RequestPrivateAsync 签名：" + DescribeSignature(method));

        // ④ PrivateStreamParser.Feed：阶段 2 的挂载点
        var parsers = HookProbe.AssembliesDefining("NationalSpire.PrivateStreamParser");
        TestRunner.True(parsers.Count >= 1, "应有程序集定义 NationalSpire.PrivateStreamParser");
        var feed = FindMethod(parsers, "NationalSpire.PrivateStreamParser", "Feed");
        TestRunner.True(feed != null, "应能解析到 PrivateStreamParser.Feed");
        Console.WriteLine("      · Feed 签名：" + DescribeSignature(feed!));
    }

    private static void HookProbeResolvesAllFourTargets()
    {
        var state = SetupMod(out var config);

        // 交付默认必须是【关】的：探针只在排障时要，而它给每个候选方法挂补丁、每次调用写文件
        // （真机实测一次私信的 Feed 命中 4369 次）。所以这里先钉默认值，再显式打开来测装配。
        TestRunner.False(config.Economy.Injection.ProbeEnabled,
            "★交付默认必须是关的（挂载点问题早已定案，不该让玩家替我们付探针的代价）");

        // 显式传 true 来验证"打开时确实能挂上四个目标"——测的是能力，不是默认值。
        var patched = HookProbe.Run(EconomyModEntry.HarmonyInstance, Silent, null, enabled: true);

        TestRunner.True(HookProbe.Enabled, "显式打开之后体检应处于启用状态");
        Console.WriteLine("      · 探针清单：" + string.Join(" + ", patched));
        foreach (string line in HookProbe.Inventory) Console.WriteLine("        " + line);

        TestRunner.True(patched.Any(p => p.Contains("RequestPrivateAsync")),
            "必须挂上「最后一站」探针，实际：" + string.Join(" + ", patched));
        TestRunner.True(patched.Any(p => p.Contains("Compose")), "应挂上 Compose 探针");
        TestRunner.True(patched.Any(p => p.Contains("Context")), "应挂上 Context 探针");
        TestRunner.True(patched.Any(p => p.Contains("Feed")), "应挂上 Feed 探针");
        TestRunner.True(state.Patches.Count > 0, "前置：模组应处于启用状态");
        TestRunner.True(HookProbe.Inventory.Count > 0, "体检清单不该为空");
        TestRunner.True(HookProbe.LastReport.Length > 0, "体检报告不该为空");
    }

    private static void HookProbeSummaryFormatIsStable()
    {
        // 摘要会被塞进 [Economy-Tick] 那一行，格式必须稳定、可 grep。
        TestRunner.Contains(HookProbe.Summary, "compose=");
        TestRunner.Contains(HookProbe.Summary, "context=");
        TestRunner.Contains(HookProbe.Summary, "request=");
        TestRunner.Contains(HookProbe.Summary, "feed=");

        // 诊断通道状态必须给出结论文本（绑不上也要说清原因），而不是空串。
        // 这里刻意真的构造一个运行时适配器，逼它去解析 Godot 与 Diagnostics，从而验证报告被写出来。
        var adapter = new EconomyLogAdapter();
        TestRunner.True(adapter.DiagnosticsAvailable == (EconomyLogAdapter.LastBindingReport.StartsWith("已绑定")),
            "DiagnosticsAvailable 必须与绑定报告一致：" + EconomyLogAdapter.LastBindingReport);
        TestRunner.True(EconomyLogAdapter.LastBindingReport.Length > 0, "诊断通道报告不该为空");
        TestRunner.True(EconomyLogAdapter.LastPrintReport.Length > 0, "控制台通道报告不该为空");
        Console.WriteLine("      · 诊断文件通道：" + EconomyLogAdapter.LastBindingReport);
        Console.WriteLine("      · 控制台通道：" + EconomyLogAdapter.LastPrintReport);
    }

    private static List<MethodInfo> FindRequestPrivateMethods()
    {
        var found = new List<MethodInfo>();
        foreach (var assembly in HookProbe.NationalSpireCopies())
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).Cast<Type>().ToArray(); }

            foreach (var type in types)
            {
                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (method.Name != "RequestPrivateAsync") continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length == 0 || parameters[0].ParameterType.Name != "AiOptions") continue;
                    found.Add(method);
                }
            }
        }
        return found;
    }

    private static MethodInfo? FindMethod(IReadOnlyList<Assembly> assemblies, string typeName, string methodName)
    {
        foreach (var assembly in assemblies)
        {
            var type = assembly.GetType(typeName, throwOnError: false);
            if (type == null) continue;
            // Feed 是【实例】方法，漏掉 Instance 会解析不到——这里必须四种标志都给。
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                                                  BindingFlags.Public | BindingFlags.NonPublic))
                if (method.Name == methodName) return method;
        }
        return null;
    }

    private static string DescribeSignature(MethodBase method)
    {
        string parameters = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name));
        string returns = method is MethodInfo info ? info.ReturnType.Name : "?";
        return $"{method.DeclaringType?.FullName}.{method.Name}({parameters}) → {returns}" +
               $"｜token=0x{method.MetadataToken:X8}｜程序集 {method.Module.Assembly.GetName().Name}";
    }

    // ── 日期推进补丁 ────────────────────────────────────────────────

    private static void DayPatchTargetIsMoveToNextDay()
    {
        var type = CareerDayPatch.FindTargetType();
        TestRunner.True(type != null, "应能找到 NationalSpire.CareerEngine");

        var method = CareerDayPatch.FindAdvanceMethod(type!);
        TestRunner.True(method != null, "应能找到日期推进方法");
        TestRunner.Equal(CareerDayPatch.PrimaryMethodName, method!.Name,
            "必须挂在 MoveToNextDay 上——整份原版源码里只有它改 d.Day / d.Season");
        TestRunner.Equal(1, method.GetParameters().Length, "签名应是 (CareerData)");
        TestRunner.Equal("CareerData", method.GetParameters()[0].ParameterType.Name);
    }

    private static void AdvancePostfixToleratesNullData()
    {
        SetupMod(out _);
        int before = CareerDayPatch.Ticks;

        CareerDayPatch.AdvancePostfix(null);

        TestRunner.Equal(before, CareerDayPatch.Ticks, "参数缺失时不该推进任何东西");
    }

    // ── 启动期自证 ──────────────────────────────────────────────────

    private static void InjectDiagnosticsSelfProofPasses()
    {
        // 刻意【不】挂 Compose 夹具：这条要验的正是"生产路径上初始化之后补丁清单里没有 Compose"。
        var state = SetupMod(out var config, installComposeFixture: false);

        var stable = InjectDiagnostics.LastStableProof;
        var insertion = InjectDiagnostics.LastInsertionProof;
        var tick = InjectDiagnostics.LastTickProof;

        TestRunner.True(stable != null, "应留下稳定段自证结论");
        TestRunner.True(stable!.Passed, "稳定段自证必须通过：" + (stable.Error ?? ""));
        TestRunner.True(stable.Length > 0, "稳定段不该是空的");
        TestRunner.False(stable.HasDigits, "稳定段必须零数字");
        TestRunner.False(stable.BalanceLeaked, "余额不得泄漏进稳定段");
        TestRunner.Equal(8, stable.Fingerprint.Length, "指纹应是 8 位十六进制");

        TestRunner.True(insertion != null && insertion.Passed, "插入自证必须通过：" + (insertion?.Error ?? ""));
        TestRunner.Equal(InsertPlacements.BeforeAnchor, insertion!.Placement, "插入位置应命中锚点");
        TestRunner.True(insertion.MessageCountUnchanged, "插入自证里 message 条数必须不变");
        TestRunner.True(insertion.Idempotent, "重复插入必须幂等");

        TestRunner.True(tick != null && tick.Passed, "影子推进自证必须通过：" + (tick?.Error ?? ""));
        TestRunner.Equal(2, tick!.Seeded, "自证世界应播种 2 人");
        TestRunner.Equal(EconomyTick.SalaryDueDay(config.Economy, EconomyTick.FirstSeason), tick.FirstDueDay,
            "第 1 赛季应在赛季末结算");
        TestRunner.Equal(tick.FirstDueDay, tick.AdvancedTo, "自证应推进到第 1 赛季结算日");
        TestRunner.Equal(2, tick.SalariesPaid, "结算日两个人各发一次");
        TestRunner.True(tick.NoEarlyPayout, "结算日之前一分钱都不该发");
        TestRunner.True(tick.Persisted, "自证账本必须真的落盘");
        TestRunner.True(tick.LedgerBalanced, "余额增量必须与工资一致（账实一致）");

        TestRunner.True(InjectDiagnostics.SelfTestPassed == true,
            "总自证必须通过，实际：" + InjectDiagnostics.LastReport);

        // ── 单轨原则（甲方拍板）：启动期【不再】挂 PrivateMessagePrompts.Compose 注入补丁 ──
        // 阶段 3-D 实测证明它不在真实私信的执行路径上；注入改由方案 A（RequestPrivateAsync）单轨接管。
        // 这里必须断言"确实没挂"，否则两处注入点各插一份的隐患会悄悄回来。
        TestRunner.False(PrivateMessagePatch.Applied,
            "启动期不该再挂 Compose 注入补丁（单轨：方案 A 接管）");
        TestRunner.False(state.Patches.Any(p => p.Contains("Compose") && !p.Contains("probe")),
            "补丁清单里不该再有【注入用】的 Compose，实际：" + string.Join(" + ", state.Patches));

        // 注意：这里【不再】断言"观测用的 Compose 探针应当在"。
        // 探针现在由 injection.probeEnabled 控制、交付默认关（它给 19 个候选方法各挂 3 个补丁、
        // 每次调用同步写文件，真机实测一次私信的 Feed 命中 4369 次）。
        // 上面那条"没有注入用 Compose"的断言才是单轨原则的真正落点，它不依赖探针开关；
        // 探针本身的能力由 HookProbeResolvesAllFourTargets 显式打开来验证。
        Console.WriteLine("      · 补丁清单：" + string.Join(" + ", state.Patches));

        TestRunner.True(CareerDayPatch.Applied, "日期推进补丁应已挂载");
        TestRunner.True(state.Patches.Any(p => p.Contains(CareerDayPatch.PrimaryMethodName)),
            "补丁清单应包含 MoveToNextDay，实际：" + string.Join(" + ", state.Patches));

        // 自证产物必须清理干净，不能往玩家数据目录里留垃圾。
        string selfcheck = Path.Combine(state.DataRoot, "selfcheck");
        if (Directory.Exists(selfcheck))
            TestRunner.Equal(0, Directory.GetDirectories(selfcheck).Length, "自证临时世界应被删除");
    }

    /// <summary>
    /// <b>反向控制</b>：把模板里塞一个数字，自证必须报失败。
    /// 没有这条，"自证通过"就可能只是恒真断言。
    /// </summary>
    private static void StableProofActuallyFailsOnDigitTemplate()
    {
        string configDir = CopyRealConfig(TempRoot());
        string promptPath = Path.Combine(configDir, ConfigLoader.PromptFileName);
        string text = File.ReadAllText(promptPath);
        text = text.Replace("\"stableTierLine\": \"你{wealthBand}。你的收入主要来自{incomeSource}。\"",
            "\"stableTierLine\": \"你{wealthBand}，净资产大约 3 万。\"");
        File.WriteAllText(promptPath, text);

        var broken = new ConfigLoader(configDir, Silent).Load();
        TestRunner.True(broken.HasErrors, "前置：含数字的稳定段模板应被配置校验判为 Error");

        var proof = InjectDiagnostics.ProveStableSegment(broken);

        TestRunner.False(proof.Passed, "含数字的稳定段必须让自证失败");
        TestRunner.True(proof.HasDigits, "应识别出数字");
        TestRunner.True(proof.Error != null, "应给出失败原因");
    }

    private static void DisabledInitLeavesRuntimeNull()
    {
        var state = EconomyModEntry.Initialize(Silent, TempRoot(), Path.Combine(TempRoot(), "missing"));

        TestRunner.False(state.Enabled, "配置缺失时必须拒绝启用");
        TestRunner.True(EconomyModEntry.Runtime == null, "未启用时 Runtime 必须为 null，补丁才会彻底不动原版");
    }

    // ── 夹具 ────────────────────────────────────────────────────────

    private static List<INpcFacts> Facts() =>
    [
        new NpcFacts(NpcId, "职业选手", Rating: 1500, MaxAscension: 8, PersonalityKind: "热心同好",
            PersonalityVersion: 1),
        new NpcFacts("npc-2", "普通玩家", Rating: 900, MaxAscension: 3, PersonalityKind: "看戏起哄者",
            PersonalityVersion: 1)
    ];

    private static CareerPerson NewPerson(string id, string role, string kind)
    {
        var person = new CareerPerson { Id = id, Name = "选手" + id, Handle = id, Role = role, Rating = 1500, MaxAscension = 8 };
        person.Personality.Kind = kind;
        return person;
    }

    /// <summary>
    /// 一份与原版同形的私信消息列表，顺序与 <c>Compose</c> 一致：
    /// system → 人物资料 → 上下文（<c>ordered.Current</c>，以「当前日期：」开头）→ 玩家这一轮的消息。
    /// </summary>
    private static List<Dictionary<string, string>> OriginalMessages() =>
    [
        new()
        {
            ["role"] = "system",
            ["content"] = "【模拟的原版 system：世界设定 + 七串 Protocol】\n"
                          + SystemPromptInsert.DefaultAnchor + "，性格字段描述可以变化的倾向；本次行为与表达遵循上述私信提示词。"
        },
        new() { ["role"] = "user", ["content"] = "【模拟的人物资料】" },
        new()
        {
            ["role"] = "user",
            ["content"] = "当前日期：第 1 赛季第 12 天。\n\n…档案…\n\n小李对你的关系\n好感42，关系为熟悉。\n\n…近期赛程…"
        },
        new() { ["role"] = "user", ["content"] = "第 1 赛季第 12 天，玩家发给小李的消息\n你好" }
    ];

    private static (CareerData Data, PrivateConversation Conversation) ConversationFixture(string worldId)
    {
        var data = new CareerData { WorldId = worldId, Day = 12, Season = 1 };
        data.People.Add(NewPerson(NpcId, "职业选手", "热心同好"));
        return (data, new PrivateConversation { PersonId = NpcId });
    }

    /// <summary>
    /// 把补丁的控制台通道接到 <see cref="ConsoleLines"/>。
    /// <c>ApplyTo</c> 先写日志/控制台字段、再判断"是否已装配"，所以这里不需要先卸载补丁。
    /// </summary>
    private static void InstallCapturingConsole()
    {
        var harmony = EconomyModEntry.HarmonyInstance;
        TestRunner.True(harmony != null, "前置：Harmony 实例应已建立");

        var compose = PrivateMessagePatch.FindComposeMethod();
        TestRunner.True(compose != null, "前置：应能找到 Compose 方法");

        bool ok = PrivateMessagePatch.ApplyTo(harmony!, compose!.DeclaringType!, compose, Silent,
            (line, _) => ConsoleLines.Add(line));
        TestRunner.True(ok, "前置：补丁应处于已装配状态");
    }

    /// <summary>用真实配置初始化模组，从而把静态 <see cref="EconomyModEntry.Runtime"/> 装好。</summary>
    /// <param name="installComposeFixture">
    /// 是否在初始化之后手工挂上 Compose 注入补丁。<b>仅供行为用例</b>：
    /// 生产路径已经不再挂它（单轨：方案 A 接管），所以"补丁清单/是否已装配"这类断言
    /// 必须在不挂夹具的路径上验证。
    /// </param>
    private static ModState SetupMod(out ConfigLoadResult config, bool installComposeFixture = true)
    {
        string configDir = CopyRealConfig(TempRoot());
        var state = EconomyModEntry.Initialize(Silent, TempRoot(), configDir);
        TestRunner.True(state.Enabled, "前置：真实配置应通过门禁：" + state.Describe());
        config = state.Config!;

        // 初始化会把补丁的控制台通道重置成 null（测试传的是 Silent 日志），
        // 所以行为用例在每次初始化之后都重新接到捕获列表，这样"首次调用一定说话"才验得到。
        if (installComposeFixture) InstallCapturingConsole();
        return state;
    }

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "nse-wire-" + Guid.NewGuid().ToString("N"));

    private static string CopyRealConfig(string target)
    {
        Directory.CreateDirectory(target);
        string source = RealConfigDirectory();
        foreach (string name in new[]
                 {
                     ConfigLoader.EconomyFileName, ConfigLoader.PersonalityFileName, ConfigLoader.PromptFileName
                 })
            File.Copy(Path.Combine(source, name), Path.Combine(target, name), overwrite: true);
        return target;
    }

    private static string RealConfigDirectory()
    {
        // 收敛到共享实现（原先 19 个文件各抄一份）。
        return TestConfig.Directory();
    }

    private static ConfigLoadResult LoadReal()
    {
        string root = RealConfigDirectory();
        return new ConfigLoader(root, Silent).Load();
    }
}
