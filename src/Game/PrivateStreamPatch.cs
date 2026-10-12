using System.Reflection;
using HarmonyLib;
using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Game;

/// <summary>
/// 给原版 <c>NationalSpire.PrivateStreamParser</c> 打的 Harmony 前缀补丁。
/// <para>
/// <b>为什么是前缀补丁而不是换掉原方法</b>：我们只想"改写进入的文本"，
/// 其余行为（指令解析、<c>Error</c> 置位、可见文本累积）全部保持原版。
/// 前缀拿到 <c>ref string text</c> 就能改写入参，再 <c>return true</c> 让原方法照常跑。
/// </para>
/// <para>
/// <b>目标类型与成员名用字符串定位</b>（<c>AccessTools.TypeByName</c> / <c>Method</c>）。
/// 好处是原版改名时我们只会"补丁装不上并记一条错误日志"，
/// 而不是让整个模组在加载期抛异常、连带把别人的 Harmony 补丁也搞崩。
/// </para>
/// <para>
/// <b>本阶段只改文本，不写任何状态</b>：不注入 Prompt、不动金额、不碰影子存档。
/// </para>
/// </summary>
public static class PrivateStreamPatch
{
    /// <summary>原版解析器的完整类型名。</summary>
    public const string TargetTypeName = "NationalSpire.PrivateStreamParser";

    /// <summary>喂入分片的方法名。</summary>
    public const string FeedMethodName = "Feed";

    /// <summary>流结束时被调用的方法名。</summary>
    public const string FinishMethodName = "Finish";

    /// <summary>补丁是否已经装上。</summary>
    public static bool Applied { get; private set; }

    /// <summary>补丁装配的细节（成功或失败原因），用于日志。</summary>
    public static string Detail { get; private set; } = "未装配";

    /// <summary>已成功打上的补丁方法名，便于自检。</summary>
    public static IReadOnlyList<string> PatchedMethods { get; private set; } = [];

    /// <summary>
    /// 装配补丁。<b>这是旧入口</b>，保留给单测与向后兼容；
    /// 生产路径走 <see cref="PatchDiagnostics.Run"/>，它会先确保 NationalSpire 已被加载。
    /// <para>失败时只记日志、返回 false，不抛异常——补丁装不上不该让模组或游戏崩。</para>
    /// </summary>
    public static bool Apply(Harmony harmony, IEconomyLog? log)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        TradeMarkerInterceptor.Install(log);

