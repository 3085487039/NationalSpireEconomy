using System.Text.RegularExpressions;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Inject;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 阶段 3 的 S 段注入<b>纯逻辑</b>契约：
/// <list type="number">
/// <item>插在 system 字符串里、锚点句之前，<b>绝不新增 message</b>；</item>
/// <item>稳定段零数字、不含余额；</item>
/// <item><b>只注入 S 段时绝不能清空 <c>PendingVoice</c></b>——它由易变段承载，
/// 这一轮不注入 V 段，清空就等于把软失败的"未竟之言"悄悄吞掉。</item>
/// </list>
/// </summary>
public static class StableInsertionTests
{
    public static Action[] Cases =>
    [
        InsertsBeforeAnchor,
        FallsBackToTailWhenAnchorMissing,
        KeepsMessageCountAndOrder,
        RepeatedInsertIsIdempotent,
        EmptyStableTextIsNoOp,
        CustomAnchorIsHonored,
        InsertDoesNotMutateInput,
        NoSystemMessageIsReported,
        BuildStableOnlyKeepsPendingVoice,
        BuildStableOnlyCommitsFingerprint,
        StableSegmentIsDigitFreeForEveryKind,
        RealConfigProvidesAnchorAndSwitch
    ];

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    private const string TailSentence = "，性格字段描述可以变化的倾向；本次行为与表达遵循上述私信提示词。";

    private static string SystemTemplate() =>
        "【模拟的原版 system：世界设定 + 七串 Protocol】\n" + SystemPromptInsert.DefaultAnchor + TailSentence;

    // ── 插入位置 ────────────────────────────────────────────────────

    private static void InsertsBeforeAnchor()
    {
        string system = SystemTemplate();
        const string stable = "【经济状况】\n你手头宽裕。你的收入主要来自俱乐部工资与胜场奖金。";

        var result = SystemPromptInsert.Insert(system, stable);

        TestRunner.True(result.Changed, "新内容应当被插入");
        TestRunner.Equal(InsertPlacements.BeforeAnchor, result.Placement, "锚点存在时应插在锚点之前");
        TestRunner.Contains(result.Text, stable, "插入后的正文里应有稳定段");
        TestRunner.True(
            result.Text.IndexOf(stable, StringComparison.Ordinal)
            < result.Text.IndexOf(SystemPromptInsert.DefaultAnchor, StringComparison.Ordinal),
            "稳定段必须出现在锚点句之前（紧邻原版协议串之后）");
        TestRunner.True(result.Text.EndsWith("本次行为与表达遵循上述私信提示词。", StringComparison.Ordinal),
            "原版收尾句仍应收尾，不能把稳定段塞到它后面");
    }

    private static void FallsBackToTailWhenAnchorMissing()
    {
        var result = SystemPromptInsert.Insert("原版改了收尾文案，锚点不在了。", "【经济状况】\n你手头宽裕。");

        TestRunner.Equal(InsertPlacements.AtTail, result.Placement, "锚点缺失时应退化为追加到尾部");
        TestRunner.True(result.Changed, "退化路径也应当真的插入");
        TestRunner.Contains(result.Text, "【经济状况】", "退化路径也要写进去");
        TestRunner.Contains(result.Text, "锚点不在了", "原有正文不能被吃掉");
    }

    private static void KeepsMessageCountAndOrder()
    {
        var messages = new List<Dictionary<string, string>>
        {
            new() { ["role"] = "system", ["content"] = SystemTemplate() },
            new() { ["role"] = "user", ["content"] = "【人物资料】" },
            new() { ["role"] = "user", ["content"] = "【当前上下文】" }
        };
        string[] before = messages.Select(m => m["role"]).ToArray();
        const string stable = "【经济状况】\n你手头宽裕。";

        var insertion = SystemPromptInsert.InsertIntoMessages(messages, stable);

        TestRunner.True(insertion.Changed, "应当插入成功");
        TestRunner.Equal(3, messages.Count, "绝不能新增 message——条数必须与原版一致");
        TestRunner.Equal(string.Join(",", before), string.Join(",", messages.Select(m => m["role"])),
            "message 顺序也必须与原版一致");
        TestRunner.Contains(messages[0]["content"], stable, "稳定段应落在 system 消息里");
        TestRunner.NotContains(messages[1]["content"], stable, "不能污染 user 消息");
        TestRunner.NotContains(messages[2]["content"], stable, "不能污染 user 消息");
    }

