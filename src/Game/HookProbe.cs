using System.Reflection;
using HarmonyLib;
using NationalSpire;
using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Game;

/// <summary>
/// <b>挂载点体检（方案 D）</b>：只观测、绝不改动任何数据。
/// <para>
/// 它存在的理由是一次真实事故：阶段 3-A/3-B 的启动自证<b>全部通过</b>，
/// 同一会话里也确实发生了 4 次真实私信请求，但"第一次被调用必定说话"的注入日志
/// <b>一行都没有</b>。这说明我们的自证有盲区——它证明了"逻辑正确 + Harmony 挂得上"，
/// 却没证明"挂上的那份就是游戏在跑的那份"。
/// </para>
/// <para>
/// 本模块回答四个问题：
/// <list type="number">
/// <item>进程里一共有几份 <c>NationalSpire</c> 程序集？各在哪里（路径 + 文件大小 + 修改时间 + 元数据 token）？</item>
/// <item><c>PrivateMessagePrompts</c> / <c>PrivateStreamParser</c> 各有几份副本？分别在哪个程序集里？</item>
/// <item><c>Compose</c> / <c>Context</c> / <c>RequestPrivateAsync</c> / <c>Feed</c> 这四个方法在运行期<b>到底有没有被调用</b>？各调用了几次？</item>
/// <item>诊断日志的下沉通道（<c>NationalSpire.Diagnostics.Record</c>）到底绑上没有？没绑上的原因是什么？</item>
/// </list>
/// </para>
/// <para>
/// <b>多副本全挂载</b>：所有查找都遍历<b>全部</b>已加载程序集，凡是定义了目标的副本都会挂上探针
/// （而不是只挂第一个匹配）。这条纪律后面会被方案 A 的注入代码复用。
/// </para>
/// </summary>
public static class HookProbe
{
    /// <summary>探针控制台前缀。搜它就能看到全部体检结论。</summary>
    public const string ConsolePrefix = "[Economy-Probe]";

    /// <summary>探针是否已启用（由配置 <c>injection.probeEnabled</c> 决定）。</summary>
    public static bool Enabled { get; private set; }

    /// <summary>四个探针各自的命中次数。</summary>
    public static int ComposeHits { get; private set; }
    public static int ContextHits { get; private set; }
    public static int RequestHits { get; private set; }
    public static int FeedHits { get; private set; }

    /// <summary>一行摘要，供 <c>[Economy-Tick]</c> 附带（玩家推进一天就能看到全部计数）。</summary>
    public static string Summary =>
        $"compose={ComposeHits} context={ContextHits} request={RequestHits} feed={FeedHits}";

    /// <summary>程序集/副本清单（多行），供自证与单测断言。</summary>
    public static IReadOnlyList<string> Inventory { get; private set; } = [];

    /// <summary>已经挂上探针的目标说明。</summary>
    public static IReadOnlyList<string> PatchedTargets { get; private set; } = [];

    /// <summary>完整报告，供日志与单测。</summary>
    public static string LastReport { get; private set; } = "";

    private static IEconomyLog? _log;
    private static Action<string, bool>? _console;
    private static int _requestLogged, _contextLogged, _composeLogged, _feedLogged;

    /// <summary>每个探针前几次命中直接上控制台，之后只计数（避免刷日志）。</summary>
    private const int ConsoleHitsPerProbe = 2;

