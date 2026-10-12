using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace NationalSpireEconomy.Game;

/// <summary>
/// <b>跨 ALC 的程序集盘点</b>。
/// <para>
/// <b>为什么必须跨 ALC（本轮实测缺口）</b>：<c>AppDomain.CurrentDomain.GetAssemblies()</c>
/// <b>只返回当前 <see cref="AssemblyLoadContext"/></b> 里的程序集。而游戏的模组加载器
/// （<c>ModManager</c>）用 <c>AssemblyLoadContext.LoadFromAssemblyPath</c> 装载模组 DLL 时，
/// 我们的补丁代码可能跑在默认 ALC，而 <c>NationalSpire</c> 在另一个 ALC 里——
/// 于是<b>我们"看得到自己挂载成功"，却"扫不到游戏真正执行的那份程序集"</b>，
/// 补丁自然永远不会命中。这与"同一类型存在多份副本"是同一类问题的两种表现。
/// </para>
/// <para>
/// 这里把进程内<b>所有</b> ALC 的程序集都摊开来，并单独回答"哪些 ALC 里有 NationalSpire"。
/// 所有查找/挂载路径都必须经过本类，不允许再直接用 <c>AppDomain.GetAssemblies()</c>。
/// </para>
/// </summary>
public static class AssemblyInventory
{
    /// <summary>
    /// 原版程序集的<b>精确简单名</b>。
    /// <para>
    /// <b>必须用精确名，不能用"名字含 NationalSpire"这种模糊匹配</b>（阶段 3 真机实测）：
    /// 模糊匹配会把<b>我们自己</b>（<c>NationalSpireEconomy</c>）也算成"原版副本"，
    /// 于是体检报告里的"原版程序集副本数：2"中有一份其实是我们自己，
    /// 真正的"补丁挂错副本"结论反而被这行噪音挡住（真机日志 §2.3 就是这个形态）。
    /// </para>
    /// </summary>
    public const string GameAssemblyName = "NationalSpire";

    /// <summary>名字里含这个片段的程序集（<b>含我们自己</b>；只用于盘点展示，绝不可当补丁目标）。</summary>
    public const string GameAssemblyMarker = "NationalSpire";

