// 需要 src/Game 与游戏程序集，零依赖构建里整体排除。
#if !NSE_NO_GAME
using System.Reflection;
using NationalSpire;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Game;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// <b>旁路取证探针的自证</b>——这一组存在的唯一理由：
/// 方案 A 的补丁在真机日志里显示"已挂载成功"，但私信请求真实发生时<b>一行注入日志都没有</b>。
/// 也就是说"挂上了"不等于"被执行了"，而我们原来的自证恰好证明不了后者。
/// <para>
/// 这里用<b>真实原版程序集</b>（不是模拟类型）验证：探针装上之后，
/// 真的去调一个原版方法，探针必须留下进入记录。如果这条失败，
/// 那么下次真机测试拿不到任何痕迹就不是"游戏没走那条路"，而是我们的取证手段本身没生效。
/// </para>
/// </summary>
public static class PathProbeTests
{
    public static Action[] Cases =>
    [
        ProbeRecordsRealMethodEntry,
        ProbeInstallsAcrossLoadedAssemblies,
        ProbeLogIsIndependentOfLogAdapter,
        ProbeCoversPrivateTurnTimeline,
        ProbeTagsPrivateSpecificMethods,
        AssemblyInventorySeesGameAssembliesAcrossAlc,
        ProbeInstallsOnlyAfterGameAssemblyIsLoaded,
        LiveSelfCheckProvesProbeRuns,
        InjectionPrefixIsNotBlockedByObserverProbe,
        DiagnosticsListenerCapturesStack,
        DiagnosticsListenerCountsEveryCategory,
        InventoryKeepsSameDllLoadedInTwoAlcs,

        // ★ 阶段 3 卡点（补丁挂在死副本上）的回归测试：
        //   必须按精确程序集名统计、必须装进我们自己所在的 ALC、
        //   后出现的副本必须也挂上、补挂必须幂等。
        GameAssembliesUsesExactName,
        LocatorLoadsGameAssemblyIntoOurOwnContext,
        ProbeInstallCoversEveryCopy,
        ReinforcePatchesCoversLateCopy
    ];

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    /// <summary>真的调一次原版方法，探针必须记下来。</summary>
    private static void ProbeRecordsRealMethodEntry()
    {
        var state = Initialize();
        TestRunner.True(state.Enabled, "前置：模组应启用");

        // 每一条用例都用自己的目录，互不依赖执行顺序。
        string log = EnableProbe();

        var harmony = new HarmonyLib.Harmony("nationalspire.economy.pathprobe.test");
        int patched = PathProbe.Install(harmony);
        TestRunner.True(patched > 0, "探针应至少挂上一个候选方法");
        PathProbe.DumpPatches(harmony, PathProbe.CandidateMethods);

        Console.WriteLine($"      · 本次挂载 {patched} 个候选方法");
        Console.WriteLine("      · 计数：" + PathProbe.Summary);

        // 找一个确定安全、没有副作用、且确实在私信链路里的原版方法：PrivateMessages.Conversation。
        var conversationType = FindType("NationalSpire.PrivateMessages");
        TestRunner.True(conversationType != null, "应能找到 NationalSpire.PrivateMessages");

        var method = conversationType!.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Conversation" && m.GetParameters().Length == 2);
        TestRunner.True(method != null, "应能找到 PrivateMessages.Conversation(CareerData, string)");

        var data = new CareerData { WorldId = "path-probe-world", Day = 1, Season = 1 };
        int hitsBefore = PathProbe.HitsOf("Conversation");

        try
        {
            method!.Invoke(null, [data, "nobody"]);
        }
        catch (Exception e)
        {
            // 方法本身抛异常没关系：探针的终结器也应该记下这一笔。
            Console.WriteLine("      · 原版方法抛出（预期内，参数是空世界）：" + e.GetType().Name);
        }

        TestRunner.True(PathProbe.HitsOf("Conversation") > hitsBefore,
            "探针必须记下原版 Conversation 的进入（否则取证手段本身失效）");
        TestRunner.True(PathProbe.Summary.Contains("Conversation"),
            "摘要里应出现 Conversation，实际：" + PathProbe.Summary);

        TestRunner.True(File.Exists(log), "旁路日志文件必须存在：" + log);
        string text = File.ReadAllText(log);
        TestRunner.Contains(text, "进入 NationalSpire.PrivateMessages.Conversation",
            "旁路日志里必须有进入记录（这就是真机取证要用的那行）");
        TestRunner.Contains(text, "Harmony 补丁表", "装配之后必须转储补丁表（用于识别别的模组的拦截）");
    }

