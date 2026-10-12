using System.Reflection;
using HarmonyLib;
using NationalSpire;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Inject;

namespace NationalSpireEconomy.Game;

/// <summary>一次前缀注入的结果（诊断与"首次说话"打点用）。</summary>
/// <param name="Injected">本次是否真的改了消息内容。</param>
/// <param name="Context">原版传进来的上下文串。</param>
/// <param name="Identity">身份槽摘要。</param>
/// <param name="BeforeCount">注入前后 message 条数。</param>
/// <param name="AfterCount">注入后 message 条数。</param>
/// <param name="StableDetail">S 段一句话结论。</param>
/// <param name="VolatileDetail">V 段一句话结论。</param>
/// <param name="SkipReason">未注入的原因（未注入时非空）。</param>
public sealed record RequestInjectionOutcome(
    bool Injected,
    string Context,
    string Identity,
    int BeforeCount,
    int AfterCount,
    string StableDetail,
    string VolatileDetail,
    string? SkipReason)
{
    /// <summary>要写进日志的一行。</summary>
    public string LogLine =>
        $"注入{(Injected ? "成功" : "未发生")}｜context=<{Context}>｜身份 {Identity}" +
        $"｜message {BeforeCount} → {AfterCount} 条{(BeforeCount == AfterCount ? "（条数不变）" : "（★条数变了！）")}" +
        $"｜S 段 {StableDetail}｜V 段 {VolatileDetail}" +
        (SkipReason != null ? $"｜原因 {SkipReason}" : "");
}

/// <summary>
/// <b>方案 A：末端注入</b>。
/// <para>
/// 干预点从 <c>PrivateMessagePrompts.Compose</c>（阶段 3-D 实测<b>不在真实私信路径上</b>）
/// 后移到 <c>AiService.RequestPrivateAsync(AiOptions, List, …)</c> 的<b>前缀</b>——
/// 那是消息列表被序列化发出去之前的最后一站，下游无法覆盖。
/// </para>
/// <para>
/// <b>为什么用 <c>object[] __args</c></b>：不写具名参数就不怕原版改参数名/数量。
/// 最后一个 <c>string</c> 参数是上下文串（<c>private/…</c> / <c>group/…</c> / <c>weekly</c> …），
/// 我们<b>只处理 <c>private/</c></b>：群聊、发帖、周报、仲裁各有自己的 context，一律不碰。
/// </para>
/// <para>
/// <b>身份从哪来</b>：这里只有 <c>AiOptions</c>，没有 <c>CareerData</c>；
/// 所以身份由 <see cref="PrivateIdentitySlot"/>（挂在 <c>Context</c> 后缀上）提前记好，
/// 这里只读，并且要用账本核对"这个人真的存在"才动手。
/// </para>
/// <para>
/// <b>三条纪律</b>：① <b>不新增 message</b>（只改字符串，并且逐次断言条数不变）；
/// ② <b>幂等</b>（内容已含我们的标题就跳过，防止重复装配/重复调用插两份）；
/// ③ <b>绝不抛异常</b>（前缀抛出会炸掉原版私信发送，整个进程的事务也就断了）。
/// </para>
/// </summary>
public static class PrivateRequestInjectionPatch
{
    /// <summary>控制台/日志固定前缀。搜这个就能找到方案 A 的全部结论。</summary>
    public const string ConsolePrefix = "[Economy-Inject-A]";

    /// <summary>目标方法名。</summary>
    public const string MethodName = "RequestPrivateAsync";

    /// <summary>首参类型名。用名字而不是类型引用，避免对原版类型的编译期依赖。</summary>
    public const string FirstParameterTypeName = "AiOptions";

    /// <summary>只处理这个前缀的上下文；群聊/周报/仲裁等一律不注入。</summary>
    public const string PrivateContextPrefix = "private/";

    /// <summary>S 段标题。用于幂等判定：内容里已经有它就不再插。</summary>
    public const string StableHeader = "【经济状况】";

    /// <summary>V 段标题。</summary>
    public const string VolatileHeader = "【当前经济】";

    /// <summary>已成功挂载的目标方法描述（含签名与 token）。</summary>
    public static IReadOnlyList<string> PatchedMethods { get; private set; } = [];

    /// <summary>补丁是否已经装上。</summary>
    public static bool Applied { get; private set; }

    /// <summary>装配细节（成功或失败原因）。</summary>
    public static string Detail { get; private set; } = "未装配";

    /// <summary>本进程内前缀被调用过多少次（<b>无论是否注入</b>）。</summary>
    public static int Hits { get; private set; }

