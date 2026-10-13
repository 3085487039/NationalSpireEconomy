using System.Runtime.CompilerServices;
using System.Text;
using NationalSpire;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Trade;

namespace NationalSpireEconomy.Game;

/// <summary>
/// <b>一次性清理原版存档里已经固化的 <c>[Trade: …]</c> 标记</b>（2026-10-12 真机挖出的历史遗留）。
/// <para>
/// <b>问题从哪来</b>：真机上玩家在私信里看到了
/// <c>[Trade: 雇佣, Amount: 3000, Direction: playerToNpc]</c>。查清后是两件不同的事：
/// </para>
/// <list type="number">
/// <item><b>新消息</b>：已由 <see cref="PrivateReplyPatch"/> 在渲染前剥掉（补丁落点正确，有探针判据）；</item>
/// <item><b>旧消息</b>：标记在补丁生效之前就已经<b>写进了原版生涯存档</b>，位置是
/// <c>Life/Mailbox/Conversations/&lt;npcId&gt;/Turns[N]/Reply|Reasoning|User</c>
/// （实测该存档里共 53 处）。这些轮次是<b>反序列化</b>出来的 ——
/// 直接给字段赋值，<b>不经过 <c>set_Reply</c></b>，所以那个补丁永远碰不到它们。</item>
/// </list>
/// <para>
/// <b>本类怎么修（甲方 2026-10-12 拍板的方案①）</b>：在模组第一次拿到
/// <see cref="CareerData"/> 时，把它<b>内存里</b>那棵会话树的这三个字段过一遍剥离。
/// 改的是**已经反序列化出来的对象**，于是：
/// </para>
/// <list type="bullet">
/// <item>界面立刻变干净（不需要玩家做任何事）；</item>
/// <item>游戏<b>下一次正常存档</b>时，这些字段自然被覆写成干净的 ——
/// <b>我们从头到尾没有自己写过原版存档文件</b>（铁律 1 仍然成立）；</item>
/// <item>万一某个字段的写入路径读的是别处的原文，下次启动会再清一遍（幂等）。</item>
/// </list>
/// <para>
/// <b>它只删"完整的交易标记"</b>：剥离用的是 <see cref="TradeProtocol.StripMarkers"/>，
/// 它已经处理好"未闭合标记原样放行"与"括号里套着别的标记原样放行"两个边界，
/// 并且只认 <c>Trade</c> / <c>Buy</c> 两个关键字 —— 玩家正文字里那些方括号不会被误伤。
/// </para>
/// <para>
/// <b>失败一律不抛出</b>：这是清理历史数据，坏掉不该影响任何游戏流程。
/// </para>
/// </summary>
public static class MailboxSanitizer
{
    /// <summary>已经清理过的 <see cref="CareerData"/> 实例（避免重复遍历整棵树）。</summary>
    private static readonly ConditionalWeakTable<object, object> Done = new();

    /// <summary>本进程累计清理掉的字段数（诊断用，写进日志）。</summary>
    public static int CleanedFields { get; private set; }

    /// <summary>本进程是否已经跑过清理。</summary>
    public static bool Ran { get; private set; }

    /// <summary>最近一次清理的结果说明（诊断用）。</summary>
    public static string LastReport { get; private set; } = "未运行";

    /// <summary>
    /// <b>这个成员是不是我们已经处理过的那一份</b>（按对象身份记）。
    /// <para>
    /// 存在的理由：清理现在挂在"第一次看到这份 <c>CareerData</c>"上，而
    /// <c>WorldFor</c> 会被多条路径重复调用；没有这道闸就会反复遍历整棵会话树。
    /// </para>
    /// </summary>
    public static bool AlreadyHandled(CareerData? data)
    {
        if (data == null) return true;
        lock (Done) return Done.TryGetValue(data, out _);
    }

