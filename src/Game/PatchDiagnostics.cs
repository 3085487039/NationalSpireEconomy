using System.Reflection;
using System.Text;
using HarmonyLib;
using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Game;

/// <summary>
/// 补丁挂载诊断。**无论成功还是失败都必须留下痕迹**——阶段 2 的第一次交付就是因为
/// 失败原因只写进了没人会去看的那条日志通道，白白浪费了一轮排查。
/// <para>
/// 它做四件事：
/// <list type="number">
/// <item>把 NationalSpire 程序集<b>显式加载</b>进来（我们排在它前面初始化，不加载就一定找不到类型）。</item>
/// <item>逐个探测补丁目标，把"目标类型/方法在不在、签名对不对"写清楚。</item>
/// <item>用<b>不依赖 AI</b> 的方式自证补丁生效：自己造一个解析器实例喂文本、看标记有没有被吃掉。</item>
/// <item>把结论打到 Godot 控制台（<c>logs\godot.log</c>）与诊断日志两处。</item>
/// </list>
/// </para>
/// </summary>
public static class PatchDiagnostics
{
    /// <summary>控制台的固定前缀。搜这个就能找到全部诊断结论。</summary>
    public const string ConsolePrefix = "[Economy-Patch]";

    /// <summary>自证用的探针文本。含一个必然被识别的标记。</summary>
    public const string ProbeMarker = "[Trade: 诊断, Amount: 5000]";

    /// <summary>最近一次诊断的完整结论，供单测与日志使用。</summary>
    public static string LastReport { get; private set; } = "";

    /// <summary>自证是否通过。<c>null</c> 表示没能验证（例如类型都找不到）。</summary>
    public static bool? SelfTestPassed { get; private set; }

    /// <summary>自证过程中抛出的异常说明。为 null 表示没有异常。</summary>
    public static string? SelfTestError { get; private set; }

