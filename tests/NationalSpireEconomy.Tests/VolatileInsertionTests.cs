using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Inject;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 阶段 3-B 的 V 段（易变段）<b>纯逻辑</b>契约：
/// <list type="number">
/// <item>数字只允许出现在 V 段，S 段必须保持零数字（<b>两段隔离</b>）；</item>
/// <item>V 段落进承载 <c>ordered.Current</c> 的那条 user 消息，<b>不新增 message</b>、
/// 也绝不碰玩家自己那条消息；</item>
/// <item>找不到锚点就放弃，而不是随便挑一条消息塞进去；</item>
/// <item><c>PendingVoice</c> 只有在 V 段真的被注入之后才清空。</item>
/// </list>
/// </summary>
public static class VolatileInsertionTests
{
    public static Action[] Cases =>
    [
        AppendsToCurrentMessageOnly,
        PrefersCandidateWithRelationMarker,
        FallsBackToLastCandidateWithoutMarker,
        RepeatedInsertIsIdempotent,
        CustomAnchorIsHonored,
        MissingAnchorMeansNoInjection,
        EmptyVolatileTextIsNoOp,
        InsertDoesNotMutateInput,
        DigitsAreIsolatedToVolatileSegment,
        PendingVoiceIsClearedOnlyAfterCommit
    ];

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    private const string ProbeBalance = "$123,456";
    private const string CurrentBody =
        "当前日期：第 1 赛季第 12 天。\n\n…档案…\n\n小李对你的关系\n好感42，关系为熟悉。\n\n…近期赛程…\n";
    private const string VolatileBody = "【当前经济】\n你可用余额 $123,456。";

    // ── 落点 ────────────────────────────────────────────────────────

    private static void AppendsToCurrentMessageOnly()
    {
        var messages = OriginalMessages();

        var insertion = CurrentSegmentInsert.InsertIntoMessages(messages, VolatileBody);

        TestRunner.True(insertion.Changed, "应当插入成功");
        TestRunner.Equal(VolatilePlacements.Appended, insertion.Placement, "落点应是「追加」");
        TestRunner.Equal(3, insertion.MessageIndex, "应命中承载 ordered.Current 的那条 user 消息");
        TestRunner.Equal(5, messages.Count, "绝不能新增 message");
        TestRunner.Contains(messages[3]["content"], VolatileBody, "易变段要落在 Current 消息里");
        TestRunner.Contains(messages[3]["content"], "当前日期：", "原有上下文一个字都不能少");
        TestRunner.NotContains(messages[0]["content"], VolatileBody, "system 里绝不能出现数字（那是 S 段的地盘）");
        TestRunner.Equal("第 1 赛季第 12 天，玩家发给小李的消息\n你好", messages[4]["content"],
            "玩家自己那条消息必须逐字节不变");
    }

    /// <summary>
    /// 玩家完全可能在私信正文里以「当前日期：」开头打字。那时"最后一条候选"会挑错，
    /// 所以候选里带原版关系行的那条优先。
    /// </summary>
    private static void PrefersCandidateWithRelationMarker()
    {
        var messages = OriginalMessages();
        // 玩家那条消息伪装成候选：以锚点开头，但没有原版的关系行。
        messages[4]["content"] = "当前日期：这游戏真好玩，我随口说的。";

        var insertion = CurrentSegmentInsert.InsertIntoMessages(messages, VolatileBody);

        TestRunner.Equal(3, insertion.MessageIndex, "应挑带关系行的那条，而不是玩家那条");
        TestRunner.NotContains(messages[4]["content"], VolatileBody, "玩家消息不能被污染");
    }

    private static void FallsBackToLastCandidateWithoutMarker()
    {
        var messages = OriginalMessages();
        // 两条候选都不带关系行：此时退回"最后一条"，绝不因此拒绝注入。
        messages[3]["content"] = "当前日期：第 1 赛季第 12 天。\n\n（这段上下文没有关系行）";
        messages[4]["content"] = "当前日期：两条都不带标记。";

        var insertion = CurrentSegmentInsert.InsertIntoMessages(messages, VolatileBody);

        TestRunner.Equal(VolatilePlacements.Appended, insertion.Placement, "仍然必须注入，不能静默放弃");
        TestRunner.Equal(4, insertion.MessageIndex, "没有标记时取最后一条候选");
    }