    private static void RepeatedInsertIsIdempotent()
    {
        var messages = new List<Dictionary<string, string>>
        {
            new() { ["role"] = "system", ["content"] = SystemTemplate() }
        };
        const string stable = "【经济状况】\n你手头宽裕。";

        var first = SystemPromptInsert.InsertIntoMessages(messages, stable);
        string afterFirst = messages[0]["content"];
        var second = SystemPromptInsert.InsertIntoMessages(messages, stable);

        TestRunner.True(first.Changed, "第一次应插入");
        TestRunner.False(second.Changed, "第二次不该再插一遍");
        TestRunner.Equal(InsertPlacements.AlreadyPresent, second.Placement, "第二次应报告「已存在」");
        TestRunner.Equal(afterFirst, messages[0]["content"], "重复插入不该改动正文");
        TestRunner.Equal(1, CountOccurrences(afterFirst, "【经济状况】"), "正文里只应出现一次稳定段标题");
    }

    private static void EmptyStableTextIsNoOp()
    {
        string system = SystemTemplate();
        var result = SystemPromptInsert.Insert(system, "");

        TestRunner.False(result.Changed, "稳定段为空时不该改动正文");
        TestRunner.Equal(InsertPlacements.NoStableText, result.Placement);
        TestRunner.Equal(system, result.Text, "正文必须逐字节不变");
    }

    private static void CustomAnchorIsHonored()
    {
        const string custom = "这是一条自定义锚点";
        var result = SystemPromptInsert.Insert("前缀\n" + custom + "\n后缀", "【经济状况】\n你手头宽裕。", custom);

        TestRunner.Equal(InsertPlacements.BeforeAnchor, result.Placement, "自定义锚点也要生效");
        TestRunner.True(
            result.Text.IndexOf("【经济状况】", StringComparison.Ordinal)
            < result.Text.IndexOf(custom, StringComparison.Ordinal),
            "自定义锚点下同样要插在锚点之前");
    }

    private static void InsertDoesNotMutateInput()
    {
        string system = SystemTemplate();
        string original = system;
        SystemPromptInsert.Insert(system, "【经济状况】\n你手头宽裕。");

        TestRunner.Equal(original, system, "Insert 不该原地修改入参字符串");
    }

    private static void NoSystemMessageIsReported()
    {
        var messages = new List<Dictionary<string, string>>
        {
            new() { ["role"] = "user", ["content"] = "只有 user" }
        };

        var insertion = SystemPromptInsert.InsertIntoMessages(messages, "【经济状况】\n你手头宽裕。");

        TestRunner.False(insertion.Changed, "没有 system 消息时不该改任何东西");
        TestRunner.Equal(InsertPlacements.NoSystemMessage, insertion.Placement);
        TestRunner.Equal("只有 user", messages[0]["content"], "user 消息必须原样保留");
    }

    // ── 只注入 S 段的副作用契约 ──────────────────────────────────────

    /// <summary>
    /// <b>本轮最关键的一条</b>：<c>BuildStableOnly</c> 绝不能清空 <c>PendingVoice</c>。
    /// 后半段用 <c>Build(commit: true)</c> 做对照，证明这条断言不是废话——
    /// 完整注入路径<b>会</b>清空它，所以 S 段专用路径必须是另一个方法。
    /// </summary>
    private static void BuildStableOnlyKeepsPendingVoice()
    {
        var config = LoadReal();

        var (world, npc, input) = Fixture(config, "热心同好");
        npc.PendingVoice = "囊中羞涩，只能口头支持。";

        var injection = new PromptInjector(Silent).BuildStableOnly(input, commit: true);

        TestRunner.True(injection.StableText.Length > 0, "稳定段不该是空的");
        TestRunner.Equal("", injection.VolatileText, "只注入 S 段时易变段必须是空串");
        TestRunner.False(injection.PendingVoiceInjected, "本轮没有注入 PendingVoice，必须如实报告");
        TestRunner.Equal("囊中羞涩，只能口头支持。", npc.PendingVoice,
            "只注入 S 段时绝不能清空 PendingVoice——它由 V 段承载，清掉就等于弄丢了");

        // 对照：完整注入会消耗掉它。这说明"两个方法"不是重复设计。
        var (_, otherNpc, otherInput) = Fixture(config, "热心同好");
        otherNpc.PendingVoice = "囊中羞涩，只能口头支持。";
        var full = new PromptInjector(Silent).Build(otherInput, commit: true);

        TestRunner.True(full.PendingVoiceInjected, "完整注入应当注入 PendingVoice");
        TestRunner.Equal("", otherNpc.PendingVoice, "完整注入之后 PendingVoice 才该被清空");

        TestRunner.True(world.Npcs.ContainsKey(npc.PersonId), "自检用世界应持有该 NPC");
    }