    /// <summary>本进程内真正注入成功的次数。</summary>
    public static int Injections { get; private set; }

    /// <summary>因为上下文不是私信而跳过的次数（群聊、周报等）。</summary>
    public static int SkippedNonPrivate { get; private set; }

    /// <summary>最近一次结果。</summary>
    public static RequestInjectionOutcome? LastOutcome { get; private set; }

    /// <summary>最近一次错误。</summary>
    public static string? LastError { get; private set; }

    private static IEconomyLog? _log;
    private static Action<string, bool>? _console;
    private static bool _firstCallReported;

    /// <summary>
    /// 在<b>所有</b>定义了目标方法的程序集副本上挂前缀。
    /// <para>
    /// 甲方环境装了补丁框架/程序集加载器，同一个类型可能存在多份副本；
    /// 只挂"第一个找到的"正是阶段 3-D 那个坑的成因之一，所以这里必须全挂。
    /// </para>
    /// </summary>
    /// <returns>是否至少挂上了一份。</returns>
    public static bool ApplyTo(Harmony? harmony, IEnumerable<MethodInfo> targets, IEconomyLog? log,
        Action<string, bool>? console)
    {
        ArgumentNullException.ThrowIfNull(targets);

        _log = log;
        _console = console;

        var patched = new List<string>();
        if (harmony == null)
        {
            Detail = "Harmony 实例未建立";
            return false;
        }

        var probe = AccessTools.Method(typeof(PrivateRequestInjectionPatch), nameof(RequestPrefix));
        if (probe == null)
        {
            Detail = "找不到补丁方法 RequestPrefix";
            return false;
        }

        foreach (var method in targets.Distinct())
        {
            try
            {
                // 幂等：同一方法已经被我们挂过就跳过。
                // （Initialize 可能被反复调用——测试、热重载——重复装配会让同一前缀叠很多层；
                //   Harmony 不会替我们挡这个。）
                if (AlreadyPatched(method, probe))
                {
                    patched.Add(SignatureLine(method) + "｜(已挂过，跳过)");
                    continue;
                }

                harmony.Patch(method, prefix: new HarmonyMethod(probe));
                string line = SignatureLine(method);
                patched.Add(line);
                log?.Info("inject", ConsolePrefix + " 已挂载注入前缀：" + line);
            }
            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                log?.Error("inject", $"{ConsolePrefix} 挂载失败：{SignatureOf(method)}", e);
            }
        }

