// 方案 A 的接线用例需要 src/Game 与游戏程序集，零依赖构建（GameRefsEnabled=false）里必须整体排除，
// 否则 294 条那套会因为找不到 Game 命名空间而编译失败。
#if !NSE_NO_GAME
using NationalSpire;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Game;
using NationalSpireEconomy.Trade;
using NationalSpireEconomy.Inject;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// <b>方案 A（末端注入）</b>的接线契约。
/// <para>
/// 干预点是 <c>AiService.RequestPrivateAsync</c> 的前缀——私信消息被序列化发出去之前的最后一站。
/// 这些用例全部走"初始化 → 播种账本 → 登记身份 → 直接调 <c>Inject</c>"的离线路径，
/// 不启动游戏、不诱导 AI，断言全是确定性的。
/// </para>
/// <para>
/// 为什么必须单独有这一组：阶段 3-D 的教训是<b>自证全过但注入 0 行</b>。
/// 所以这里既钉"注入内容与落点"，也钉"身份对不上时必须拒绝注入"。
/// </para>
/// </summary>
public static class PlanAInjectionTests
{
    public static Action[] Cases =>
    [
        RequestPatchResolvesRealTargets,
        InjectsStableIntoSystemAndVolatileIntoCurrent,
        SecondPassIsIdempotent,
        SkipsNonPrivateContext,
        RejectsUnknownPerson,
        RejectsStaleIdentity,
        RejectsWhenMessageAlreadyInjected,
        TrackedFallbackInjectsThroughContextKey,
        TrackedFallbackRefusesAmbiguity,
        LoadsWorldOnDemandForFirstPrivateMessage,
        ModelIntentBecomesReadOnlyPreview,
        PreviewAndSettlementUseTheSamePlayerBalance,
        PlayerConfirmCommandIsHonouredWithoutMovingMoney,
        PlayerConfirmCommandSurvivesVanillaWrapper,
        PlayerConfirmSettlesWhenSwitchIsOn,
        RejectedIntentBecomesVisibleNotSettledNote,
        SettledReceiptReachesTheVolatileSegment,
        PlayerDeclineBecomesVisibleNotSettledNote,
        TransferClaimSentinelTellsTheLedgerTruth,
        PlayerVoteSurvivesExpiredIdentitySlot,
        ReConfirmingSettledOfferRePersistsTheReceipt,
        OtherOffersMustNotClobberTheSettledReceipt,
        EveryTruthWriteSurvivesAReload,
        StateMachineFieldsReadByType
    ];

    private const string NpcId = "npc-1";
    private const string WorldId = "plan-a-world";

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    // ── 挂载点解析与装配 ────────────────────────────────────────────

    /// <summary>
    /// 目标方法必须真的能在<b>已部署的原版程序集</b>里解析到，并且装上。
    /// 这条如果失败，方案 A 在游戏里不会生效。
    /// </summary>
    private static void RequestPatchResolvesRealTargets()
    {
        var state = Setup();

        var targets = PrivateRequestInjectionPatch.FindRequestPrivateMethods();
        TestRunner.True(targets.Count >= 1,
            "应能在已加载程序集里解析到 RequestPrivateAsync(AiOptions, …)");

        foreach (var target in targets)
            Console.WriteLine("      · 方案 A 目标：" + PrivateRequestInjectionPatch.SignatureOf(target) +
                              $"，token=0x{target.MetadataToken:X8}，程序集 {target.Module.Assembly.GetName().Name}");

        TestRunner.True(PrivateRequestInjectionPatch.Applied, "初始化时方案 A 应已挂载成功");
        TestRunner.True(PrivateRequestInjectionPatch.PatchedMethods.Count >= 1, "应记下已挂载的目标");
        TestRunner.True(PrivateRequestInjectionPatch.PatchedMethods[0].Contains("token=0x"),
            "已挂载清单里必须带 token（静默失败的第一手证据）");
        TestRunner.True(state.Patches.Any(p => p.Contains("RequestPrivateAsync")),
            "补丁清单应包含 RequestPrivateAsync，实际：" + string.Join(" + ", state.Patches));

        // 身份来源也必须挂上：没有它，方案 A 拿不到人物与世界。
        TestRunner.True(PrivateIdentityTrackPatch.FindStateMachines().Count >= 1,
            "应找到 ProcessPrivateAsync 的异步状态机");
        TestRunner.True(PrivateIdentityTrackPatch.Applied, "身份来源应已挂载");
    }

    // ── 注入本体 ────────────────────────────────────────────────────

    /// <summary>私信上下文：S 段进 system、V 段追加到「当前日期：」那条 user 消息，且条数不变。</summary>
    private static void InjectsStableIntoSystemAndVolatileIntoCurrent()
    {
        var world = SetupWorld();
        var messages = OriginalMessages();
        string[] rolesBefore = messages.Select(m => m["role"]).ToArray();
        string playerBefore = messages[4]["content"];

        PrivateIdentitySlot.Set(WorldId, NpcId, NpcId, 12, 1, "test", Data());
        var outcome = PrivateRequestInjectionPatch.Inject(
            PrivateIdentitySlot.Take()!, "private/" + NpcId + "/topic/turn", messages);

        TestRunner.True(outcome.Injected, "应该注入成功，实际：" + outcome.LogLine);
        TestRunner.Equal(5, messages.Count, "绝不能新增 message");
        TestRunner.Equal(5, outcome.AfterCount, "结果里也要记下条数不变");
        TestRunner.True(outcome.SkipReason == null, "成功时不该有跳过原因");
        TestRunner.True(world.Npcs.ContainsKey(NpcId), "前置：账本里应有这个人");

        string system = messages[0]["content"];
        TestRunner.Contains(system, PrivateRequestInjectionPatch.StableHeader, "S 段必须落进 system");
        TestRunner.Contains(system, SystemPromptInsert.DefaultAnchor, "原版收尾句必须保留");
        TestRunner.True(
            system.IndexOf(PrivateRequestInjectionPatch.StableHeader, StringComparison.Ordinal)
            < system.IndexOf(SystemPromptInsert.DefaultAnchor, StringComparison.Ordinal),
            "稳定段必须插在锚点句之前（与阶段 3 的契约一致）");
        TestRunner.False(System.Text.RegularExpressions.Regex.IsMatch(
                system[..system.IndexOf(PrivateRequestInjectionPatch.StableHeader, StringComparison.Ordinal)],
                @"\d"),
            "稳定段之前的内容不得因为我们的注入出现数字");

        string current = messages[3]["content"];
        TestRunner.Contains(current, PrivateRequestInjectionPatch.VolatileHeader, "V 段必须落进 Current 那条消息");
        TestRunner.Equal(VolatilePlacements.Appended, outcome.VolatileDetail.Contains("appended") ? "appended" : "?",
            "V 段落点应是「追加」");
        TestRunner.Contains(messages[4]["content"], playerBefore, "玩家那条消息一字不能动");
        TestRunner.Equal(playerBefore, messages[4]["content"], "玩家那条消息必须逐字不变");
        TestRunner.Equal(string.Join(",", rolesBefore), string.Join(",", messages.Select(m => m["role"])),
            "message 顺序必须不变");
    }

    /// <summary>同一个消息列表被处理两次时，第二次必须什么都不做（防双注入）。</summary>
    private static void SecondPassIsIdempotent()
    {
        SetupWorld();
        var messages = OriginalMessages();

        PrivateIdentitySlot.Set(WorldId, NpcId, NpcId, 12, 1, "test", Data());
        var first = PrivateRequestInjectionPatch.Inject(PrivateIdentitySlot.Take()!, "private/x/t/1", messages);
        string afterFirst = messages[0]["content"] + "\u0001" + messages[3]["content"];

        PrivateIdentitySlot.Set(WorldId, NpcId, NpcId, 12, 1, "test", Data());
        var second = PrivateRequestInjectionPatch.Inject(PrivateIdentitySlot.Take()!, "private/x/t/2", messages);

        TestRunner.True(first.Injected, "第一次应注入");
        TestRunner.False(second.Injected, "第二次必须幂等跳过，实际：" + second.LogLine);
        TestRunner.True(second.SkipReason != null && second.SkipReason.Contains("幂等"),
            "跳过原因要写明是幂等，实际：" + second.SkipReason);
        TestRunner.Equal(afterFirst, messages[0]["content"] + "\u0001" + messages[3]["content"],
            "第二次不该再改动任何内容");
        TestRunner.Equal(1, CountOccurrences(messages[0]["content"], PrivateRequestInjectionPatch.StableHeader),
            "S 段标题只能出现一次");
        TestRunner.Equal(1, CountOccurrences(messages[3]["content"], PrivateRequestInjectionPatch.VolatileHeader),
            "V 段标题只能出现一次");
    }

    /// <summary>
    /// 非私信上下文一律不注入。这条必须走<b>真正的前缀入口</b>（<c>RequestPrefix</c>），
    /// 因为"只处理 private/"这道闸就在那里；直接调 <c>Inject</c> 会绕过它。
    /// </summary>
    private static void SkipsNonPrivateContext()
    {
        SetupWorld();
        int skippedBefore = PrivateRequestInjectionPatch.SkippedNonPrivate;

        var contexts = new[] { "group/npc-1/topic", "weekly", "arbitration", "", "private" };
        foreach (string context in contexts)
        {
            var messages = OriginalMessages();
            string[] before = messages.Select(m => m["content"]).ToArray();
            PrivateIdentitySlot.Set(WorldId, NpcId, NpcId, 12, 1, "test", Data());

            // 前缀的真实形状：第一个 string 是上下文，List 在别的位置，其余参数无关紧要。
            // 这里的两个 null 是【故意】喂进去的畸形实参（原版改了参数形状/回调传空时前缀也不能崩），
            // 用 null! 而不是改签名：参数声明是非空引用类型，而"容忍空"正是这条用例要证明的。
            PrivateRequestInjectionPatch.RequestPrefix(
                [context, messages, false, null!, default(System.Threading.CancellationToken), null!, context]);

            TestRunner.Equal(string.Join("\u0001", before),
                string.Join("\u0001", messages.Select(m => m["content"])),
                $"上下文 <{context}> 下所有消息都必须原封不动");
        }

        TestRunner.Equal(skippedBefore + contexts.Length, PrivateRequestInjectionPatch.SkippedNonPrivate,
            "每一次非私信上下文都应被计入跳过计数");

        // 反向控制：同样的形状换成 private/ 就必须真的注入。
        var privateMessages = OriginalMessages();
        PrivateIdentitySlot.Set(WorldId, NpcId, NpcId, 12, 1, "test", Data());
        PrivateRequestInjectionPatch.RequestPrefix(
            ["private/" + NpcId + "/topic/turn", privateMessages, false, null!,
                default(System.Threading.CancellationToken), null!, "private/" + NpcId + "/topic/turn"]);

        TestRunner.Contains(privateMessages[0]["content"], PrivateRequestInjectionPatch.StableHeader,
            "private/ 上下文必须真的注入（否则上面那条断言毫无意义）");
        TestRunner.True(PrivateRequestInjectionPatch.LastOutcome?.Injected == true,
            "最后一次结果应是「已注入」，实际：" + PrivateRequestInjectionPatch.LastOutcome?.LogLine);
    }