    /// <summary>
    /// 对一份生涯数据做一次清理。<b>同一份数据只做一次</b>（按对象身份记）。
    /// </summary>
    /// <param name="data">原版生涯数据（进程里那个正在被游戏使用的实例）。</param>
    /// <param name="log">日志出口，可为 null。</param>
    /// <returns>本次是否真的改动了字段（供日志与自证使用）。</returns>
    public static bool Sanitize(CareerData? data, IEconomyLog? log = null)
    {
        if (data == null) return false;

        lock (Done)
        {
            if (Done.TryGetValue(data, out _)) return false;
        }

        Ran = true;
        try
        {
            // 逐层取，每一步都留痕：拿不到的时候必须能一眼看出是【哪一层】断了。
            // （第一版就是在这里静默返回，导致真机上"清理器没跑"和"跑了但没找到标记"
            //   完全无法区分 —— 这正是本项目反复踩的"静默失败"坑。）
            object? life = Member(data, "Life");
            if (life == null)
            {
                LastReport = "拿不到 Life（原版结构变了？）";
                log?.Warn("trade", "[Economy-Marker] 存档清理：跳过｜" + LastReport +
                                  "｜本次不登记，下次调用会重试");
                return false;
            }

            object? mailbox = Member(life, "Mailbox");
            if (mailbox == null)
            {
                LastReport = "拿不到 Life.Mailbox（原版结构变了？）";
                log?.Warn("trade", "[Economy-Marker] 存档清理：跳过｜" + LastReport +
                                  "｜本次不登记，下次调用会重试");
                return false;
            }

            object? conversationsRaw = Member(mailbox, "Conversations");
            if (conversationsRaw is not System.Collections.IDictionary conversations)
            {
                LastReport = $"Life.Mailbox.Conversations 不是字典而是 " +
                             (conversationsRaw?.GetType().FullName ?? "null");
                log?.Warn("trade", "[Economy-Marker] 存档清理：跳过｜" + LastReport +
                                  "｜本次不登记，下次调用会重试");
                return false;
            }

            // ★ 登记必须发生在【三层结构都确认拿到之后】。
            //
            // 曾经的写法是在方法开头（取结构之前）就 Done.Add(data, data)，于是
            // "Life 取不到"这类提前返回会把它永久标记成"处理过"（ConditionalWeakTable
            // 按对象身份记），而 WorldFor 的注释还承诺"下次启动会再清一遍（幂等）"——
            // 对同一份对象这个承诺当时是不成立的。真机症状会是"标记一直在，日志说清过了"。
            // 现在：取不到结构 ⇒ 不登记 ⇒ 下一次调用（可能数据已经填好）仍会重试。
            lock (Done)
            {
                if (Done.TryGetValue(data, out _)) return false;
                Done.Add(data, data);
            }

            int touched = 0;
            int scanned = 0;
            int conversationCount = 0;
            // 用 DictionaryEntry 遍历：Conversations 是原版的字典类型，
            // 我们只当 IDictionary 用（不依赖它的泛型参数）。
            foreach (System.Collections.DictionaryEntry pair in conversations)
            {
                conversationCount++;
                foreach (var turn in TurnsOf(pair.Value))
                {
                    scanned++;
                    if (StripFieldInPlace(turn, "Reply")) touched++;
                    if (StripFieldInPlace(turn, "Reasoning")) touched++;
                    if (StripFieldInPlace(turn, "User")) touched++;
                }
            }

            CleanedFields += touched;
            LastReport = touched > 0
                ? $"扫过 {scanned} 轮，清理了 {touched} 个字段里的固化标记（共 {conversationCount} 段会话）"
                : $"扫过 {scanned} 轮，没有发现固化的标记（{conversationCount} 段会话）";

            // 无论有没有清理到，都写一行 —— "跑了但没找到"与"压根没跑"必须能分开。
            if (touched > 0)
                log?.Info("trade", "[Economy-Marker] 存档里的固化标记已清理：" + LastReport);
            else
                log?.Info("trade", "[Economy-Marker] 存档清理（无需处理）：" + LastReport);

            return touched > 0;
        }
        catch (Exception e)
        {
            // 清理历史数据失败绝不能影响任何流程。
            LastReport = e.GetType().Name + ": " + e.Message;
            log?.Warn("trade", "[Economy-Marker] 存档固化标记清理失败（已忽略，不影响游戏）：" + LastReport, e);
            return false;
        }
    }

    /// <summary>
    /// 取 <c>Life.Mailbox.Conversations</c>（公开给诊断用）。
    /// <para>
    /// <b>为什么要走反射</b>：这三个成员都是原版的内部结构，我们没有它们的编译期契约；
    /// 用反射 + 逐层判空，原版改名时我们只是"清理不了"（记一条日志），
    /// 而不是在加载期抛异常把别人的补丁也带崩。
    /// </para>
    /// </summary>
    public static System.Collections.IDictionary? ConversationsOf(CareerData data)
    {
        object? life = Member(data, "Life");
        object? mailbox = Member(life, "Mailbox");
        return Member(mailbox, "Conversations") as System.Collections.IDictionary;
    }

