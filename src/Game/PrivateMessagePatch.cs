using System.Reflection;
using HarmonyLib;
using NationalSpire;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Inject;

namespace NationalSpireEconomy.Game;

/// <summary>一次 S 段注入的结果摘要（供日志与"首次说话"打点使用）。</summary>
/// <param name="Changed">是否真的改了 system 字符串。</param>
/// <param name="Placement">落点（见 <c>InsertPlacements</c>）。</param>
/// <param name="Length">稳定段长度（未注入时为 0）。</param>
/// <param name="Fingerprint">稳定段指纹。</param>
/// <param name="MessageCountUnchanged">message 条数是否保持不变。</param>
/// <param name="Detail">可直接写日志的一句话。</param>
/// <param name="SkipReason">未注入时的原因；已注入时为 null。</param>
public sealed record StableInjectionResult(
    bool Changed,
    string Placement,
    int Length,
    string Fingerprint,
    bool MessageCountUnchanged,
    string Detail,
    string? SkipReason);

/// <summary>一次 V 段注入的结果摘要。</summary>
/// <param name="Changed">是否真的改了那条 user 消息。</param>
/// <param name="Placement">落点（见 <c>VolatilePlacements</c>）。</param>
/// <param name="MessageIndex">命中的消息下标；-1 表示没找到。</param>
/// <param name="Length">易变段长度（未注入时为 0）。</param>
/// <param name="Fingerprint">易变段指纹。</param>
/// <param name="PendingVoiceInjected">本次是否真的把"未竟之言"读给了模型。</param>
/// <param name="MessageCountUnchanged">message 条数是否保持不变。</param>
/// <param name="Detail">可直接写日志的一句话。</param>
/// <param name="SkipReason">未注入时的原因；已注入时为 null。</param>
public sealed record VolatileInjectionResult(
    bool Changed,
    string Placement,
    int MessageIndex,
    int Length,
    string Fingerprint,
    bool PendingVoiceInjected,
    bool MessageCountUnchanged,
    string Detail,
    string? SkipReason);

/// <summary>
/// 给原版 <c>NationalSpire.PrivateMessagePrompts.Compose</c> 打的后缀补丁：
/// 把稳定段（S 段）追加进 system 字符串。
/// <para>
/// <b>为什么是后缀补丁</b>：原版把 system 字符串在方法体里一次拼好（含七串 Protocol），
/// 后缀拿到的就是最终列表，改一条消息的 <c>content</c> 即可，不需要重建任何东西，
/// 也不碰原版任何逻辑。
/// </para>
/// <para>
/// <b>三条纪律</b>：
/// <list type="number">
/// <item><b>不新增 message</b>。message 数组的长度与顺序与原版逐字节一致——这是甲方拍板的。
/// 所以 <see cref="SystemPromptInsert.InsertIntoMessages"/> 只改字符串。</item>
/// <item><b>参数按位注入</b>（<c>__result</c> / <c>__0</c> / <c>__1</c>）。
/// 按名字注入（<c>data</c>/<c>c</c>）会在原版重命名参数时静默失效，
/// 而按位注入不会——这与阶段 2 的 <c>Feed</c> 前缀补丁同一套做法。</item>
/// <item><b>绝不抛异常</b>。后缀补丁抛出会直接炸掉原版的私信组装。</item>
/// </list>
/// </para>
/// </summary>
public static class PrivateMessagePatch
{
    /// <summary>原版私信提示词的完整类型名。</summary>
    public const string TargetTypeName = "NationalSpire.PrivateMessagePrompts";

    /// <summary>目标方法名。</summary>
    public const string MethodName = "Compose";

    /// <summary>补丁是否已经装上。</summary>
    public static bool Applied { get; private set; }

    /// <summary>装配细节（成功或失败原因）。</summary>
    public static string Detail { get; private set; } = "未装配";

    /// <summary>已打上的补丁方法名。</summary>
    public static IReadOnlyList<string> PatchedMethods { get; private set; } = [];

    /// <summary>本进程内 <c>Compose</c> 后缀被调用过多少次（<b>无论是否注入成功</b>）。</summary>
    public static int Invocations { get; private set; }

    /// <summary>本进程内成功注入过多少次 S 段。</summary>
    public static int Injections { get; private set; }

    /// <summary>本进程内成功注入过多少次 V 段。</summary>
    public static int VolatileInjections { get; private set; }

    /// <summary>最近一次 S 段插入位置（见 <see cref="InsertPlacements"/>）。</summary>
    public static string LastPlacement { get; private set; } = "";

