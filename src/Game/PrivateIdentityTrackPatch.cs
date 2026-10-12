using System.Reflection;
using HarmonyLib;
using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Game;

/// <summary>
/// <b>方案 A 的身份来源（主）</b>：<c>AiService.ProcessPrivateAsync</c>。
/// <para>
/// <b>为什么是它而不是猜 <c>CareerEngine.Current</c></b>：用元数据逐条扫过整份
/// <c>NationalSpire.dll</c> 之后确认，<c>CareerEngine</c> 里<b>只有 <c>Person(CareerData, string)</c>，
/// 根本没有"当前生涯"的静态入口</b>。而
/// <c>ProcessPrivateAsync(Func&lt;CareerData&gt;, Action&lt;CareerData&gt;, string, Action&lt;string,string&gt;)</c>
/// 同时握着<b>真实 <c>CareerData</c> 与人物 id</b>，是私信链路上唯一两者兼得的地方。
/// </para>
/// <para>
/// <b>怎么挂</b>：<c>ProcessPrivateAsync</c> 是 <c>async</c>，方法体在编译器生成的
/// 状态机（<c>&lt;ProcessPrivateAsync&gt;d__N</c>）的 <c>MoveNext</c> 里。所以这里给
/// <b>状态机类型</b>挂 <c>MoveNext</c> 的后缀，再按<b>字段类型</b>（<c>Func&lt;CareerData&gt;</c>、
/// <c>Action&lt;CareerData&gt;</c>、以及第一个 <c>string</c>）取出三样东西——
/// 不写死编译器生成的字段名，所以原版重编译后也不会静默失效。
/// </para>
/// <para>
/// <b>它只做一件事</b>：把 (世界, 人物, 天数, 赛季, CareerData) 记进
/// <see cref="PrivateIdentitySlot"/>，供 <see cref="PrivateRequestInjectionPatch"/> 使用。
/// <b>不改任何游戏数据</b>，也绝不抛异常。
/// </para>
/// </summary>
public static class PrivateIdentityTrackPatch
{
    /// <summary>目标方法名（async 状态机就是从它派生的）。</summary>
    public const string MethodName = "ProcessPrivateAsync";

    /// <summary>状态机类型名里的标记。</summary>
    public const string StateMachineMarker = "<" + MethodName + ">d__";

    /// <summary>状态机方法名。</summary>
    public const string MoveNextName = "MoveNext";

    /// <summary>补丁是否已经装上。</summary>
    public static bool Applied { get; private set; }

    /// <summary>装配细节。</summary>
    public static string Detail { get; private set; } = "未装配";

    /// <summary>已挂载的目标（签名 + token）。</summary>
    public static IReadOnlyList<string> PatchedMethods { get; private set; } = [];

    /// <summary>后缀被调用过多少次。</summary>
    public static int Hits { get; private set; }

    /// <summary>成功登记身份的次数。</summary>
    public static int Tracked { get; private set; }

    /// <summary>拿不到 <c>CareerData</c> 而跳过的次数。</summary>
    public static int SkippedNoData { get; private set; }

    /// <summary>最近一次错误的说明。</summary>
    public static string? LastError { get; private set; }

    private static IEconomyLog? _log;
    private static bool _firstCallReported;

    /// <summary>挂载：给所有找到的状态机 <c>MoveNext</c> 装后缀。</summary>
    public static bool ApplyTo(Harmony? harmony, IEconomyLog? log)
    {
        _log = log;
        var targets = FindStateMachines();
        return ApplyTo(harmony, targets, log);
    }