    /// <summary>
    /// 诊断：把 <c>CareerData</c> 那三层结构的**真实情况**写进日志。
    /// <para>
    /// 存在的理由只有一个：真机上"清理器没清到东西"时能立刻看出是
    /// <c>Life</c> 名字不对、<c>Mailbox</c> 名字不对、还是 <c>Turns</c> 结构不同 ——
    /// 而不用再靠猜（第一版就是在这里静默返回，白跑了一轮真机）。
    /// </para>
    /// </summary>
    public static string ProbeStructure(CareerData? data)
    {
        if (data == null) return "CareerData 为 null";

        try
        {
            object? life = Member(data, "Life");
            if (life == null) return "CareerData.Life 取不到";

            object? mailbox = Member(life, "Mailbox");
            if (mailbox == null) return $"Life({life.GetType().Name}) 上取不到 Mailbox";

            object? conversations = Member(mailbox, "Conversations");
            if (conversations is not System.Collections.IDictionary dict)
                return $"Mailbox({mailbox.GetType().Name}) 上取不到 Conversations（得到 " +
                       (conversations?.GetType().Name ?? "null") + "）";

            int turns = 0;
            string? firstTurnType = null;
            foreach (System.Collections.DictionaryEntry pair in dict)
            {
                foreach (var turn in TurnsOf(pair.Value))
                {
                    turns++;
                    firstTurnType ??= turn.GetType().Name;
                }
            }

            return $"Conversations={dict.Count} 段｜Turns 共 {turns} 轮｜轮次类型 {firstTurnType ?? "(无)"}";
        }
        catch (Exception e)
        {
            return "探测抛异常：" + e.GetType().Name + ": " + e.Message;
        }
    }

    /// <summary>取某段会话的轮次列表；结构不认识时返回空（调用方按"扫过 0 轮"报告）。</summary>
    private static IEnumerable<object> TurnsOf(object? conversation)
    {
        object? turns = Member(conversation, "Turns");
        if (turns is System.Collections.IEnumerable list and not string)
        {
            foreach (object? item in list)
                if (item != null) yield return item;
        }
    }

    /// <summary>
    /// 把某个成员里的标记剥掉。<b>属性与字段都认</b>（<c>Reasoning</c> 是字段还是属性随原版版本）。
    /// </summary>
    /// <returns>是否真的改了。</returns>
    private static bool StripField(object turn, string name) =>
        StripFieldInPlace(turn, name);

    /// <summary>
    /// <b>剥离逻辑本身（可单测）</b>：对 <paramref name="target"/> 上名为
    /// <paramref name="name"/> 的字符串成员做一次剥离，成功返回 true。
    /// <para>
    /// 公开它只有一个理由：这段逻辑决定"会不会误删玩家的正文"，
    /// 必须有测试直接钉住，而不是只能靠整棵会话树的集成测试间接覆盖。
    /// </para>
    /// </summary>
    public static bool StripFieldInPlace(object? target, string name)
    {
        if (target == null || string.IsNullOrEmpty(name)) return false;

        try
        {
            var type = target.GetType();
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;

            string? current;
            System.Reflection.PropertyInfo? property = type.GetProperty(name, flags);
            System.Reflection.FieldInfo? field = null;

            if (property is { CanRead: true, CanWrite: true } && property.PropertyType == typeof(string))
            {
                current = property.GetValue(target) as string;
            }
            else
            {
                property = null;
                field = type.GetField(name, flags);
                if (field == null || field.FieldType != typeof(string)) return false;
                current = field.GetValue(target) as string;
            }

            if (string.IsNullOrEmpty(current)) return false;
            // 没有左括号就必然没有标记：省掉一次完整扫描。
            if (current.IndexOf('[', StringComparison.Ordinal) < 0) return false;

            string stripped = TradeProtocol.StripMarkers(current);
            if (string.Equals(stripped, current, StringComparison.Ordinal)) return false;

            // 与显示出口同样的纪律：剥完为空就退回归还原文，绝不静默把字段清空。
            if (stripped.Trim().Length == 0) return false;

            if (property != null) property.SetValue(target, stripped);
            else field!.SetValue(target, stripped);
            return true;
        }
        catch (Exception)
        {
            // 单个字段的异常不该中断整棵树。
            return false;
        }
    }

    /// <summary>按名字取成员（属性优先，其次字段）；取不到返回 null。</summary>
    private static object? Member(object? instance, string name)
    {
        if (instance == null) return null;
        try
        {
            var type = instance.GetType();
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;

            var property = type.GetProperty(name, flags);
            if (property is { CanRead: true }) return property.GetValue(instance);

            var field = type.GetField(name, flags);
            return field?.GetValue(instance);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>诊断用：一行摘要。</summary>
    public static string Describe() => $"[Economy-Marker] 存档清理：{LastReport}";
}