    private static void RepeatedInsertIsIdempotent()
    {
        var messages = OriginalMessages();

        var first = CurrentSegmentInsert.InsertIntoMessages(messages, VolatileBody);
        string afterFirst = messages[3]["content"];
        var second = CurrentSegmentInsert.InsertIntoMessages(messages, VolatileBody);

        TestRunner.True(first.Changed, "第一次应插入");
        TestRunner.False(second.Changed, "第二次不该再插一遍");
        TestRunner.Equal(VolatilePlacements.AlreadyPresent, second.Placement, "第二次应报告「已存在」");
        TestRunner.Equal(afterFirst, messages[3]["content"], "重复插入不该改动正文");
        TestRunner.Equal(1, CountOccurrences(afterFirst, "【当前经济】"), "正文里只应出现一次易变段");
    }

    private static void CustomAnchorIsHonored()
    {
        var messages = OriginalMessages();
        messages[3]["content"] = "本题上下文：第 1 赛季第 12 天。";

        var insertion = CurrentSegmentInsert.InsertIntoMessages(messages, VolatileBody, "本题上下文：");

        TestRunner.Equal(VolatilePlacements.Appended, insertion.Placement, "自定义锚点也要生效");
        TestRunner.Equal(3, insertion.MessageIndex);
    }

    /// <summary>
    /// <b>安全阀</b>：找不到锚点时必须放弃，而不是退化成"随便挑一条 user 消息追加"——
    /// 那会把经济信息塞进玩家自己说的话里。
    /// </summary>
    private static void MissingAnchorMeansNoInjection()
    {
        var messages = OriginalMessages();
        messages[3]["content"] = "原版换了文案，锚点不在了。";
        string[] before = messages.Select(m => m["content"]).ToArray();

        var insertion = CurrentSegmentInsert.InsertIntoMessages(messages, VolatileBody);

        TestRunner.False(insertion.Changed, "找不到锚点时不该改任何东西");
        TestRunner.Equal(VolatilePlacements.NoCurrentMessage, insertion.Placement);
        TestRunner.Equal(-1, insertion.MessageIndex);
        TestRunner.Equal(string.Join("\u0001", before), string.Join("\u0001", messages.Select(m => m["content"])),
            "所有消息必须逐字节不变");
    }

    private static void EmptyVolatileTextIsNoOp()
    {
        var messages = OriginalMessages();
        string before = messages[3]["content"];

        var insertion = CurrentSegmentInsert.InsertIntoMessages(messages, "");

        TestRunner.False(insertion.Changed, "易变段为空时不该改动正文");
        TestRunner.Equal(VolatilePlacements.NoVolatileText, insertion.Placement);
        TestRunner.Equal(before, messages[3]["content"]);
    }

    private static void InsertDoesNotMutateInput()
    {
        string text = CurrentBody;
        string original = text;

        CurrentSegmentInsert.Insert(text, VolatileBody);

        TestRunner.Equal(original, text, "Insert 不该原地修改入参字符串");
    }

    // ── 两段隔离（本阶段最重要的一条） ───────────────────────────────

    private static void DigitsAreIsolatedToVolatileSegment()
    {
        var config = LoadReal();
        var (world, npc, input) = Fixture(config, "热心同好", 123_456);
        var injector = new PromptInjector(Silent);

        var stable = injector.BuildStableOnly(input, commit: false);
        var volatileSegment = injector.BuildVolatileOnly(input, commit: false);

        TestRunner.True(stable.StableText.Length > 0, "稳定段不该为空");
        TestRunner.False(PrivateMessagePatchContainsDigit(stable.StableText),
            "S 段里出现数字会破坏前缀缓存：<" + stable.StableText + ">");
        TestRunner.True(PrivateMessagePatchContainsDigit(volatileSegment.Text),
            "V 段必须承载数字：<" + volatileSegment.Text + ">");
        TestRunner.Contains(volatileSegment.Text, ProbeBalance, "V 段要写出精确余额");
        TestRunner.NotContains(stable.StableText, ProbeBalance, "S 段绝不能泄漏余额");
        TestRunner.Equal(8, volatileSegment.Fingerprint.Length, "V 段指纹应是 8 位十六进制");

        // 只改余额：V 段必须变，S 段指纹必须不变（这就是"数字隔离"的可证形式）。
        npc.Balance += 999_999;
        var stableAfterMoney = injector.BuildStableOnly(input, commit: false);
        var volatileAfterMoney = injector.BuildVolatileOnly(input, commit: false);

        TestRunner.Equal(stable.Fingerprint, stableAfterMoney.Fingerprint,
            "余额变了，稳定段指纹绝不能变（否则前缀缓存每笔交易都失效）");
        TestRunner.NotEqual(volatileSegment.Fingerprint, volatileAfterMoney.Fingerprint,
            "余额变了，易变段必须跟着变");

        // 反过来改性格：S 段必须变，V 段不该受影响。
        var kindInput = input with
        {
            Facts = new NpcFacts(npc.PersonId, "职业选手", Rating: 1500, MaxAscension: 8,
                PersonalityKind: "暴躁喷子", PersonalityVersion: 1)
        };
        npc.PersonalityKind = "暴躁喷子";

        TestRunner.NotEqual(stable.Fingerprint, injector.BuildStableOnly(kindInput, commit: false).Fingerprint,
            "性格变了，稳定段必须跟着变");
        TestRunner.Equal(volatileAfterMoney.Text, injector.BuildVolatileOnly(kindInput, commit: false).Text,
            "性格与易变段无关：V 段不该被性格影响");

        TestRunner.True(world.Npcs.ContainsKey(npc.PersonId), "自检用世界应持有该 NPC");
    }