    // ─────────────────────────────────────────────────────────────────
    // 入口
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 跑一次体检：先打清单，再挂探针。
    /// </summary>
    /// <returns>本次挂上的补丁名（并入 <c>ModState.Patches</c>）。</returns>
    public static IReadOnlyList<string> Run(Harmony? harmony, IEconomyLog? log, Action<string, bool>? console,
        bool enabled)
    {
        _log = log;
        _console = console;
        Enabled = enabled;

        var lines = new List<string>();
        var patched = new List<string>();
        var report = new System.Text.StringBuilder();

        Emit($"{ConsolePrefix} 开始挂载点体检（只观测，不改任何数据）…");

        // ── 1. 程序集清单 ──
        var copies = NationalSpireCopies();
        Emit($"{ConsolePrefix} NationalSpire 程序集：共 {copies.Count} 份");
        lines.Add($"NationalSpire 程序集：{copies.Count} 份");
        foreach (var (index, assembly) in copies.Select((a, i) => (i + 1, a)))
        {
            string line = $"{ConsolePrefix}   #{index} {Describe(assembly)}";
            Emit(line);
            lines.Add(line);
        }
        if (copies.Count > 1)
            Emit($"{ConsolePrefix}   注意：多于一份！这正是「补丁挂在不执行的那一份上」的典型症状。");

        // ── 2. 目标类型副本 ──
        foreach (string typeName in new[] { "NationalSpire.PrivateMessagePrompts", "NationalSpire.PrivateStreamParser" })
        {
            var owners = AssembliesDefining(typeName);
            Emit($"{ConsolePrefix} 类型副本 {typeName}：共 {owners.Count} 份");
            lines.Add($"{typeName}：{owners.Count} 份");
            foreach (var assembly in owners)
            {
                var type = SafeType(assembly, typeName);
                string line = $"{ConsolePrefix}   · {assembly.GetName().Name}｜token={(type == null ? "?" : "0x" + type.MetadataToken.ToString("X8"))}｜{Describe(assembly)}";
                Emit(line);
                lines.Add(line);
            }
        }

        // ── 3. 诊断日志下沉通道 ──
        Emit($"{ConsolePrefix} 诊断通道：{DiagnosticsChannelReport()}");
        lines.Add("诊断通道：" + DiagnosticsChannelReport());

        if (!enabled)
        {
            Emit($"{ConsolePrefix} 探针已关闭（economy_config.json → injection.probeEnabled = false），只保留上面的清单。");
            Inventory = lines;
            LastReport = string.Join("\n", lines);
            return patched;
        }

        // ── 4. 挂四个探针 ──
        if (harmony == null)
        {
            Emit($"{ConsolePrefix} 探针未挂载：Harmony 实例未建立");
            Inventory = lines;
            LastReport = string.Join("\n", lines);
            return patched;
        }

        patched.AddRange(InstallRequestProbe(harmony, report));
        patched.AddRange(InstallComposeProbe(harmony, report));
        patched.AddRange(InstallContextProbe(harmony, report));
        patched.AddRange(InstallFeedProbe(harmony, report));

        PatchedTargets = patched;
        Inventory = lines;
        LastReport = report.ToString();
        Emit($"{ConsolePrefix} 体检装配完成：{patched.Count} 个探针｜{string.Join(" + ", patched)}");
        return patched;
    }

    // ─────────────────────────────────────────────────────────────────
    // 清单
    // ─────────────────────────────────────────────────────────────────

    /// <summary>所有名字里含 <c>NationalSpire</c> 的程序集（进程级）。</summary>
    public static IReadOnlyList<Assembly> NationalSpireCopies()
    {
        var list = new List<Assembly>();
        foreach (var assembly in Snapshot())
        {
            string name;
            try { name = assembly.GetName().Name ?? ""; } catch (Exception) { continue; }
            if (name.Contains("NationalSpire", StringComparison.OrdinalIgnoreCase)) list.Add(assembly);
        }
        return list;
    }

    /// <summary>哪些程序集定义了某个完整类型名（多副本的关键证据）。</summary>
    public static IReadOnlyList<Assembly> AssembliesDefining(string fullTypeName)
    {
        var list = new List<Assembly>();
        foreach (var assembly in Snapshot())
        {
            if (SafeType(assembly, fullTypeName) != null) list.Add(assembly);
        }
        return list;
    }

    /// <summary>程序集描述：名字 + 位置 + 文件大小/修改时间（用于区分"同名不同份"）。</summary>
    public static string Describe(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        string name, location = "(无位置)";
        bool dynamic;
        try
        {
            name = assembly.FullName ?? assembly.GetName().Name ?? "?";
            location = string.IsNullOrEmpty(assembly.Location) ? "(无位置：动态或内存加载)" : assembly.Location;
        }
        catch (Exception e)
        {
            return "读取程序集信息失败：" + e.GetType().Name;
        }

        try
        {
            dynamic = assembly.IsDynamic;
        }
        catch (Exception)
        {
            dynamic = false;
        }

        string file = "";
        try
        {
            if (File.Exists(location))
            {
                var info = new FileInfo(location);
                file = $"｜{info.Length} 字节｜改动 {info.LastWriteTime:MM-dd HH:mm:ss}";
            }
        }
        catch (Exception)
        {
            // 拿不到文件信息不影响结论。
        }

        return $"{name}｜位置 {location}{file}｜动态={dynamic}";
    }