    /// <summary>最近一次注入的稳定段长度与指纹。</summary>
    public static int LastStableLength { get; private set; }
    public static string LastFingerprint { get; private set; } = "";

    /// <summary>最近一次 S 段注入后，message 条数是否保持原样。</summary>
    public static bool LastMessageCountUnchanged { get; private set; }

    /// <summary>最近一次 V 段的插入位置、长度、指纹与命中的消息下标。</summary>
    public static string LastVolatilePlacement { get; private set; } = "";
    public static int LastVolatileLength { get; private set; }
    public static string LastVolatileFingerprint { get; private set; } = "";
    public static int LastVolatileMessageIndex { get; private set; } = -1;

    /// <summary>最近一次错误说明。null 表示没有错误。</summary>
    public static string? LastError { get; private set; }

    private static IEconomyLog? _log;
    private static Action<string, bool>? _console;
    private static bool _firstReportDone;
    private static bool _firstVolatileReported;
    private static bool _firstCallReported;
    private static bool _digitViolationReported;

    /// <summary>
    /// 装配补丁。失败只记日志并返回 false，<b>不抛异常</b>。
    /// </summary>
    public static bool ApplyTo(Harmony harmony, Type target, MethodBase compose, IEconomyLog? log,
        Action<string, bool>? console)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(compose);

        _log = log;
        _console = console;

        // 幂等：反复 Initialize（测试里会反复调）不该把补丁叠两遍。
        if (Applied && PatchedMethods.Count > 0) return true;