    private static void PendingVoiceIsClearedOnlyAfterCommit()
    {
        var config = LoadReal();
        var (_, npc, input) = Fixture(config, "热心同好", 123_456);
        npc.PendingVoice = "囊中羞涩，只能口头支持。";
        var injector = new PromptInjector(Silent);

        var probe = injector.BuildVolatileOnly(input, commit: false);

        TestRunner.True(probe.PendingVoiceInjected, "探测应当如实报告「这一轮会读出未竟之言」");
        TestRunner.Contains(probe.Text, "囊中羞涩，只能口头支持。", "V 段要带上未竟之言");
        TestRunner.Equal("囊中羞涩，只能口头支持。", npc.PendingVoice,
            "commit:false 时绝不能清空 PendingVoice——插入还没发生呢");

        var committed = injector.BuildVolatileOnly(input, commit: true);

        TestRunner.True(committed.PendingVoiceInjected, "提交时同样要如实报告");
        TestRunner.Equal("", npc.PendingVoice, "提交之后才该清空未竟之言");
    }

    // ── 夹具 ────────────────────────────────────────────────────────

    private static List<Dictionary<string, string>> OriginalMessages() =>
    [
        new() { ["role"] = "system", ["content"] = "【模拟的原版 system：世界设定 + 七串 Protocol】" },
        new() { ["role"] = "user", ["content"] = "【模拟的人物资料】" },
        new() { ["role"] = "user", ["content"] = "【模拟的历史记录：上一轮聊天】" },
        new() { ["role"] = "user", ["content"] = CurrentBody },
        new() { ["role"] = "user", ["content"] = "第 1 赛季第 12 天，玩家发给小李的消息\n你好" }
    ];

    private static (EconomyWorld World, NpcEconomy Npc, PromptInjectInput Input) Fixture(
        ConfigLoadResult config, string kind, long balance)
    {
        const string worldId = "volatile-test";
        const string personId = "npc-1";

        var world = new EconomyWorld { WorldId = worldId, LastTickedDay = 84, LastSeason = 1 };
        var npc = new NpcEconomy
        {
            PersonId = personId,
            Balance = balance,
            TierId = "professional",
            PersonalityKind = kind,
            Season = 1
        };
        npc.SeasonStats.SeasonIncome = 96_000;
        npc.SeasonStats.SalaryIncome = 72_000;
        world.Npcs[personId] = npc;

        var facts = new NpcFacts(personId, "职业选手", Rating: 1500, MaxAscension: 8,
            PersonalityKind: kind, PersonalityVersion: 1);

        var input = new PromptInjectInput(world, npc, facts, "professional",
            config.Personality, config.Prompt, Favour: 42, Day: 90, Season: 2);

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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "config");
            if (File.Exists(Path.Combine(candidate, ConfigLoader.EconomyFileName))) return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    /// <summary>零依赖套件里不能引用 Game 层的 <c>PrivateMessagePatch</c>（它需要游戏程序集）。</summary>
    private static bool PrivateMessagePatchContainsDigit(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, @"\d");

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