    /// <summary>
    /// 程序集快照：<b>跨 ALC</b> 取全部（<c>AppDomain.GetAssemblies</c> 只看得到当前 ALC，
    /// 而游戏的模组加载器可能把 NationalSpire 装在另一个 ALC 里）。
    /// </summary>
    private static Assembly[] Snapshot()
    {
        try { return AssemblyInventory.AllAssemblies().ToArray(); }
        catch (Exception) { return []; }
    }

    private static Type? SafeType(Assembly assembly, string fullName)
    {
        try { return assembly.GetType(fullName, throwOnError: false); }
        catch (Exception) { return null; }
    }

    /// <summary>诊断日志下沉通道的状态（未绑上就给出原因）。</summary>
    public static string DiagnosticsChannelReport() =>
        $"诊断文件 {EconomyLogAdapter.LastBindingReport}｜控制台 {EconomyLogAdapter.LastPrintReport}";

    // ─────────────────────────────────────────────────────────────────
    // 探针装配（多副本全挂载）
    // ─────────────────────────────────────────────────────────────────

    private static IEnumerable<MethodBase> AllRequestPrivateMethods()
    {
        foreach (var assembly in NationalSpireCopies())
        {
            foreach (var type in SafeTypes(assembly))
            {
                MethodInfo[] methods;
                try { methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
                catch (Exception) { continue; }

                foreach (var method in methods)
                {
                    if (!string.Equals(method.Name, "RequestPrivateAsync", StringComparison.Ordinal)) continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length == 0) continue;
                    if (parameters[0].ParameterType.Name != "AiOptions") continue;
                    yield return method;
                }
            }
        }
    }