    /// <summary>挂载到给定的状态机类型集合（便于单测注入）。</summary>
    public static bool ApplyTo(Harmony? harmony, IEnumerable<Type> stateMachines, IEconomyLog? log)
    {
        ArgumentNullException.ThrowIfNull(stateMachines);

        _log = log;
        var patched = new List<string>();

        if (harmony == null)
        {
            Detail = "Harmony 实例未建立";
            return false;
        }

        var probe = AccessTools.Method(typeof(PrivateIdentityTrackPatch), nameof(MoveNextPostfix));
        if (probe == null)
        {
            Detail = "找不到补丁方法 MoveNextPostfix";
            return false;
        }

        foreach (var type in stateMachines.Distinct())
        {
            var moveNext = type.GetMethod(MoveNextName, BindingFlags.Instance | BindingFlags.Public |
                                                       BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (moveNext == null) continue;

            try
            {
                // 幂等必须精确到"本补丁方法"：PathProbe 的观测探针挂在同一批方法上，
                // 按"有我们任意补丁就跳过"会把身份来源整段挡掉（真机实测踩过同款 bug）。
                if (PathProbe.HasPatch(moveNext, probe))
                {
                    patched.Add($"{type.FullName}.{MoveNextName}()｜(已挂过，跳过)");
                    continue;
                }

                harmony.Patch(moveNext, postfix: new HarmonyMethod(probe));
                string line = $"{type.FullName}.{MoveNextName}()｜token=0x{TokenOf(moveNext)}" +
                              $"｜程序集 {moveNext.Module.Assembly.GetName().Name}";
                patched.Add(line);
                log?.Info("inject", $"[Economy-Inject-A] 已挂载身份来源：{line}");
            }
            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                log?.Error("inject", $"[Economy-Inject-A] 身份来源挂载失败：{type.FullName}", e);
            }
        }

        PatchedMethods = patched;
        Applied = patched.Count > 0;
        Detail = Applied
            ? $"已挂载 {patched.Count} 份｜" + string.Join(" ／ ", patched)
            : $"未找到 {StateMachineMarker}* 状态机";
        return Applied;
    }