    /// <summary>不在影子账本里的人（真人/陌生人）绝不注入。</summary>
    private static void RejectsUnknownPerson()
    {
        SetupWorld();
        var messages = OriginalMessages();
        string systemBefore = messages[0]["content"];

        PrivateIdentitySlot.Set(WorldId, "human-1", "human-1", 12, 1, "test", Data());
        var outcome = PrivateRequestInjectionPatch.Inject(
            PrivateIdentitySlot.Take()!, "private/human-1/t/1", messages);

        TestRunner.False(outcome.Injected, "账本里没有的人不能注入，实际：" + outcome.LogLine);
        TestRunner.True(outcome.SkipReason != null && outcome.SkipReason.Contains("账本"),
            "原因要说明「不在账本里」，实际：" + outcome.SkipReason);
        TestRunner.Equal(systemBefore, messages[0]["content"], "system 必须原封不动");
    }

    /// <summary>身份槽过期后必须拒绝注入，而不是拿旧身份往别人身上插。</summary>
    private static void RejectsStaleIdentity()
    {
        SetupWorld();
        PrivateIdentitySlot.Clear();

        PrivateIdentitySlot.Set(WorldId, NpcId, NpcId, 12, 1, "test", Data(),
            ttl: TimeSpan.FromMilliseconds(-1));

        TestRunner.False(PrivateIdentitySlot.HasFresh, "前置：槽里应已过期");
        TestRunner.True(PrivateIdentitySlot.Take() == null, "过期身份必须读不出来");
        TestRunner.True(PrivateIdentitySlot.Expired >= 1, "过期次数应被记下（诊断用）");
    }

    /// <summary>消息里已经有我们的标题时，即使身份正确也不再插（防重复装配）。</summary>
    private static void RejectsWhenMessageAlreadyInjected()
    {
        SetupWorld();
        var messages = OriginalMessages();
        messages[0]["content"] = PrivateRequestInjectionPatch.StableHeader + "\n" + messages[0]["content"];
        string before = messages[0]["content"];

        PrivateIdentitySlot.Set(WorldId, NpcId, NpcId, 12, 1, "test", Data());
        var outcome = PrivateRequestInjectionPatch.Inject(PrivateIdentitySlot.Take()!, "private/x/t/1", messages);

        TestRunner.False(outcome.Injected, "已含标题时不该再插");
        TestRunner.True(outcome.SkipReason != null && outcome.SkipReason.Contains("幂等"),
            "原因应说明幂等，实际：" + outcome.SkipReason);
        TestRunner.Equal(before, messages[0]["content"], "内容必须保持不变");
    }

    // ── 兜底轨迹 ────────────────────────────────────────────────────

    /// <summary>
    /// 精确槽缺失时，用原版上下文里的人物键 + 轨迹把身份对上，并且真的能完成注入。
    /// 这是"Context 后缀万一没跑"的保险丝，所以直接验到注入结果为止。
    /// </summary>
    private static void TrackedFallbackInjectsThroughContextKey()
    {
        SetupWorld();
        PrivateIdentitySlot.Clear();

        // 只有轨迹、没有精确身份；而且候选人的 Data 是【空壳】，用来模拟真实场景：
        // 轨迹里的 Data 可能已经过时或为空，真正的权威数据由 MatchFromContext 回填。
        PrivateIdentitySlot.SetForWorld(WorldId, [(NpcId, "")], 12, 1, "test·轨迹", new CareerData
        {
            WorldId = WorldId,
            Day = 12,
            Season = 1
        });

        var messages = OriginalMessages();
        var matched = PrivateRequestInjectionPatch.MatchFromContext("private/" + NpcId + "/topic/turn", messages);
        TestRunner.True(matched == null, "轨迹里没有权威数据时必须拒绝（这是安全兜底，不是失败）");

        // 换成带权威数据的轨迹：必须能对上人、并真的注入。
        PrivateIdentitySlot.SetForWorld(WorldId, [(NpcId, "")], 12, 1, "test·轨迹", Data());
        matched = PrivateRequestInjectionPatch.MatchFromContext("private/" + NpcId + "/topic/turn", messages);
        TestRunner.True(matched != null, "轨迹应能按上下文键把身份对上");
        TestRunner.Equal(NpcId, matched!.PersonId, "对上的人必须是上下文里那个人");
        TestRunner.True(matched.Source.Contains("兜底"), "来源要标明是轨迹兜底，便于排查");

        var outcome = PrivateRequestInjectionPatch.Inject(matched, "private/" + NpcId + "/topic/turn", messages);
        TestRunner.True(outcome.Injected, "兜底路径也必须真的注入，实际：" + outcome.LogLine);
        TestRunner.Contains(messages[0]["content"], PrivateRequestInjectionPatch.StableHeader, "S 段要落 system");
        TestRunner.Contains(messages[3]["content"], PrivateRequestInjectionPatch.VolatileHeader, "V 段要落 Current");
        TestRunner.Equal(5, messages.Count, "条数必须不变");
    }

    /// <summary>轨迹里歧义（同一个键指向两个人）时必须拒绝，绝不猜。</summary>
    private static void TrackedFallbackRefusesAmbiguity()
    {
        SetupWorld();
        PrivateIdentitySlot.Clear();
        // 两个人的 Handle 都登记成 "shared"，于是键 "shared" 有歧义。
        PrivateIdentitySlot.SetForWorld(WorldId, [(NpcId, "shared"), ("npc-2", "shared")], 12, 1, "test", Data());

        var matched = PrivateIdentitySlot.TryMatchTracked("shared", WorldId);
        TestRunner.True(matched == null, "歧义必须返回 null（拒绝注入）");
    }

    // ── 状态机取字段（方案 A 身份来源的核心手法）─────────────────────

    /// <summary>
    /// 异步状态机是靠<b>字段类型</b>认出来的，不写死编译器生成的字段名。
    /// 这里用一个人造状态机验证：即使字段顺序颠倒、名字古怪，也要取对。
    /// </summary>
    private static void StateMachineFieldsReadByType()
    {
        var data = Data();
        var machine = new FakeStateMachine
        {
            SomethingElse = "不是人物 id 的字符串",
            PersonIdField = NpcId,
            Callback = _ => { },
            Accessor = () => data
        };

        var state = PrivateIdentityTrackPatch.ReadState(machine);
        TestRunner.True(state != null, "人造状态机应能被读取");
        TestRunner.True(state!.Data != null, "应能取出 CareerData（字段名无关）");
        TestRunner.Equal(WorldId, state.Data!.WorldId, "取出的必须是那一个 CareerData");
        TestRunner.Equal(NpcId, state.PersonId, "应挑出能在 People 里对上的那个字符串当人物 id");
        TestRunner.True(state.Strings.Count >= 2, "所有字符串字段都应被收集（诊断用）");

        // 只有 Func<CareerData> 没有直接字段时，也必须能拿到。
        var indirect = new FakeIndirectMachine { Accessor = () => data, Id = NpcId };
        var indirectState = PrivateIdentityTrackPatch.ReadState(indirect);
        TestRunner.True(indirectState?.Data != null, "只有 Func<CareerData> 时也必须能拿到数据");
        TestRunner.Equal(NpcId, indirectState!.PersonId, "间接状态机也要能挑出人物 id");
    }

    // ── 夹具 ────────────────────────────────────────────────────────

    /// <summary>人造异步状态机：字段名故意起得不像，用来验证"按类型取字段"。</summary>
    private sealed class FakeStateMachine
    {
        public string SomethingElse = "";
        public Action<CareerData>? Callback;
        public Func<CareerData>? Accessor;
        public string PersonIdField = "";
    }

    private sealed class FakeIndirectMachine
    {
        public Func<CareerData>? Accessor;
        public string Id = "";
    }