    private static Type[] SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t != null).Cast<Type>().ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static MethodInfo? FirstMethod(Assembly assembly, string typeName, string methodName,
        Func<MethodInfo, bool>? predicate = null)
    {
        var type = SafeType(assembly, typeName);
        if (type == null) return null;

        foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
                                              BindingFlags.NonPublic))
        {
            if (!string.Equals(method.Name, methodName, StringComparison.Ordinal)) continue;
            if (predicate != null && !predicate(method)) continue;
            return method;
        }
        return null;
    }

    private static string SignatureOf(MethodBase method) =>
        $"{method.DeclaringType?.FullName}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";

    private static IReadOnlyList<string> InstallRequestProbe(Harmony harmony, System.Text.StringBuilder report)
    {
        var patched = new List<string>();
        var probe = AccessTools.Method(typeof(HookProbe), nameof(RequestPrefix));
        if (probe == null) { Emit($"{ConsolePrefix} 探针方法未找到：RequestPrefix"); return patched; }

        foreach (var method in AllRequestPrivateMethods().Distinct())
        {
            try
            {
                harmony.Patch(method, prefix: new HarmonyMethod(probe));
                string line = $"{ConsolePrefix} 探针已挂：{SignatureOf(method)}｜token=0x{method.MetadataToken:X8}｜程序集 {method.Module.Assembly.GetName().Name}";
                Emit(line);
                report.AppendLine(line);
                patched.Add($"{method.DeclaringType?.Name}.{method.Name} (probe)");
            }
            catch (Exception e)
            {
                Emit($"{ConsolePrefix} 探针挂载失败：{SignatureOf(method)}｜{e.GetType().Name}: {e.Message}");
            }
        }

        if (patched.Count == 0)
            Emit($"{ConsolePrefix} 未找到 RequestPrivateAsync(AiOptions, …)——" +
                 "它是「私信上线前的最后一站」，找不到就只能回到上游挂载点（方案 B/C）");
        return patched;
    }

    private static IReadOnlyList<string> InstallComposeProbe(Harmony harmony, System.Text.StringBuilder report)
    {
        var patched = new List<string>();
        var probe = AccessTools.Method(typeof(HookProbe), nameof(ComposePostfix));
        if (probe == null) { Emit($"{ConsolePrefix} 探针方法未找到：ComposePostfix"); return patched; }

        foreach (var assembly in AssembliesDefining("NationalSpire.PrivateMessagePrompts"))
        {
            var method = FirstMethod(assembly, "NationalSpire.PrivateMessagePrompts", "Compose",
                m => m.GetParameters().Length >= 2);
            if (method == null) continue;
            try
            {
                harmony.Patch(method, postfix: new HarmonyMethod(probe));
                string line = $"{ConsolePrefix} 探针已挂：{SignatureOf(method)}｜token=0x{method.MetadataToken:X8}｜程序集 {assembly.GetName().Name}";
                Emit(line);
                report.AppendLine(line);
                patched.Add($"PrivateMessagePrompts.Compose (probe)");
            }
            catch (Exception e)
            {
                Emit($"{ConsolePrefix} 探针挂载失败：{SignatureOf(method)}｜{e.GetType().Name}: {e.Message}");
            }
        }
        return patched;
    }

    private static IReadOnlyList<string> InstallContextProbe(Harmony harmony, System.Text.StringBuilder report)
    {
        var patched = new List<string>();
        var probe = AccessTools.Method(typeof(HookProbe), nameof(ContextPostfix));
        if (probe == null) { Emit($"{ConsolePrefix} 探针方法未找到：ContextPostfix"); return patched; }

        foreach (var assembly in AssembliesDefining("NationalSpire.PrivateMessagePrompts"))
        {
            var method = FirstMethod(assembly, "NationalSpire.PrivateMessagePrompts", "Context",
                m => m.ReturnType == typeof(string) && m.GetParameters().Length >= 2);
            if (method == null) continue;
            try
            {
                harmony.Patch(method, postfix: new HarmonyMethod(probe));
                string line = $"{ConsolePrefix} 探针已挂：{SignatureOf(method)}｜token=0x{method.MetadataToken:X8}｜程序集 {assembly.GetName().Name}";
                Emit(line);
                report.AppendLine(line);
                patched.Add("PrivateMessagePrompts.Context (probe)");
            }
            catch (Exception e)
            {
                Emit($"{ConsolePrefix} 探针挂载失败：{SignatureOf(method)}｜{e.GetType().Name}: {e.Message}");
            }
        }
        return patched;
    }

    private static IReadOnlyList<string> InstallFeedProbe(Harmony harmony, System.Text.StringBuilder report)
    {
        var patched = new List<string>();
        var probe = AccessTools.Method(typeof(HookProbe), nameof(FeedPrefix));
        if (probe == null) { Emit($"{ConsolePrefix} 探针方法未找到：FeedPrefix"); return patched; }

        foreach (var assembly in AssembliesDefining("NationalSpire.PrivateStreamParser"))
        {
            var method = FirstMethod(assembly, "NationalSpire.PrivateStreamParser", "Feed",
                m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            if (method == null) continue;
            try
            {
                harmony.Patch(method, prefix: new HarmonyMethod(probe));
                string line = $"{ConsolePrefix} 探针已挂：{SignatureOf(method)}｜token=0x{method.MetadataToken:X8}｜程序集 {assembly.GetName().Name}";
                Emit(line);
                report.AppendLine(line);
                patched.Add("PrivateStreamParser.Feed (probe)");
            }
            catch (Exception e)
            {
                Emit($"{ConsolePrefix} 探针挂载失败：{SignatureOf(method)}｜{e.GetType().Name}: {e.Message}");
            }
        }
        return patched;
    }

    // ─────────────────────────────────────────────────────────────────
    // 探针本体（绝不改动任何数据）
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>AiService.RequestPrivateAsync</c> 的前缀：<b>私信上线前的最后一站</b>。
    /// 用 <c>object[] __args</c> 注入，这样即使原版改了参数个数/类型也照样能观测到。
    /// </summary>
    public static void RequestPrefix(object[]? __args)
    {
        try
        {
            RequestHits++;
            if (_requestLogged++ >= ConsoleHitsPerProbe) return;

            int count = -1;
            string first = "(无参数)";
            string context = "";
            if (__args is { Length: > 0 })
            {
                first = __args[0]?.GetType().Name ?? "null";
                if (__args.Length > 1 && __args[1] is System.Collections.ICollection collection) count = collection.Count;
                for (int i = __args.Length - 1; i >= 0; i--)
                {
                    if (__args[i] is string text) { context = text; break; }
                }
            }

            Emit($"{ConsolePrefix} 命中 RequestPrivateAsync（第 {RequestHits} 次）" +
                 $"｜参数 {__args?.Length ?? 0} 个｜首参 {first}｜messages={count}｜context={context}");
            _log?.Info("probe", $"RequestPrivateAsync 命中（第 {RequestHits} 次），messages={count}，context={context}");
        }
        catch (Exception)
        {
            // 探针绝不抛回原版。
        }
    }

    /// <summary><c>PrivateMessagePrompts.Compose</c> 的后缀：看它到底有没有被调用，以及是否已含我们的段。</summary>
    public static void ComposePostfix(List<Dictionary<string, string>>? __result)
    {
        try
        {
            ComposeHits++;
            if (_composeLogged++ >= ConsoleHitsPerProbe) return;

            int count = __result?.Count ?? -1;
            bool hasStable = __result?.Any(m => m.TryGetValue("content", out var text)
                                                && text.Contains("【经济状况】", StringComparison.Ordinal)) == true;
            bool hasVolatile = __result?.Any(m => m.TryGetValue("content", out var text)
                                                  && text.Contains("【当前经济】", StringComparison.Ordinal)) == true;
            Emit($"{ConsolePrefix} 命中 Compose（第 {ComposeHits} 次）｜messages={count}" +
                 $"｜已含【经济状况】={hasStable}｜已含【当前经济】={hasVolatile}");
            _log?.Info("probe", $"Compose 命中（第 {ComposeHits} 次），messages={count}，已含 S={hasStable}，已含 V={hasVolatile}");
        }
        catch (Exception)
        {
        }
    }

    /// <summary><c>PrivateMessagePrompts.Context</c> 的后缀：私信组装流程中它一定在 <c>Compose</c> 之前跑。</summary>
    public static void ContextPostfix(ref string __result, CareerData? __0, PrivateConversation? __1)
    {
        try
        {
            ContextHits++;

            // ★ 方案 A 的身份来源：Context 有 CareerData 与 PrivateConversation，
            //   而真正发送的 RequestPrivateAsync 只有 AiOptions。这里是唯一能拿到
            //   "这次在给谁、哪个世界、第几天"的地方，所以顺手记进身份槽。
            PrivateIdentitySlot.Set(__0?.WorldId, __1?.PersonId, __1?.PersonId,
                __0?.Day ?? 0, __0?.Season ?? 0, "ContextPostfix", __0);

            if (_contextLogged++ >= ConsoleHitsPerProbe) return;

            string head = (__result ?? "").Replace("\n", " ").Trim();
            if (head.Length > 40) head = head[..40] + "…";
            Emit($"{ConsolePrefix} 命中 Context（第 {ContextHits} 次）｜长度 {(__result?.Length ?? 0)}｜开头 <{head}>");
            _log?.Info("probe", $"Context 命中（第 {ContextHits} 次），长度 {__result?.Length ?? 0}");
        }
        catch (Exception)
        {
        }
    }

    /// <summary><c>PrivateStreamParser.Feed</c> 的前缀：阶段 2 的补丁用的是同一个目标，一起体检。</summary>
    public static void FeedPrefix(ref string __0)
    {
        try
        {
            FeedHits++;
            if (_feedLogged++ >= ConsoleHitsPerProbe) return;

            string text = __0 ?? "";
            bool marker = text.Contains("[Trade:", StringComparison.Ordinal);
            Emit($"{ConsolePrefix} 命中 Feed（第 {FeedHits} 次）｜分片长度 {text.Length}｜本片含 [Trade: = {marker}");
            _log?.Info("probe", $"Feed 命中（第 {FeedHits} 次），长度 {text.Length}");
        }
        catch (Exception)
        {
        }
    }

    private static void Emit(string line)
    {
        try { _console?.Invoke(line, false); } catch (Exception) { }
    }
}
