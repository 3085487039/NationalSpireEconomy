using System.Reflection;
using System.Text;
using HarmonyLib;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Trade;

namespace NationalSpireEconomy.Game;

/// <summary>
/// 给原版 <c>NationalSpire.PrivateTurn.set_Reply(String)</c> 打的 Harmony 前缀补丁：
/// <b>在"这条回复被界面渲染 / 被存档"之前，把 <c>[Trade: …]</c> 标记从正文里剥掉。</b>
/// <para>
/// <b>为什么非要有它（2026-10-12 真机 + <c>path-probe.log</c> 实测，本项目最贵的一个盲区）</b>：
/// 我们原本只在 <c>PrivateStreamParser.Feed</c> 上剥标记，并且启动自证也"通过"了 ——
/// 但玩家在私信里<b>照样看到</b> <c>[Trade: 雇佣, Amount: 3000, Direction: playerToNpc]</c>。
/// 探针把原因钉死了：真机有<b>两个</b>流式出口，我们只打了内层那个。
/// </para>
/// <code>LeadingReasoningStream.Feed("[")   ┐ 外层流：把模型的原文【逐片原样累积】
///     └→ PrivateStreamParser.Feed("[")  ┘ 内层：我们打了补丁，但它只影响解析器自己的 _visible
/// …（整个标记走完）
/// PrivateTurn.set_Reply("…含标记的完整原文…")   ← ★ 界面与存档用的是这一份，我们从没碰过
/// </code>
/// <para>
/// 旁证（同一个 4 秒窗口的探针记录，互相独立）：
/// <list type="bullet">
/// <item><c>进入 PrivateStreamParser.Feed(String)｜[0]"Trade"</c>
/// ——标记确实经过了 Feed，所以我们的识别与落单是正常的（用户单号 o0006 就是这么来的）；</item>
/// <item><c>进入 PrivateTurn.set_Reply(String)｜[0]"哦，那就是又加一小时…[Trade: …"</c>
/// ——渲染用的原文里标记<b>还在</b>。</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么启动自证没抓到它</b>：<c>PatchDiagnostics.SelfTest</c> 造的是一个
/// <c>PrivateStreamParser</c> 并读它的 <c>Text</c> —— 那验证的是"解析器的可见文本"，
/// 而玩家看的是 <c>PrivateTurn.Reply</c>。<b>"我测过的那条路"≠"真机走的那条路"</b>，
/// 这条铁律在这个项目里已经第三次应验（前两次见 HANDOFF-PHASE-4-STEP4 §7）。
/// </para>
/// <para>
/// <b>它只改文本，不写任何状态</b>：不解析、不落单（落单只归
/// <see cref="TradeMarkerInterceptor.RewriteFeed"/>）、不动金额、不碰影子存档。
/// 因此即使它装不上，也只是"玩家多看到一行机器语法"，不会破坏账。
/// </para>
/// </summary>
public static class PrivateReplyPatch
{
    /// <summary>原版承载"回复正文"的类型。</summary>
    public const string TargetTypeName = "NationalSpire.PrivateTurn";

    /// <summary>被渲染/被存档的那个属性赋值方法。</summary>
    public const string MethodName = "set_Reply";

    /// <summary>补丁是否已经装上。</summary>
    public static bool Applied { get; private set; }

    /// <summary>装配细节（成功或失败原因），用于日志与自检。</summary>
    public static string Detail { get; private set; } = "未装配";

    /// <summary>
    /// 运行期真正剥掉过标记的次数。<b>这是"补丁命中了真机那条路"的唯一硬证据</b>——
    /// 光看"已挂载成功"没用，阶段 3 就是栽在这里。
    /// </summary>
    public static int StrippedCount { get; private set; }

    /// <summary>
    /// 运行期<b>看到过标记</b>的次数（在剥离之前）。
    /// <para>
    /// 它和 <see cref="StrippedCount"/> 的关系是"够不够"的问题：两者一起涨说明标记确实
    /// 到过这个渲染出口、而且被我们处理了；<b>只有这一个涨而界面仍显示标记</b>，
    /// 就说明渲染读的不是这个字段（那才需要换出口）。
    /// </para>
    /// </summary>
    public static int SawMarkerCount { get; private set; }

