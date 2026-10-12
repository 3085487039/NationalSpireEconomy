using System.Reflection;
using HarmonyLib;
using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Game;

/// <summary>
/// 给原版 <c>NationalSpire.LeadingReasoningStream</c> 打的 Harmony 前缀补丁：
/// <b>把 <c>[Trade: …]</c> 标记从"另一条并行的可见流"里也剥掉。</b>
/// <para>
/// <b>为什么还需要它（2026-10-12 真机截图 + <c>path-probe.log</c> 实测）</b>：玩家在私信里
/// <b>仍然看到</b>
/// <c>[Trade: 赞助, Amount: 2000, Direction: playerToNpc]</c> 与 <c>[Deal: accept]</c>，
/// 而正文完好。逐条排除后的结论是：
/// </para>
/// <list type="bullet">
/// <item><c>PrivateStreamParser.Feed</c> 的补丁**确实在跑**（探针判据：这一份就是挂了补丁的那一份）；</item>
/// <item><c>PrivateTurn.set_Reply</c> 的补丁**也在跑**，并且确实剥到了文本（日志有"剥离后正文"）；</item>
/// <item>但那一帧的<b>渲染源头不是上面两个</b> —— 探针显示真机上有<b>两条并行的流式出口</b>：
/// <code>LeadingReasoningStream.Feed("[")  ──►  PrivateStreamParser.Feed("[")
/// LeadingReasoningStream.Feed("De")  ──►  PrivateStreamParser.Feed("De")
/// …（实测 4369 次一一对应，同一批分片）</code>
/// 两条各 4369 次、<b>参数逐字相同</b>。我们只给右边那条打了补丁，
/// 于是左边那条自己累积出来的文本把标记带到了界面上。</item>
/// </list>
/// <para>
/// <b>为什么"顺手把 Feed 的入参剥掉"是安全的</b>：<see cref="PrivateStreamPatch"/> 用同一套做法
/// 已经在 <c>PrivateStreamParser</c> 上验收过；而且两条流是<b>同一批分片</b>，
/// 各自维护独立的解析状态，剥掉标记对两条都是想要的效果。
/// </para>
/// <para>
/// <b>它只改文本，不写任何状态</b>：不解析、不落单（落单只归 <c>PrivateStreamParser</c> 那条）、
/// 不动金额、不碰影子存档。因此它装不上也只是"玩家多看到一行机器语法"。
/// </para>
/// </summary>
public static class LeadingStreamPatch
{
    /// <summary>并行可见流的类型名。与 <c>PrivateStreamParser</c> 一样可能是嵌套类型，
    /// 所以查找方式是"逐个程序集按完整名找"（阶段 2 的实测结论）。</summary>
    public const string TargetTypeName = "NationalSpire.LeadingReasoningStream";

    /// <summary>喂入分片的方法名（与另一条流同名）。</summary>
    public const string FeedMethodName = "Feed";

    /// <summary>流结束的方法名。</summary>
    public const string FinishMethodName = "Finish";

    /// <summary>补丁是否已经装上。</summary>
    public static bool Applied { get; private set; }

    /// <summary>装配细节（成功或失败原因）。</summary>
    public static string Detail { get; private set; } = "未装配";

    /// <summary>已打上的方法名。</summary>
    public static IReadOnlyList<string> PatchedMethods { get; private set; } = [];