    private static int CountOccurrences(string text, string marker)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }
        return count;
    }

    private static CareerData Data(string? worldId = null) => new()
    {
        WorldId = worldId ?? WorldId,
        Day = 12,
        Season = 1,
        People = { NewPerson(NpcId, "职业选手", "热心同好"), NewPerson("npc-2", "普通玩家", "看戏起哄者") }
    };

    private static CareerPerson NewPerson(string id, string role, string kind)
    {
        var person = new CareerPerson
        {
            Id = id,
            Name = "选手" + id,
            Handle = id,
            Role = role,
            Rating = 1500,
            MaxAscension = 8
        };
        person.Personality.Kind = kind;
        return person;
    }

    private static IReadOnlyList<INpcFacts> Facts() =>
    [
        new NpcFacts(NpcId, "职业选手", Rating: 1500, MaxAscension: 8, PersonalityKind: "热心同好",
            PersonalityVersion: 1),
        new NpcFacts("npc-2", "普通玩家", Rating: 900, MaxAscension: 3, PersonalityKind: "看戏起哄者",
            PersonalityVersion: 1)
    ];

    /// <summary>一份与原版同形的私信消息列表。</summary>
    private static List<Dictionary<string, string>> OriginalMessages() =>
    [
        new()
        {
            ["role"] = "system",
            ["content"] = "【模拟的原版 system：世界设定 + 七串 Protocol】\n"
                          + SystemPromptInsert.DefaultAnchor + "，性格字段描述可以变化的倾向。"
        },
        new() { ["role"] = "user", ["content"] = "【模拟的人物资料】" },
        new() { ["role"] = "user", ["content"] = "历史记录：上一次聊天内容" },
        new()
        {
            ["role"] = "user",
            ["content"] = "当前日期：第 1 赛季第 12 天。\n\n…档案…\n\n小李对你的关系\n好感42，关系为熟悉。"
        },
        new() { ["role"] = "user", ["content"] = "第 1 赛季第 12 天，玩家发给小李的消息\n你好" }
    ];

    /// <summary>跑一次真实初始化（挂载方案 A 的两个补丁），并把账本装好。</summary>
    private static ModState Setup()
    {
        string configDir = CopyRealConfig(TempRoot());
        var state = EconomyModEntry.Initialize(Silent, TempRoot(), configDir);
        TestRunner.True(state.Enabled, "前置：真实配置应通过门禁：" + state.Describe());
        PrivateIdentitySlot.Clear();
        return state;
    }

    private static EconomyWorld SetupWorld()
    {
        var state = Setup();
        var world = EconomyModEntry.Runtime!.EnsureWorld(WorldId, Facts());
        TestRunner.True(world != null, "前置：应能建立影子世界");
        return world!;
    }

    /// <summary>
    /// <b>真机事故回归（2026-10-11 20:11）</b>：影子存档原先<b>只在"游戏日期推进"时</b>才载入 +
    /// 增量播种，于是"刚进游戏、还没跳过任何一天就先发一条私信"必然命中"世界不在影子存档里"而被拒绝注入。
    /// <para>
    /// 现在私信路径必须能<b>按需载入</b>：它与"跳天"走同一条 <c>EnsureWorld</c> 路径（幂等播种），
    /// 因为那一刻身份槽里已经带着权威 <c>CareerData</c>。
    /// </para>
    /// </summary>
    private static void LoadsWorldOnDemandForFirstPrivateMessage()
    {
        Setup();                        // 全新 Runtime：缓存里没有任何世界
        var runtime = EconomyModEntry.Runtime!;
        string fresh = "ondemand" + Guid.NewGuid().ToString("N")[..6];

        TestRunner.True(runtime.CachedWorld(fresh) == null, "前置：这个世界号不该在缓存里");

        var messages = OriginalMessages();
        PrivateIdentitySlot.Set(fresh, NpcId, NpcId, 12, 1, "test", Data(fresh));
        var outcome = PrivateRequestInjectionPatch.Inject(
            PrivateIdentitySlot.Take()!, "private/" + NpcId + "/topic/turn", messages);

        TestRunner.True(runtime.CachedWorld(fresh) != null,
            "私信路径必须按需把这个世界载入（否则重启后的第一条私信永远不注入）");
        TestRunner.True(runtime.CachedWorld(fresh)!.Npcs.ContainsKey(NpcId),
            "按需载入之后账本里必须有这个人（增量播种要真的跑了）");
        TestRunner.True(outcome.Injected, "按需载入之后应当注入成功，实际：" + outcome.LogLine);
        TestRunner.False((outcome.SkipReason ?? "").Contains("不在影子存档里", StringComparison.Ordinal),
            "不该再因为「世界不在影子存档里」而跳过");
        TestRunner.Equal(5, messages.Count, "按需载入也不能改变 message 条数");
        Console.WriteLine("      · 按需载入结果：" + outcome.LogLine);
    }

    /// <summary>
    /// <b>阶段 4 第一步（游戏侧）</b>：模型在私信里提出的交易标记，必须变成
    /// 「待确认单 + 只读预览」，并且<b>一个字节的钱都不动</b>；NPC 自己发起的报价算它已经同意。
    /// </summary>
    private static void ModelIntentBecomesReadOnlyPreview()
    {
        Setup();
        var runtime = EconomyModEntry.Runtime!;
        PrivateIdentitySlot.Set(WorldId, NpcId, NpcId, 12, 1, "test", Data());

        var intent = new TradeIntent(TradeKinds.Sponsor, TradeDirections.NpcToPlayer, NpcId,
            EconomyTier.PlayerId, 10_000, [], "测试报价", "[Trade: 赞助, Amount: 10000]");

        var world = runtime.EnsureWorld(WorldId, Facts())!;
        int offersBefore = world.Offers.Count;
        string moneyBefore = TradePreviewTests.BalanceSnapshot(Ctx(world));

        TradePreviewWiring.HandleModelIntent(intent, Silent);

        var preview = TradePreviewWiring.LastPreview;
        TestRunner.True(preview != null, "应生成只读预览");
        TestRunner.False(preview!.MutatesLedger, "预览必须声明自己不写账");
        TestRunner.True(preview.OfferExists, "预览应对着一张真实的待确认单");
        TestRunner.True(world.Offers.TryGetValue(preview.OfferId, out var offer), "交易单必须落在世界里");
        TestRunner.True(TradeOfferStates.IsPending(offer!.State), "它必须是 pending（还没到动钱那一步）");

        var status = TradeConfirmationGate.Status(Ctx(world), offer.Id);
        TestRunner.True(status.NpcAccepted, "NPC 自己发起的报价应算它已同意");
        TestRunner.False(status.PlayerConfirmed, "玩家还没确认");
        TestRunner.False(status.ReadyToSettle, "只差玩家时不能进入待结算");

        string? note = TradePreviewWiring.NoteFor(world);
        TestRunner.True(note != null, "V 段注记必须拿得到（玩家要在私信窗口里看到它）");
        TestRunner.Contains(note!, "成交预览", "注记里要有可检索的标题");
        TestRunner.Contains(note!, "确认", "注记要告诉玩家怎么确认");

        TestRunner.Equal(moneyBefore, TradePreviewTests.BalanceSnapshot(Ctx(world)),
            "★生成预览与落待确认单全程不得动钱（余额与流水一字不变）");
        TestRunner.Equal(offersBefore + 1, world.Offers.Count,
            "本步只允许新增一张【待确认】单，它不动钱（Reserve 的既有契约）");
        Console.WriteLine("      · " + (note ?? "").Split('\n')[0].Trim());
    }

    /// <summary>
    /// <b>★ 真机验收①挖出的 bug 的回归：预览显示的那个玩家余额，必须就是结算要扣的那一份。</b>
    /// <para>
    /// <b>真机症状（2026-10-12 13:19）</b>：确认弹窗写着
    /// <c>player: $196,820 → $193,820</c>，而实际结算日志写
    /// <c>玩家 $198,020 → $195,020</c> —— 差了 $1,200。
    /// </para>
    /// <para>
    /// <b>根因</b>：预览走 <c>PlayerWallet</c>（原版 <c>CareerData.Credits</c>），结算走
    /// <see cref="ShadowPlayerWallet"/>。两者从"一次性只读导入"之后就按甲方拍板的 1(a)
    /// <b>合法地分叉</b>了，而原版那两个字段我们<b>只读不写</b> ——
    /// 于是弹窗上那个数字会永远停在导入那一刻。
    /// </para>
    /// <para>
    /// <b>它不只是显示问题</b>：那个数字同时是 <c>BalanceValidator</c> 判断"钱够不够"的依据，
    /// 所以预览说"够"、结算可能说"不够"（反过来也一样）。
    /// </para>
    /// <para>
    /// <b>这条测试怎么造出分叉</b>：先让 <c>EnsureWorld</c> 按原版 <c>Credits = 0</c> 完成导入
    /// （所以原版数字是 $0），再往影子钱包里加 $100,000 ——
    /// 于是"用错钱包"与"用对钱包"会给出两个截然不同的数字，无法蒙混过关。
    /// </para>
    /// </summary>
    private static void PreviewAndSettlementUseTheSamePlayerBalance()
    {
        Setup();
        var runtime = EconomyModEntry.Runtime!;
        string worldId = "balance-divergence-" + Guid.NewGuid().ToString("N")[..8];
        PrivateIdentitySlot.Set(worldId, NpcId, NpcId, 12, 1, "test", Data(worldId));

        // 前置：原版 Credits 是 0（Data() 没设它），所以"用原版数字"会得到 $0。
        var data = Data(worldId);
        TestRunner.Equal(0L, new PlayerWallet(data).BalanceUsd,
            "前置：原版 Credits 是 0 —— 用它算出来的余额就是 $0");

        // 与私信路径同源：WorldFor(data) 会 EnsureWorld + 一次性导入玩家钱包（EnsureWorld 不导入）。
        var world = runtime.WorldFor(data)!;
        TestRunner.True(world != null, "前置：应能建立影子世界");
        TestRunner.True(world.Player.Imported, "前置：WorldFor 会做一次性导入");

        // 把影子余额推到 $100,000：两个数字从此明确分叉。
        var credit = new ShadowPlayerWallet(world).Apply(100_000,
            new LedgerEntry { Day = 12, Season = 1, Kind = "测试入账", Title = "测试：制造分叉" });
        TestRunner.Equal(MoneyMoveStatus.Applied, credit.Status, "前置：应能把影子余额加到 $100,000");
        TestRunner.Equal(100_000L, world.Player.Balance, "前置：影子余额 $100,000");
        TestRunner.Equal(0L, new PlayerWallet(Data(worldId)).BalanceUsd, "前置：原版数字仍是 $0（分叉成立）");

        // 玩家出钱：金额取 3,000（下限之上、远低于 maxSingleAmount，且"雇佣"不过施舍闸）。
        var intent = new TradeIntent(TradeKinds.Hire, TradeDirections.PlayerToNpc,
            EconomyTier.PlayerId, NpcId, 3_000, [], "测试报价", "[Trade: 雇佣, Amount: 3000]");

        TradePreviewWiring.HandleModelIntent(intent, Silent);

        var preview = TradePreviewWiring.LastPreview;
        TestRunner.True(preview != null, "应生成只读预览");
        TestRunner.True(preview!.OfferExists, "应真的落下一张待确认单：" + preview.BlockingMessage);

        // ★ 核心断言：预览里的玩家余额必须是【影子余额】，不是原版那 0。
        TestRunner.Equal(100_000L, preview.Player.BalanceBeforeUsd,
            "★预览必须显示影子余额 $100,000（用原版数字会显示 $0）");
        TestRunner.Equal(97_000L, preview.Player.BalanceAfterUsd, "★扣款后应是 $97,000");
        TestRunner.NotEqual(0L, preview.Player.BalanceBeforeUsd, "★绝不能是原版那个 $0");

        // 弹窗正文与日志里那个数字也必须同源（玩家看到的就是它）。
        TestRunner.Contains(preview.ReadOnlyText, MoneyUnits.Format(100_000),
            "★弹窗正文必须写影子余额");
        TestRunner.NotContains(preview.ReadOnlyText, MoneyUnits.Format(0),
            "★弹窗正文不许出现原版那个 $0");

        // 真跑一遍：结算必须从同一个 $100,000 里扣，结果与预览一致。
        long npcBefore = world.Npcs[NpcId].Balance;
        TradeConfirmationGate.WireSettlement(true);
        try
        {
            var answer = TradePreviewWiring.ApplyPlayerAnswer(worldId, preview.OfferId, confirm: true, Silent);
            TestRunner.True(answer != null, "应能登记玩家那一票");

            var outcome = TradePreviewWiring.LastSettlement;
            TestRunner.True(outcome != null, "确认到齐后应触发结算");
            TestRunner.Equal(SettlementStatus.Applied, outcome!.Status, "应真的结算：" + outcome.Detail);

            // 预览说什么，账上就必须是什么。
            TestRunner.Equal(preview.Player.BalanceBeforeUsd, outcome.PlayerBalanceBefore,
                "★预览的「扣款前」必须等于结算看到的「扣款前」");
            TestRunner.Equal(preview.Player.BalanceAfterUsd, outcome.PlayerBalanceAfter,
                "★预览的「扣款后」必须等于结算之后的真实余额");
            TestRunner.Equal(97_000L, world.Player.Balance, "★影子余额必须真的变成 $97,000");

            // 顺带钉住"玩家→NPC"这个方向本身（本步 A 项的最终目的）。
            TestRunner.Equal(TradeDirections.PlayerToNpc, outcome.Direction);
            TestRunner.Equal(npcBefore + 3_000L, world.Npcs[NpcId].Balance, "★NPC 那边必须真的收到 $3,000");
        }
        finally
        {
            TradeConfirmationGate.WireSettlement(false);
            TradePreviewWiring.Clear(worldId);
            PrivateIdentitySlot.Clear();
        }
    }

    /// <summary>
    /// <b>阶段 4 第一步（游戏侧）</b>：模型在私信里回「确认 &lt;单号&gt;」必须被认出来并投上玩家那一票，
    /// 然后停在「双方已确认，待结算」——<b>钱仍然一分不动</b>。
    /// </summary>
    private static void PlayerConfirmCommandIsHonouredWithoutMovingMoney()
    {
        Setup();
        var runtime = EconomyModEntry.Runtime!;
        PrivateIdentitySlot.Set(WorldId, NpcId, NpcId, 12, 1, "test", Data());

        var world = runtime.EnsureWorld(WorldId, Facts())!;
        var ctx = Ctx(world);
        var engine = new TradeEngine(runtime.Config.Economy, runtime.Config.Personality,
            runtime.Config.Prompt, Silent);
        var reserve = engine.Submit(ctx, new TradeIntent(TradeKinds.Sponsor, TradeDirections.NpcToPlayer,
            NpcId, EconomyTier.PlayerId, 10_000, [], "测试报价", "[Trade: test]"));
        TestRunner.True(reserve.Ok, "前置：应能落单：" + reserve.Check.Message);
        string offerId = reserve.OfferId!;

        string moneyBefore = TradePreviewTests.MoneySnapshot(ctx);
        var identity = PrivateIdentitySlot.Peek();
        TestRunner.True(identity != null, "前置：身份槽里应有身份（Peek 不清空）");

        int handled = TradePreviewWiring.HandlePlayerCommands(identity!, ["确认 " + offerId], Silent);
        TestRunner.Equal(1, handled, "应认出并处理一条玩家确认指令");

        var status = TradeConfirmationGate.Status(ctx, offerId);
        TestRunner.True(status.PlayerConfirmed, "玩家那一票必须被记上");
        TestRunner.False(status.ReadyToSettle, "NPC 还没投票时不能算双方就绪");
        TestRunner.Equal(moneyBefore, TradePreviewTests.MoneySnapshot(ctx),
            "★玩家确认指令不得动任何钱");

        // 注意：玩家那一票在收据本上，收据【不是】钱。
        TestRunner.True(ctx.World.Receipts.Count > 0, "确认事实应被登记成幂等收据");

        // 再来一次同样的指令：必须幂等（不重复登记）。
        int receiptsOnce = ctx.World.Receipts.Count;
        TradePreviewWiring.HandlePlayerCommands(identity!, ["确认 " + offerId], Silent);
        TestRunner.Equal(receiptsOnce, ctx.World.Receipts.Count, "重复确认必须幂等");
        Console.WriteLine("      · " + status.DetailText);
    }

    /// <summary>
    /// <b>真机事故回归（2026-10-11 22:12）</b>：玩家在私信里打了「确认 o0001」，
    /// 而模组<b>一声不吭</b>——`[Economy-Confirm]` 一条日志都没有，玩家也就得不到任何反馈。
    /// <para>
    /// 根因：原版把私信里每句话都渲染成"第 X 赛季第 Y 天，玩家发给 Z 的消息\n&lt;正文&gt;"，
    /// 而解析要求"文本以『确认』开头"，于是永远匹配不上。
    /// 这条用例喂的<b>就是那个真实形状</b>（形状证据见
    /// <c>PlayerTradeCommand</c> 的注释与 <c>InjectDiagnostics</c> 的组装自检）。
    /// </para>
    /// </summary>
    private static void PlayerConfirmCommandSurvivesVanillaWrapper()
    {
        Setup();
        var runtime = EconomyModEntry.Runtime!;
        PrivateIdentitySlot.Set(WorldId, NpcId, NpcId, 12, 1, "test", Data());

        var world = runtime.EnsureWorld(WorldId, Facts())!;
        var ctx = Ctx(world);
        var engine = new TradeEngine(runtime.Config.Economy, runtime.Config.Personality,
            runtime.Config.Prompt, Silent);
        var reserve = engine.Submit(ctx, new TradeIntent(TradeKinds.Sponsor, TradeDirections.NpcToPlayer,
            NpcId, EconomyTier.PlayerId, 10_000, [], "测试报价", "[Trade: test]"));
        TestRunner.True(reserve.Ok, "前置：应能落单：" + reserve.Check.Message);
        string offerId = reserve.OfferId!;

        var identity = PrivateIdentitySlot.Peek();
        TestRunner.True(identity != null, "前置：身份槽里应有身份");

        // 原版真实形状：壳在首行，玩家正文在换行之后。
        string wrapped = $"第 1 赛季第 12 天，玩家发给{NpcId}的消息\n确认 {offerId}";
        string moneyBefore = TradePreviewTests.MoneySnapshot(ctx);

        int handled = TradePreviewWiring.HandlePlayerCommands(identity!, [wrapped], Silent);

        TestRunner.Equal(1, handled, "包了壳的确认指令必须被认出来（真机就是在这里静默失效的）");
        var status = TradeConfirmationGate.Status(ctx, offerId);
        TestRunner.True(status.PlayerConfirmed, "玩家那一票必须被记上");
        TestRunner.Equal(moneyBefore, TradePreviewTests.MoneySnapshot(ctx),
            "★认出指令也不得动任何钱");
        Console.WriteLine("      · 包壳原文：" + wrapped.Replace("\n", "\\n"));
    }

    /// <summary>
    /// <b>真机事故回归（2026-10-11 21:36）</b>：弹窗 21:30:55 弹出、玩家 21:36:08 才点，
    /// 隔了 5 分 13 秒 —— 而身份槽保鲜期是 5 分钟，于是那一票被"身份已过期"挡掉，
    /// 交易卡在未就绪（日志原文：`[Economy-Confirm] 身份槽已空/过期，无法登记玩家那一票`）。
    /// <para>
    /// 登记一票只需要"哪个世界、哪张单"，人的思考时间不该被 TTL 限制。
    /// 这条钉住：<b>身份槽清空之后，玩家的确认/拒绝仍然必须被登记</b>。
    /// </para>
    /// </summary>
    private static void PlayerVoteSurvivesExpiredIdentitySlot()
    {
        Setup();
        var runtime = EconomyModEntry.Runtime!;
        var world = runtime.EnsureWorld(WorldId, Facts())!;
        var ctx = Ctx(world);
        var engine = new TradeEngine(runtime.Config.Economy, runtime.Config.Personality,
            runtime.Config.Prompt, Silent);
        var reserve = engine.Submit(ctx, new TradeIntent(TradeKinds.Sponsor, TradeDirections.NpcToPlayer,
            NpcId, EconomyTier.PlayerId, 10_000, [], "测试报价", "[Trade: test]"));
        TestRunner.True(reserve.Ok, "前置：应能落单：" + reserve.Check.Message);
        string offerId = reserve.OfferId!;

        // 模拟"玩家想了几分钟才点"：身份槽已经过期/清空。
        PrivateIdentitySlot.Clear();
        TestRunner.True(PrivateIdentitySlot.Peek() == null, "前置：身份槽应已空");

        string moneyBefore = TradePreviewTests.BalanceSnapshot(ctx);
        var status = TradePreviewWiring.ApplyPlayerAnswer(WorldId, offerId, confirm: true, Silent, "单元测试");
        TestRunner.True(status != null, "身份槽为空也必须能登记玩家那一票（否则真机会丢票）");
        TestRunner.True(status!.PlayerConfirmed, "玩家那一票必须被记上");
        TestRunner.Equal(moneyBefore, TradePreviewTests.BalanceSnapshot(ctx),
            "登记一票绝不能动钱");
        Console.WriteLine("      · " + status.DetailText);
    }

    /// <summary>
    /// <b>阶段 4 第二步（§7.2/§7.3）端到端</b>：打开动钱开关后，玩家确认的那一刻要
    /// <b>真的结算</b>——NPC 余额减少、玩家影子余额增加、交易单置为 confirmed、
    /// 状态落盘、audit jsonl 追加一行。
    /// <para>
    /// 它是"接线是否正确"的唯一证据：<c>TradeSettlement</c> 本身有 11 条零依赖单测，
    /// 但"确认 → 结算"这根线只有在这里能被证明接上了。
    /// </para>
    /// </summary>
    private static void PlayerConfirmSettlesWhenSwitchIsOn()
    {
        Setup();
        var runtime = EconomyModEntry.Runtime!;

        // 走 WorldFor 而不是 EnsureWorld：前者顺带完成"玩家影子钱包首次导入"。
        var world = runtime.WorldFor(Data())!;
        TestRunner.True(world.Player.Imported, "前置：WorldFor 应完成玩家影子钱包首次导入");
        long playerBefore = world.Player.Balance;

        PrivateIdentitySlot.Set(WorldId, NpcId, NpcId, 12, 1, "test", Data());
        var ctx = Ctx(world);

        // 走【真实路径】落单（模型标记 → 补全 → Reserve → NPC 投票 → 落盘），
        // 而不是直接调 engine.Submit：待确认单的强制落盘就挂在这条路上。
        var intent = new TradeIntent(TradeKinds.Sponsor, TradeDirections.NpcToPlayer, NpcId,
            EconomyTier.PlayerId, 10_000, [], "测试报价", "[Trade: 赞助, Amount: 10000]");
        TradePreviewWiring.HandleModelIntent(intent, Silent);

        var preview = TradePreviewWiring.LastPreview;
        TestRunner.True(preview != null, "前置：应生成只读预览");
        string offerId = preview!.OfferId;

        // 待确认单必须【已经落盘】（§7.3：单子不再只在内存里）。
        var onDisk = new EconomyStore(runtime.Store.Root).LoadWorld(WorldId).World;
        TestRunner.True(onDisk.Offers.ContainsKey(offerId),
            "★待确认单必须已经写进磁盘（真机就是在这里栽过：重启后单号失效）");

        var identity = PrivateIdentitySlot.Peek();
        TestRunner.True(identity != null, "前置：身份槽里应有身份");

        long npcBefore = world.Npcs[NpcId].Balance;

        // 打开"动钱"开关（默认是关的），跑完一定还原。
        TradeConfirmationGate.WireSettlement(true);
        try
        {
            int handled = TradePreviewWiring.HandlePlayerCommands(identity!, ["确认 " + offerId], Silent);
            TestRunner.Equal(1, handled, "玩家指令应被处理");
        }
        finally
        {
            TradeConfirmationGate.WireSettlement(false);
        }

        TestRunner.Equal(npcBefore - 10_000, world.Npcs[NpcId].Balance, "★NPC 的余额必须真的减少");
        TestRunner.Equal(playerBefore + 10_000, world.Player.Balance, "★玩家影子余额必须真的增加");
        TestRunner.Equal(TradeOfferStates.Confirmed, world.Offers[offerId].State, "单子应置为已确认");
        TestRunner.True(world.Offers[offerId].LedgerEntryId.Length > 0, "单子要记下流水 id");

        var settled = TradePreviewWiring.LastSettlement;
        TestRunner.True(settled != null, "应留下最近一次结算结论");
        TestRunner.Equal(SettlementStatus.Applied, settled!.Status, "结论应是「已结算」");
        TestRunner.Equal(10_000L, settled.AmountUsd);

        // §7.3 验收：结算之后重启（重新读盘）状态仍在，审计文件里多一行。
        var reloaded = new EconomyStore(runtime.Store.Root).LoadWorld(WorldId).World;
        TestRunner.Equal(TradeOfferStates.Confirmed, reloaded.Offers[offerId].State,
            "重启后单子仍是已确认（强制落盘生效）");
        TestRunner.Equal(playerBefore + 10_000, reloaded.Player.Balance, "重启后影子余额仍是结算后的");
        TestRunner.True(reloaded.Receipts.Contains(settled.ReceiptKey),
            "幂等键要落盘，否则重启后同一笔会再动一次钱");

        var audit = runtime.Audit;
        TestRunner.True(audit != null, "运行时应有审计写入器");
        var lines = audit!.Read(WorldId);
        TestRunner.True(lines.Any(l => l.Kind == AuditKinds.TradeSettled && l.OfferId == offerId),
            "audit jsonl 里应有一行本次结算");
        Console.WriteLine("      · " + settled.ReceiptText);
    }

    // ── 阶段 4 第三步：账本真相通道（未成立 / 已结算 都要到玩家与模型眼前）──

    /// <summary>
    /// <b>真机症状 1+2 的回归（2026-10-11 真机三轮）</b>：模型提出一个必然被校验链拒掉的金额时，
    /// 原先只有一行日志 —— 玩家看不到"这笔没成"、模型下一轮还用"钱我已经转过去了"继续编。
    /// <para>
    /// 现在要求：<b>被拒之后，影子存档上留下"未成立 + 原因 + 金额"，并且同一份内容真的进了 V 段</b>
    /// （V 段是玩家与模型唯一都看得见的地方）。
    /// </para>
    /// </summary>
    /// <summary>
    /// <b>真机症状 1+2 的回归（2026-10-11 真机三轮）</b>：模型提出一个必然被校验链拒掉的金额时，
    /// 原先只有一行日志 —— 玩家看不到"这笔没成"、模型下一轮还用"钱我已经转过去了"继续编。
    /// <para>
    /// 现在要求：<b>被拒之后，影子存档上留下"未成立 + 原因 + 金额"，并且同一份内容真的进了 V 段</b>
    /// （V 段是玩家与模型唯一都看得见的地方）。
    /// </para>
    /// <para>
    /// 这套件里每个用例<b>各用一个独立世界号</b>：真机上一个世界只有一份账本，
    /// 而测试进程里 <c>EnsureWorld</c> 会跨用例缓存同一个世界——共用世界号会让"上一次拒了什么"
    /// 污染下一次断言（本用例踩过：幂等闸与每日上限会让新意图在第一道闸就被挡掉）。
    /// </para>
    /// </summary>
    private static void RejectedIntentBecomesVisibleNotSettledNote()
    {
        Setup();
        var runtime = EconomyModEntry.Runtime!;
        string worldId = "note-reject";
        var world = runtime.EnsureWorld(worldId, Facts())!;
        PrivateIdentitySlot.Set(worldId, NpcId, NpcId, 12, 1, "test", Data(worldId));
        TradePreviewWiring.Clear(worldId);
        TradePreviewWiring.RememberPlayerTexts(worldId, []);

        string moneyBefore = TradePreviewTests.MoneySnapshot(Ctx(world));

        // 5,000 万：任何档位的余额校验之前，金额边界闸就会把它挡下（AMOUNT_TOO_LARGE）。
        const long huge = 50_000_000;
        var intent = new TradeIntent(TradeKinds.Sponsor, TradeDirections.NpcToPlayer, NpcId,
            EconomyTier.PlayerId, huge, [], "测试报价", $"[Trade: 赞助, Amount: {huge}]");
        TradePreviewWiring.HandleModelIntent(intent, Silent);

        var note = TradePreviewWiring.LastNoteOf(world);
        TestRunner.True(note != null, "★被拒之后必须留下「未成立」的结论（这就是本阶段的病根）");
        TestRunner.False(note!.Settled, "被拒的绝不能被当成已结算");
        TestRunner.Contains(note.Reason, "AMOUNT_TOO_LARGE", "原因里要带拒绝码（模型据此换金额）");
        TestRunner.Equal(huge, note.AmountUsd, "要记下它想给多少（「该要多少钱」是可操作信息）");
        TestRunner.Equal(NpcId, note.NpcId, "要指名道姓，否则文案里说不清是谁");
        TestRunner.True(TradePreviewWiring.PendingFor(worldId) == null,
            "★被拒时不该留下任何待确认预览（否则界面会说还有一笔在等确认）");

        string? vNote = TradePreviewWiring.NoteFor(world);
        TestRunner.True(vNote != null, "V 段注记必须拿得到");
        TestRunner.Contains(vNote!, "未成立", "★V 段里必须出现「未成立」——玩家与模型都靠这一行");
        TestRunner.Contains(vNote!, "AMOUNT_TOO_LARGE", "V 段里要带拒绝码");
        TestRunner.Contains(vNote!, MoneyUnits.Format(huge), "V 段里要带金额");
        TestRunner.Contains(vNote!, "钱一分没动", "V 段里必须明说钱没动（这句就是给模型看的）");
        TestRunner.Contains(vNote!, TradeNoteText.LedgerRuleLine, "每轮都要带上账本口径");
        TestRunner.NotContains(vNote!.Split('\n')[0], "已结算",
            "未成立那一行绝不能出现「已结算」（口径行里出现它是在讲规则，不算）");

        TestRunner.Equal(moneyBefore, TradePreviewTests.MoneySnapshot(Ctx(world)),
            "★记录「未成立」绝不能动钱");

        // 未成立也要跨重启可见（真机是关掉游戏再进来看的）。
        var reloaded = new EconomyStore(runtime.Store.Root).LoadWorld(worldId).World;
        TestRunner.True(reloaded.LastTrade != null, "未成立的结论必须落了盘（重启后玩家还要看得见）");
        TestRunner.Contains(reloaded.LastTrade!.Reason, "AMOUNT_TOO_LARGE", "落盘的结论要带原因");
        Console.WriteLine("      · V 段注记：" + vNote!.Replace("\n", " ⏎ "));
        TradePreviewWiring.Clear(worldId);
    }

    /// <summary>
    /// <b>真机症状 3 的回归</b>：结算之后玩家<b>不翻日志</b>也要能知道成了。
    /// <para>
    /// 这里钉三件事：① 结算真的发生了（钱动了）；② 之后<b>开关已关</b>、同一张单再来一次标记命中幂等闸时，
    /// V 段给的仍然是"已结算"的回执——回执是终局事实，不许被任何后续"未成立"覆盖；
    /// ③ 回执与审计回读都落了盘，重启后仍在。
    /// </para>
    /// </summary>
    private static void SettledReceiptReachesTheVolatileSegment()
    {
        Setup();
        var runtime = EconomyModEntry.Runtime!;
        string worldId = "note-settle-fresh";
        var world = runtime.WorldFor(Data(worldId))!;
        PrivateIdentitySlot.Set(worldId, NpcId, NpcId, 12, 1, "test", Data(worldId));
        TradePreviewWiring.Clear(worldId);
        TradePreviewWiring.RememberPlayerTexts(worldId, []);

        TestRunner.True(world.Player.Imported, "前置：WorldFor 应完成玩家影子钱包首次导入");
        long npcBefore = world.Npcs[NpcId].Balance;
        long playerBefore = world.Player.Balance;

        // 金额取 "余额的 30%" 与 1 万中的较大者，并夹在单笔上限内：
        // 下限是为了过"情绪闸"（金额 ÷ max(500, 余额×10%) 必须 ≥ 1，即至少 10% 余额），
        // 上限是为了远离性格上限。这三个数都只看本条用例自己的世界，所以与别的用例无关。
        long amount = Math.Clamp(npcBefore * 3 / 10, 1_000, 50_000);

        var intent = new TradeIntent(TradeKinds.Sponsor, TradeDirections.NpcToPlayer, NpcId,
            EconomyTier.PlayerId, amount, [], "测试报价", $"[Trade: 赞助, Amount: {amount}]");
        TradePreviewWiring.HandleModelIntent(intent, Silent);
        var preview = TradePreviewWiring.LastPreview;
        TestRunner.True(preview != null, "前置：应当落一张待确认单（金额 " + amount + "）");
        TestRunner.Equal(amount, preview!.AmountUsd, "前置：预览的金额必须就是我们要的那一笔");
        string offerId = preview.OfferId;
        var identity = PrivateIdentitySlot.Peek()!;

        TradeConfirmationGate.WireSettlement(true);
        try
        {
            TradePreviewWiring.HandlePlayerCommands(identity, ["确认 " + offerId], Silent);
        }
        finally
        {
            TradeConfirmationGate.WireSettlement(false);
        }

        var settled = TradePreviewWiring.LastSettlement;
        TestRunner.True(settled != null && settled.Status == SettlementStatus.Applied,
            "前置：这一笔应当真的结算：" + (settled?.Detail ?? "<无结论>"));
        TestRunner.Equal(npcBefore - amount, world.Npcs[NpcId].Balance, "前置：NPC 余额应真的减少");
        TestRunner.Equal(playerBefore + amount, world.Player.Balance, "前置：玩家影子余额应真的增加");

        var justSettled = TradePreviewWiring.LastNoteOf(world);
        TestRunner.True(justSettled != null && justSettled.Settled, "★结算之后必须是「已结算」结论");
        TestRunner.Equal(offerId, justSettled!.OfferId, "回执要能对上单号");

        // 关键一步：开关【已经关掉】（真机验收完就是这么收尾的），同一张单再来一次标记。
        // 它必然命中幂等闸并被记成"未成立"，但**已结算的回执不许被它覆盖**。
        TradePreviewWiring.HandleModelIntent(intent, Silent);

        var note = TradePreviewWiring.LastNoteOf(world);
        TestRunner.True(note != null && note.Settled,
            "★已结算的回执不许被后续任何动作覆盖成未成立（否则玩家会以为钱没到）");
        TestRunner.Contains(note!.Receipt, "已结算", "回执要说清成了");
        TestRunner.Contains(note.Receipt, MoneyUnits.Format(playerBefore + amount),
            "回执要带结算后的玩家影子余额");
        TestRunner.True(TradePreviewWiring.PendingFor(worldId) == null,
            "这一笔已终态，不该再有待确认预览");

        string? vNote = TradePreviewWiring.NoteFor(world);
        TestRunner.True(vNote != null, "V 段注记必须拿得到");
        TestRunner.Contains(vNote!, "已结算", "★玩家下次打开私信就能看到「已结算」回执");
        TestRunner.Contains(vNote!, MoneyUnits.Format(playerBefore + amount), "V 段回执要带余额");
        TestRunner.Contains(vNote!, "最近结算", "§7.4：审计回读也要出现在 V 段");
        TestRunner.Contains(vNote!, offerId, "回读里要能对上这一笔的单号");
        TestRunner.Contains(vNote!, TradeNoteText.LedgerRuleLine, "账本口径每轮都在");

        var reloaded = new EconomyStore(runtime.Store.Root).LoadWorld(worldId).World;
        TestRunner.True(reloaded.LastTrade != null && reloaded.LastTrade.Settled,
            "已结算的回执也要落盘（重启后仍能看到）");
        Console.WriteLine("      · V 段注记：" + vNote!.Replace("\n", " ⏎ "));
        TradePreviewWiring.Clear(worldId);
    }
    /// <summary>玩家拒绝也要成为可见的"未成立"（否则 NPC 下一轮还会重复报价）。</summary>
    private static void PlayerDeclineBecomesVisibleNotSettledNote()
    {
        Setup();
        var runtime = EconomyModEntry.Runtime!;
        var world = runtime.EnsureWorld(WorldId, Facts())!;
        PrivateIdentitySlot.Set(WorldId, NpcId, NpcId, 12, 1, "test", Data());
        // 这个用例与世界号 "plan-a-world" 共用账本：先复位"最近一笔"并清掉回声文本，
        // 否则上一条用例留下的"已结算"回执会挡住本次拒绝（RecordNote 刻意不许覆盖已结算）。
        TradePreviewWiring.Clear(WorldId);
        TradePreviewWiring.RememberPlayerTexts(WorldId, []);
        TradePreviewWiring.ResetNote(world);

        var intent = new TradeIntent(TradeKinds.Sponsor, TradeDirections.NpcToPlayer, NpcId,
            EconomyTier.PlayerId, 10_000, [], "测试报价", "[Trade: 赞助, Amount: 10000]");
        TradePreviewWiring.HandleModelIntent(intent, Silent);
        string offerId = TradePreviewWiring.LastPreview!.OfferId;

        var status = TradePreviewWiring.ApplyPlayerAnswer(WorldId, offerId, confirm: false, Silent, "单元测试");
        TestRunner.True(status != null, "前置：拒绝应当被登记");

        var note = TradePreviewWiring.LastNoteOf(world);
        TestRunner.True(note != null, "★玩家拒绝之后也要留下结论");
        TestRunner.Equal(TradeNoteStatuses.Declined, note!.Status, "状态应是「玩家拒绝」");
        TestRunner.False(note.Settled);

        string? vNote = TradePreviewWiring.NoteFor(world);
        TestRunner.Contains(vNote ?? "", "被你拒绝", "V 段要如实说这是玩家自己拒的");
        TradePreviewWiring.Clear(WorldId);
        TradePreviewWiring.RememberPlayerTexts(WorldId, []);
    }

    /// <summary>
    /// <b>第三步 §7.3 的"哨兵应该变少"在日志上怎么判读</b>：
    /// 模型这一轮说"钱我打过去了"而<b>没发标记</b> ⇒ 哨兵仍然照写（这是它存在的意义），
    /// 但它现在会**带上账本真相**：
    /// <list type="bullet">
    /// <item>账上确实转过 ⇒ 写"已给过你 $X（钱确实动过）"，看日志的人不会把复述当撒谎；</item>
    /// <item>账上没动 ⇒ 写"未成立…钱没动"。</item>
    /// </list>
    /// 于是"模型嘴上说转了"与"系统真的动了账"在同一行里就能分开，不需要再去翻别的日志。
    /// </summary>
    private static void TransferClaimSentinelTellsTheLedgerTruth()
    {
        Setup();
        var runtime = EconomyModEntry.Runtime!;
        string worldId = "note-sentinel";
        var world = runtime.EnsureWorld(worldId, Facts())!;
        PrivateIdentitySlot.Set(worldId, NpcId, NpcId, 12, 1, "test", Data(worldId));

        var lines = new List<(LogLevel Level, string Message)>();
        var captured = new DelegateEconomyLog((level, _, message, _) => lines.Add((level, message)));

        // 先造一条"未成立"的真相：$5,000 万必然被金额上限挡下。
        TradePreviewWiring.Clear(worldId);
        TradePreviewWiring.ResetNote(world);
        TradePreviewWiring.RememberPlayerTexts(worldId, []);
        var rejected = new TradeIntent(TradeKinds.Sponsor, TradeDirections.NpcToPlayer, NpcId,
            EconomyTier.PlayerId, 50_000_000, [], "测试报价", "[Trade: 赞助, Amount: 50000000]");
        TradePreviewWiring.HandleModelIntent(rejected, Silent);
        TestRunner.False(TradePreviewWiring.LastNoteOf(world)!.Settled, "前置：这一笔必须是未成立");

        TradeMarkerInterceptor.Install(captured);
        try
        {
            var parser = new SentinelParserDouble();
            FeedSentenceThrough(parser, "嗯，钱我已经打过去了，你查一下到没到。");

            string? sentinel = lines
                .Where(l => l.Level == LogLevel.Warning && l.Message.Contains("没有】交易标记"))
                .Select(l => l.Message)
                .FirstOrDefault();
            TestRunner.True(sentinel != null,
                "没有标记却声称转账时，哨兵仍必须留下。实际：" + string.Join(" / ", lines.Select(l => l.Message)));
            TestRunner.Contains(sentinel!, "钱没动", "★哨兵必须说出账本真相（这一笔真的没动钱）");
            TestRunner.Contains(sentinel!, "AMOUNT_TOO_LARGE", "要能对上拒绝码");
        }
        finally
        {
            TradeMarkerInterceptor.Install(Silent);
        }

        // 再把它变成"钱真的动过"的那种复述：哨兵行里应改成"已给过你…钱确实动过"。
        lines.Clear();
        TradePreviewWiring.RecordNote(world, TradeNoteStatuses.Refused, "", "", 0, "清场");
        TradePreviewWiring.ResetNote(world);
        world.LastTrade = new LastTradeNote
        {
            OfferId = "o0009", Kind = TradeKinds.Sponsor, AmountUsd = 500, NpcId = NpcId,
            Status = TradeNoteStatuses.Settled, Receipt = "【已结算】赞助 $500"
        };
        TradeMarkerInterceptor.Install(captured);
        try
        {
            var parser = new SentinelParserDouble();
            FeedSentenceThrough(parser, "钱我上次就转过去了，你没收到吗？");

            string? sentinel = lines
                .Where(l => l.Level == LogLevel.Warning && l.Message.Contains("没有】交易标记"))
                .Select(l => l.Message)
                .FirstOrDefault();
            TestRunner.True(sentinel != null, "这一轮同样没有标记，哨兵仍要写");
            TestRunner.Contains(sentinel!, "钱确实动过", "★钱真的转过时不许再说「账上一分未动」");
            TestRunner.Contains(sentinel!, MoneyUnits.Format(500), "要写出转过的金额");
        }
        finally
        {
            TradeMarkerInterceptor.Install(Silent);
            TradePreviewWiring.ResetNote(world);
            TradePreviewWiring.Clear(worldId);
        }
    }

    /// <summary>
    /// 哨兵用例用的最小原版解析器替身：只负责"喂字符 → 攒可见正文"，
    /// 形状与 <c>TradeMarkerInterceptorTests.StreamParserDouble</c> 同构（那边是 private，
    /// 但**必须**经由 <c>RewriteFeed</c>/<c>RewriteFinish</c> 喂进去，否则拦截器根本看不见）。
    /// </summary>
    private sealed class SentinelParserDouble
    {
        private readonly System.Text.StringBuilder _visible = new();
        private readonly System.Text.StringBuilder _pending = new();
        private int _depth;

        public string Text => _visible.ToString();

        public string Feed(string rewritten)
        {
            foreach (char ch in rewritten)
            {
                if (_pending.Length == 0)
                {
                    if (ch == '[') { _pending.Append(ch); _depth = 1; }
                    else _visible.Append(ch);
                    continue;
                }

                _pending.Append(ch);
                if (ch == '[') _depth++;
                if (ch != ']' || --_depth != 0) continue;
                _pending.Clear();
            }
            return Text;
        }

        public void Finish()
        {
            if (_pending.Length > 0) _visible.Append(_pending);
            _pending.Clear();
            _depth = 0;
        }
    }

    /// <summary>把一句话按"原版逐块喂进来"的方式走一遍拦截器，并结束这一轮。</summary>
    private static void FeedSentenceThrough(SentinelParserDouble parser, string text)
    {
        TradeMarkerInterceptor.RewriteFeed(parser, text, out string rewritten);
        parser.Feed(rewritten);
        TradeMarkerInterceptor.RewriteFinish(parser);
        parser.Finish();
    }

    /// <summary>
    /// <b>真机 2026-10-12 01:31 的第二次落盘踩坑（回归）</b>：重启后对**已经结算过**的单再发一次
    /// <c>确认 o0001</c>，日志明写"已是 confirmed 状态，确认被拒绝"，
    /// 而影子存档里**连 <c>LastTrade</c> 这个键都没有**——因为幂等命中时
    /// <c>TradeConfirmationGate.Confirm</c> 直接返回、**根本走不到 <c>TrySettle</c>**，
    /// 于是补在 <c>TradeSettlement</c> 里的落盘永远轮不到。
    /// <para>
    /// 这条钉住：**玩家对一张已终结的单表态，也必须把"这一笔已结算"的结论落进存档**，
    /// 且账本余额一分不动（它只是记账，不是结算）。
    /// </para>
    /// </summary>
    private static void ReConfirmingSettledOfferRePersistsTheReceipt()
    {
        Setup();
        var runtime = EconomyModEntry.Runtime!;
        string worldId = "note-reconfirm";
        var world = runtime.WorldFor(Data(worldId))!;
        PrivateIdentitySlot.Set(worldId, NpcId, NpcId, 12, 1, "test", Data(worldId));
        TradePreviewWiring.Clear(worldId);
        TradePreviewWiring.RememberPlayerTexts(worldId, []);
        TradePreviewWiring.ResetNote(world);

        long npcBefore = world.Npcs[NpcId].Balance;
        long playerBefore = world.Player.Balance;
        long amount = Math.Clamp(npcBefore * 3 / 10, 1_000, 50_000);

        var intent = new TradeIntent(TradeKinds.Sponsor, TradeDirections.NpcToPlayer, NpcId,
            EconomyTier.PlayerId, amount, [], "测试报价", $"[Trade: 赞助, Amount: {amount}]");
        TradePreviewWiring.HandleModelIntent(intent, Silent);
        string offerId = TradePreviewWiring.LastPreview!.OfferId;
        var identity = PrivateIdentitySlot.Peek()!;

        // ① 真的结算一次（开关临时打开），确认单子进了 confirmed 终态。
        TradeConfirmationGate.WireSettlement(true);
        try
        {
            TradePreviewWiring.HandlePlayerCommands(identity, ["确认 " + offerId], Silent);
        }
        finally
        {
            TradeConfirmationGate.WireSettlement(false);
        }
        TestRunner.Equal(SettlementStatus.Applied, TradePreviewWiring.LastSettlement!.Status,
            "前置：这一笔应当真的结算");
        long npcAfterSettle = world.Npcs[NpcId].Balance;
        long playerAfterSettle = world.Player.Balance;

        // ② 模拟"重启后注记丢了"（旧 build 就是这么躺在盘上的）：把内存里的结论清掉。
        //    这一步很关键——它让下面走到的正是真机 01:31 那条出口。
        TradePreviewWiring.ResetNote(world);
        TradePreviewWiring.Clear(worldId);
        TestRunner.True(world.LastTrade == null, "前置：注记已清空，模拟重启后的旧存档");

        // ③ 再发一次确认（幂等闸会拒）：日志里会写"已是 confirmed 状态，确认被拒绝"。
        int handled = TradePreviewWiring.HandlePlayerCommands(identity, ["确认 " + offerId], Silent);
        TestRunner.Equal(1, handled, "指令应被认出");

        // ④ 核心断言：结论必须被补记回来（否则重启后私信里永远看不到回执）。
        var note = TradePreviewWiring.LastNoteOf(world);
        TestRunner.True(note != null,
            "★对已终结的单表态，也必须把「已结算」结论补进存档（真机 01:31 就是这里丢的）");
        TestRunner.True(note!.Settled, "补记的必须是「已结算」，不能是「未成立」");
        TestRunner.Equal(TradeNoteStatuses.AlreadySettled, note.Status, "状态应是 already_settled");
        TestRunner.Equal(offerId, note.OfferId, "要能对上单号");
        TestRunner.Contains(note.Receipt, "已结算", "回执正文要能直接给玩家看");
        TestRunner.Equal(amount, note.AmountUsd, "金额要来自那张单");
        TestRunner.Contains(note.Receipt, MoneyUnits.Format(npcAfterSettle),
            "回执里的 NPC 余额要取账本现值，不能是编出来的 0");
        TestRunner.Contains(note.Receipt, MoneyUnits.Format(playerAfterSettle),
            "回执里的玩家影子余额也要取现值");

        // ⑤ 记账不许动钱：余额与 audit 都不该变。
        TestRunner.Equal(npcAfterSettle, world.Npcs[NpcId].Balance, "★补记结论不得动 NPC 余额");
        TestRunner.Equal(playerAfterSettle, world.Player.Balance, "★补记结论不得动玩家影子余额");

        var reloaded = new EconomyStore(runtime.Store.Root).LoadWorld(worldId).World;
        TestRunner.True(reloaded.LastTrade != null && reloaded.LastTrade.Settled,
            "★补记之后必须真的在盘上（重启再读还能看到回执）");
        TestRunner.Contains(reloaded.LastTrade!.Receipt, offerId, "落盘的回执要带单号");
        Console.WriteLine("      · 补记回执：" + note.Receipt);
        TradePreviewWiring.ResetNote(world);
        TradePreviewWiring.Clear(worldId);
    }

    /// <summary>
    /// <b>补记不许张冠李戴</b>：世界上的"最近一笔"是单 A 的已结算回执，而玩家在弹窗上对
    /// **另一张还没到齐的单 B** 表态（`ReadyToSettle == false`）——这条出口绝不能用 B 去覆盖 A 的回执，
    /// 也不该凭空给 B 编一条"已结算"。
    /// <para>
    /// 它是 `NoteClosedTrade` 那条前置闸的回归：**只认"这张单确实处于 confirmed"**，
    /// 否则"还差 NPC 一票"也会掉进同一条分支。
    /// </para>
    /// </summary>
    /// <summary>
    /// <b>补记不许张冠李戴</b>（`NoteClosedTrade` 那条前置闸的回归）：
    /// 玩家在弹窗上对一张**已经终结**的单再点一次确认时（`ReadyToSettle` 为 false，
    /// 而且**不会**走到 `TrySettle`），`NoteClosedTrade` 必须：
    /// <list type="number">
    /// <item>在"最近一笔"是**同一张单**时，把已结算结论补落盘；</item>
    /// <item>在"最近一笔"是**别的单**时，<b>什么都不写</b> —— 绝不能用别的单的结论顶掉它。</item>
    /// </list>
    /// 真机 01:31 就是第 1 条没成立（存档里连 <c>LastTrade</c> 键都没有）；第 2 条是我改这个函数时
    /// 自己差点引进的反向 bug，所以一并钉住。
    /// </summary>
    private static void OtherOffersMustNotClobberTheSettledReceipt()
    {
        Setup();
        var runtime = EconomyModEntry.Runtime!;
        string worldId = "note-decoys";
        var world = runtime.WorldFor(Data(worldId))!;
        PrivateIdentitySlot.Set(worldId, NpcId, NpcId, 12, 1, "test", Data(worldId));
        TradePreviewWiring.Clear(worldId);
        TradePreviewWiring.RememberPlayerTexts(worldId, []);
        TradePreviewWiring.ResetNote(world);

        long npcBefore = world.Npcs[NpcId].Balance;
        long amount = Math.Clamp(npcBefore * 3 / 10, 1_000, 50_000);

        var intent = new TradeIntent(TradeKinds.Sponsor, TradeDirections.NpcToPlayer, NpcId,
            EconomyTier.PlayerId, amount, [], "测试报价", $"[Trade: 赞助, Amount: {amount}]");
        TradePreviewWiring.HandleModelIntent(intent, Silent);
        TestRunner.True(TradePreviewWiring.LastPreview != null, "前置：应落一张待确认单");
        string offerId = TradePreviewWiring.LastPreview!.OfferId;

        // ① 先真的结算一次，单子进入 confirmed 终态。
        TradeConfirmationGate.WireSettlement(true);
        try
        {
            TradePreviewWiring.HandlePlayerCommands(PrivateIdentitySlot.Peek()!, ["确认 " + offerId], Silent);
        }
        finally
        {
            TradeConfirmationGate.WireSettlement(false);
        }
        TestRunner.Equal(SettlementStatus.Applied, TradePreviewWiring.LastSettlement!.Status, "前置：应当结算");
        TestRunner.Equal(TradeOfferStates.Confirmed, world.Offers[offerId].State, "前置：单子应是 confirmed");

        // ② 把"最近一笔"换成一个**别的单号**的假结论，然后清掉内存（模拟重启后的旧存档）。
        string decoyReceipt = "【已结算】别的单，不许被顶掉";
        world.LastTrade = new LastTradeNote
        {
            OfferId = "o9999", Kind = TradeKinds.Sponsor, AmountUsd = 999, NpcId = NpcId,
            Status = TradeNoteStatuses.Settled, Receipt = decoyReceipt
        };

        // ③ 玩家在弹窗上对这张已终结的单再点一次确认：
        //    ReadyToSettle 为 false，TrySettle 不会被调用 —— 正是真机 01:31 那条出口。
        long npcAfterSettle = world.Npcs[NpcId].Balance;
        long playerAfterSettle = world.Player.Balance;
        var status = TradePreviewWiring.ApplyPlayerAnswer(worldId, offerId, confirm: true, Silent, "单元测试");
        TestRunner.True(status != null, "前置：表态应被登记");
        TestRunner.False(status!.ReadyToSettle, "前置：已终结的单不该就绪");

        // ④ 补记的必须是【这张单自己的】结论，而不是把别单的数字照抄过来。
        //    （注意：用 A 的真结论覆盖"最近一笔是别的单"是**正确**的——"最近一笔"本来就该更新为
        //      刚刚结算过的这一张；真正要防的是"拿 B 的数据写成 A 的回执"。）
        var afterStep3 = TradePreviewWiring.LastNoteOf(world);
        TestRunner.True(afterStep3 != null && afterStep3.Settled, "★补记后仍应是「已结算」");
        TestRunner.Equal(offerId, afterStep3!.OfferId, "★回执必须指向这张单，不能停留在别的单号上");
        TestRunner.Equal(amount, afterStep3.AmountUsd, "★金额必须来自这张单");
        TestRunner.Contains(afterStep3.Receipt, MoneyUnits.Format(npcAfterSettle),
            "★余额必须是账本现值，不能照抄别单的假数字");
        TestRunner.NotContains(afterStep3.Receipt, "别单", "★别单那句假话不该出现在回执里");

        // ⑤ 再复位一次并重复表态：结论必须能重建（覆盖"注记丢了 / 跨重启"的情形）。
        TradePreviewWiring.ResetNote(world);
        var status2 = TradePreviewWiring.ApplyPlayerAnswer(worldId, offerId, confirm: true, Silent, "单元测试");
        TestRunner.True(status2 != null, "前置：第二次表态也要被登记");

        var note = TradePreviewWiring.LastNoteOf(world);
        TestRunner.True(note != null, "★对此前已结算的单再表态，必须把「已结算」结论补进存档");
        TestRunner.Equal(offerId, note!.OfferId, "补记的必须就是这张单");
        TestRunner.Equal(TradeNoteStatuses.AlreadySettled, note.Status, "状态应是 already_settled");
        TestRunner.Contains(note.Receipt, "已结算", "回执要说清已经结算过");
        TestRunner.Contains(note.Receipt, MoneyUnits.Format(npcAfterSettle),
            "回执里的 NPC 余额要取账本现值，不能是编出来的 0");

        // ⑥ 补记只记账：余额一分不动，而且必须真的落盘。
        TestRunner.Equal(npcAfterSettle, world.Npcs[NpcId].Balance, "★补记不得动 NPC 余额");
        TestRunner.Equal(playerAfterSettle, world.Player.Balance, "★补记不得动玩家影子余额");
        var reloaded = new EconomyStore(runtime.Store.Root).LoadWorld(worldId).World;
        TestRunner.True(reloaded.LastTrade != null && reloaded.LastTrade.Settled,
            "★补记之后必须真的在盘上（重启再读还能看到回执）");
        TestRunner.Equal(offerId, reloaded.LastTrade!.OfferId, "落盘的回执要指向这张单");
        Console.WriteLine("      · 补记后的回执：" + note.Receipt);

        TradePreviewWiring.ResetNote(world);
        TradePreviewWiring.Clear(worldId);
    }

    /// <summary>
    /// <b>把"写了真相就必须落盘"这条不变量变成测试</b>（真机连踩三次的教训）。
    /// <para>
    /// 真机前两次失败都是同一个形状：**内存里记了、盘上什么都没有**——
    /// 而"记录"与"落盘"是两行分开写的代码，单测各测一半就永远发现不了。
    /// 这条用例改成**只从磁盘判**：每个出口跑完，都用<b>另一个 store 重新读盘</b>，
    /// 盘上必须有那条结论。任何"只写内存忘了落盘"的改动都会在这里当场变红。
    /// </para>
    /// </summary>
    private static void EveryTruthWriteSurvivesAReload()
    {
        Setup();
        var runtime = EconomyModEntry.Runtime!;
        string worldId = "note-durable";
        var world = runtime.EnsureWorld(worldId, Facts())!;
        world.Player = new PlayerEconomy { Imported = true, Balance = 500_000 };
        PrivateIdentitySlot.Set(worldId, NpcId, NpcId, 12, 1, "test", Data(worldId));
        TradePreviewWiring.Clear(worldId);
        TradePreviewWiring.RememberPlayerTexts(worldId, []);
        TradePreviewWiring.ResetNote(world);

        long huge = 50_000_000;

        // 出口 ①：模型提一个必被拒的金额（Reserve 被拒分支）。
        TradePreviewWiring.HandleModelIntent(new TradeIntent(TradeKinds.Sponsor,
            TradeDirections.NpcToPlayer, NpcId, EconomyTier.PlayerId, huge, [], "测试",
            $"[Trade: 赞助, Amount: {huge}]"), Silent);
        AssertOnDisk(runtime, worldId, "被拒（未落单）", note =>
        {
            TestRunner.False(note.Settled, "被拒的不能算已结算");
            TestRunner.Contains(note.Reason, "AMOUNT_TOO_LARGE", "盘上要留下拒绝码");
        });

        // 出口 ②：玩家拒绝（私信指令分支）。
        TradePreviewWiring.ResetNote(world);
        long amount = Math.Clamp(world.Npcs[NpcId].Balance * 3 / 10, 1_000, 50_000);
        TradePreviewWiring.HandleModelIntent(new TradeIntent(TradeKinds.Sponsor,
            TradeDirections.NpcToPlayer, NpcId, EconomyTier.PlayerId, amount, [], "测试",
            $"[Trade: 赞助, Amount: {amount}]"), Silent);
        var preview = TradePreviewWiring.LastPreview;
        TestRunner.True(preview != null, "前置：应落一张待确认单（金额 " + amount + "）");
        PrivateIdentitySlot.Set(worldId, NpcId, NpcId, 12, 1, "test", Data(worldId));
        TradePreviewWiring.HandlePlayerCommands(PrivateIdentitySlot.Peek()!,
            ["确认 " + preview!.OfferId], Silent);   // → 双方到齐但开关是关的 ⇒ NotWired，不落盘（故意）
        TradePreviewWiring.ResetNote(world);
        PrivateIdentitySlot.Set(worldId, NpcId, NpcId, 12, 1, "test", Data(worldId));
        TradePreviewWiring.HandlePlayerCommands(PrivateIdentitySlot.Peek()!,
            ["拒绝 " + preview.OfferId], Silent);
        AssertOnDisk(runtime, worldId, "玩家拒绝", note =>
        {
            TestRunner.Equal(TradeNoteStatuses.Declined, note.Status, "盘上应是「玩家拒绝」");
            TestRunner.False(note.Settled);
        });

        // 出口 ③：对一张已结算的单再表态（真机 01:31/01:37 走的那条）。
        TradePreviewWiring.ResetNote(world);
        long amount2 = Math.Clamp(world.Npcs[NpcId].Balance * 3 / 10, 1_000, 50_000) + 313;
        PrivateIdentitySlot.Set(worldId, NpcId, NpcId, 12, 1, "test", Data(worldId));
        TradePreviewWiring.HandleModelIntent(new TradeIntent(TradeKinds.Sponsor,
            TradeDirections.NpcToPlayer, NpcId, EconomyTier.PlayerId, amount2, [], "测试",
            $"[Trade: 赞助, Amount: {amount2}]"), Silent);
        var preview2 = TradePreviewWiring.LastPreview;
        TestRunner.True(preview2 != null, "前置：应落第二张待确认单");
        TradeConfirmationGate.WireSettlement(true);
        try
        {
            PrivateIdentitySlot.Set(worldId, NpcId, NpcId, 12, 1, "test", Data(worldId));
            TradePreviewWiring.HandlePlayerCommands(PrivateIdentitySlot.Peek()!,
                ["确认 " + preview2!.OfferId], Silent);
        }
        finally
        {
            TradeConfirmationGate.WireSettlement(false);
        }
        TestRunner.Equal(SettlementStatus.Applied, TradePreviewWiring.LastSettlement!.Status,
            "前置：第二笔应当真的结算");

        // 模拟重启：内存里的注记清掉，然后对已终结的单再表一次态。
        TradePreviewWiring.ResetNote(world);
        PrivateIdentitySlot.Set(worldId, NpcId, NpcId, 12, 1, "test", Data(worldId));
        var status = TradePreviewWiring.ApplyPlayerAnswer(worldId, preview2.OfferId, true, Silent, "单元测试");
        TestRunner.True(status != null, "前置：表态应被登记");
        AssertOnDisk(runtime, worldId, "对已结算单再表态", note =>
        {
            TestRunner.True(note.Settled, "★盘上必须是「已结算」");
            TestRunner.Equal(preview2.OfferId, note.OfferId, "要指向这张单");
        });

        TradePreviewWiring.ResetNote(world);
        TradePreviewWiring.Clear(worldId);
    }

    /// <summary>
    /// <b>只从磁盘判</b>：换一个 store 重新读盘，取出 <c>LastTrade</c>，再交给断言。
    /// 读不到就直接判失败 —— 因为"内存里有、盘上没有"正是真机三次失败的形状。
    /// </summary>
    private static void AssertOnDisk(ShadowEconomyRuntime runtime, string worldId, string label,
        Action<NationalSpireEconomy.Economy.LastTradeNote> assert)
    {
        var onDisk = new EconomyStore(runtime.Store.Root).LoadWorld(worldId).World;
        var note = onDisk.LastTrade;
        TestRunner.True(note != null, $"★[{label}] 结论必须落在磁盘上（重启后还要看得见）");
        assert(note!);
    }

    /// <summary>阶段 4 测试用的交易上下文（与 TradeEngineTests 同构）。</summary>
    private static TradeContext Ctx(EconomyWorld world) => new()
    {
        World = world,
        Config = EconomyModEntry.Runtime!.Config.Economy,
        Personality = EconomyModEntry.Runtime.Config.Personality,
        Prompt = EconomyModEntry.Runtime.Config.Prompt,
        Counterparty = Facts().First(f => f.Id == NpcId),
        Favour = 0,
        Day = 12,
        Season = 1,
        Wallet = new PlayerWallet(Data())
    };

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "nse-plana-" + Guid.NewGuid().ToString("N"));
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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "config");
            if (File.Exists(Path.Combine(candidate, ConfigLoader.EconomyFileName))) return candidate;
            directory = directory.Parent;
        }
        throw new Exception("找不到 config/ 目录");
    }
}
#endif