    /// <summary>把这个类型上的补丁卸掉（幂等）。失败只记日志。</summary>
    public static void Unapply(Harmony harmony, IEconomyLog? log)
    {
        try
        {
            var target = FindTargetType();
            var setter = target == null ? null : FindSetter(target);
            if (setter != null)
                harmony.Unpatch(setter, AccessTools.Method(typeof(PrivateReplyPatch), nameof(ReplyPrefix)));
        }
        catch (Exception e)
        {
            log?.Warn("patch", "卸载 set_Reply 补丁失败（游戏即将退出时属正常）：" + e.Message, e);
        }

        Applied = false;
        Detail = "未装配";
        StrippedCount = 0;
    }

    /// <summary>
    /// 在已加载的程序集里找目标类型。
    /// <para>
    /// <b>不用 <c>AccessTools.TypeByName</c></b>：我们排在 NationalSpire 前面初始化，
    /// 它只搜已加载的程序集，必然返回 null（阶段 2 实测）。这里逐个程序集找。
    /// </para>
    /// </summary>
    public static Type? FindTargetType()
    {
        try
        {
            foreach (var assembly in AssemblyInventory.AllAssemblies())
            {
                try
                {
                    var found = assembly.GetType(TargetTypeName, throwOnError: false);
                    if (found != null) return found;
                }
                catch (Exception)
                {
                    // 动态程序集/加载异常：跳过这一个，不影响其他程序集。
                }
            }
        }
        catch (Exception)
        {
            // 枚举程序集失败时放弃，由调用方报"找不到类型"。
        }

        return null;
    }