        try
        {
            harmony.Patch(compose,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(PrivateMessagePatch), nameof(ComposePostfix))));

            PatchedMethods = [$"{MethodName} (postfix)"];
            Applied = true;
            Detail = $"{target.FullName}.{MethodName} 后缀";
            log?.Info("inject", "已挂载 " + Detail);
            return true;
        }
        catch (Exception e)
        {
            Detail = e.GetType().Name + ": " + e.Message;
            LastError = Detail;
            log?.Error("inject", "挂载 S 段注入补丁失败：" + Detail, e);
            return false;
        }
    }

    /// <summary>
    /// <b>按设计停用本补丁</b>（单轨原则：注入改由方案 A 的 <c>RequestPrivateAsync</c> 接管）。
    /// <para>
    /// 阶段 3-D 实测证明 <c>PrivateMessagePrompts.Compose</c> 不在真实私信的执行路径上；
    /// 保留两处注入点会导致同一条提示词被插两份。这里的语义是"退场"，不是"挂载失败"，
    /// 所以 <see cref="Detail"/> 会明确写出原因，便于日志里一眼分辨。
    /// </para>
    /// <para>
    /// <b>为什么不在这里用 <c>Unpatch</c> 摘掉</b>：本方法可能在 Harmony 实例还没建立时被调用
    /// （初始化早期），那时 <c>Unpatch</c> 既做不成事、又会写一条像故障的警告。
    /// 而"是否挂载"的真相由 <see cref="Applied"/> 表达，重置它就足够可靠。
    /// </para>
    /// </summary>
    public static void MarkRetired(string reason)
    {
        Applied = false;
        PatchedMethods = [];
        Detail = "已按设计停用：" + reason;
    }

    /// <summary>卸载。失败只记日志。</summary>
    public static void Unapply(Harmony? harmony, IEconomyLog? log)
    {
        try
        {
            if (harmony != null && FindComposeMethod() is { } compose)
                harmony.Unpatch(compose, AccessTools.Method(typeof(PrivateMessagePatch), nameof(ComposePostfix)));
        }
        catch (Exception e)
        {
            log?.Warn("inject", "卸载 S 段注入补丁失败（游戏退出时属正常）：" + e.Message);
        }

        Applied = false;
        PatchedMethods = [];
    }

    /// <summary>在已加载的程序集里找目标方法。找不到返回 null。</summary>
    public static MethodBase? FindComposeMethod()
    {
        foreach (var assembly in AssemblyInventory.AllAssemblies())
        {
            Type? type;
            try
            {
                type = assembly.GetType(TargetTypeName, throwOnError: false);
            }
            catch (Exception)
            {
                continue;
            }
            if (type == null) continue;
            var method = FindComposeMethod(type);
            if (method != null) return method;
        }
        return null;
    }

    /// <summary>在本类型上找 <c>Compose</c>：返回类型是消息列表、参数 3 个。</summary>
    public static MethodBase? FindComposeMethod(Type target)
    {
        ArgumentNullException.ThrowIfNull(target);

        foreach (var method in target.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
        {
            if (!string.Equals(method.Name, MethodName, StringComparison.Ordinal)) continue;
            if (method.GetParameters().Length != 3) continue;
            if (!typeof(List<Dictionary<string, string>>).IsAssignableFrom(method.ReturnType)) continue;
            return method;
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────
    // Harmony 后缀
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>Compose</c> 的后缀：先注入 S 段（system 尾部），再注入 V 段（<c>ordered.Current</c> 那条 user 消息）。
    /// <para>
    /// 参数名 <c>__result</c> / <c>__0</c> / <c>__1</c> 是 Harmony 的注入约定。
    /// 这里刻意用<b>按位</b>注入而不是按名字（<c>data</c>/<c>c</c>）：原版重命名参数时按名字注入会静默失效。
    /// </para>
    /// <para>
    /// <b>打点纪律（阶段 3-B 修）</b>：本方法<b>第一次被调用时无论走到哪一步都要说话</b>。
    /// 阶段 3-A 的实测问题正是"装上了、但静默提前返回"，于是日志里查不到任何动态痕迹，
    /// 分不清"补丁没被调用"和"被调用了但条件不满足"。现在每条提前返回都会带上具体原因。
    /// </para>
    /// </summary>
    public static void ComposePostfix(List<Dictionary<string, string>>? __result, CareerData? __0,
        PrivateConversation? __1)
    {
        Invocations++;

        try
        {
            var runtime = EconomyModEntry.Runtime;
            if (runtime == null)
            {
                ReportFirstCall(null, "模组未启用（EconomyModEntry.Runtime 为空）");
                return;
            }

            if (__result == null || __0 == null || __1 == null)
            {
                ReportFirstCall(runtime, "Harmony 按位注入没拿到参数（原版改了参数个数或类型？）");
                return;
            }

            string personId = __1.PersonId ?? "";
            if (personId.Length == 0)
            {
                ReportFirstCall(runtime, "这次会话没有 PersonId");
                return;
            }

            // ★ 方案 A 的关键：把"这次在给谁组装私信"记进身份槽。
            //   Compose 之后不久就是真正发送；RequestPrivateAsync 只有 AiOptions，拿不到人物，
            //   只能靠这个槽。顺带它也把"Compose 到底跑没跑"变成了可观测事实。
            PrivateIdentitySlot.Set(__0.WorldId, personId, personId, __0.Day, __0.Season, "ComposePostfix", __0);

            var person = CareerEngine.Person(__0, personId);
            if (person == null)
            {
                ReportFirstCall(runtime, $"人物 {personId} 已不在 People / DeletedPeople 里");
                return;
            }

            var world = runtime.WorldFor(__0);
            if (world == null)
            {
                ReportFirstCall(runtime,
                    $"影子存档不可用（WorldId=<{__0.WorldId ?? ""}>，为空或载入失败）");
                return;
            }

            var input = runtime.BuildInput(__0, person, world);
            if (input == null)
            {
                ReportFirstCall(runtime,
                    $"人物 {personId} 不在影子账本里（真人？或从未播种）｜账本 {world.Npcs.Count} 人");
                return;
            }

            IfStableEnabled(runtime, input, __result);
            IfVolatileEnabled(runtime, input, __result);
        }
        catch (Exception e)
        {
            // 后缀里抛异常会炸掉原版私信组装，所以这里必须兜住一切。
            LastError = e.GetType().Name + ": " + e.Message;
            try
            {
                EconomyModEntry.Runtime?.Log.Error("inject", "S/V 段注入抛异常，已忽略本次注入。", e);
                ReportFirstCall(EconomyModEntry.Runtime, "注入过程抛异常：" + LastError);
            }
            catch (Exception)
            {
                // 连日志都失败了就只能放弃。
            }
        }
    }

    // ── S 段：system 尾部 ──────────────────────────────────────────────

    /// <summary>
    /// 注入稳定段。返回结构化结果——方案 A 的前缀补丁要用它决定"该写什么日志"。
    /// </summary>
    public static StableInjectionResult IfStableEnabled(ShadowEconomyRuntime runtime, PromptInjectInput input,
        List<Dictionary<string, string>> messages)
    {
        // 总开关在 economy_config.json 的 injection.stableEnabled。
        // 另有 ai_prompt_economy.json 的 stableEnabled（字符串），两处都得开着才注入。
        if (!runtime.Config.Economy.Injection.StableEnabled)
        {
            string reason = "总开关 injection.stableEnabled = false，本次不注入";
            ReportStable(runtime, reason, messages.Count, messageCountUnchanged: true, skipped: true);
            return new StableInjectionResult(false, InsertPlacements.NoStableText, 0, "", true, reason, reason);
        }

        // 先算不提交，插入成功之后才提交指纹：
        // 否则"这次没能插进去"会被误记成"稳定段已经是最新的"，下次也不重试。
        var probe = runtime.Injector.BuildStableOnly(input, commit: false);
        if (probe.StableText.Length == 0)
        {
            string reason = "稳定段为空（配置里 stableEnabled 关闭？）";
            ReportStable(runtime, reason, messages.Count, messageCountUnchanged: true, skipped: true);
            return new StableInjectionResult(false, InsertPlacements.NoStableText, 0, probe.Fingerprint, true,
                reason, reason);
        }

        int before = messages.Count;
        string anchor = runtime.Config.Prompt.Get("stableAnchor", SystemPromptInsert.DefaultAnchor);
        var insertion = SystemPromptInsert.InsertIntoMessages(messages, probe.StableText, anchor);
        if (!insertion.Changed)
        {
            string reason = "未插入（" + insertion.Placement + "）";
            ReportStable(runtime, reason, before, messageCountUnchanged: true, skipped: true);
            return new StableInjectionResult(false, insertion.Placement, probe.StableText.Length, probe.Fingerprint,
                true, reason, reason);
        }

        CacheGuard.RefreshFingerprint(input.World, probe.Fingerprint);

        bool countUnchanged = messages.Count == before;
        bool digits = ContainsDigit(probe.StableText);

        Injections++;
        LastPlacement = insertion.Placement;
        LastStableLength = probe.StableText.Length;
        LastFingerprint = probe.Fingerprint;
        LastMessageCountUnchanged = countUnchanged;
        LastError = null;

        if (digits && !_digitViolationReported)
        {
            // 稳定段混进数字会破坏前缀缓存，这是硬性契约，必须响一声。
            _digitViolationReported = true;
            EmitConsole($"[Economy-Inject] 警告：稳定段出现数字，前缀缓存会失效（长度 {probe.StableText.Length}）。");
            runtime.Log.Warn("inject", "稳定段出现数字，前缀缓存会失效。");
        }

        string detail =
            $"注入成功｜位置 {insertion.Placement}｜长度 {probe.StableText.Length}｜指纹 {probe.Fingerprint}" +
            $"｜零数字 {(digits ? "否" : "是")}";

        ReportStable(runtime, detail, before, countUnchanged, skipped: false);
        return new StableInjectionResult(true, insertion.Placement, probe.StableText.Length, probe.Fingerprint,
            countUnchanged, detail, null);
    }

    // ── V 段：ordered.Current 那条 user 消息 ──────────────────────────

    /// <summary>
    /// 注入易变段。<b>所有数字只允许出现在这里</b>，稳定段必须保持零数字。
    /// 返回结构化结果，供方案 A 的前缀补丁打点。
    /// </summary>
    public static VolatileInjectionResult IfVolatileEnabled(ShadowEconomyRuntime runtime, PromptInjectInput input,
        List<Dictionary<string, string>> messages)
    {
        string anchor = runtime.Config.Prompt.Get("volatileAnchor", CurrentSegmentInsert.DefaultAnchor);
        int before = messages.Count;

        if (!runtime.Config.Economy.Injection.VolatileEnabled)
        {
            string reason = "总开关 injection.volatileEnabled = false，本次不注入";
            ReportVolatile(runtime, reason, messages.Count, true);
            return new VolatileInjectionResult(false, VolatilePlacements.NoVolatileText, -1, 0, "", false, true,
                reason, reason);
        }

        // 同样先算不提交：清空 PendingVoice 这件事只有插入真的发生了才做。
        var probe = runtime.Injector.BuildVolatileOnly(input, commit: false);
        if (!probe.Enabled)
        {
            string reason = "易变段被配置关闭（ai_prompt_economy.json 的 volatileEnabled）";
            ReportVolatile(runtime, reason, messages.Count, true);
            return new VolatileInjectionResult(false, VolatilePlacements.NoVolatileText, -1, 0, probe.Fingerprint,
                false, true, reason, reason);
        }

        if (!probe.HasText)
        {
            string reason = "易变段为空（模板缺失？）";
            ReportVolatile(runtime, reason, messages.Count, true);
            return new VolatileInjectionResult(false, VolatilePlacements.NoVolatileText, -1, 0, probe.Fingerprint,
                false, true, reason, reason);
        }

        var insertion = CurrentSegmentInsert.InsertIntoMessages(messages, probe.Text, anchor);
        bool countUnchanged = messages.Count == before;

        if (!insertion.Changed)
        {
            // 计数器与"最近一次"状态只在真正插入成功时更新——
            // 与 S 段保持同一套语义：看日志的人必须能把"注入 N 次"对上自己的操作次数。
            string reason = $"未插入（{insertion.Placement}）｜锚点 <{anchor}>｜长度 {probe.Text.Length}" +
                            $"｜指纹 {probe.Fingerprint}";
            ReportVolatile(runtime, reason, messages.Count, countUnchanged);
            return new VolatileInjectionResult(false, insertion.Placement, insertion.MessageIndex, probe.Text.Length,
                probe.Fingerprint, false, countUnchanged, reason, reason);
        }

        VolatileInjections++;
        LastVolatilePlacement = insertion.Placement;
        LastVolatileLength = probe.Text.Length;
        LastVolatileFingerprint = probe.Fingerprint;
        LastVolatileMessageIndex = insertion.MessageIndex;

        // 插入成功之后才提交副作用：这一轮真的把"未竟之言"读给模型听了，可以清空。
        runtime.Injector.ClearPendingVoice(input.Npc, probe.PendingVoiceInjected);

        string detail =
            $"注入成功｜位置 {insertion.Placement}｜message 下标 {insertion.MessageIndex}" +
            $"｜长度 {probe.Text.Length}｜指纹 {probe.Fingerprint}" +
            $"｜含数字 {(ContainsDigit(probe.Text) ? "是" : "否")}" +
            $"｜读出未竟之言 {(probe.PendingVoiceInjected ? "是" : "否")}";

        ReportVolatile(runtime, detail, messages.Count, countUnchanged);
        return new VolatileInjectionResult(true, insertion.Placement, insertion.MessageIndex, probe.Text.Length,
            probe.Fingerprint, probe.PendingVoiceInjected, countUnchanged, detail, null);
    }

    // ─────────────────────────────────────────────────────────────────
    // 打点
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>第一次被调用时无条件写控制台</b>，把"走到哪一步/为什么没注入"说清楚；
    /// 之后同样的说明只进诊断日志。
    /// </summary>
    private static void ReportFirstCall(ShadowEconomyRuntime? runtime, string reason)
    {
        if (_firstCallReported) return;
        _firstCallReported = true;
        EmitConsole($"[Economy-Inject] 首次实测注入：未注入｜原因 {reason}");
        try { runtime?.Log.Warn("inject", "首次实测注入未发生：" + reason); } catch (Exception) { }
    }

    /// <summary>S 段的汇报：首次一定上控制台，之后只进诊断日志。</summary>
    private static void ReportStable(ShadowEconomyRuntime runtime, string detail, int messageCount,
        bool messageCountUnchanged, bool skipped)
    {
        string tail = $"｜message {messageCount} 条（{(messageCountUnchanged ? "条数不变" : "条数变了")}）";

        if (!_firstReportDone)
        {
            _firstReportDone = true;
            EmitConsole($"[Economy-Inject] 首次实测注入：{detail}{tail}");
        }
        runtime.Log.Debug("inject", detail + tail);
        if (skipped) runtime.Log.Info("inject", "S 段本次未注入：" + detail);
    }

    /// <summary>V 段的汇报。关键词是 <c>[Economy-Inject-V]</c>，首次同样一定上控制台。</summary>
    private static void ReportVolatile(ShadowEconomyRuntime runtime, string detail, int messageCount,
        bool messageCountUnchanged)
    {
        string tail = $"｜message {messageCount} 条（{(messageCountUnchanged ? "条数不变" : "条数变了")}）";

        if (!_firstVolatileReported)
        {
            _firstVolatileReported = true;
            EmitConsole($"[Economy-Inject-V] 首次实测注入：{detail}{tail}");
        }
        runtime.Log.Debug("inject-v", detail + tail);
    }

    /// <summary>直接写控制台。刻意不依赖 <see cref="ShadowEconomyRuntime"/>——
    /// 模组未启用时 Runtime 是 null，而那时更需要把"没启用"说出来。</summary>
    private static void EmitConsole(string line)
    {
        try { _console?.Invoke(line, false); } catch (Exception) { }
    }

    /// <summary>文本是否含数字。稳定段与易变段的契约分别靠它来断言。</summary>
    public static bool ContainsDigit(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, @"\d");
}