        PatchedMethods = patched;
        Applied = patched.Count > 0;
        Detail = Applied
            ? $"已挂载 {patched.Count} 份｜" + string.Join(" ／ ", patched)
            : "未找到可挂载的目标方法";
        return Applied;
    }

    /// <summary>运行时装配：自己去找目标方法（供启动自证在游戏外跑时也能说话）。</summary>
    public static bool ApplyTo(Harmony? harmony, IEconomyLog? log, Action<string, bool>? console)
        => ApplyTo(harmony, FindRequestPrivateMethods(), log, console);

    /// <summary>
    /// 遍历<b>全部已加载程序集</b>，找出所有 <c>RequestPrivateAsync</c>（首参 <c>AiOptions</c>）。
    /// </summary>
    public static IReadOnlyList<MethodInfo> FindRequestPrivateMethods()
    {
        var found = new List<MethodInfo>();
        foreach (var assembly in AssemblyInventory.AllAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).Cast<Type>().ToArray();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var type in types)
            {
                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                                              BindingFlags.Public | BindingFlags.NonPublic);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var method in methods)
                {
                    if (!string.Equals(method.Name, MethodName, StringComparison.Ordinal)) continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length == 0) continue;
                    if (!string.Equals(parameters[0].ParameterType.Name, FirstParameterTypeName,
                            StringComparison.Ordinal)) continue;
                    found.Add(method);
                }
            }
        }
        return found;
    }

    /// <summary>方法签名（诊断用）。</summary>
    public static string SignatureOf(MethodBase method) =>
        $"{method.DeclaringType?.FullName}.{method.Name}(" +
        $"{string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))}) → " +
        $"{(method is MethodInfo info ? info.ReturnType.Name : "?")}";

    /// <summary>签名 + token + 程序集，用于日志与补丁清单。</summary>
    public static string SignatureLine(MethodBase method) =>
        $"{SignatureOf(method)}｜token=0x{TokenOf(method)}｜程序集 {AssemblyOf(method)}";

    /// <summary>
    /// 这个目标方法上是否已经有<b>本补丁的前缀</b>（幂等守卫）。
    /// <para>
    /// 必须精确到"具体补丁方法"：<see cref="PathProbe"/> 的只读观测探针挂在同一批方法上，
    /// 若按"有我们任意补丁就跳过"，注入前缀会被整体挡掉——真机实测踩过
    /// （日志里表现为 <c>RequestPrivateAsync …（已挂过，跳过）</c>，注入前缀实际不存在）。
    /// </para>
    /// </summary>
    private static bool AlreadyPatched(MethodBase method, MethodInfo probe) =>
        PathProbe.HasPatch(method, probe);

    private static string TokenOf(MethodBase method)
    {
        try
        {
            return method.MetadataToken.ToString("X8");
        }
        catch (Exception)
        {
            return "不可用（动态方法？）";
        }
    }

    private static string AssemblyOf(MethodBase method)
    {
        try
        {
            return method.Module.Assembly.GetName().Name ?? "?";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    /// <summary>清空装配状态（测试用；初始化时也会重置，保证单轨）。</summary>
    public static void MarkRetired(string reason)
    {
        Applied = false;
        PatchedMethods = [];
        Detail = "已按设计停用：" + reason;
    }

    // ─────────────────────────────────────────────────────────────────
    // 前缀本体
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>RequestPrivateAsync</c> 的前缀：私信上线前的最后一站。
    /// <para>
    /// <c>__args</c> 是 Harmony 给出的完整实参数组——用它能免疫原版改参数名与参数个数。
    /// </para>
    /// </summary>
    public static void RequestPrefix(object[]? __args)
    {
        try
        {
            Hits++;

            string context = TrailingContext(__args);
            var messages = MessagesOf(__args);

            if (!context.StartsWith(PrivateContextPrefix, StringComparison.Ordinal))
            {
                // 群聊 / 周报 / 仲裁 / 发帖：不是私信，一律不注入。
                SkippedNonPrivate++;
                ReportFirstCall($"非私信上下文 <{context}>，按设计不注入");
                return;
            }

            if (messages == null || messages.Count == 0)
            {
                ReportFirstCall("没拿到消息列表（原版改了参数形状？）");
                return;
            }

            var identity = PrivateIdentitySlot.Take();
            if (identity == null)
            {
                // 精确身份没拿到：走兜底轨迹（由日期推进登记），用原版上下文里的人物键去对。
                identity = MatchFromContext(context, messages);
            }

            if (identity == null)
            {
                ReportFirstCall($"身份无法确定｜context=<{context}>｜精确槽空或过期｜轨迹 {PrivateIdentitySlot.TrackedSummary}");
                return;
            }

            var outcome = Inject(identity, context, messages);
            LastOutcome = outcome;

            if (outcome.Injected) Injections++;
            ReportFirstCall(outcome.SkipReason ?? "已注入");

            var log = _log ?? EconomyModEntry.Runtime?.Log;
            if (outcome.Injected)
                log?.Info("inject", ConsolePrefix + " " + outcome.LogLine);
            else
                log?.Warn("inject", ConsolePrefix + " " + outcome.LogLine);
        }
        catch (Exception e)
        {
            // 前缀里抛异常会炸掉原版私信发送，所以这里必须兜住一切。
            LastError = e.GetType().Name + ": " + e.Message;
            try
            {
                (_log ?? EconomyModEntry.Runtime?.Log)?.Error("inject",
                    ConsolePrefix + " 注入抛异常，已忽略本次注入。", e);
                ReportFirstCall("注入过程抛异常：" + LastError);
            }
            catch (Exception)
            {
                // 连日志都失败了就只能放弃。
            }
        }
    }

    /// <summary>
    /// 取"玩家这一轮说的话"（消息列表末尾的 user 消息），供阶段 4 的确认指令解析。
    /// <para>
    /// 只回看最后两条，并跳过"当前日期/档案"那条原版消息——否则历史里的任何一句话
    /// 都可能被误当成确认指令。解析本身是纯文本匹配（<see cref="TradePreviewWiring.ParseCommand"/>）。
    /// </para>
    /// </summary>
    private static IEnumerable<string> PlayerTextsOf(List<Dictionary<string, string>> messages)
    {
        if (messages == null) yield break;

        int looked = 0;
        for (int i = messages.Count - 1; i >= 0 && looked < 2; i--)
        {
            var message = messages[i];
            if (message == null) continue;
            if (!message.TryGetValue("role", out string? role) ||
                !string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)) continue;
            if (!message.TryGetValue("content", out string? content) || string.IsNullOrWhiteSpace(content)) continue;

            looked++;
            // 原版那条"当前日期：…"的消息不是玩家说的话，跳过。
            if (content!.TrimStart().StartsWith("当前日期", StringComparison.Ordinal)) continue;
            yield return content;
        }
    }

    /// <summary>真正做事的部分：拆出来便于单测直接喂一个身份与消息列表。</summary>
    public static RequestInjectionOutcome Inject(PrivateIdentity identity, string context,
        List<Dictionary<string, string>> messages)    {
        string identityText = $"世界 <{identity.WorldId}>｜人物 <{identity.PersonId}>｜" +
                              $"第 {identity.Day} 天 / 第 {identity.Season} 赛季｜来源 {identity.Source}";
        int before = messages.Count;

        var runtime = EconomyModEntry.Runtime;
        if (runtime == null)
            return Skip(context, identityText, before, "模组未启用（EconomyModEntry.Runtime 为空）");

        // 幂等：已经含我们的标题就什么都不做（防止补丁被重复装配或同一列表被处理两次）。
        if (Contains(messages, StableHeader) || Contains(messages, VolatileHeader))
            return Skip(context, identityText, before,
                "消息里已经含【经济状况】或【当前经济】（幂等跳过，绝不插两份）");

        // 身份必须能在账本里核对上：真人（HumanIds）与陌生人都不注入。
        var world = runtime.CachedWorld(identity.WorldId);

        // ★ 缓存里没有就【按需载入】（真机事故 2026-10-11 20:11）：
        //   影子存档原先<b>只在"游戏日期推进"时</b>才载入 + 增量播种（CareerDayPatch 的后缀补丁）。
        //   于是"刚进游戏、还没跳过任何一天就先发一条私信"必然命中 null，注入被拒绝。真机原文：
        //   `注入未发生｜…｜原因 世界 <e35d0119> 不在影子存档里（未播种或载入失败）`
        //   —— 而那一刻身份其实是完整的（来自 ContextPostfix 的权威 CareerData）。
        //   按需载入与"跳天"走的是同一条 EnsureWorld 路径（EconomySeed 幂等，不会重复发钱），
        //   只是把时机提前到第一条私信；对"钱只发一次"的语义没有任何改变。
        if (world == null && identity.Data != null &&
            string.Equals(identity.Data.WorldId, identity.WorldId, StringComparison.Ordinal))
        {
            world = runtime.WorldFor(identity.Data);
            if (world != null)
                (_log ?? runtime.Log)?.Info("inject",
                    ConsolePrefix + $" 世界 <{identity.WorldId}> 此前未载入，已在私信路径上按需载入" +
                    $"（{world.Npcs.Count} 人）。");
        }

        if (world == null)
            return Skip(context, identityText, before,
                $"世界 <{identity.WorldId}> 不在影子存档里（未播种或载入失败）" +
                (identity.Data == null
                    ? "｜身份槽没带权威 CareerData，无法按需载入"
                    : "｜身份的 CareerData 与身份槽的世界号不一致，拒绝按需载入"));
        if (!world.Npcs.ContainsKey(identity.PersonId))
            return Skip(context, identityText, before,
                $"人物 <{identity.PersonId}> 不在影子账本里（真人？或从未播种）｜账本 {world.Npcs.Count} 人");

        var person = FindPerson(identity);
        if (person == null)
            return Skip(context, identityText, before,
                identity.Data == null
                    ? "身份槽没带权威 CareerData（拿不到原版人物，拒绝注入）"
                    : "原版 People 里找不到人物 <" + identity.PersonId + ">");

        // ★ 阶段 4 第一步：玩家可能在私信里回「确认 <单号>」/「拒绝 <单号>」。
        //   纯文本匹配、不依赖模型；命中就投玩家那一票（确认闸门【不动钱】）。
        //   同时把"玩家这一轮说了什么"记下来：模型回复里若逐字引用玩家的话，
        //   那不能算它提出了交易（回声判定，见 TradeIntentCompleter.LooksLikePlayerEcho）。
        var playerTexts = PlayerTextsOf(messages).ToArray();
        TradePreviewWiring.RememberPlayerTexts(identity.WorldId, playerTexts);
        TradePreviewWiring.HandlePlayerCommands(identity, playerTexts, _log);

        var input = runtime.BuildInputFor(world, person, identity.Day, identity.Season, identity.Data);
        if (input == null)
            return Skip(context, identityText, before, "组装注入输入失败（档位/性格/配置缺失）");

        var stable = PrivateMessagePatch.IfStableEnabled(runtime, input, messages);
        var volatileResult = PrivateMessagePatch.IfVolatileEnabled(runtime, input, messages);
        int after = messages.Count;

        if (stable.Changed && !stable.MessageCountUnchanged)
            return Skip(context, identityText, before, "S 段插入后 message 条数变了——按纪律拒绝注入");
        if (volatileResult.Changed && !volatileResult.MessageCountUnchanged)
            return Skip(context, identityText, before, "V 段插入后 message 条数变了——按纪律拒绝注入");

        bool injected = stable.Changed || volatileResult.Changed;
        string? skipReason = injected
            ? null
            : $"S 段未变（{stable.Placement}）且 V 段未变（{volatileResult.Placement}）";

        return new RequestInjectionOutcome(injected, context, identityText, before, after,
            stable.Detail, volatileResult.Detail, skipReason);
    }

    private static RequestInjectionOutcome Skip(string context, string identityText, int before, string reason) =>
        new(false, context, identityText, before, before, "(未执行)", "(未执行)", reason);

    /// <summary>内容里是否已经含某个标题（幂等判定）。</summary>
    private static bool Contains(List<Dictionary<string, string>> messages, string marker) =>
        messages.Any(m => m != null
                          && m.TryGetValue(SystemPromptInsert.ContentKey, out var text)
                          && text != null
                          && text.Contains(marker, StringComparison.Ordinal));

    /// <summary>取最后一个 <c>string</c> 实参——那就是原版的上下文串。</summary>
    internal static string TrailingContext(object[]? args)
    {
        if (args == null) return "";
        for (int i = args.Length - 1; i >= 0; i--)
            if (args[i] is string text) return text;
        return "";
    }

    /// <summary>从 <c>private/&lt;人物键&gt;/&lt;题材&gt;/&lt;轮次&gt;</c> 里取人物键，仅用于诊断。</summary>
    internal static string ConversationKeyOf(string? context)
    {
        if (string.IsNullOrEmpty(context)) return "";
        string[] parts = context.Split('/');
        return parts.Length >= 2 ? parts[1] : "";
    }

    /// <summary>
    /// 精确身份没拿到时的兜底：用上下文里的人物键去轨迹里对，对上了再用权威 <c>CareerData</c> 找人。
    /// <b>对不上、有歧义、或轨迹里没有权威数据，都返回 null（拒绝注入），绝不猜。</b>
    /// </summary>
    public static PrivateIdentity? MatchFromContext(string context, List<Dictionary<string, string>> messages)
    {
        string key = ConversationKeyOf(context);
        if (key.Length == 0) return null;

        var candidate = PrivateIdentitySlot.TryMatchTracked(key, null);
        if (candidate == null) return null;

        // 必须能用权威数据核对出这个人物，否则宁可不动手。
        var person = FindPerson(candidate);
        if (person == null) return null;

        string personId = person.Id ?? candidate.PersonId;
        return candidate with { PersonId = personId, Source = candidate.Source + "·轨迹兜底" };
    }

    private static List<Dictionary<string, string>>? MessagesOf(object[]? args)
    {
        if (args == null) return null;
        foreach (var argument in args)
            if (argument is List<Dictionary<string, string>> list) return list;
        return null;
    }

    /// <summary>
    /// 在身份自带的权威 <c>CareerData</c> 里按 id 找人物——<b>这是这条路径上唯一靠得住的找人方式</b>。
    /// <para>
    /// 为什么不用 <c>CareerEngine</c> 的静态入口：元数据扫描确认它<b>只有</b>
    /// <c>Person(CareerData, string)</c>，没有任何"当前生涯"属性/字段。
    /// </para>
    /// </summary>
    public static NationalSpire.CareerPerson? FindPerson(PrivateIdentity identity)
    {
        var data = identity.Data;
        if (data == null) return null;

        try
        {
            return CareerEngine.Person(data, identity.PersonId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ReportFirstCall(string reason)
    {
        if (_firstCallReported) return;
        _firstCallReported = true;
        Emit($"{ConsolePrefix} 首次实测注入：{(reason.StartsWith("已注入", StringComparison.Ordinal) ? reason : "未注入｜原因 " + reason)}");
        try { (_log ?? EconomyModEntry.Runtime?.Log)?.Info("inject", $"{ConsolePrefix} 首次实测：{reason}"); }
        catch (Exception) { }
    }

    private static void Emit(string line)
    {
        try { _console?.Invoke(line, false); } catch (Exception) { }
    }
}