    /// <summary>
    /// 跑一遍完整诊断并输出结论。
    /// </summary>
    /// <param name="harmony">已建立的 Harmony 实例；为 null 表示连 Harmony 都没起来。</param>
    /// <param name="log">日志出口。</param>
    /// <param name="console">控制台输出（可为 null）。</param>
    /// <param name="modsDir">mods 目录，用于回退查找 NationalSpire。</param>
    /// <param name="dataDir">游戏程序集目录，用于回退查找。</param>
    /// <returns>诊断报告（多行文本）。</returns>
    public static string Run(Harmony? harmony, IEconomyLog? log, Action<string, bool>? console,
        string? modsDir, string? dataDir)
    {
        var report = new StringBuilder();

        Emit(console, log, $"{ConsolePrefix} 正在尝试挂载补丁…");

        // ── 1. NationalSpire 程序集 ──
        Assembly? nationalSpire = NationalSpireLocator.EnsureLoaded(modsDir, dataDir);
        if (nationalSpire == null)
        {
            string reason = NationalSpireLocator.FailureReason;
            Emit(console, log, $"{ConsolePrefix} 失败：找不到 NationalSpire 程序集。{reason}");
            Emit(console, log, $"  · 模组清单路径：{DescribeCandidates(modsDir, dataDir)}");
            LastReport = reason;
            SelfTestPassed = null;
            return LastReport;
        }

        report.AppendLine($"NationalSpire 程序集：{NationalSpireLocator.LoadedFrom}");
        // ★ 必须把 ALC 写出来（阶段 3 定案的判据）：落在 Default 就说明又造出了一份
        //   "游戏永远不会执行"的假副本，后面所有"挂载成功"都是假的。
        report.AppendLine($"  所在 ALC：{NationalSpireLocator.LoadedContext}" +
                          (NationalSpireLocator.LoadedContext == "Default"
                              ? "（★Default：与游戏模组加载器不是同一个 ALC，可能又是假副本）"
                              : "（与模组同一个 ALC：游戏执行的就是这一份）"));
        Emit(console, log,
            $"{ConsolePrefix} 原版程序集：ALC {NationalSpireLocator.LoadedContext}｜{NationalSpireLocator.LoadedFrom}");

        // ── 2. 目标类型与方法 ──
        var target = nationalSpire.GetType(PrivateStreamPatch.TargetTypeName, throwOnError: false);
        if (target == null)
        {
            string reason = $"找不到类型 {PrivateStreamPatch.TargetTypeName}";
            Emit(console, log, $"{ConsolePrefix} 失败：{reason}（原版改名了？）");
            AppendSimilarTypes(report, nationalSpire);
            LastReport = reason;
            SelfTestPassed = null;
            return LastReport;
        }

        var feed = target.GetMethod(PrivateStreamPatch.FeedMethodName,
            BindingFlags.Public | BindingFlags.Instance, null, [typeof(string)], null);
        var finish = target.GetMethod(PrivateStreamPatch.FinishMethodName,
            BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

        report.AppendLine($"  目标类型：{target.FullName}（public={target.IsPublic}）");
        report.AppendLine($"  Feed(string)：{(feed == null ? "缺失" : "存在")}");
        report.AppendLine($"  Finish()    ：{(finish == null ? "缺失" : "存在")}");

        if (feed == null)
        {
            string reason = $"{PrivateStreamPatch.TargetTypeName}.{PrivateStreamPatch.FeedMethodName}(string) 不存在";
            Emit(console, log, $"{ConsolePrefix} 失败：{reason}（签名变了）");
            LastReport = reason;
            SelfTestPassed = null;
            return LastReport;
        }

        // ── 3. 装配 ──
        if (harmony == null)
        {
            const string reason = "Harmony 实例未建立";
            Emit(console, log, $"{ConsolePrefix} 失败：{reason}");
            LastReport = reason;
            SelfTestPassed = null;
            return LastReport;
        }

        bool applied = PrivateStreamPatch.ApplyTo(harmony, target, feed, finish, log);
        report.AppendLine($"  装配结果：{(applied ? "成功" : "失败")}｜{PrivateStreamPatch.Detail}");

        // ── 4. 自证：不依赖 AI，直接验证标记有没有被吃掉 ──
        bool? selfTest = SelfTest(target);
        report.AppendLine($"  自证（喂 {ProbeMarker}）：{(selfTest == true ? "通过，标记已被剥离" : selfTest == false ? "未通过，标记仍在正文里" : "无法验证")}");
        SelfTestPassed = selfTest;

        // ── 5. ★ 显示出口补丁（2026-10-12 真机挖出的盲区，见 PrivateReplyPatch 的说明）──
        //    上面那条自证只验证了"解析器的可见文本"，而玩家看的是 PrivateTurn.Reply。
        //    这一节补上真正被渲染的那条路。
        Emit(console, log, ApplyDisplayPatch(harmony, nationalSpire, log, report));

        // ── 6. ★ 并行可见流补丁（同日第二轮实测：真机有【两条】并行的流式出口）──
        //    LeadingReasoningStream.Feed 与 PrivateStreamParser.Feed 逐分片一一对应（各 4369 次），
        //    我们原先只给后者打了补丁，于是前者自己累积的文本把标记带到了界面上。
        Emit(console, log, ApplyLeadingStreamPatch(harmony, nationalSpire, log, report));

        string summary = applied
            ? $"{ConsolePrefix} 结果：成功｜{PrivateStreamPatch.Detail}｜自证 {(selfTest == true ? "通过" : "未通过")}"
            : $"{ConsolePrefix} 结果：失败｜{PrivateStreamPatch.Detail}";

        Emit(console, log, summary);
        report.AppendLine(summary);
        LastReport = report.ToString();
        return LastReport;
    }

    /// <summary>
    /// 不含 AI 的自证：造一个真实的 <c>PrivateStreamParser</c>，喂一段带标记的文本，
    /// 再读它的 <c>Text</c>。补丁生效时标记会被抽走，于是正文里不含 <c>[Trade:</c>。
    /// <para>
    /// 这正是"不需要 AI 配合"的验证：它检验的是<b>补丁链路</b>，
    /// 与模型肯不肯输出标记完全无关。
    /// </para>
    /// </summary>
    public static bool? SelfTest(Type target)
    {
        SelfTestError = null;
        try
        {
            // 注意：原版的构造函数是 ctor(params string[] extraFields)，
            // 【没有】无参构造。直接用 Activator.CreateInstance(type) 会抛
            // "No parameterless constructor defined"——这是实测出来的，别改成无参形式。
            var constructor = target.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();
            if (constructor == null)
            {
                SelfTestError = "找不到可用的构造函数";
                return null;
            }

            object? parser = constructor.Invoke(
                constructor.GetParameters().Select(_ => (object?)Array.Empty<string>()).ToArray());
            if (parser == null)
            {
                SelfTestError = "构造函数返回 null";
                return null;
            }

            string text = "PROBE" + ProbeMarker + "END";
            var feed = target.GetMethod("Feed", BindingFlags.Public | BindingFlags.Instance, null, [typeof(string)], null);
            if (feed == null)
            {
                SelfTestError = "找不到 Feed(string)";
                return null;
            }
            feed.Invoke(parser, [text]);

            var textProperty = target.GetProperty("Text");
            if (textProperty?.GetValue(parser) is not string visible)
            {
                SelfTestError = "读不到 Text 属性";
                return null;
            }

            // 补丁生效：正文只剩 PROBEEND，标记不见了。
            bool markerGone = !visible.Contains("[Trade:", StringComparison.Ordinal);
            bool bodyIntact = visible.Contains("PROBE", StringComparison.Ordinal)
                              && visible.Contains("END", StringComparison.Ordinal);

            if (!markerGone) SelfTestError = $"标记仍在正文里：<{visible}>";
            else if (!bodyIntact) SelfTestError = $"正文被破坏：<{visible}>";

            return markerGone && bodyIntact;
        }
        catch (Exception e)
        {
            // 把异常记下来而不是静默返回 null：静默失败正是阶段 2 第一轮踩的坑。
            SelfTestError = e.GetType().Name + ": " + e.Message;
            return null;
        }
    }

    /// <summary>
    /// <b>并行可见流补丁</b>：给 <c>LeadingReasoningStream.Feed(string)</c> 也装上"剥标记"。
    /// <para>
    /// <b>为什么单独一节（2026-10-12 第二轮实测）</b>：探针显示真机有<b>两条并行的流式出口</b>，
    /// 参数逐字相同（各 4369 次）；我们只给 <c>PrivateStreamParser</c> 打了补丁，
    /// 于是另一条自己累积的文本把 <c>[Trade: …]</c> 带到了界面上（玩家截图证实）。
    /// </para>
    /// <para>装不上不算整体失败，但必须显式报出来。</para>
    /// </summary>
    private static string ApplyLeadingStreamPatch(Harmony harmony, Assembly nationalSpire,
        IEconomyLog? log, StringBuilder report)
    {
        try
        {
            var type = nationalSpire.GetType(LeadingStreamPatch.TargetTypeName, throwOnError: false);
            if (type == null)
            {
                // 这条流在有些版本里可能不存在（例如推理流被关掉时）——报"跳过"而不是"失败"。
                string line = $"{ConsolePrefix} 并行可见流补丁：跳过｜本版没有 {LeadingStreamPatch.TargetTypeName}";
                report.AppendLine("  " + line);
                return line;
            }

            bool ok = LeadingStreamPatch.ApplyTo(harmony, type, log);
            string text = ok
                ? $"{ConsolePrefix} 并行可见流补丁：成功｜{LeadingStreamPatch.Detail}"
                : $"{ConsolePrefix} 并行可见流补丁：失败｜{LeadingStreamPatch.Detail}";
            report.AppendLine("  " + text);
            return text;
        }
        catch (Exception e)
        {
            string line = $"{ConsolePrefix} 并行可见流补丁：异常｜{e.GetType().Name}: {e.Message}";
            report.AppendLine("  " + line);
            return line;
        }
    }

    /// <summary>
    /// <b>显示出口补丁</b>：给 <c>PrivateTurn.set_Reply(string)</c> 装上"渲染前剥标记"。
    /// <para>
    /// <b>为什么单独一节（2026-10-12）</b>：真机里玩家明明该看不到标记，却看到了。
    /// 探针证明渲染用的是 <c>PrivateTurn.Reply</c>，而上面那条自证只覆盖了
    /// <c>PrivateStreamParser</c> 的可见文本 —— 两者是<b>不同的出口</b>。
    /// 这一节负责把"真正被渲染的那条路"也装上、并且<b>对着它</b>做自证。
    /// </para>
    /// <para>
    /// 它装不上不算整体失败（解析与落单仍然照常工作），但必须<b>显式报出来</b>，
    /// 否则玩家又会看到一串机器语法而没人知道为什么。
    /// </para>
    /// </summary>
    private static string ApplyDisplayPatch(Harmony harmony, Assembly nationalSpire,
        IEconomyLog? log, StringBuilder report)
    {
        try
        {
            var turn = nationalSpire.GetType(PrivateReplyPatch.TargetTypeName, throwOnError: false);
            if (turn == null)
            {
                string line = $"{ConsolePrefix} 显示出口补丁：跳过｜找不到类型 {PrivateReplyPatch.TargetTypeName}";
                report.AppendLine("  " + line);
                return line;
            }

            var setter = PrivateReplyPatch.FindSetter(turn);
            report.AppendLine($"  {PrivateReplyPatch.TargetTypeName}.{PrivateReplyPatch.MethodName}(string)：" +
                              (setter == null ? "缺失" : "存在"));

            if (setter == null)
            {
                string line = $"{ConsolePrefix} 显示出口补丁：跳过｜找不到 set_Reply(string)（签名变了）";
                report.AppendLine("  " + line);
                return line;
            }

            bool ok = PrivateReplyPatch.ApplyTo(harmony, turn, log);

            bool? selfTest = PrivateReplyPatch.SelfTest(turn);
            report.AppendLine($"  显示出口自证（调真实的 set_Reply 再读回 Reply）：" +
                              (selfTest == true ? "通过，标记没有进入 Reply"
                                  : selfTest == false ? "未通过，标记仍在 Reply 里"
                                  : "无法验证"));

            string text = ok
                ? $"{ConsolePrefix} 显示出口补丁：成功｜{PrivateReplyPatch.Detail}｜自证 " +
                  (selfTest == true ? "通过" : selfTest == false ? "未通过" : "无法验证")
                : $"{ConsolePrefix} 显示出口补丁：失败｜{PrivateReplyPatch.Detail}";
            report.AppendLine("  " + text);
            return text;
        }
        catch (Exception e)
        {
            string line = $"{ConsolePrefix} 显示出口补丁：异常｜{e.GetType().Name}: {e.Message}";
            report.AppendLine("  " + line);
            return line;
        }
    }

    /// <summary>把结论同时写到控制台与诊断日志两处。</summary>
    private static void Emit(Action<string, bool>? console, IEconomyLog? log, string line)
    {
        // 控制台一定要写：它落在 %APPDATA%\SlayTheSpire2\logs\godot.log，
        // 而那才是"模组初始化阶段"唯一能看到的日志（诊断日志的钩子可能同样因为
        // NationalSpire 未加载而没接上——阶段 2 就踩了这个）。
        try
        {
            console?.Invoke(line, false);
        }
        catch (Exception)
        {
            // 控制台不可用不影响结论。
        }

        // 诊断日志是加分项，接上了就写。
        log?.Info("patch", line.Replace(ConsolePrefix, "").Trim());
    }

    private static string DescribeCandidates(string? modsDir, string? dataDir)
    {
        var paths = NationalSpireLocator.CandidatePaths(modsDir, dataDir);
        return paths.Count == 0 ? "（无候选路径存在）" : string.Join(" ; ", paths);
    }

    private static void AppendSimilarTypes(StringBuilder report, Assembly assembly)
    {
        try
        {
            var similar = assembly.GetTypes()
                .Where(t => t.Name.Contains("Stream", StringComparison.OrdinalIgnoreCase))
                .Take(10)
                .ToArray();
            if (similar.Length == 0) return;

            report.AppendLine("  名字里带 Stream 的类型：");
            foreach (var type in similar) report.AppendLine("    " + type.FullName);
        }
        catch (Exception)
        {
            // 枚举类型失败（ReflectionTypeLoadException）时跳过，不影响结论。
        }
    }
}