    /// <summary>遍历全部程序集，找 <c>ProcessPrivateAsync</c> 的异步状态机。</summary>
    public static IReadOnlyList<Type> FindStateMachines()
    {
        var found = new List<Type>();
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
                string name;
                try
                {
                    name = type.Name ?? "";
                }
                catch (Exception)
                {
                    continue;
                }
                if (name.StartsWith(StateMachineMarker, StringComparison.Ordinal)) found.Add(type);
            }
        }
        return found;
    }

    /// <summary>
    /// 状态机 <c>MoveNext</c> 的后缀：按字段类型取出 <c>CareerData</c> 与人物 id 并登记身份。
    /// </summary>
    public static void MoveNextPostfix(object? __instance)
    {
        try
        {
            Hits++;
            var state = ReadState(__instance);
            if (state == null) return;

            var career = state.Data!;
            string personId = state.PersonId;

            if (personId.Length > 0)
            {
                PrivateIdentitySlot.Set(career.WorldId, personId, personId, career.Day, career.Season,
                    "ProcessPrivateAsync", career);
                Tracked++;
                ReportFirstCall($"已登记身份｜世界 <{career.WorldId}>｜人物 <{personId}>" +
                                $"｜第 {career.Day} 天 / 第 {career.Season} 赛季");
            }
            else
            {
                // 没拿到人物 id 就不覆盖已有身份（Context 后缀可能已经写好了）。
                PrivateIdentitySlot.SetForWorld(career.WorldId, AllPeople(career), career.Day, career.Season,
                    "ProcessPrivateAsync·轨迹", career);
                Tracked++;
                ReportFirstCall($"已登记兜底轨迹（本次没取到人物 id）｜世界 <{career.WorldId}>" +
                                $"｜第 {career.Day} 天 / 第 {career.Season} 赛季");
            }
        }
        catch (Exception e)
        {
            LastError = e.GetType().Name + ": " + e.Message;
            try
            {
                _log?.Error("inject", "[Economy-Inject-A] 身份登记抛异常，已忽略。", e);
            }
            catch (Exception)
            {
                // 日志也失败就只能放弃。
            }
        }
    }

    /// <summary>从一个状态机实例里读出来的三样东西。</summary>
    /// <param name="Data">那份 <c>CareerData</c>。</param>
    /// <param name="PersonId">人物 id（可能为空）。</param>
    /// <param name="Strings">实例里所有非空字符串字段（诊断用）。</param>
    public sealed record TrackedState(NationalSpire.CareerData? Data, string PersonId, IReadOnlyList<string> Strings);

    /// <summary>
    /// 按<b>字段类型</b>取状态机里的数据：不写死编译器生成的字段名。
    /// <para>
    /// <c>CareerData</c> 优先取 <c>Func&lt;CareerData&gt;</c> 那个字段（它是私信链路真正用那份）；
    /// 人物 id 则先尝试<b>能在 People 里对上的那个字符串</b>，对不上才退回第一个字符串字段。
    /// </para>
    /// </summary>
    public static TrackedState? ReadState(object? instance)
    {
        if (instance == null) return null;

        var type = instance.GetType();
        object? direct = null;
        object? accessor = null;
        var strings = new List<string>();

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object? value;
            try
            {
                value = field.GetValue(instance);
            }
            catch (Exception)
            {
                continue;
            }

            switch (value)
            {
                case NationalSpire.CareerData data:
                    direct ??= data;
                    break;
                case string text when text.Length > 0:
                    strings.Add(text);
                    break;
                case not null when IsFuncOfCareerData(value.GetType()):
                    accessor ??= value;
                    break;
            }
        }

        var career = direct as NationalSpire.CareerData;
        if (career == null && accessor != null)
        {
            try
            {
                // 委托的目标实例/方法名是 <ProcessPrivateAsync>b__N；直接调它拿当前生涯数据。
                career = accessor.GetType().GetMethod("Invoke")?.Invoke(accessor, null) as NationalSpire.CareerData;
            }
            catch (Exception)
            {
                career = null;
            }
        }

        if (career == null && strings.Count == 0) return null;

        return new TrackedState(career, PickPersonId(career, strings), strings);
    }

    /// <summary>优先挑"能在 People / DeletedPeople 里对上"的那个字符串当人物 id。</summary>
    private static string PickPersonId(NationalSpire.CareerData? data, IReadOnlyList<string> strings)
    {
        if (data != null)
        {
            foreach (string candidate in strings)
            {
                try
                {
                    if (data.People.Any(p => p != null && p.Id == candidate)) return candidate;
                }
                catch (Exception)
                {
                    break;
                }
            }
        }
        return strings.Count > 0 ? strings[0] : "";
    }

    private static bool IsFuncOfCareerData(Type type)
    {
        if (!type.IsGenericType) return false;
        if (type.GetGenericTypeDefinition() != typeof(Func<>)) return false;
        var arguments = type.GetGenericArguments();
        return arguments.Length == 1 && arguments[0] == typeof(NationalSpire.CareerData);
    }

    /// <summary>排除真人与本地玩家的候选人物列表（兜底轨迹用）。</summary>
    private static IEnumerable<(string Id, string Handle)> AllPeople(NationalSpire.CareerData data)
    {
        var human = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (string id in data.HumanIds) if (id != null) human.Add(id);
        }
        catch (Exception)
        {
            // 读不到就当没有真人。
        }

        var list = new List<(string, string)>();
        try
        {
            foreach (var person in data.People)
            {
                string id = person?.Id ?? "";
                if (id.Length == 0) continue;
                if (human.Contains(id) || data.LocalHumanId == id) continue;
                list.Add((id, person!.Handle ?? ""));
            }
        }
        catch (Exception)
        {
            // 枚举中途失败：已收集到的部分照常返回。
        }
        return list;
    }

    private static string TokenOf(MethodBase method)
    {
        try
        {
            return method.MetadataToken.ToString("X8");
        }
        catch (Exception)
        {
            return "不可用";
        }
    }

    private static void ReportFirstCall(string reason)
    {
        if (_firstCallReported) return;
        _firstCallReported = true;
        try { _log?.Info("inject", $"[Economy-Inject-A] 身份来源首次实测：{reason}"); }
        catch (Exception) { }
    }

    /// <summary>清空状态（初始化时重置，保证单轨）。</summary>
    public static void MarkRetired(string reason)
    {
        Applied = false;
        PatchedMethods = [];
        Detail = "已按设计停用：" + reason;
    }
}