    /// <summary>
    /// 帧内所有 ALC 的所有程序集（<b>按对象身份去重，不按名字</b>）。
    /// <para>
    /// <b>实测事故（必须记住）</b>：本方法原先按 <c>FullName + Location</c> 去重。
    /// 而<b>同一个 DLL 被两个 ALC 各加载一次</b>时，两份 <see cref="Assembly"/> 对象的
    /// <c>FullName</c> 与 <c>Location</c> <b>完全相同</b> —— 于是其中一份被静默丢掉。
    /// 真机表现就是：我们"跨 ALC 枚举"了、也"挂载成功"了，
    /// 但挂的是 Default 里那一份，而游戏真正执行的是
    /// <c>IsolatedComponentLoadContext</c> 里那一份，补丁因此永远不命中。
    /// 所以这里必须按<b>对象引用</b>去重。
    /// </para>
    /// </summary>
    public static IReadOnlyList<Assembly> AllAssemblies()
    {
        var seen = new HashSet<Assembly>(ReferenceEqualityComparer.Instance);
        var result = new List<Assembly>();

        void Add(Assembly? assembly)
        {
            if (assembly == null) return;
            if (seen.Add(assembly)) result.Add(assembly);
        }

        // ① 当前 ALC（含默认 ALC 里绝大多数程序集）
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) Add(assembly);
        }
        catch (Exception)
        {
            // 枚举失败不影响后面的补齐。
        }

        // ② 其它 ALC：这是关键补充（同一个 DLL 可能在多个 ALC 里各有一份）。
        try
        {
            foreach (var context in AssemblyLoadContext.All)
            {
                IEnumerable<Assembly> assemblies;
                try
                {
                    assemblies = context.Assemblies;
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var assembly in assemblies) Add(assembly);
            }
        }
        catch (Exception)
        {
            // 某些运行时/环境不支持 AssemblyLoadContext.All，忽略。
        }

        return result;
    }

    /// <summary>
    /// 进程内所有<b>精确名字</b>为 <see cref="GameAssemblyName"/> 的程序集（跨 ALC、按对象身份）。
    /// <para>
    /// <b>这是唯一允许作为补丁目标的副本集合。</b>它必须是精确名匹配：
    /// 模糊匹配会把我们自己的 <c>NationalSpireEconomy</c> 也带进来，
    /// 于是"原版副本 N 份"永远 ≥ 1、而那份上的命中数当然是 0，
    /// 真正"挂错副本"的结论就被淹没了。
    /// </para>
    /// </summary>
    public static IReadOnlyList<Assembly> GameAssemblies()
    {
        var result = new List<Assembly>();
        foreach (var assembly in AllAssemblies())
            if (IsGameAssembly(assembly)) result.Add(assembly);
        return result;
    }

    /// <summary>
    /// 名字里含 <see cref="GameAssemblyMarker"/> 的程序集（<b>含我们自己</b>）。
    /// 只用于盘点展示，<b>不要</b>拿它当补丁目标。
    /// </summary>
    public static IReadOnlyList<Assembly> MarkerAssemblies()
    {
        var result = new List<Assembly>();
        foreach (var assembly in AllAssemblies())
        {
            if (SafeName(assembly).Contains(GameAssemblyMarker, StringComparison.OrdinalIgnoreCase))
                result.Add(assembly);
        }
        return result;
    }

    /// <summary>这个程序集是不是原版 <c>NationalSpire</c>（按<b>精确简单名</b>判断）。</summary>
    public static bool IsGameAssembly(Assembly? assembly) =>
        string.Equals(SafeName(assembly), GameAssemblyName, StringComparison.OrdinalIgnoreCase);

    /// <summary>程序集简单名（拿不到返回空串，绝不抛）。</summary>
    public static string SafeName(Assembly? assembly)
    {
        try
        {
            return assembly?.GetName().Name ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>某个程序集属于哪个 ALC（诊断用；拿不到返回 <c>?</c>）。</summary>
    public static string ContextOf(Assembly? assembly)
    {
        if (assembly == null) return "?";
        try
        {
            var context = AssemblyLoadContext.GetLoadContext(assembly);
            if (context == null) return "(无 ALC)";
            return context == AssemblyLoadContext.Default ? "Default" : context.Name ?? "(匿名 ALC)";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    /// <summary>程序集位置（拿不到就说明是动态/内存加载）。</summary>
    public static string SafeLocation(Assembly? assembly)
    {
        try
        {
            if (assembly == null) return "?";
            string location = assembly.Location;
            return string.IsNullOrEmpty(location) ? "(无位置：动态或内存加载)" : location;
        }
        catch (Exception)
        {
            return "(读取位置失败)";
        }
    }

    /// <summary>一行说清一个程序集：名字、版本、ALC、位置。</summary>
    public static string Describe(Assembly assembly)
    {
        try
        {
            string name = assembly.FullName ?? assembly.GetName().Name ?? "?";
            return $"{name}｜ALC {ContextOf(assembly)}｜{SafeLocation(assembly)}";
        }
        catch (Exception e)
        {
            return "读取程序集信息失败：" + e.GetType().Name;
        }
    }

    /// <summary>
    /// 检查"我们挂过补丁的目标方法，是否真的落在<b>正在执行的那份程序集</b>里"。
    /// <para>
    /// 这是对"多副本挂错"的直接体检：拿一个<b>确定在跑</b>的方法
    /// （例如游戏的 <c>Diagnostics.Record</c>）作为基准，
    /// 比较它与各补丁目标所属的 ALC / 程序集对象。
    /// </para>
    /// </summary>
    /// <param name="reference">基准方法：必须是游戏确实会执行到的方法。</param>
    /// <param name="targets">我们挂过补丁的目标方法。</param>
    /// <returns>可读结论（同时适合写进日志）。</returns>
    public static string AuditPatchLocation(MethodBase? reference, IEnumerable<MethodBase> targets)
    {
        if (reference == null) return "体检失败：没有基准方法";

        var referenceAssembly = AssemblyOf(reference);
        string referenceInfo = referenceAssembly == null
            ? "未知"
            : $"{reference.DeclaringType?.FullName}.{reference.Name}｜" +
              $"程序集 {referenceAssembly.GetName().Name}｜ALC {ContextOf(referenceAssembly)}｜" +
              SafeLocation(referenceAssembly);

        var report = new StringBuilder();
        report.Append("补丁落点体检｜基准（游戏真正执行的方法）：" + referenceInfo);

        int same = 0, different = 0;
        foreach (var target in targets)
        {
            var assembly = AssemblyOf(target);
            bool identical = ReferenceEquals(assembly, referenceAssembly);
            if (identical) same++; else different++;

            report.Append(Environment.NewLine);
            report.Append(identical ? "  ✅ 同一份：" : "  ❌ 不同份：");
            report.Append($"{target.DeclaringType?.FullName}.{target.Name}｜");
            report.Append(assembly == null
                ? "未知程序集"
                : $"ALC {ContextOf(assembly)}｜{SafeLocation(assembly)}");
        }

        report.Append(Environment.NewLine);
        report.Append(different == 0
            ? $"结论：全部 {same} 个目标都在同一份程序集上（正确）"
            : $"★结论：{different} 个目标挂在【别的副本】上、{same} 个正确 —— 这就是补丁不命中的原因");

        return report.ToString();
    }

    /// <summary>
    /// 全量盘点（用于启动期写进日志）：总数、ALC 数、原版程序集逐份清单。
    /// <para>
    /// 这里刻意<b>同时</b>报两个口径：精确名（真正的补丁目标）与"名字含 NationalSpire"
    /// （含我们自己）。上一轮的事故就是把后者当成了前者。
    /// </para>
    /// </summary>
    public static string Report()
    {
        var report = new StringBuilder();
        var all = AllAssemblies();
        var games = GameAssemblies();
        var marked = MarkerAssemblies();

        int contexts;
        try
        {
            contexts = AssemblyLoadContext.All.Count();
        }
        catch (Exception)
        {
            contexts = -1;
        }

        report.Append($"进程内程序集 {all.Count} 份｜ALC {contexts} 个" +
                      $"｜原版（精确名 {GameAssemblyName}）{games.Count} 份" +
                      $"｜名字含 {GameAssemblyMarker} 的 {marked.Count} 份（含我们自己）");

        foreach (var assembly in games) report.Append(Environment.NewLine + "  · " + Describe(assembly));

        foreach (var assembly in marked)
        {
            if (IsGameAssembly(assembly)) continue;
            report.Append(Environment.NewLine + "  ·（非目标）" + Describe(assembly));
        }

        return report.ToString();
    }

    /// <summary>
    /// 从"真正被调用的那个方法"反查它所属的程序集。
    /// <para>
    /// 这是最可靠的判据：谁在跑，就挂谁——不必假设游戏用的是哪一份。
    /// </para>
    /// </summary>
    public static Assembly? AssemblyOf(MethodBase? method)
    {
        try
        {
            return method?.Module?.Assembly;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