    /// <summary>
    /// 在已加载的程序集里找目标类型。
    /// <b>不用 <c>AccessTools.TypeByName</c></b>：它只搜已加载的程序集，
    /// 而我们排在 NationalSpire 前面初始化（阶段 2 实测返回 null）。
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
                    // 动态程序集/加载异常：跳过这一个。
                }
            }
        }
        catch (Exception)
        {
            // 枚举程序集失败时放弃，由调用方报"找不到类型"。
        }

        return null;
    }

    /// <summary>装配补丁。<b>失败只记日志、返回 false</b>，绝不抛回游戏。</summary>
    public static bool ApplyTo(Harmony harmony, Type target, IEconomyLog? log)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        ArgumentNullException.ThrowIfNull(target);

        var patched = new List<string>();
        try
        {
            var feed = AccessTools.Method(target, FeedMethodName, [typeof(string)]);
            if (feed == null)
            {
                Detail = $"找不到 {target.FullName}.{FeedMethodName}(string)";
                log?.Error("patch", Detail);
                return false;
            }

            var finish = AccessTools.Method(target, FinishMethodName, Type.EmptyTypes);

            var feedPrefix = AccessTools.Method(typeof(LeadingStreamPatch), nameof(FeedPrefix));
            var finishPrefix = AccessTools.Method(typeof(LeadingStreamPatch), nameof(FinishPrefix));

            // 先卸后挂：Harmony 不挡重复挂载，而补丁记录又可能比 detour 活得久
            //（与 PrivateStreamPatch 同一条实测结论）。
            if (feedPrefix != null)
            {
                try { harmony.Unpatch(feed, feedPrefix); } catch (Exception) { }
                harmony.Patch(feed, prefix: new HarmonyMethod(feedPrefix));
                patched.Add(FeedMethodName);
                log?.Info("patch", $"已拦截 {target.FullName}.{FeedMethodName}(string)（并行可见流：同样剥标记）");
            }

            if (finish != null && finishPrefix != null)
            {
                try { harmony.Unpatch(finish, finishPrefix); } catch (Exception) { }
                harmony.Patch(finish, prefix: new HarmonyMethod(finishPrefix));
                patched.Add(FinishMethodName);
            }

            PatchedMethods = patched;
            Applied = patched.Count > 0;
            Detail = string.Join(" + ", patched);
            return Applied;
        }
        catch (Exception e)
        {
            Detail = e.GetType().Name + ": " + e.Message;
            log?.Error("patch", "装配并行可见流补丁失败：" + Detail, e);
            return false;
        }
    }

    /// <summary>
    /// <c>Feed</c> 的前缀：把标记从入参里剥掉，再交给原方法。
    /// <para>
    /// <b>为什么不自己解析意图</b>：落单只归 <c>PrivateStreamParser</c> 那条路，
    /// 这里重复解析只会让同一个标记被处理两遍。本方法<b>纯净化文本</b>。
    /// </para>
    /// </summary>
    public static bool FeedPrefix(ref string __0)
    {
        if (!TradeMarkerInterceptor.StripForDisplay(__0, out string cleaned)) return true;
        __0 = cleaned;
        return true;
    }

    /// <summary>
    /// <c>Finish</c> 的前缀：只保证原方法被调用。
    /// <para>
    /// 这里<b>刻意不</b>把我们待定的标记也丢掉 —— 那条职责归
    /// <c>PrivateStreamParser.Finish</c> 的补丁（它有自己的待定缓冲）。
    /// 两条流各自维护状态，互不越界。
    /// </para>
    /// </summary>
    public static bool FinishPrefix(object __instance) => true;

    /// <summary>卸载补丁（幂等）。失败只记日志。</summary>
    public static void Unapply(Harmony harmony, IEconomyLog? log)
    {
        try
        {
            var target = FindTargetType();
            if (target != null)
            {
                var feed = AccessTools.Method(target, FeedMethodName, [typeof(string)]);
                if (feed != null)
                    harmony.Unpatch(feed, AccessTools.Method(typeof(LeadingStreamPatch), nameof(FeedPrefix)));

                var finish = AccessTools.Method(target, FinishMethodName, Type.EmptyTypes);
                if (finish != null)
                    harmony.Unpatch(finish, AccessTools.Method(typeof(LeadingStreamPatch), nameof(FinishPrefix)));
            }
        }
        catch (Exception e)
        {
            log?.Warn("patch", "卸载并行可见流补丁失败（游戏退出时属正常）：" + e.Message, e);
        }

        Applied = false;
        PatchedMethods = [];
        Detail = "未装配";
    }

    /// <summary>一行摘要（供启动诊断汇总）。</summary>
    public static string Describe() =>
        Applied
            ? $"[Economy-Patch] 并行可见流补丁：成功｜{Detail}"
            : $"[Economy-Patch] 并行可见流补丁：失败｜{Detail}";
}