    /// <summary>探针必须跨程序集工作：覆盖到 NationalSpire 里的候选方法，并转储补丁表。</summary>
    private static void ProbeInstallsAcrossLoadedAssemblies()
    {
        Initialize();

        string log = EnableProbe();

        var harmony = new HarmonyLib.Harmony("nationalspire.economy.pathprobe.test2");
        int patched = PathProbe.Install(harmony);
        TestRunner.True(patched > 0, "应挂上候选方法（每进程只装一次，所以这里可能是重复调用）");
        PathProbe.DumpPatches(harmony, PathProbe.CandidateMethods);

        TestRunner.True(File.Exists(log), "旁路日志应存在：" + log);
        string text = File.ReadAllText(log);

        // 私信链路上最关键的三个候选方法必须真的被找到并挂上。
        foreach (string name in new[] { "RequestPrivateAsync", "Compose", "Conversation" })
            TestRunner.Contains(text, name, $"旁路日志里应出现 {name}");

        // 补丁表必须写出「前缀/后缀/转移器/终结器」的分类（否则无法判断别人是否替换了方法体）。
        TestRunner.Contains(text, "转移器", "补丁表必须分类统计（转移器为 0 也是有效信息）");
    }

    /// <summary>旁路日志必须独立于我们的日志适配器：适配器关着它也要能写。</summary>
    private static void ProbeLogIsIndependentOfLogAdapter()
    {
        string path = EnableProbe();

        string marker = "独立通道自证-" + Guid.NewGuid().ToString("N")[..8];
        PathProbe.Write(marker);

        TestRunner.True(File.Exists(path), "旁路日志应被创建：" + path);
        TestRunner.Contains(File.ReadAllText(path), marker, "写进去的内容必须能读回来");
    }

    /// <summary>
    /// 私信轮的字段赋值时间线必须被覆盖：<c>PrivateTurn.set_Context/set_Status/…</c>。
    /// <para>
    /// 这是"请求发出去了、我们的补丁却没命中"时唯一能回答"流程走到哪一步"的东西，
    /// 所以它必须真的挂上、而且真调用一次就能留下痕迹。
    /// </para>
    /// </summary>
    private static void ProbeCoversPrivateTurnTimeline()
    {
        Initialize();
        EnableProbe();

        var harmony = new HarmonyLib.Harmony("nationalspire.economy.pathprobe.test3");
        PathProbe.Install(harmony);
        PathProbe.DumpPatches(harmony, PathProbe.CandidateMethods);

        // 装配只做一次，而且可能在别的用例里就已经做完；断言必须读【装配真正写入的那个文件】。
        string log = PathProbe.InstallLogPath.Length > 0 ? PathProbe.InstallLogPath : PathProbe.LogPath;
        TestRunner.True(File.Exists(log), "旁路日志应存在：" + log);
        string text = File.ReadAllText(log);

        TestRunner.Contains(text, "NationalSpire.PrivateTurn.set_Context",
            "PrivateTurn.set_Context 这个检查点必须被挂上（它是「流程走到 Context」的唯一证据）");

        // 真调用一次 set_Context，探针必须留下记录（带 ★私信专属 标记）。
        var turnType = FindType("NationalSpire.PrivateTurn");
        TestRunner.True(turnType != null, "应能找到 NationalSpire.PrivateTurn");

        var contextSetter = turnType!.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "set_Context");
        TestRunner.True(contextSetter != null, "应能找到 set_Context");

        object? turn = null;
        try
        {
            turn = Activator.CreateInstance(turnType);
            contextSetter!.Invoke(turn, ["时间线自证：模拟上下文"]);
        }
        catch (Exception e)
        {
            Console.WriteLine("      · 构造/赋值抛出（可接受，探针应已记录进入）：" + e.GetType().Name);
        }