    private static void BuildStableOnlyCommitsFingerprint()
    {
        var config = LoadReal();
        var (world, npc, input) = Fixture(config, "热心同好");
        var injector = new PromptInjector(Silent);

        // 不提交：指纹不该被写进存档。
        injector.BuildStableOnly(input, commit: false);
        TestRunner.Equal("", world.StableSegmentFingerprint, "commit:false 不该写指纹");

        var first = injector.BuildStableOnly(input, commit: true);
        TestRunner.Equal(8, first.Fingerprint.Length, "指纹应是 8 位十六进制");
        TestRunner.Equal(first.Fingerprint, world.StableSegmentFingerprint, "commit:true 应写入指纹");
        TestRunner.False(first.StableChanged, "首次建立指纹不算缓存失效");

        var same = injector.BuildStableOnly(input, commit: true);
        TestRunner.False(same.StableChanged, "同样的输入必须给出同样的指纹");

        // 换性格 → 金钱观与标签变化 → 稳定段必须变，于是缓存需要重写。
        var changedInput = input with
        {
            Facts = new NpcFacts(npc.PersonId, "职业选手", Rating: 1500, MaxAscension: 8,
                PersonalityKind: "暴躁喷子", PersonalityVersion: 1)
        };
        npc.PersonalityKind = "暴躁喷子";
        var changed = injector.BuildStableOnly(changedInput, commit: true);

        TestRunner.True(changed.StableChanged, "性格变了，稳定段指纹必须变（否则缓存会读到过期前缀）");
        TestRunner.NotEqual(first.Fingerprint, changed.Fingerprint, "指纹必须不同");
    }

    private static void StableSegmentIsDigitFreeForEveryKind()
    {
        var config = LoadReal();

        foreach (string kind in ConfigLoader.CanonicalPersonalityKinds)
        {
            var (_, _, input) = Fixture(config, kind);
            string text = new PromptInjector(Silent).BuildStableOnly(input, commit: false).StableText;

            TestRunner.True(text.Length > 0, $"{kind} 的稳定段不该是空的");
            TestRunner.False(Regex.IsMatch(text, @"\d"), $"{kind} 的稳定段出现了数字，会破坏前缀缓存：<{text}>");
            TestRunner.NotContains(text, "123456", $"{kind} 的稳定段泄漏了余额");
        }
    }

    private static void RealConfigProvidesAnchorAndSwitch()
    {
        var config = LoadReal();

        TestRunner.Equal(SystemPromptInsert.DefaultAnchor, config.Prompt.Get("stableAnchor"),
            "ai_prompt_economy.json 的 stableAnchor 必须与代码里的原版锚点一致");
        TestRunner.Equal("true", config.Prompt.Get("stableEnabled"),
            "ai_prompt_economy.json 的 stableEnabled 应为 true");
        TestRunner.True(config.Economy.Injection.StableEnabled,
            "economy_config.json 的 injection.stableEnabled 应为 true（总开关）");
    }

    // ── 夹具 ────────────────────────────────────────────────────────

    private const long ProbeBalanceUsd = 123_456;

    private static (EconomyWorld World, NpcEconomy Npc, PromptInjectInput Input) Fixture(
        ConfigLoadResult config, string kind)
    {
        const string worldId = "test-world";
        const string personId = "npc-1";

        var world = new EconomyWorld { WorldId = worldId, LastTickedDay = 1, LastSeason = 1 };
        var npc = new NpcEconomy
        {
            PersonId = personId,
            Balance = ProbeBalanceUsd,
            TierId = "professional",
            PersonalityKind = kind,
            Season = 1
        };
        world.Npcs[personId] = npc;

        var facts = new NpcFacts(personId, "职业选手", Rating: 1500, MaxAscension: 8,
            PersonalityKind: kind, PersonalityVersion: 1);

        var input = new PromptInjectInput(world, npc, facts, "professional",
            config.Personality, config.Prompt, Favour: 42, Day: 30, Season: 1);

        return (world, npc, input);
    }

    private static ConfigLoadResult LoadReal()
    {
        string? root = FindConfigDirectory();
        TestRunner.True(root != null, "找不到仓库的 config/ 目录，无法验证真实配置。");
        return new ConfigLoader(root!, Silent).Load();
    }

    private static string? FindConfigDirectory()
    {
        // 收敛到共享实现（原先 19 个文件各抄一份，副本之间已经开始漂移）。
        try { return TestConfig.Directory(); }
        catch (Exception) { return null; }
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