        try
        {
            var target = FindTargetType();
            if (target == null)
            {
                Detail = $"找不到类型 {TargetTypeName}（原版改名或未加载）";
                log?.Error("patch", Detail);
                return false;
            }

            var feed = AccessTools.Method(target, FeedMethodName, [typeof(string)]);
            var finish = AccessTools.Method(target, FinishMethodName, Type.EmptyTypes);
            return ApplyTo(harmony, target, feed, finish, log);
        }
        catch (Exception e)
        {
            Detail = e.GetType().Name + ": " + e.Message;
            log?.Error("patch", "装配补丁失败，正在回滚已打上的部分。", e);
            TryUnpatch(harmony, log);
            return false;
        }
    }

    /// <summary>
    /// 已经解析好目标类型与方法之后真正打补丁。
    /// 由 <see cref="PatchDiagnostics"/> 调用，因此诊断信息能与装配结果一一对应。
    /// </summary>
    /// <param name="harmony">Harmony 实例。</param>
    /// <param name="target">目标类型。</param>
    /// <param name="feed"><c>Feed(string)</c>；为 null 视为失败。</param>
    /// <param name="finish"><c>Finish()</c>；为 null 只降级不失败。</param>
    /// <param name="log">日志出口。</param>
    public static bool ApplyTo(Harmony harmony, Type target, MethodBase? feed, MethodBase? finish, IEconomyLog? log)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        ArgumentNullException.ThrowIfNull(target);
        TradeMarkerInterceptor.Install(log);

        var patched = new List<string>();
        try
        {
            if (feed == null)
            {
                Detail = $"找不到 {target.FullName}.{FeedMethodName}(string)";
                log?.Error("patch", Detail);
                return false;
            }

            var feedPrefix = AccessTools.Method(typeof(PrivateStreamPatch), nameof(FeedPrefix));
            var finishPrefix = AccessTools.Method(typeof(PrivateStreamPatch), nameof(FinishPrefix));

            // 幂等写法：<b>先卸后挂</b>，而不是"查记录→有就跳过"。
            // <list type="number">
            // <item><b>Harmony 不会挡重复挂载</b>：实测重复调用会在同一方法上叠出多份同名前缀
            // （一段会话里 HookProbe.FeedPrefix 叠到过 4 份），所以必须先卸。</item>
            // <item><b>记录存在 ≠ detour 还活着</b>：反复 Unpatch / 反复 Initialize 之后，
            // 实测出现过"补丁记录还在、方法体却已还原"的僵尸状态，症状就是标记不再被剥离
            // （TradeMarkerInterceptorTests.PatchAppliesToRealParserType 就是靠这条抓出来的）。
            // 先卸后挂能把这个状态修回来。</item>
            // <item>目标方法换了副本（游戏后加载的那份）时，Unpatch 是空操作，行为不变。</item>
            // </list>
            if (feedPrefix != null)
            {
                TryUnpatchMethod(harmony, feed, feedPrefix);
                harmony.Patch(feed, prefix: new HarmonyMethod(feedPrefix));
                patched.Add(FeedMethodName);
                log?.Info("patch", $"已拦截 {target.FullName}.{FeedMethodName}(string)");
            }

            // Finish 是可选目标：拿不到不算失败，只是截断的标记会被原版当普通括号显示。
            if (finish != null)
            {
                if (finishPrefix != null)
                {
                    TryUnpatchMethod(harmony, finish, finishPrefix);
                    harmony.Patch(finish, prefix: new HarmonyMethod(finishPrefix));
                    patched.Add(FinishMethodName);
                    log?.Info("patch", $"已拦截 {target.FullName}.{FinishMethodName}()");
                }
            }
            else
            {
                log?.Warn("patch", $"找不到 {target.FullName}.{FinishMethodName}()，截断的标记将按普通括号显示。");
            }

            PatchedMethods = patched;
            Applied = true;
            Detail = string.Join(" + ", patched);
            return true;
        }
        catch (Exception e)
        {
            // 装配途中失败：把已经打上的撤掉，避免半装状态。
            Detail = e.GetType().Name + ": " + e.Message;
            log?.Error("patch", "装配补丁失败，正在回滚已打上的部分。", e);
            TryUnpatch(harmony, log);
            return false;
        }
    }

    /// <summary>
    /// 卸掉某方法上"我们这条补丁方法"的既有实例（没有就什么都不做）。
    /// <para>
    /// 用它来保证"先卸后挂"：Harmony 不挡重复挂载，而补丁记录又可能比 detour 活得久，
    /// 两者都只能靠"卸一次再挂一次"来收敛到"恰好一份且是活的"。
    /// 绝不抛异常——卸不掉就照常挂（最多多一份，也比整条补丁静默失效强）。
    /// </para>
    /// </summary>
    private static void TryUnpatchMethod(Harmony harmony, MethodBase target, MethodInfo patchMethod)
    {
        try
        {
            harmony.Unpatch(target, patchMethod);
        }
        catch (Exception)
        {
            // Unpatch 失败不影响后面的 Patch。
        }
    }

    /// <summary>
    /// 在已加载的程序集里找补丁目标类型。
    /// <para>
    /// <b>不用 <c>AccessTools.TypeByName</c> 作为唯一手段</b>：它只搜已加载的程序集，
    /// 而我们<b>排在 NationalSpire 前面初始化</b>，调用它必然返回 null（阶段 2 实测）。
    /// 这里改成自己逐个程序集找，并且不依赖 <c>AssemblyQualifiedName</c> 解析。
    /// </para>
    /// <para>生产路径会先用 <see cref="NationalSpireLocator"/> 把程序集显式加载进来。</para>
    /// </summary>
    public static Type? FindTargetType()
    {
        try
        {
            foreach (var assembly in AssemblyInventory.AllAssemblies())
            {
                Type? found;
                try
                {
                    found = assembly.GetType(TargetTypeName, throwOnError: false);
                }
                catch (Exception)
                {
                    continue;
                }
                if (found != null) return found;
            }
        }
        catch (Exception)
        {
            // 枚举程序集失败时放弃，由调用方报"找不到类型"。
        }

        return null;
    }

    /// <summary>卸载补丁。失败只记日志。</summary>
    public static void Unapply(Harmony harmony, IEconomyLog? log)
    {
        TryUnpatch(harmony, log);
        TradeMarkerInterceptor.Uninstall();
    }

    private static void TryUnpatch(Harmony harmony, IEconomyLog? log)
    {
        try
        {
            var target = FindTargetType();
            if (target != null)
            {
                var feed = AccessTools.Method(target, FeedMethodName, [typeof(string)]);
                if (feed != null) harmony.Unpatch(feed, AccessTools.Method(typeof(PrivateStreamPatch), nameof(FeedPrefix)));

                var finish = AccessTools.Method(target, FinishMethodName, Type.EmptyTypes);
                if (finish != null) harmony.Unpatch(finish, AccessTools.Method(typeof(PrivateStreamPatch), nameof(FinishPrefix)));
            }
        }
        catch (Exception e)
        {
            log?.Warn("patch", "卸载补丁失败（游戏即将退出时属正常）：" + e.Message, e);
        }

        Applied = false;
        PatchedMethods = [];
    }

    // ─────────────────────────────────────────────────────────────────
    // Harmony 前缀
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>Feed</c> 的前缀。参数名 <c>__instance</c> / <c>__0</c> 是 Harmony 的注入约定，
    /// <b>改名会让注入失效</b>（注入不到时 Harmony 会把它们当普通参数传 null/默认值）。
    /// </summary>
    public static bool FeedPrefix(object __instance, ref string __0)
    {
        if (!TradeMarkerInterceptor.RewriteFeed(__instance, __0, out string rewritten)) return false;
        __0 = rewritten;
        return true;
    }

    /// <summary><c>Finish</c> 的前缀。</summary>
    public static bool FinishPrefix(object __instance) => TradeMarkerInterceptor.RewriteFinish(__instance);
}