        TestRunner.True(PathProbe.HitsOf("set_Context") > 0,
            "set_Context 的进入必须被记下，实际计数：" + PathProbe.Summary);
        TestRunner.Contains(File.ReadAllText(log), "★私信",
            "时间线/私信专属的命中必须带标记，便于真机日志里一眼筛出");
    }

    /// <summary>私信专属方法名清单必须覆盖真正有区分度的那几个（不能拿共用方法充数）。</summary>
    private static void ProbeTagsPrivateSpecificMethods()
    {
        foreach (string name in new[] { "ProcessPrivateAsync", "EnterPrivateQueue", "set_ReplyOrder" })
            TestRunner.True(PathProbe.PrivateSpecificMethods.Contains(name),
                $"私信专属清单里应有 {name}");

        // Conversation / Compose 是共用方法（界面预览、存档、群聊都会调），绝不能算私信证据。
        foreach (string name in new[] { "Conversation", "Compose", "Feed", "CanChat" })
            TestRunner.True(!PathProbe.PrivateSpecificMethods.Contains(name),
                $"{name} 是共用方法，不能标成私信专属（否则日志会误导判定）");

        TestRunner.True(PathProbe.ExactTargets.Any(t => t.TypeName == "NationalSpire.PrivateTurn"),
            "精确检查点必须指向 PrivateTurn");
    }

    /// <summary>
    /// 跨 ALC 盘点必须真的看得到原版程序集，并给出它属于哪个 ALC。
    /// <para>
    /// 这条防的是本轮真机那个坑：<c>AppDomain.GetAssemblies</c> 只枚举当前 ALC，
    /// 而游戏的模组加载器可能把 <c>NationalSpire</c> 装在别的 ALC 里——
    /// 那时我们会"挂载成功却永远不命中"，日志却只会显示"扫到 0 份"。
    /// </para>
    /// </summary>
    private static void AssemblyInventorySeesGameAssembliesAcrossAlc()
    {
        Initialize();

        var all = AssemblyInventory.AllAssemblies();
        var games = AssemblyInventory.GameAssemblies();

        TestRunner.True(all.Count > 0, "跨 ALC 盘点不该是空的");
        TestRunner.True(games.Count >= 1, "应至少看到一份 NationalSpire（测试进程里它已被加载）");

        string report = AssemblyInventory.Report();
        TestRunner.Contains(report, "ALC", "盘点报告必须写出 ALC（否则多副本/隔离无从判断）");
        TestRunner.Contains(report, "NationalSpire", "盘点报告必须列出原版程序集");
        Console.WriteLine("      · " + report.Replace(Environment.NewLine, " / "));

        // 每份原版程序集都必须能报出位置与 ALC，方便和 mod-debug.log 对照。
        foreach (var assembly in games)
        {
            string described = AssemblyInventory.Describe(assembly);
            TestRunner.Contains(described, "ALC", "每个程序集都要带 ALC 标注：" + described);
        }
    }

    /// <summary>
    /// 装配时机必须晚于"原版程序集被加载"：真机日志里 "命中类型 0 个" 就是装配早了一步。
    /// 这条钉住"Initialize 之后探针一定已经扫得到原版程序集"。
    /// </summary>
    private static void ProbeInstallsOnlyAfterGameAssemblyIsLoaded()
    {
        string log = EnableProbe();
        bool wasInstalled = PathProbe.Installed;
        var state = Initialize();
        TestRunner.True(state.Enabled, "前置：模组应启用");

        string text = File.ReadAllText(log);
        TestRunner.Contains(text, "程序集盘点（第 5 步", "启动早期应留一条盘点（那时可能是 0 份，这是允许的）");
        TestRunner.Contains(text, "程序集盘点（第 6 步之后", "原版加载之后必须再盘点一次");

        // 关键断言：装配那一刻（或本进程此前已装配时）扫到的原版程序集必须 ≥ 1。
        // 真机上一轮日志里这个数字是 0 —— 那正是"补丁永远不命中"的根因。
        var games = AssemblyInventory.GameAssemblies();
        TestRunner.True(games.Count >= 1,
            "Initialize 之后进程里必须至少有一份原版程序集，实际：" + games.Count);
        Console.WriteLine($"      · 原版程序集 {games.Count} 份：" +
                          string.Join(" ／ ", games.Select(AssemblyInventory.Describe)));

        if (wasInstalled)
        {
            // 本进程先前已装配过（测试里的正常情形）：装配日志在第一次装配时写的那个文件里。
            TestRunner.True(PathProbe.PatchedCount > 0, "先前装配应已挂上目标，实际：" + PathProbe.PatchedCount);
            Console.WriteLine("      · 本进程此前已装配，装配清单见：" + PathProbe.InstallLogPath);
            return;
        }

        TestRunner.Contains(text, "按名字装配：跨 ALC 扫到原版程序集",
            "装配时必须报出扫到几份原版程序集（真机上一轮这里是 0）");
        int scanned = ExtractScannedCount(text);
        TestRunner.True(scanned >= 1, "装配时扫到的原版程序集必须 ≥ 1，实际：" + scanned);
    }

    /// <summary>
    /// <b>活性自检本身必须能证明"探针会被执行"</b>——这正是本轮真机缺失的那块证据。
    /// 真机日志里 53 个探针"挂载成功"，私信却零痕迹；这个自检就是为了把
    /// "挂上了" 与 "会被执行" 分开判。
    /// </summary>
    private static void LiveSelfCheckProvesProbeRuns()
    {
        Initialize();
        string log = EnableProbe();

        var harmony = new HarmonyLib.Harmony("nationalspire.economy.pathprobe.live");
        PathProbe.Install(harmony);
        int before = PathProbe.HitsOf("Conversation");

        bool passed = PathProbe.VerifyLive();

        TestRunner.True(passed, "活性自检必须通过：装了探针就必须能观测到原版方法被调用");
        TestRunner.True(PathProbe.LiveSelfCheckPassed == true, "结论应被记下来供日志使用");
        TestRunner.True(PathProbe.HitsOf("Conversation") > before, "自检必须真的让命中数增加");

        // 自检过程必须完整写进旁路日志（真机上就靠这几行判读）。
        string text = File.ReadAllText(PathProbe.InstallLogPath.Length > 0 ? PathProbe.InstallLogPath : log);
        TestRunner.Contains(text, "活性自检", "旁路日志里必须有自检段");
        TestRunner.Contains(text, "活性自检结果：", "自检必须给出明确结论");
    }

    /// <summary>
    /// <b>本轮真机 bug 的回归测试</b>：观测探针（PathProbe）先挂在
    /// <c>RequestPrivateAsync</c> 上，之后方案 A 的注入前缀<b>仍然必须被挂上</b>。
    /// <para>
    /// 真机日志曾出现 <c>RequestPrivateAsync …（已挂过，跳过）</c>：幂等守卫被写成
    /// "该方法上有我们任意补丁就跳过"，于是注入前缀被整体挡掉、注入根本不存在。
    /// </para>
    /// </summary>
    private static void InjectionPrefixIsNotBlockedByObserverProbe()
    {
        Initialize();   // 这一步里 PathProbe 先挂、方案 A 注入随后挂

        var targets = PrivateRequestInjectionPatch.FindRequestPrivateMethods();
        TestRunner.True(targets.Count >= 1, "前置：应能找到 RequestPrivateAsync");

        var probe = typeof(PrivateRequestInjectionPatch)
            .GetMethod("RequestPrefix", BindingFlags.Public | BindingFlags.Static);
        TestRunner.True(probe != null, "前置：应有 RequestPrefix");

        foreach (var target in targets)
            TestRunner.True(PathProbe.HasPatch(target, probe!),
                "方案 A 的注入前缀必须真的挂在这个方法上（不能被观测探针挡住）：" +
                PrivateRequestInjectionPatch.SignatureOf(target));

        // 同时：观测探针也必须在（两边互不干扰）。
        var observer = typeof(PathProbe).GetMethod("EntryPrefix", BindingFlags.Public | BindingFlags.Static);
        TestRunner.True(observer != null && targets.Any(t => PathProbe.HasPatch(t, observer)),
            "观测探针也必须仍在（注入与观测必须共存）");
    }

    /// <summary>从"按名字装配：跨 ALC 扫到原版程序集 N 份"里取出 N。</summary>
    private static int ExtractScannedCount(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text, @"按名字装配：跨 ALC 扫到原版程序集 (\d+) 份");
        return match.Success && int.TryParse(match.Groups[1].Value, out int value) ? value : -1;
    }

    /// <summary>
    /// 抓栈监听必须真的挂上，并且真的能在原版写诊断时打印调用栈。
    /// <para>
    /// 这条是当前最重要的取证手段：真实私信不触发我们挂在私信方法上的探针，
    /// 但游戏自己会写 <c>private.request</c>——抓到那一刻的栈，就知道私信是谁发起的。
    /// </para>
    /// </summary>
    private static void DiagnosticsListenerCapturesStack()
    {
        Initialize();
        string log = EnableProbe();

        var harmony = new HarmonyLib.Harmony("nationalspire.economy.pathprobe.diag");
        bool installed = PathProbe.InstallDiagnosticsListener(harmony, Silent);
        TestRunner.True(installed, "抓栈监听必须挂上（Diagnostics.Record 应能被拦到）");

        // 真调一次 Diagnostics.Record，模拟原版写 private.request。
        var diagnostics = FindType("NationalSpire.Diagnostics");
        TestRunner.True(diagnostics != null, "应能找到 NationalSpire.Diagnostics");

        var record = diagnostics!.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Record" && m.GetParameters().Length == 2);
        TestRunner.True(record != null, "应能找到 Record(string, object)");

        int before = PathProbe.TotalHits;
        try
        {
            record!.Invoke(null, ["private.request", "{\"context\":\"private/selfcheck/unit/turn\"}"]);
        }
        catch (Exception e)
        {
            Console.WriteLine("      · Record 调用抛出（可接受，监听应已记录）：" + e.GetType().Name);
        }

        string path = PathProbe.LogPath;
        string text = File.ReadAllText(path);
        TestRunner.Contains(text, "触发式抓栈", "原版写 private.request 时必须打印调用栈");
        TestRunner.Contains(text, "栈[0]", "栈里必须有帧（否则定位不到发起者）");
        TestRunner.True(PathProbe.TotalHits >= before, "计数应被更新");
    }

    /// <summary>
    /// 分类计数必须<b>记住每一个分类</b>（不只是 private.request）。
    /// <para>
    /// 这是本轮真机矛盾的关键判据：如果真机日志里"诊断分类统计"只有我们自己写的那几类、
    /// 没有任何游戏自己的分类（例如 <c>mod.loaded</c>、<c>ai.credential.read</c>），
    /// 那就说明游戏根本没走这个 <c>Record</c>，写文件的是另一条通道——必须换取证位置。
    /// </para>
    /// </summary>
    private static void DiagnosticsListenerCountsEveryCategory()
    {
        Initialize();
        string log = EnableProbe();

        var harmony = new HarmonyLib.Harmony("nationalspire.economy.pathprobe.diag2");
        PathProbe.InstallDiagnosticsListener(harmony, Silent);
        PathProbe.InstallFileSinkListeners(harmony, Silent);

        var diagnostics = FindType("NationalSpire.Diagnostics");
        TestRunner.True(diagnostics != null, "应能找到 NationalSpire.Diagnostics");
        var record = diagnostics!.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Record" && m.GetParameters().Length == 2);
        TestRunner.True(record != null, "应能找到 Record(string, object)");

        foreach (string category in new[] { "unit.cat.a", "unit.cat.b", "unit.cat.a" })
        {
            try
            {
                record!.Invoke(null, [category, "{}"]);
            }
            catch (Exception)
            {
                // 原版可能对类别有额外要求；探针应已计数。
            }
        }

        string summary = PathProbe.CategorySummary;
        Console.WriteLine("      · 分类统计：" + summary);
        TestRunner.Contains(summary, "unit.cat.a", "分类计数必须记住每个分类，实际：" + summary);
        TestRunner.Contains(summary, "unit.cat.b", "分类计数必须记住每个分类，实际：" + summary);
        TestRunner.Contains(summary, "unit.cat.a=2", "同一分类必须累加，实际：" + summary);

        PathProbe.DumpCategoryCounts("测试");
        TestRunner.Contains(File.ReadAllText(log), "诊断分类统计（测试）",
            "必须能把分类统计写进旁路日志");

        // 取证监听必须真的挂上（上一版因参数名写错而挂载失败，这条钉住它）。
        // 注意：装配每进程只做一次，所以后续用例看到的是「已挂过（跳过）」——两者都算成功。
        string[] probeLines = File.ReadAllLines(log);
        string text = string.Join("\n", probeLines);
        bool mounted = text.Contains("取证监听：已挂") || text.Contains("已挂过（跳过）");
        string sinkLines = string.Join(" / ",
            probeLines.Where(l => l.Contains("取证监听") || l.Contains("文件写入监听")).Take(4));
        TestRunner.True(mounted,
            "取证监听必须挂载成功（参数名必须与真实形参一致），实际：" + sinkLines);
        TestRunner.True(!text.Contains("挂载失败"),
            "不该再出现取证监听挂载失败（上一版的 bug）");
        TestRunner.Contains(text, "Diagnostics 方法：", "必须列出 Diagnostics 的方法清单（供下一轮判断写入途径）");
        TestRunner.Contains(text, "诊断配置层", "必须尝试挂 Diagnostics.Configure（诊断文件初始化入口）");
    }

    /// <summary>
    /// <b>多副本回归测试（本轮真机事故）</b>：同一条 DLL 路径被两个 <c>AssemblyLoadContext</c>
    /// 各加载一次时，两份 <c>Assembly</c> 对象的全名与位置<b>完全相同</b>。
    /// 盘点<b>必须把它们都保留</b>——原来的实现按 <c>FullName+Location</c> 去重，
    /// 直接把其中一份丢掉，于是我们只挂到一份、游戏执行另一份，
    /// 症状就是"挂载成功、token 都对，却永远不命中"。
    /// </summary>
    private static void InventoryKeepsSameDllLoadedInTwoAlcs()
    {
        Initialize();

        // 找一条已存在的 DLL 来复现"同一路径被两个 ALC 各加载一次"。
        string? probe = FindReferenceDll();
        if (probe == null)
        {
            TestRunner.Skip("找不到可用于多副本验证的 DLL，无法验证「同 DLL 两份都要保留」");
            return;
        }

        // 只保留引用身份：把两个 ALC 里加载到的对象与盘点结果逐一比对。
        var first = new System.Runtime.Loader.AssemblyLoadContext("nse-alc-A-" + Guid.NewGuid().ToString("N"));
        var second = new System.Runtime.Loader.AssemblyLoadContext("nse-alc-B-" + Guid.NewGuid().ToString("N"));

        Assembly? assemblyA = null, assemblyB = null;
        try
        {
            assemblyA = first.LoadFromAssemblyPath(probe);
            assemblyB = second.LoadFromAssemblyPath(probe);
        }
        catch (Exception e)
        {
            TestRunner.Skip("无法在自定义 ALC 里加载该 DLL（" + e.GetType().Name + "），无法验证多副本");
            return;
        }

        TestRunner.True(!ReferenceEquals(assemblyA, assemblyB), "前置：两次加载必须是两个不同的对象");
        TestRunner.Equal(assemblyA.FullName, assemblyB.FullName, "前置：两者全名相同（正是原 bug 的诱因）");
        TestRunner.Equal(AssemblyInventory.SafeLocation(assemblyA), AssemblyInventory.SafeLocation(assemblyB),
            "前置：两者位置相同（正是原 bug 的诱因）");

        var all = AssemblyInventory.AllAssemblies();
        TestRunner.True(all.Any(a => ReferenceEquals(a, assemblyA)),
            "盘点必须包含 ALC-A 里的那一份（按对象身份去重）");
        TestRunner.True(all.Any(a => ReferenceEquals(a, assemblyB)),
            "盘点必须包含 ALC-B 里的那一份（原 bug 就是把它丢掉了）");

        int sameName = all.Count(a => SafeName(a) == SafeName(assemblyA));
        Console.WriteLine($"      · 同名（{SafeName(assemblyA)}）副本在盘点里出现 {sameName} 次");
        TestRunner.True(sameName >= 2, "同名多副本必须都被保留，实际：" + sameName);
    }

    // ─────────────────────────────────────────────────────────────────
    // ★ 阶段 3 卡点回归：补丁必须挂在"游戏真正执行的那一份"上
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 原版副本必须按<b>精确程序集名</b>统计。
    /// <para>
    /// 原来的模糊匹配（名字含 <c>NationalSpire</c>）会把<b>我们自己</b>
    /// （<c>NationalSpireEconomy</c>）也算成"原版副本"，于是体检报告里那份
    /// "命中 0 个目标"的副本其实是我们自己，真正的"挂错副本"结论被这行噪音挡住。
    /// </para>
    /// </summary>
    private static void GameAssembliesUsesExactName()
    {
        Initialize();

        var games = AssemblyInventory.GameAssemblies();
        TestRunner.True(games.Count >= 1, "测试进程里应至少有一份 NationalSpire");
        foreach (var assembly in games)
            TestRunner.Equal("NationalSpire", AssemblyInventory.SafeName(assembly),
                "原版副本集合只能包含精确名为 NationalSpire 的程序集");

        var marked = AssemblyInventory.MarkerAssemblies();
        TestRunner.True(marked.Any(a => AssemblyInventory.SafeName(a) == "NationalSpireEconomy"),
            "模糊集合必须包含我们自己（这正是原 bug 的诱因，留着它是为了让报告能自证）");
        TestRunner.True(marked.Count >= games.Count, "模糊集合不应小于精确集合");

        TestRunner.True(!games.Any(a => ReferenceEquals(a, typeof(PathProbe).Assembly)),
            "我们自己绝不能出现在补丁目标副本集合里");

        string report = AssemblyInventory.Report();
        TestRunner.Contains(report, $"精确名 NationalSpire）{games.Count} 份",
            "盘点报告必须分开报「精确名」与「含名字片段」两个口径：" + report);
    }

    /// <summary>
    /// <b>本轮修复的核心回归</b>：原版必须被装进<b>我们自己所在的 ALC</b>。
    /// <para>
    /// 旧实现用 <c>Assembly.LoadFrom</c>，它总是装进 Default——于是造出一份
    /// "游戏永远不会执行"的假副本，补丁 16 个目标全挂在那上面，真机表现就是
    /// "挂载成功、token 都对、真实私信零命中"。装进自己的 ALC 之后，
    /// 游戏加载器稍后来装同一个模组 DLL 时会拿到<b>我们已经打好补丁的那一份</b>。
    /// </para>
    /// </summary>
    private static void LocatorLoadsGameAssemblyIntoOurOwnContext()
    {
        Initialize();

        var loaded = NationalSpireLocator.EnsureLoaded(null, null);
        TestRunner.True(loaded != null, "应能找到/加载原版程序集（前置）");

        var itsContext = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(loaded!);
        TestRunner.True(ReferenceEquals(NationalSpireLocator.OurContext, itsContext),
            "原版必须与我们同处一个 ALC（那才是游戏执行模组的 ALC）。实际：" +
            AssemblyInventory.ContextOf(loaded));

        TestRunner.Equal(AssemblyInventory.ContextOf(loaded), NationalSpireLocator.LoadedContext,
            "加载结论里的 ALC 必须与真实 ALC 一致（日志判据就靠这一行）");

        Console.WriteLine($"      · 原版 ALC = {NationalSpireLocator.LoadedContext}｜{NationalSpireLocator.LoadedFrom}");
    }

    /// <summary>
    /// <b>全副本挂载</b>：后出现的第二份原版副本必须也挂上探针。
    /// <para>
    /// 这条直接钉住"只挂上了 1 份"那个事故（真机体检：Default 那份命中 16 个目标、
    /// 而游戏执行的副本 0 个）。它同时验证"可重复装配不会叠层"。
    /// </para>
    /// </summary>
    private static void ProbeInstallCoversEveryCopy()
    {
        Initialize();

        Assembly? late = LoadSecondGameCopy("nse-late-copy-");
        if (late == null)
        {
            TestRunner.Skip("无法构造第二份副本：" + _loadCopyFailure);
            return;
        }

        var setContext = FindSetContext(late);
        TestRunner.True(setContext != null, "新副本里应有 PrivateTurn.set_Context");

        var entryPrefix = typeof(PathProbe).GetMethod("EntryPrefix", BindingFlags.Public | BindingFlags.Static);
        TestRunner.True(entryPrefix != null, "前置：应有 EntryPrefix");

        TestRunner.False(PathProbe.HasPatch(setContext!, entryPrefix!),
            "前置：补挂之前，新副本上不该有我们的探针");

        var harmony = new HarmonyLib.Harmony("nationalspire.economy.pathprobe.copies");
        int total = PathProbe.Install(harmony);
        TestRunner.True(total >= 1, "返回值应是「当前带探针的方法数」，实际：" + total);

        TestRunner.True(PathProbe.HasPatch(setContext!, entryPrefix!),
            "★新出现的副本上必须也挂上探针——只挂第一份就是本轮事故");

        // 再装一次：不能叠成两层（去掉"每进程只装一次"的闩锁之后，这条是安全网）。
        PathProbe.Install(harmony);
        var info = HarmonyLib.Harmony.GetPatchInfo(setContext!);
        int prefixCount = info?.Prefixes?.Count(p => ReferenceEquals(p.PatchMethod, entryPrefix)) ?? 0;
        TestRunner.Equal(1, prefixCount, "同一方法上的观测前缀必须恰好一份（可重复装配不能叠层）");

        // 落点体检必须得出"多份都已挂上"。
        string audit = PathProbe.AuditPatchLocation(["set_Context", "RequestPrivateAsync", "Feed", "Record"]);
        Console.WriteLine("      · " + audit.Replace(Environment.NewLine, " / "));
        TestRunner.True(PathProbe.LastCopyCount >= 2,
            "体检必须看到两份副本，实际：" + PathProbe.LastCopyCount);
        TestRunner.Equal(PathProbe.LastCopyCount, PathProbe.LastPatchedCopyCount,
            "★体检结论必须是「每一份副本都挂上了」");
        TestRunner.Contains(audit, "都已挂上", "体检文字必须明确给出结论：" + audit);
    }

    /// <summary>
    /// <b>落点警卫</b>：游戏在我们之后才加载原版时，补挂必须把方案 A 的注入前缀
    /// 补到新副本上。这是"游戏执行副本 = 我们挂补丁的副本"在机制上的第二道保险。
    /// </summary>
    private static void ReinforcePatchesCoversLateCopy()
    {
        Initialize();

        TestRunner.True(EconomyModEntry.ReinforcementArmed, "Initialize 之后落点警卫必须已装好");

        Assembly? late = LoadSecondGameCopy("nse-reinforce-");
        if (late == null)
        {
            TestRunner.Skip("无法构造第二份副本：" + _loadCopyFailure);
            return;
        }

        var request = FindRequestPrivate(late);
        TestRunner.True(request != null, "新副本里应有 AiService.RequestPrivateAsync");

        var injection = typeof(PrivateRequestInjectionPatch)
            .GetMethod("RequestPrefix", BindingFlags.Public | BindingFlags.Static);
        TestRunner.True(injection != null, "前置：应有 RequestPrefix");

        TestRunner.False(PathProbe.HasPatch(request!, injection!),
            "前置：补挂之前，新副本上不该有方案 A 的注入前缀");

        int runsBefore = EconomyModEntry.ReinforceRuns;
        EconomyModEntry.ReinforcePatches("单元测试：模拟「游戏在我们之后才加载原版」");

        TestRunner.True(EconomyModEntry.ReinforceRuns > runsBefore, "补挂必须真的跑了一轮");
        TestRunner.True(PathProbe.HasPatch(request!, injection!),
            "★补挂必须把方案 A 的注入前缀挂到新副本上，否则真机还是零命中");
        TestRunner.Contains(EconomyModEntry.LastReinforceReport, "副本", "补挂结论必须可读");
        Console.WriteLine("      · " + EconomyModEntry.LastReinforceReport);

        // 补挂之后，体检必须仍然得出"每份都挂上了"。
        TestRunner.Equal(PathProbe.LastCopyCount, PathProbe.LastPatchedCopyCount,
            "补挂之后必须每一份副本都覆盖到");
    }

    /// <summary>再加载一份原版副本（独立 ALC，模拟"游戏稍后才加载的那一份"）。</summary>
    /// <summary>上一次 <see cref="LoadSecondGameCopy"/> 为什么没拿到副本（供 Skip 的原因用）。</summary>
    private static string _loadCopyFailure = "未尝试";

    private static Assembly? LoadSecondGameCopy(string namePrefix)
    {
        string? dll = NseProbe.ChosenNationalSpirePath;
        if (dll == null || !File.Exists(dll))
        {
            _loadCopyFailure = "找不到 NationalSpire.dll（NseProbe 没解析到路径）";
            Console.WriteLine("      · 找不到 NationalSpire.dll 的路径");
            return null;
        }

        try
        {
            var alc = new System.Runtime.Loader.AssemblyLoadContext(namePrefix + Guid.NewGuid().ToString("N"));
            var late = alc.LoadFromAssemblyPath(dll);
            TestRunner.False(ReferenceEquals(late, NationalSpireLocator.EnsureLoaded(null, null)),
                "前置：必须是【另一份】副本对象");
            Console.WriteLine($"      · 新副本：{AssemblyInventory.Describe(late)}");
            return late;
        }
        catch (Exception e)
        {
            _loadCopyFailure = $"无法在自定义 ALC 里加载原版（{e.GetType().Name}: {e.Message}）";
            Console.WriteLine("      · " + _loadCopyFailure);
            return null;
        }
    }

    private static MethodInfo? FindSetContext(Assembly assembly) =>
        assembly.GetType("NationalSpire.PrivateTurn", throwOnError: false)?
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "set_Context");

    private static MethodInfo? FindRequestPrivate(Assembly assembly) =>
        assembly.GetType("NationalSpire.AiService", throwOnError: false)?
            .GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "RequestPrivateAsync");

    /// <summary>找一条已存在、且不依赖游戏程序集的 DLL 用于多副本验证。</summary>
    private static string? FindReferenceDll()
    {
        try
        {
            // 我们自己依赖的 Harmony 就在输出目录里，且不依赖游戏程序集。
            string local = Path.Combine(AppContext.BaseDirectory, "0Harmony.dll");
            if (File.Exists(local)) return local;

            string? runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (runtime == null) return null;
            string systemRuntime = Path.Combine(runtime, "System.Runtime.dll");
            return File.Exists(systemRuntime) ? systemRuntime : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string SafeName(Assembly assembly)
    {
        try { return assembly.GetName().Name ?? "?"; }
        catch (Exception) { return "?"; }
    }

    private static Type? FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, throwOnError: false);
                if (type != null) return type;
            }
            catch (Exception)
            {
            }
        }
        return null;
    }

    private static ModState Initialize()
    {
        // 测试里刻意关掉取证探针（它会采样每个候选方法，且每进程只装一次）。
        // 本套件需要它时自己显式打开（见 EnableProbe）。
        Environment.SetEnvironmentVariable("NSE_PATH_PROBE", "0");
        string configDir = CopyRealConfig(TempRoot());
        // ★ 交付默认是 probeEnabled=false（探针代价太大，不该让玩家付），
        //   而本套件测的就是探针本身，所以把拷贝出来的那份配置显式打开。
        //   不改仓库里的 config/ —— 那里必须保持交付默认值。
        SetProbeEnabled(configDir, enabled: true);
        return EconomyModEntry.Initialize(Silent, TempRoot(), configDir);
    }

    /// <summary>显式打开旁路日志（测试专用；交付默认是关的，由配置决定）。</summary>
    private static string EnableProbe()
    {
        Environment.SetEnvironmentVariable("NSE_PATH_PROBE", "1");
        string dir = Path.Combine(Path.GetTempPath(), "nse-pathprobe-" + Guid.NewGuid().ToString("N"));
        PathProbe.Enable(dir);
        return Path.Combine(dir, PathProbe.FileName);
    }

    /// <summary>
    /// 改<b>临时拷贝</b>里的 <c>injection.probeEnabled</c>。
    /// 用 JSON 感知的方式读写（本项目吃过 <c>Set-Content</c> 加 BOM / 改数字格式的亏）。
    /// </summary>
    private static void SetProbeEnabled(string configDir, bool enabled)
    {
        string path = Path.Combine(configDir, ConfigLoader.EconomyFileName);
        string json = File.ReadAllText(path);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["injection"]!["probeEnabled"] = enabled;
        File.WriteAllText(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            new System.Text.UTF8Encoding(false));
    }

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "nse-pathprobe-cfg-" + Guid.NewGuid().ToString("N"));

    private static string CopyRealConfig(string target)
    {
        Directory.CreateDirectory(target);
        string source = RealConfigDirectory();
        foreach (string name in new[]
                 {
                     ConfigLoader.EconomyFileName, ConfigLoader.PersonalityFileName, ConfigLoader.PromptFileName
                 })
            File.Copy(Path.Combine(source, name), Path.Combine(target, name), overwrite: true);
        return target;
    }

    private static string RealConfigDirectory()
    {
        // 收敛到共享实现（原先 19 个文件各抄一份）。
        return TestConfig.Directory();
    }
}
#endif
