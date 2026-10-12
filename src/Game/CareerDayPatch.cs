using System.Reflection;
using HarmonyLib;
using NationalSpire;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Game;

/// <summary>
/// 把原版的<b>日期推进</b>接到影子存档的推进上。
/// <para>
/// <b>挂载点为什么是 <c>CareerEngine.MoveToNextDay</c></b>：整份原版源码里
/// <c>d.Day++</c> 与 <c>d.Season++</c> <b>只出现在这一个方法里</b>（CareerEngine.cs:85），
/// 而它有四个调用点——<c>AdvanceOneDay</c>（走一天）、<c>AdvanceToDay</c>（跳天，循环调用它）、
/// 以及两处赛后自动推进。补在这一个点上，等于<b>所有</b>日期推进路径都被覆盖，
/// 而且每推进一天恰好触发一次后缀。
/// </para>
/// <para>
/// 它是 private static，所以用 <see cref="AccessTools"/> 按名 + 参数类型定位
/// （<c>AccessTools.Method</c> 默认就包含非公开成员）。
/// 原版哪天把它改名或内联了，这里会明确报"找不到"，而不是静默失效。
/// 后备候选是公开的 <c>AdvanceToDay</c>：它覆盖不了"走一天"，但好过完全没有推进。
/// </para>
/// </summary>
public static class CareerDayPatch
{
    /// <summary>原版生涯引擎的完整类型名。</summary>
    public const string TargetTypeName = "NationalSpire.CareerEngine";

    /// <summary>首选目标：唯一改动日期的那个方法。</summary>
    public const string PrimaryMethodName = "MoveToNextDay";

    /// <summary>后备目标：公开的跳天方法。</summary>
    public const string FallbackMethodName = "AdvanceToDay";

    public static bool Applied { get; private set; }
    public static string Detail { get; private set; } = "未装配";
    public static IReadOnlyList<string> PatchedMethods { get; private set; } = [];

    /// <summary>实际挂上的方法名，便于日志与自检。</summary>
    public static string PatchedTarget { get; private set; } = "";

    /// <summary>本进程内触发过多少次日期推进。</summary>
    public static int Ticks { get; private set; }

    /// <summary>最近一次推进后的游戏日。</summary>
    public static int LastDay { get; private set; }

    /// <summary>最近一次推进的账本结果。</summary>
    public static TickResult LastResult { get; private set; } = TickResult.Skipped;

    public static string? LastError { get; private set; }

    private static IEconomyLog? _log;

    /// <summary>
    /// 找目标方法：先 <c>MoveToNextDay(CareerData)</c>，再退到 <c>AdvanceToDay(CareerData,int)</c>。
    /// 找不到返回 null，由调用方报"原版改名了"。
    /// </summary>
    public static MethodBase? FindAdvanceMethod(Type target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var primary = AccessTools.Method(target, PrimaryMethodName, [typeof(CareerData)]);
        if (primary != null) return primary;

        return AccessTools.Method(target, FallbackMethodName, [typeof(CareerData), typeof(int)]);
    }