    /// <summary>找 <c>set_Reply(string)</c>。原版是属性 setter，所以参数类型是 string。</summary>
    public static MethodBase? FindSetter(Type target)
    {
        try
        {
            return target.GetMethod(MethodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, [typeof(string)], null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 装配补丁（由 <see cref="PatchDiagnostics"/> 在类型解析好之后调用）。
    /// <b>失败只记日志、返回 false</b>：装不上不该让模组或游戏崩。
    /// </summary>
    public static bool ApplyTo(Harmony harmony, Type target, IEconomyLog? log)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        ArgumentNullException.ThrowIfNull(target);

        try
        {
            var setter = FindSetter(target);
            if (setter == null)
            {
                Detail = $"找不到 {target.FullName}.{MethodName}(string)";
                log?.Error("patch", Detail);
                return false;
            }

            var prefix = AccessTools.Method(typeof(PrivateReplyPatch), nameof(ReplyPrefix));
            if (prefix == null)
            {
                Detail = "补丁方法未找到：ReplyPrefix";
                log?.Error("patch", Detail);
                return false;
            }

            // 先卸后挂：Harmony 不挡重复挂载，而补丁记录又可能比 detour 活得久
            //（与 PrivateStreamPatch 同一条实测结论，见那里的注释）。
            try { harmony.Unpatch(setter, prefix); }
            catch (Exception) { /* 卸不掉就照常挂 */ }

            harmony.Patch(setter, prefix: new HarmonyMethod(prefix));

            Applied = true;
            Detail = $"{target.FullName}.{MethodName}(string)";
            log?.Info("patch",
                $"已拦截 {Detail}（显示出口：渲染前剥 [Trade: …] 标记）");
            return true;
        }
        catch (Exception e)
        {
            Detail = e.GetType().Name + ": " + e.Message;
            log?.Error("patch", "装配 set_Reply 补丁失败：" + Detail, e);
            return false;
        }
    }

    /// <summary>
    /// <c>set_Reply</c> 的前缀。
    /// <para>
    /// 参数名 <c>__0</c> 是 Harmony 的注入约定，<b>改名会让注入失效</b>。
    /// 返回 <c>true</c> 表示继续执行原方法（我们只改入参，不替换行为）。
    /// </para>
    /// </summary>
    public static bool ReplyPrefix(ref string __0)
    {
        try
        {
            // ── 第一段：进门就自检"标记是否穿透到渲染路径" ──
            //    真机上出现了"补丁装配成功、自证通过、运行期也确实剥到了文本，
            //    但玩家界面仍能看到标记"这种自相矛盾的局面（2026-10-12）。
            //    既然 `set_Reply` **拿到过**标记（下面这行就会证明），
            //    而它最终仍出现在界面上，那就说明渲染用的**不是**这个 `Reply` 字段 ——
            //    这行日志的作用就是把"标记确实到过这里"变成无可争辩的证据。
            if (!string.IsNullOrEmpty(__0) && ContainsMarker(__0))
            {
                SawMarkerCount++;
                TradeMarkerInterceptor.Log?.Warn("trade",
                    $"[Economy-Marker] ★标记穿透到 set_Reply（第 {SawMarkerCount} 次）｜入参长度 {__0.Length}｜" +
                    $"原文 <{Flat(__0)}>");
            }

            if (!TradeMarkerInterceptor.StripForDisplay(__0, out string cleaned)) return true;

            StrippedCount++;
            __0 = cleaned;

            // 只在真的剥掉时记录；原文用 Debug 完整留档（含换行压平），
            // 便于把"模型到底写了什么"与"界面最终显示什么"逐字对照。
            TradeMarkerInterceptor.Log?.Info("trade",
                $"[Economy-Marker] 已在显示出口剥掉交易标记（第 {StrippedCount} 次）：" +
                $"正文长度 {cleaned.Length}（标记不再进入私信界面与存档）");
            TradeMarkerInterceptor.Log?.Debug("trade",
                $"[Economy-Marker] set_Reply 剥离后正文 <{Flat(cleaned)}>");
        }
        catch (Exception)
        {
            // 剥离失败绝不能影响原版渲染：照常放行原文。
        }

        return true;
    }

    /// <summary>这段文本里还有没有交易/购买标记的开头（用于"穿透"自检）。</summary>
    private static bool ContainsMarker(string text)
    {
        foreach (string keyword in TradeProtocol.MarkerKeywords)
            if (text.Contains("[" + keyword + ":", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>把换行压成字面量并截断：日志里一行一条，便于人肉比对。</summary>
    private static string Flat(string text)
    {
        string flat = text.Replace("\r", "").Replace("\n", "\\n").Trim();
        return flat.Length > 400 ? flat[..400] + "…" : flat;
    }

    /// <summary>
    /// <b>不依赖 AI 的启动自证</b>：直接调真实的 <c>set_Reply</c>，再读回 <c>Reply</c> 属性，
    /// 看标记是不是真的没了。
    /// <para>
    /// 与 <c>PrivateStreamParser</c> 那条自证的<b>关键区别</b>：这条验证的是
    /// <b>玩家真正看到的那份文本</b>，而不是解析器自己的可见缓冲区 —— 上次就是栽在这个区别上。
    /// </para>
    /// </summary>
    /// <returns>通过=true；未通过=false；无法验证（构造不出来/读不到属性）=null。</returns>
    public static bool? SelfTest(Type target)
    {
        try
        {
            // PrivateTurn 是普通类（不像 PrivateStreamParser 需要 params string[] 构造），
            // 但为稳妥起见仍然挑参数最少/无参的构造。
            var constructor = target.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .OrderBy(c => c.GetParameters().Length)
                .FirstOrDefault(c => c.GetParameters().All(p => p.HasDefaultValue));
            constructor ??= target.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .OrderBy(c => c.GetParameters().Length)
                .FirstOrDefault();
            if (constructor == null) return null;

            object? turn = constructor.Invoke(
                constructor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray());
            if (turn == null) return null;

            var setter = FindSetter(target);
            if (setter == null) return null;

            const string marker = "[Trade: 诊断, Amount: 5000]";
            string probe = "PROBE" + marker + "END";
            setter.Invoke(turn, [probe]);

            var replyProperty = target.GetProperty("Reply",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (replyProperty?.GetValue(turn) is not string stored) return null;

            bool markerGone = !stored.Contains("[Trade:", StringComparison.Ordinal);
            bool bodyIntact = stored.Contains("PROBE", StringComparison.Ordinal)
                              && stored.Contains("END", StringComparison.Ordinal);
            return markerGone && bodyIntact;
        }
        catch (Exception)
        {
            // 构造或调用失败都视为"无法验证"，交给调用方报 null（绝不谎报通过）。
            return null;
        }
    }

    /// <summary>把结论写成一行的摘要（供 <see cref="PatchDiagnostics"/> 汇总）。</summary>
    public static string Describe() =>
        Applied
            ? $"[Economy-Patch] 显示出口补丁：成功｜{Detail}｜运行期已剥离 {StrippedCount} 次"
            : $"[Economy-Patch] 显示出口补丁：失败｜{Detail}";
}