    /// <summary>当前挂载的是哪个方法名（诊断用）。</summary>
    public static string DescribeMethod(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        string parameters = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name));
        return $"{method.DeclaringType?.Name}.{method.Name}({parameters})";
    }

    /// <summary>装配后缀补丁。失败只记日志并返回 false，不抛异常。</summary>
    public static bool ApplyTo(Harmony harmony, Type target, MethodBase advance, IEconomyLog? log)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(advance);

        _log = log;

        var postfix = AccessTools.Method(typeof(CareerDayPatch), nameof(AdvancePostfix));
        if (postfix == null)
        {
            Detail = "找不到补丁方法 AdvancePostfix";
            LastError = Detail;
            return false;
        }

        // 幂等（按「目标方法 + 具体补丁方法」精确判定）：反复 Initialize、
        // 或"游戏在我们之后才加载 NationalSpire"触发的按副本补挂，都不该叠两遍后缀。
        if (PathProbe.HasPatch(advance, postfix))
        {
            PatchedTarget = DescribeMethod(advance);
            PatchedMethods = [$"{advance.Name} (postfix)"];
            Applied = true;
            Detail = PatchedTarget + " 后缀（已挂过，未重复挂载）";
            return true;
        }

        try
        {
            harmony.Patch(advance, postfix: new HarmonyMethod(postfix));

            PatchedTarget = DescribeMethod(advance);
            PatchedMethods = [$"{advance.Name} (postfix)"];
            Applied = true;
            Detail = PatchedTarget + " 后缀";
            log?.Info("tick", "已挂载日期推进接线：" + Detail);
            return true;
        }
        catch (Exception e)
        {
            Detail = e.GetType().Name + ": " + e.Message;
            LastError = Detail;
            log?.Error("tick", "挂载日期推进补丁失败：" + Detail, e);
            return false;
        }
    }

    public static void Unapply(Harmony? harmony, IEconomyLog? log)
    {
        try
        {
            if (harmony != null && FindTargetType() is { } target && FindAdvanceMethod(target) is { } advance)
                harmony.Unpatch(advance, AccessTools.Method(typeof(CareerDayPatch), nameof(AdvancePostfix)));
        }
        catch (Exception e)
        {
            log?.Warn("tick", "卸载日期推进补丁失败（游戏退出时属正常）：" + e.Message);
        }

        Applied = false;
        PatchedMethods = [];
        PatchedTarget = "";
    }

    /// <summary>在已加载的程序集里找 <c>CareerEngine</c>。</summary>
    public static Type? FindTargetType()
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
        return null;
    }

    // ─────────────────────────────────────────────────────────────────
    // Harmony 后缀
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>MoveToNextDay</c> 的后缀：此时 <c>d.Day</c>（跨赛季时还有 <c>d.Season</c>）已经是推进后的值。
    /// <para>
    /// 参数按位注入（<c>__0</c>），与阶段 2 的 <c>Feed</c> 前缀补丁同一套做法：
    /// 原版改参数名不会让注入静默失效。
    /// </para>
    /// </summary>
    public static void AdvancePostfix(CareerData? __0)
    {
        try
        {
            var runtime = EconomyModEntry.Runtime;
            if (runtime == null || __0 == null) return;

            var result = runtime.AdvanceTo(__0);
            if (result.Day == 0) return;      // 幂等跳过（水位线没动）

            Ticks++;
            LastDay = __0.Day;
            LastResult = result;
            LastError = null;

            // 顺手登记"这个世界有谁"的兜底轨迹（方案 A 的身份来源之一）。
            // 每次推进日期刷新一次即可：私信上下文里的人物键就是原版的人物 id，
            // 万一组装路径没经过 Compose，RequestPrivateAsync 还能靠它对上身份。
            RegisterIdentityTrack(__0);
        }
        catch (Exception e)
        {
            LastError = e.GetType().Name + ": " + e.Message;
            try
            {
                _log?.Error("tick", "日期推进接线抛异常，已忽略本次推进。", e);
            }
            catch (Exception)
            {
                // 日志也失败就只能放弃。
            }
        }
    }

    /// <summary>
    /// 登记兜底身份轨迹。真人与 <c>DeletedPeople</c> 一律排除——那是"绝不注入"的硬约束。
    /// </summary>
    private static void RegisterIdentityTrack(CareerData data)
    {
        try
        {
            var human = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (string id in data.HumanIds) if (id != null) human.Add(id);
            }
            catch (Exception)
            {
                // 读不到就当没有真人：下面还有账本核对兜底。
            }

            var people = new List<(string Id, string Handle)>();
            try
            {
                foreach (var person in data.People)
                {
                    string id = person?.Id ?? "";
                    if (id.Length == 0) continue;
                    if (human.Contains(id)) continue;                 // 真人绝不进轨迹
                    if (data.LocalHumanId == id) continue;
                    people.Add((id, person!.Handle ?? ""));
                }
            }
            catch (Exception)
            {
                // 枚举中途失败：已收集到的部分照常登记。
            }

            if (people.Count > 0)
                PrivateIdentitySlot.SetForWorld(data.WorldId, people, data.Day, data.Season, "CareerDayPatch", data);
        }
        catch (Exception)
        {
            // 轨迹登记失败绝不能影响日期推进本身。
        }
    }
}
