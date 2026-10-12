using System.Reflection;
using System.Text;
using HarmonyLib;
using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Game;

/// <summary>
/// <b>旁路取证探针</b>：把"私信链路上到底哪些方法真的被执行了"直接写进<b>独立文件</b>，
/// 不经过 <c>EconomyLogAdapter</c>、不经过 Godot、不经过任何我们自己的抽象。
/// <para>
/// <b>为什么需要它（本轮实测事故）</b>：方案 A 的补丁在启动日志里显示"已挂载成功"、
/// <c>national-spire.log</c> 里也确认私信请求真的发生了，但<b>四个探针一个都没命中、
/// 注入一行都没写</b>。这说明"挂上了"与"被执行了"之间还有一层我们没测到的东西
/// （别的模组的 Harmony 前缀返回 false 把原版方法整体跳过、或转移器替换了方法体）。
/// 继续靠"再挂一个方法"是猜；这里改成"把每个候选方法的进入都记下来"。
/// </para>
/// <para>
/// 三个产出（都在同一个文件里，按时间顺序）：
/// <list type="number">
/// <item><b>装配清单</b>：每个候选方法名解析到几个方法、每个方法挂成功还是失败；</item>
/// <item><b>Harmony 补丁表</b>：目标方法上现存的<b>所有</b>补丁（含别的模组装的），
/// 以及每个前缀的 <c>__result</c> 类型（返回 false 的前缀会直接跳过原版方法体！）；</item>
/// <item><b>运行痕迹</b>：每次方法进入/退出/抛异常一行，带线程 id 与关键参数。</item>
/// </list>
/// </para>
/// </summary>
public static class PathProbe
{
    /// <summary>旁路日志文件名。</summary>
    public const string FileName = "path-probe.log";

    /// <summary>
    /// 候选方法名：私信链路上一切可能"真正的入口"或"稳定检查点"。
    /// <para>
    /// <b>覆盖面按调用图扫描的结果挑的</b>（不是拍的）：
    /// <list type="bullet">
    /// <item><b>私信专属</b>（命中即证明走了私信）：<c>ProcessPrivateAsync</c>、<c>EnterPrivateQueue</c>、
    /// <c>RequestPrivateAsync</c>（注：群聊与摘要也会调它）；</item>
    /// <item><b>状态机检查点</b>（原版在私信流程里逐步写的私有字段）：
    /// <c>set_Context</c> / <c>set_Status</c> / <c>set_ReplyOrder</c> / <c>set_Reply</c> / <c>set_Reasoning</c>
    /// ——它们能回答"流程走到了哪一步、卡在哪一步"；</item>
    /// <item><b>共用方法</b>（命中只能说明"有人用了这条路"，不能当私信证据）：
    /// <c>Conversation</c>（19 处调用，含界面预览与存档）、<c>Compose</c>（预览/群聊/仲裁/直播都调）、
    /// <c>Feed</c> / <c>Finish</c> / <c>Context</c> / <c>CanChat</c> / <c>Favour</c>；</item>
    /// <item><b>收尾</b>：<c>AiRequestQueue.Configure</c>、<c>PrivateMessages.Apply</c>、
    /// <c>SendAsync</c> / <c>SendTransportAsync</c>（真正发 HTTP 的那一步）。</item>
    /// </list>
    /// </para>
    /// </summary>
    public static readonly string[] CandidateMethods =
    [
        // 私信专属入口与队列
        "ProcessPrivateAsync",
        "EnterPrivateQueue",
        "RequestPrivateAsync",
        "SummaryRequest",
        // 私信状态机的稳定检查点：回答"走到哪一步 / 卡在哪一步"
        // 注意 set_Context 与 PrivateMessagePrompts.Context 同名，所以它走【类型+方法】精确目标，不在本表。
        "set_Status",
        "set_ReplyOrder",
        "set_Reply",
        "set_Reasoning",
        // 共用方法（命中不等于私信，仅作对照）
        "Context",
        "Compose",
        "Feed",
        "Finish",
        "Conversation",
        "CanChat",
        "Favour",
        // 收尾与出网
        "Configure",
        "Apply",
        "SendAsync",
        "SendTransportAsync"
    ];

    /// <summary>私信专属方法名：命中它们才能证明"走的是私信那条链"。</summary>
    public static readonly string[] PrivateSpecificMethods =
    [
        "ProcessPrivateAsync", "EnterPrivateQueue", "set_ReplyOrder"
    ];

    /// <summary>需要<b>按类型精确</b>指定的检查点：(类型全名, 方法名)。</summary>
    /// <param name="TypeName">声明类型全名。</param>
    /// <param name="MethodName">方法名。</param>
    public sealed record ExactTarget(string TypeName, string MethodName);

    /// <summary>
    /// 精确到类型的检查点：<c>PrivateTurn</c> 的各个私有字段赋值。
    /// <para>
    /// 原版在私信流程里会把 <c>Context</c>（刚组装好）、<c>Status</c>、
    /// <c>ReplyOrder</c>、<c>Reply</c>、<c>Reasoning</c> 逐个写进 <c>PrivateTurn</c>，
    /// 所以这些 setter 合起来就是一条"流程走到哪一步"的时间线——
    /// 这正是"请求发出去了但我们的补丁没命中"最需要的东西。
    /// </para>
    /// <para>
    /// 之所以不放进按名字匹配的候选表：<c>set_Context</c> 与
    /// <c>PrivateMessagePrompts.Context</c> 同名，必须用类型区分开。
    /// </para>
    /// </summary>
    public static readonly ExactTarget[] ExactTargets =
    [
        new("NationalSpire.PrivateTurn", "set_Context"),
        new("NationalSpire.PrivateTurn", "set_Status"),
        new("NationalSpire.PrivateTurn", "set_ReplyOrder"),
        new("NationalSpire.PrivateTurn", "set_Reply"),
        new("NationalSpire.PrivateTurn", "set_Reasoning")
    ];

    private static readonly object Gate = new();
    private static string _path = "";
    private static bool _enabled;
    private static bool _installed;

    /// <summary>已经挂上过探针的<b>副本</b>（ALC＋位置＋新挂数），可重复装配时累加。</summary>
    private static readonly HashSet<string> _patchedCopies = new(StringComparer.Ordinal);

    /// <summary>装配时实际写入的日志路径（装配只做一次，但 <c>Enable</c> 会被反复调用改名）。</summary>
    public static string InstallLogPath { get; private set; } = "";

    /// <summary>各方法进入次数（<c>方法名</c> → 次数）。</summary>
    private static readonly Dictionary<string, int> Counts = new(StringComparer.Ordinal);

    /// <summary>某个方法名被命中了几次。</summary>
    public static int HitsOf(string methodName)
    {
        lock (Gate)
        {
            return Counts.TryGetValue(methodName, out int count) ? count : 0;
        }
    }

    /// <summary>已挂载的目标数（当前带探针的方法数）。</summary>
    public static int PatchedCount { get; private set; }

    /// <summary>
    /// 已经覆盖到的副本清单（每行一个副本：程序集名／ALC／本次新挂几个）。
    /// <para>用来回答"发现 N 份、已挂 M 份"——这是本轮的核心判据。</para>
    /// </summary>
    public static IReadOnlyList<string> PatchedCopies
    {
        get
        {
            lock (Gate)
            {
                return _patchedCopies.ToArray();
            }
        }
    }

    /// <summary>最近一次装配时扫到的原版副本数。</summary>
    public static int CopiesScanned { get; private set; }

    /// <summary>本进程是否已经装过探针（<b>不代表</b>之后出现的副本也挂上了——装配可重复调用）。</summary>
    public static bool Installed
    {
        get
        {
            lock (Gate)
            {
                return _installed;
            }
        }
    }

    /// <summary>装配过程中的失败说明。</summary>
    public static string LastError { get; private set; } = "";

    /// <summary>本次会话的旁路日志路径。</summary>
    public static string LogPath => _path;

    /// <summary>一份可读的计数摘要。</summary>
    public static string Summary
    {
        get
        {
            lock (Gate)
            {
                return Counts.Count == 0
                    ? "(没有任何候选方法被命中)"
                    : string.Join("、", Counts.OrderByDescending(kv => kv.Value)
                        .Select(kv => $"{kv.Key}={kv.Value}"));
            }
        }
    }

    /// <summary>
    /// 启用旁路日志（由 <c>EconomyModEntry.Init</c> 尽早调用）。
    /// <para>
    /// 默认<b>开启</b>，但可以用环境变量 <c>NSE_PATH_PROBE=0</c> 关掉：
    /// 取证探针会给每个候选方法加三个补丁，正常游玩时不需要它。
    /// </para>
    /// </summary>
    public static void Enable(string directory)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("NSE_PATH_PROBE") is "0" or "false") return;
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, FileName);
            _enabled = true;
            Write($"===== 旁路取证开始｜{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}｜进程 {Environment.ProcessId} =====");
        }
        catch (Exception e)
        {
            _enabled = false;
            LastError = e.GetType().Name + ": " + e.Message;
        }
    }

    /// <summary>写一行旁路日志。<b>永不抛异常</b>（它是取证通道，坏掉就等于又瞎一次）。</summary>
    public static void Write(string line)
    {
        if (!_enabled || _path.Length == 0) return;
        try
        {
            lock (Gate)
            {
                File.AppendAllText(_path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId:00}] {line}" +
                    Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception)
        {
            // 取证通道自己坏掉也不能影响游戏。
        }
    }

    /// <summary>
    /// 监听 <c>Diagnostics.Configure(String)</c>：诊断文件的初始化入口。
    /// <para>
    /// 真机已逐层排除：<c>Diagnostics.Record</c>（只收到我们自己写的分类）、
    /// <c>File.AppendAllText</c>／<c>WriteAllText</c>（挂上了却一次没触发）。
    /// 而 <c>Diagnostics</c> 的方法清单里有 <c>Configure(String)</c>、
    /// <c>OpenTraceSnapshot(...) → FileStream</c> 与 <c>&lt;Export&gt;g__Write</c>，
    /// 说明它是自己开 <c>FileStream</c>／<c>StreamWriter</c> 写。
    /// 从"初始化入口"抓一次栈，就能看到完整链路与真实路径。
    /// </para>
    /// </summary>
    public static void DiagnosticsConfigurePrefix(string? __0)
    {
        try
        {
            string path = __0 ?? "";
            lock (Gate)
            {
                CategoryCounts["Diagnostics.Configure"] =
                    CategoryCounts.TryGetValue("Diagnostics.Configure", out int old) ? old + 1 : 1;
            }

            if (Interlocked.Increment(ref _privateStackDumps) > MaxPrivateStackDumps) return;

            Write("===== 触发式抓栈（诊断配置层）：Diagnostics.Configure 被调用 =====");
            Write("参数 path=<" + path + ">");
            WriteStack();
            Write("===== 抓栈结束 =====");
        }
        catch (Exception)
        {
            // 监听绝不抛回原版。
        }
    }

    /// <summary>
    /// 给 <c>File.AppendAllText</c> / <c>WriteAllText</c> 装的监听（<b>参数名必须与真实形参一致</b>：
    /// 它们是 <c>path, contents</c>，写成 <c>text</c> 会被 Harmony 直接拒绝——本轮踩过）。
    /// <para>
    /// 这两条在真机上已被证明"不是游戏的写入途径"（监听挂上了却一次都没触发），
    /// 保留它们是为了：① 万一原版改回这条路；② 日志里留下结论，避免后人重复排查。
    /// </para>
    /// </summary>
    public static void FileSinkPrefix(string path, string contents)
    {
        try
        {
            if (path == null || !path.Contains("national-spire.log", StringComparison.OrdinalIgnoreCase)) return;

            string body = contents ?? "";
            lock (Gate)
            {
                CategoryCounts["File.* 写入"] =
                    CategoryCounts.TryGetValue("File.* 写入", out int old) ? old + 1 : 1;
            }

            // ★ 兜底补挂（每次都跑，代价只有一次程序集枚举）：
            //   这条写入由【游戏正在执行的那一份 NationalSpire】发起。如果它这一份上
            //   还没有我们的补丁，说明游戏是在我们之后才把 NationalSpire 加载进来的
            //   （真机实测就是这个顺序），必须立刻补挂——否则补丁永远只挂在死副本上。
            EconomyModEntry.ReinforceIfNewCopies("原版写 national-spire.log（游戏流量触发）");

            if (Interlocked.Increment(ref _privateStackDumps) > MaxPrivateStackDumps) return;

            Write("===== 触发式抓栈（File.* 写入层）：national-spire.log 收到内容 =====");
            Write($"长度 {body.Length}｜线程 {Environment.CurrentManagedThreadId}");
            Write("内容开头：" + (body.Length > 300 ? body[..300] + "…" : body));
            WriteStack();
            Write("===== 抓栈结束 =====");

            // ★ 抓完栈顺手做一次"执行副本判据"：直接从栈里的原版帧反查它属于哪一份、
            //   那一份上有没有我们的补丁。这是"落点是否正确"的直证。
            ReportExecutingCopy("原版写 national-spire.log 时", FirstGameFrame());
        }
        catch (Exception)
        {
            // 监听绝不抛回原版。
        }
    }

    /// <summary>把调用栈写进旁路日志（最多 30 帧）。</summary>
    private static void WriteStack()
    {
        try
        {
            var trace = new System.Diagnostics.StackTrace(true);
            for (int i = 0; i < trace.FrameCount && i < 30; i++)
            {
                var method = trace.GetFrame(i)?.GetMethod();
                if (method == null) continue;
                Write($"    栈[{i}] {AssemblyInventory.ContextOf(method.Module.Assembly)}｜" +
                      $"{method.DeclaringType?.FullName}.{method.Name}");
            }
        }
        catch (Exception e)
        {
            Write("抓栈失败：" + e.GetType().Name);
        }
    }

    /// <summary>
    /// 把取证监听挂到"诊断文件是怎么被写出来"的每一层上。
    /// <para>
    /// <b>取证点为什么是这几层</b>（按真机实测逐层排除的结果）：
    /// <list type="number">
    /// <item><c>Diagnostics.Record</c> —— 已排除：只收到我们自己写的 <c>economy</c> 分类；</item>
    /// <item><c>File.AppendAllText</c> / <c>WriteAllText</c> —— 已排除：一次都没触发；</item>
    /// <item><b><c>Diagnostics.Configure(String)</c></b> —— 它就是"初始化诊断文件"的入口；</item>
    /// <item><b><c>StreamWriter</c> 构造</b> —— <c>Diagnostics</c> 的方法清单里有
    /// <c>OpenTraceSnapshot(...) → FileStream</c> 与 <c>&lt;Export&gt;g__Write</c>，
    /// 说明它是自己拿 <c>FileStream</c>/<c>StreamWriter</c> 写的。</item>
    /// </list>
    /// 参数名必须与真实形参一致，否则 Harmony 会拒绝挂载（本轮踩过 <c>text</c>/<c>contents</c> 的坑）。
    /// </para>
    /// </summary>
    public static int InstallFileSinkListeners(Harmony? harmony, IEconomyLog? log)
    {
        if (harmony == null) return 0;

        var filePrefix = AccessTools.Method(typeof(PathProbe), nameof(FileSinkPrefix));
        var configurePrefix = AccessTools.Method(typeof(PathProbe), nameof(DiagnosticsConfigurePrefix));
        if (filePrefix == null || configurePrefix == null) return 0;

        int patched = 0;
        int attempted = 0;

        // ── ① File.* 静态写入（已排除，但保留：万一以后原版改回来）──
        var fileTargets = new (Type Owner, string Name, Type[] Parameters)[]
        {
            (typeof(File), "AppendAllText", [typeof(string), typeof(string)]),
            (typeof(File), "AppendAllText", [typeof(string), typeof(string), typeof(Encoding)]),
            (typeof(File), "WriteAllText", [typeof(string), typeof(string)]),
            (typeof(File), "AppendAllLines", [typeof(string), typeof(IEnumerable<string>)])
        };

        foreach (var (owner, name, parameters) in fileTargets)
        {
            if (TryPatch(owner, name, parameters, filePrefix, ref patched, log)) attempted++;
        }

        // ── ② Diagnostics.Configure：诊断文件的初始化入口 ──
        var diagnostics = FindType("NationalSpire.Diagnostics");
        if (diagnostics != null)
        {
            foreach (var method in diagnostics.GetMethods(BindingFlags.Static | BindingFlags.Public |
                                                          BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.Name != "Configure") continue;
                attempted++;
                if (PatchMethod(method, configurePrefix, ref patched, log, "诊断配置层")) { }
            }
        }
        else
        {
            Write("诊断配置层：找不到 NationalSpire.Diagnostics（跳过）");
        }

        // ── ③ StreamWriter 构造：<b>刻意不挂</b> ──
        // 它是全局框架类型（Godot / Harmony / 其它模组都在用），挂构造函数一旦包装出问题
        // 会影响整个进程的文件日志，风险远大于收益。诊断文件的初始化入口
        // （Diagnostics.Configure）已经在 ② 里挂上了，从那里就能看到完整调用栈与路径。
        Write("StreamWriter 构造：刻意不挂（全局框架类型，风险高；改用 Diagnostics.Configure 取证）");

        Write($"取证监听装配总结：尝试 {attempted} 个目标，成功挂载 {patched} 个");
        log?.Info("probe", $"文件/诊断层监听：尝试 {attempted} 个、成功 {patched} 个");

        // 顺带把 NationalSpire.Diagnostics 里所有方法列出来，供下一轮判断"它到底怎么写文件"。
        try
        {
            if (diagnostics != null)
            {
                foreach (var method in diagnostics.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                                                              BindingFlags.Public | BindingFlags.NonPublic |
                                                              BindingFlags.DeclaredOnly))
                {
                    Write($"Diagnostics 方法：{method.Name}({string.Join(",",
                        method.GetParameters().Select(p => p.ParameterType.Name))}) → " +
                          $"{(method is MethodInfo info ? info.ReturnType.Name : "?")}");
                }
            }
        }
        catch (Exception)
        {
            // 列不出来不影响监听。
        }

        return patched;
    }

    /// <summary>按签名找方法并挂载。找不到就记一行（不静默）。</summary>
    private static bool TryPatch(Type owner, string name, Type[] parameters, MethodInfo prefix,
        ref int patched, IEconomyLog? log)
    {
        MethodInfo? method = null;
        try
        {
            method = owner.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
                                           BindingFlags.Static | BindingFlags.Instance, null, parameters, null);
        }
        catch (Exception)
        {
            // 下面统一处理。
        }

        if (method == null)
        {
            Write($"取证监听：找不到 {owner.Name}.{name}({string.Join(",", parameters.Select(p => p.Name))})");
            return false;
        }

        PatchMethod(method, prefix, ref patched, log, owner.Name + "." + name);
        return true;
    }

    /// <summary>挂一个前缀并把结果写进旁路日志。已挂过也算成功（每进程只装一次）。</summary>
    private static bool PatchMethod(MethodBase method, MethodInfo prefix, ref int patched, IEconomyLog? log,
        string label)
    {
        if (HasPatch(method, prefix))
        {
            Write($"取证监听：{label} 已挂过（跳过）");
            return true;
        }

        try
        {
            var harmony = new HarmonyLib.Harmony(HarmonyId);
            harmony.Patch(method, prefix: new HarmonyMethod(prefix));
            patched++;
            Write($"取证监听：已挂 {label}" +
                  $"（形参 {string.Join(",", method.GetParameters().Select(p => p.Name))}）｜token=0x{TokenOf(method)}");
            log?.Info("probe", $"取证监听已挂：{label}");
            return true;
        }
        catch (Exception e)
        {
            Write($"取证监听挂载失败：{label}｜{e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>本模组统一的 Harmony id（与 <c>EconomyModEntry.HarmonyId</c> 一致）。</summary>
    private const string HarmonyId = "nationalspire.economy";

    // ─────────────────────────────────────────────────────────────────
    // 补丁落点体检（回答"补丁挂的是不是游戏真正执行的那一份"）
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>补丁落点体检</b>：列出"名字含 NationalSpire 的每一份程序集"，
    /// 对每份逐一检查我们关心的目标方法<b>有没有真的挂上补丁</b>。
    /// <para>
    /// <b>为什么需要它（真机实测事故）</b>：同一个 DLL 被两个 ALC 各加载一次时，
    /// 两份 <see cref="Assembly"/> 对象的 <c>FullName</c> 与 <c>Location</c> 完全相同。
    /// 我们原先按名字去重，于是<b>只挂到了 Default 里那一份</b>；
    /// 而游戏真正执行的是 <c>IsolatedComponentLoadContext</c> 里那一份——
    /// 症状正是"挂载成功、token 都对、却永远不命中"。
    /// 这份体检会直接指出"哪一份挂了、哪一份没挂"。
    /// </para>
    /// </summary>
    public static string AuditPatchLocation(IEnumerable<string> targetMethodNames)
    {
        var wanted = new HashSet<string>(targetMethodNames, StringComparer.Ordinal);
        var report = new StringBuilder();

        var copies = AssemblyInventory.GameAssemblies();
        report.Append($"原版程序集副本数：{copies.Count}（按对象身份计，不做名字去重）");

        int copiesWithPatches = 0;
        foreach (var assembly in copies)
        {
            string alc = AssemblyInventory.ContextOf(assembly);
            report.Append(Environment.NewLine +
                          $"── 副本：{assembly.GetName().Name}｜ALC {alc}｜" +
                          $"{AssemblyInventory.SafeLocation(assembly)}");

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).Cast<Type>().ToArray();
            }
            catch (Exception e)
            {
                report.Append(Environment.NewLine + "   （枚举类型失败：" + e.GetType().Name + "）");
                continue;
            }

            int ours = 0;
            foreach (var type in types)
            {
                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                                              BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.DeclaredOnly);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var method in methods)
                {
                    if (!wanted.Contains(method.Name)) continue;
                    if (!HasAnyOfOurPatches(method)) continue;
                    ours++;
                }
            }

            if (ours > 0) copiesWithPatches++;
            report.Append(Environment.NewLine + $"   我们的补丁命中目标：{ours} 个方法");
        }

        LastCopyCount = copies.Count;
        LastPatchedCopyCount = copiesWithPatches;

        report.Append(Environment.NewLine);
        report.Append($"我们的补丁覆盖：{copiesWithPatches}／{copies.Count} 份副本");
        report.Append(Environment.NewLine);
        report.Append(copies.Count == 0
            ? "★结论：进程里一份精确名 NationalSpire 的副本都没有（补丁必然全部空转）"
            : copiesWithPatches switch
            {
                0 => "★结论：没有任何一份副本挂上了补丁",
                _ when copiesWithPatches < copies.Count =>
                    $"★结论：只挂上了 {copiesWithPatches} 份（共 {copies.Count} 份）" +
                    "——如果游戏执行的是没挂上的那一份，补丁就永远不命中（阶段 3 事故）",
                _ => $"结论：{copies.Count} 份副本都已挂上"
            });

        return report.ToString();
    }

    /// <summary>最近一次落点体检看到的原版副本数。</summary>
    public static int LastCopyCount { get; private set; } = -1;

    /// <summary>最近一次落点体检里"我们的补丁真的挂上去了"的副本数。</summary>
    public static int LastPatchedCopyCount { get; private set; } = -1;

    /// <summary>已打印过多少次"执行副本判据"（避免刷屏）。</summary>
    private static int _executingCopyReports;

    private const int MaxExecutingCopyReports = 6;

    /// <summary>
    /// <b>★ 执行副本判据</b>：从"游戏<b>正在执行</b>的那个方法"反查它属于哪个程序集／ALC，
    /// 再<b>就地</b>检查那一份副本上有没有我们的补丁。
    /// <para>
    /// <b>为什么必须这样查</b>（阶段 3 的教训）：上一轮我们从"我们自己能枚举到什么"出发，
    /// 于是枚举到了一份 Default 里的假副本、把它当成"原版"，16 个目标全挂在那上面；
    /// 而游戏真正执行的是 <c>IsolatedComponentLoadContext</c> 里的另一份，0 个目标。
    /// 正确方向只有一个：<b>谁在跑，就查谁</b>。
    /// </para>
    /// <para>它也是下一轮真机测试的第一判据：日志里出现"落点正确"就能停止怀疑副本问题。</para>
    /// </summary>
    public static void ReportExecutingCopy(string where, MethodBase? executing)
    {
        try
        {
            if (executing == null) return;
            if (Interlocked.Increment(ref _executingCopyReports) > MaxExecutingCopyReports) return;

            var assembly = executing.Module.Assembly;
            var entryPrefix = AccessTools.Method(typeof(PathProbe), nameof(EntryPrefix));
            var injection = AccessTools.Method(typeof(PrivateRequestInjectionPatch),
                nameof(PrivateRequestInjectionPatch.RequestPrefix));

            Write($"===== ★执行副本判据（{where}）=====");
            Write($"  正在执行：{Describe(executing)}｜token=0x{TokenOf(executing)}");
            Write($"  所属程序集：{AssemblyInventory.SafeName(assembly)}｜ALC {AssemblyInventory.ContextOf(assembly)}｜" +
                  $"{AssemblyInventory.SafeLocation(assembly)}");

            bool probeHere = entryPrefix != null && HasPatch(executing, entryPrefix);
            Write($"  这一份上：观测探针在【本方法】上={(probeHere ? "是" : "否")}");

            // 关键目标逐个就地检查（用的是这一份副本自己的类型对象，不看任何缓存/清单）。
            int total = 0, withProbe = 0, withInjection = 0;
            foreach (var (typeName, methodName) in new[]
                     {
                         ("NationalSpire.Diagnostics", "Record"),
                         ("NationalSpire.PrivateStreamParser", "Feed"),
                         ("NationalSpire.AiService", "RequestPrivateAsync"),
                         ("NationalSpire.AiService", "ProcessPrivateAsync")
                     })
            {
                Type? type;
                try
                {
                    type = assembly.GetType(typeName, throwOnError: false);
                }
                catch (Exception)
                {
                    continue;
                }
                if (type == null) continue;

                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                                                       BindingFlags.Public | BindingFlags.NonPublic |
                                                       BindingFlags.DeclaredOnly))
                {
                    if (!string.Equals(method.Name, methodName, StringComparison.Ordinal)) continue;
                    total++;
                    if (entryPrefix != null && HasPatch(method, entryPrefix)) withProbe++;
                    if (injection != null && HasPatch(method, injection)) withInjection++;
                }
            }

            Write($"  这一份上：关键目标带观测探针 {withProbe}／{total} 个｜带方案 A 注入前缀 {withInjection} 个");
            Write(probeHere || withProbe > 0
                ? "  ⇒ 结论：游戏执行的这一份【就是】挂了补丁的那一份（落点正确）"
                : "  ⇒ ★结论：游戏执行的这一份【没有】我们的补丁（落点错误——必须补挂）");
            Write("===== 判据结束 =====");
        }
        catch (Exception)
        {
            // 判据本身绝不抛回原版。
        }
    }

    /// <summary>从当前调用栈里找第一个属于原版副本的帧（"谁在跑"的入口）。</summary>
    public static MethodBase? FirstGameFrame()
    {
        try
        {
            var trace = new System.Diagnostics.StackTrace(true);
            for (int i = 0; i < trace.FrameCount; i++)
            {
                var method = trace.GetFrame(i)?.GetMethod();
                if (method == null) continue;
                if (method.DeclaringType?.Assembly == typeof(PathProbe).Assembly) continue;
                if (AssemblyInventory.IsGameAssembly(method.Module.Assembly)) return method;
            }
        }
        catch (Exception)
        {
            // 抓不到就算了。
        }
        return null;
    }

    /// <summary>把补丁落点体检写进旁路日志。</summary>
    public static void DumpPatchLocationAudit(IEnumerable<string> targetMethodNames)
    {
        Write("===== 补丁落点体检 =====");
        foreach (string line in AuditPatchLocation(targetMethodNames).Split('\n'))
            Write(line.TrimEnd('\r'));
        Write("===== 体检结束 =====");
    }

    // ─────────────────────────────────────────────────────────────────
    // 活性自检（本轮新加：回答"我的补丁在游戏进程里到底会不会被执行"）
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>生产环境入口</b>：只在真机（进程里有 GodotSharp）里跑活性自检。
    /// <para>
    /// 为什么不放在单测里顺带跑：这条会真的调用原版的私信方法，单测进程里没有 Godot、
    /// 也没有私信状态，调了只会得到一场误导人的失败。
    /// </para>
    /// </summary>
    public static void RunLiveSelfCheck()
    {
        bool godotLoaded = false;
        try
        {
            foreach (var assembly in AssemblyInventory.AllAssemblies())
            {
                if ((assembly.GetName().Name ?? "") == "GodotSharp") { godotLoaded = true; break; }
            }
        }
        catch (Exception)
        {
            godotLoaded = false;
        }

        if (!godotLoaded) return;
        VerifyLive();
    }

    /// <summary>
    /// <b>活性自检</b>：真的去调一次原版私信链路上的方法，然后看探针有没有记下这次调用。
    /// <para>
    /// 它回答的是本轮最关键的那个问题：<b>"探针挂上了"≠"探针会被执行"</b>。
    /// 如果这次自检报告"探针没记录"，说明我们装的补丁在这个进程里根本不会被调用——
    /// 那么真机上再怎么发私信都不会有痕迹，必须换机制（而不是再换挂载点）。
    /// </para>
    /// <para>
    /// 调的是 <c>PrivateMessages.Conversation(CareerData, string)</c>：纯查表、无副作用、
    /// 参数给空世界也安全。委托一个独立方法包住异常，保证自检本身绝不把游戏带崩。
    /// </para>
    /// </summary>
    public static bool VerifyLive()
    {
        Write("===== 活性自检：探针是否真的会被执行 =====");

        var conversationType = FindType("NationalSpire.PrivateMessages");
        if (conversationType == null)
        {
            Write("活性自检：找不到 NationalSpire.PrivateMessages，无法自检");
            return false;
        }

        var method = conversationType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Conversation" && m.GetParameters().Length == 2);
        if (method == null)
        {
            Write("活性自检：找不到 Conversation(CareerData, string)，无法自检");
            return false;
        }

        Write("活性自检：目标 " + Describe(method) +
              $"｜token=0x{TokenOf(method)}｜ALC {AssemblyInventory.ContextOf(method.Module.Assembly)}");

        // 目标方法的第一个参数就是 CareerData；用它的真实类型造一个空世界（反射，避免编译期依赖）。
        object? data = null;
        try
        {
            var dataType = method.GetParameters()[0].ParameterType;
            data = dataType.GetConstructor(Type.EmptyTypes)?.Invoke(null);
        }
        catch (Exception e)
        {
            Write("活性自检：构造 CareerData 失败（不影响结论）：" + e.GetType().Name);
        }

        if (data == null)
        {
            Write("活性自检：拿不到空的 CareerData，无法自检");
            return false;
        }

        int before = HitsOf("Conversation");
        int countBefore = TotalHits;

        try
        {
            method.Invoke(null, [data, "nse-selfcheck-nobody"]);
            Write("活性自检：原版方法调用返回（正常）");
        }
        catch (Exception e)
        {
            // 抛异常也没关系：探针的进入/终结器应该在抛之前就写了。
            Write("活性自检：原版方法抛出（可接受，探针应已记录进入）：" + e.GetType().Name);
        }

        int after = HitsOf("Conversation");
        bool recorded = after > before || TotalHits > countBefore;

        Write(recorded
            ? $"活性自检结果：✅ 探针被调用（Conversation 命中 {before} → {after}，总命中 {countBefore} → {TotalHits}）"
            : $"活性自检结果：❌ 探针没有被调用（Conversation 命中仍为 {after}）" +
              "——这说明我们装的补丁在这个进程里不会被执行，换挂载点也没用，必须换机制");
        Write("===== 活性自检结束 =====");

        LiveSelfCheckPassed = recorded;
        return recorded;
    }

    /// <summary>活性自检的结论（null = 还没跑过）。</summary>
    public static bool? LiveSelfCheckPassed { get; private set; }

    /// <summary>所有候选方法的命中总数（活性的粗判据）。</summary>
    public static int TotalHits
    {
        get
        {
            lock (Gate)
            {
                int total = 0;
                foreach (int count in Counts.Values) total += count;
                return total;
            }
        }
    }

    /// <summary>
    /// 在进程里找类型。<b>优先返回"游戏执行的那一份"</b>（我们自己 ALC 里的原版副本）。
    /// <para>
    /// 为什么顺序重要：多副本时返回默认 ALC 里那份假副本的类型，补丁就又挂到死副本上
    /// （阶段 3 卡点）。"谁在跑"由 <see cref="NationalSpireLocator.OurContext"/> 决定。
    /// </para>
    /// </summary>
    private static Type? FindType(string fullName)
    {
        foreach (var assembly in AssemblyInventory.GameAssemblies().OrderByDescending(InOurContext))
        {
            var type = TypeIn(assembly, fullName);
            if (type != null) return type;
        }

        foreach (var assembly in AssemblyInventory.AllAssemblies())
        {
            var type = TypeIn(assembly, fullName);
            if (type != null) return type;
        }
        return null;
    }

    private static Type? TypeIn(Assembly assembly, string fullName)
    {
        try
        {
            return assembly.GetType(fullName, throwOnError: false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>这个程序集是不是在我们自己所在的 ALC 里（= 游戏加载并执行模组的地方）。</summary>
    public static bool InOurContext(Assembly assembly)
    {
        try
        {
            return ReferenceEquals(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly),
                NationalSpireLocator.OurContext);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // 触发式抓栈（直接回答"私信请求是谁发起的"）
    // ─────────────────────────────────────────────────────────────────

    /// <summary>已抓栈次数（避免刷屏）。</summary>
    private static int _privateStackDumps;

    /// <summary>最多抓几次栈。</summary>
    private const int MaxPrivateStackDumps = 3;

    /// <summary>诊断分类计数（用来证明"我们装在 Record 上的补丁到底有没有收到游戏流量"）。</summary>
    private static readonly Dictionary<string, int> CategoryCounts = new(StringComparer.Ordinal);

    /// <summary>已经为多少个分类打印过首见行。</summary>
    private static int _categoryLines;

    /// <summary>最多打印多少个分类首见行。</summary>
    private const int MaxCategoryLines = 40;

    /// <summary>
    /// 给 <c>NationalSpire.Diagnostics.Record(string, object)</c> 装的监听：把<b>每一个</b>分类都计数，
    /// 并在原版写下 <c>private.request</c> 时抓调用栈。
    /// <para>
    /// <b>为什么要计数所有分类</b>：真机上出现了自相矛盾的现象——私信请求确实发生
    /// （原版诊断写了 <c>private.request</c>），但我们挂在这个方法上的监听与私信链路上的探针
    /// 全都没触发。分类计数能一刀切开两种可能：
    /// ① 计数里有 <c>private.request</c> ⇒ 补丁收到了流量、只是抓栈那段出了问题；
    /// ② 计数里一条游戏分类都没有（只有我们自己写的）⇒ <b>游戏根本没走这个 <c>Record</c></b>，
    ///    那么 <c>national-spire.log</c> 是别的通道写出来的，必须去找那条通道。
    /// </para>
    /// </summary>
    public static void DiagnosticsPrefix(string __0, object? __1)
    {
        try
        {
            string category = __0 ?? "";
            int count;
            lock (Gate)
            {
                CategoryCounts[category] = count = CategoryCounts.TryGetValue(category, out int old) ? old + 1 : 1;
            }

            // 每个分类只打印首见行，避免刷屏；总数由摘要行给出。
            if (count == 1 && Interlocked.Increment(ref _categoryLines) <= MaxCategoryLines)
                Write($"诊断分类首见：<{category}>｜载荷类型 {__1?.GetType().Name ?? "null"}");

            if (!category.Contains("private.request", StringComparison.Ordinal) &&
                !category.Contains("ai.request", StringComparison.Ordinal)) return;

            if (Interlocked.Increment(ref _privateStackDumps) > MaxPrivateStackDumps) return;

            Write($"===== 触发式抓栈：原版写下 <{category}> =====");
            Write("线程 " + Environment.CurrentManagedThreadId + "｜载荷类型 " + (__1?.GetType().Name ?? "null"));

            try
            {
                string payload = __1?.ToString() ?? "";
                if (payload.Length > 400) payload = payload[..400] + "…";
                Write("载荷开头：" + payload);
            }
            catch (Exception)
            {
                // 载荷读不出来不影响抓栈。
            }

            try
            {
                var trace = new System.Diagnostics.StackTrace(true);
                for (int i = 0; i < trace.FrameCount && i < 40; i++)
                {
                    var frame = trace.GetFrame(i);
                    var method = frame?.GetMethod();
                    if (method == null) continue;

                    string location = "";
                    try
                    {
                        if (frame!.GetFileName() is { Length: > 0 } file)
                            location = $"（{System.IO.Path.GetFileName(file)}:{frame.GetFileLineNumber()}）";
                    }
                    catch (Exception)
                    {
                        // 没有 PDB 时拿不到行号，忽略。
                    }

                    Write($"    栈[{i}] {AssemblyInventory.ContextOf(method.Module.Assembly)}｜" +
                          $"{method.DeclaringType?.FullName}.{method.Name}{location}");
                }
            }
            catch (Exception e)
            {
                Write("抓栈失败：" + e.GetType().Name);
            }

            Write("===== 抓栈结束 =====");
        }
        catch (Exception)
        {
            // 监听绝不抛回原版。
        }
    }

    /// <summary>诊断分类计数摘要（由 <see cref="DumpCategoryCounts"/> 写出）。</summary>
    public static string CategorySummary
    {
        get
        {
            lock (Gate)
            {
                return CategoryCounts.Count == 0
                    ? "(一条诊断都没经过我们这个补丁)"
                    : string.Join("、", CategoryCounts.OrderByDescending(kv => kv.Value)
                        .Select(kv => $"{kv.Key}={kv.Value}"));
            }
        }
    }

    /// <summary>把诊断分类计数写进旁路日志（供真机判断"Record 上到底有没有游戏流量"）。</summary>
    public static void DumpCategoryCounts(string reason)
    {
        Write($"诊断分类统计（{reason}）：{CategorySummary}");
    }

    /// <summary>
    /// 把抓栈监听挂到 <c>Diagnostics.Record(string, object)</c> 上。
    /// 由 <c>EconomyModEntry.Initialize</c> 在装配阶段调用。
    /// </summary>
    public static bool InstallDiagnosticsListener(Harmony? harmony, IEconomyLog? log)
    {
        if (harmony == null) return false;

        var prefix = AccessTools.Method(typeof(PathProbe), nameof(DiagnosticsPrefix));
        if (prefix == null) return false;

        // ★ 逐个【副本】挂，而不是"找到第一个就完事"：
        //   同一个 DLL 可能在多个 ALC 里各有一份，只挂一份就是上一轮那个卡点。
        int found = 0, newly = 0, already = 0;
        foreach (var assembly in AssemblyInventory.GameAssemblies())
        {
            var record = FindRecord(assembly);
            if (record == null) continue;
            found++;

            if (HasPatch(record, prefix))
            {
                already++;
                continue;
            }

            try
            {
                harmony.Patch(record, prefix: new HarmonyMethod(prefix));
                newly++;
                Write($"抓栈监听：已挂到 {Describe(record)}｜token=0x{TokenOf(record)}｜" +
                      $"ALC {AssemblyInventory.ContextOf(record.Module.Assembly)}");
            }
            catch (Exception e)
            {
                Write("抓栈监听挂载失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        int copies = AssemblyInventory.GameAssemblies().Count;
        if (found == 0)
        {
            Write($"抓栈监听：{copies} 份原版副本里都找不到 Diagnostics.Record(string, object)，跳过");
            return false;
        }

        Write($"抓栈监听：原版副本 {copies} 份｜找到 Record {found} 份｜本次新挂 {newly} 份｜早前已挂 {already} 份");
        log?.Info("probe",
            $"抓栈监听：原版副本 {copies} 份，Record 找到 {found} 份、本次新挂 {newly} 份" +
            "（原版写 private.request 时会打印调用栈）");
        return newly > 0 || already > 0;
    }

    /// <summary>在<b>指定副本</b>里找 <c>NationalSpire.Diagnostics.Record(string, object)</c>。</summary>
    private static MethodInfo? FindRecord(Assembly assembly)
    {
        try
        {
            var diagnostics = assembly.GetType("NationalSpire.Diagnostics", throwOnError: false);
            if (diagnostics == null) return null;

            return diagnostics.GetMethod("Record", BindingFlags.Public | BindingFlags.Static, null,
                       [typeof(string), typeof(object)], null)
                   ?? diagnostics.GetMethods(BindingFlags.Public | BindingFlags.Static)
                       .FirstOrDefault(m => m.Name == "Record" && m.GetParameters().Length == 2);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // 装配
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 把探针挂到<b>NationalSpire 程序集</b>里所有候选方法上。
    /// <para>
    /// 刻意<b>不猜类型</b>：只要方法名在候选清单里就挂。私信链路一旦被别的模组换过类型，
    /// 这里照样能抓到——这正是本轮要验证的东西。
    /// </para>
    /// <para>
    /// <b>但必须限定程序集</b>（本轮踩过）：候选名单里有 <c>Invoke</c> 这类名字，
    /// 而 <c>System.Reflection</c> 里 <c>MethodBase.Invoke</c> 也在其中；
    /// 一旦把探针挂到反射调用本身，就会变成"探针写日志 → 触发反射 → 又进探针"的无限递归。
    /// 所以只挂名字含 <c>NationalSpire</c> 的程序集。
    /// </para>
    /// <para><b>可重复调用</b>：初始化可重入、而且游戏可能在<b>我们之后</b>才加载 NationalSpire
    /// （真机实测：ModManager 逐个模组加载并立刻初始化，NationalSpire 排在后面）。
    /// 所以装配<b>不能</b>有"每进程只装一次"的闩锁——那样后出现的执行副本永远挂不上。
    /// 幂等靠 <c>方法 + 具体补丁方法</c> 精确认定（见 <see cref="HasPatch"/>）。</para>
    /// </summary>
    public static int Install(Harmony? harmony)
    {
        if (harmony == null)
        {
            LastError = "Harmony 实例未建立";
            return 0;
        }

        var entryPrefix = AccessTools.Method(typeof(PathProbe), nameof(EntryPrefix));
        var exitPostfix = AccessTools.Method(typeof(PathProbe), nameof(ExitPostfix));
        var trap = AccessTools.Method(typeof(PathProbe), nameof(Finalizer));
        if (entryPrefix == null || exitPostfix == null || trap == null)
        {
            LastError = "探针方法未找到";
            return 0;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var patchedMethods = new List<MethodInfo>();
        var patchedCopies = new List<string>();
        int patched = 0;

        var gameAssemblies = AssemblyInventory.GameAssemblies();
        Write($"按名字装配：跨 ALC 扫到原版程序集 {gameAssemblies.Count} 份" +
              $"（精确程序集名 {AssemblyInventory.GameAssemblyName}）" +
              (gameAssemblies.Count == 0 ? "（★一份都没有——这就是补丁永远不命中的原因）" : ""));

        foreach (var assembly in gameAssemblies)
        {
            string assemblyName;
            try
            {
                assemblyName = assembly.GetName().Name ?? "";
            }
            catch (Exception)
            {
                continue;
            }

            // 记录来源 ALC：多副本/隔离问题全靠这一行判断。
            Write($"  候选程序集：{AssemblyInventory.Describe(assembly)}");

            // ★ 每个副本单独记一行：本方法可能被反复调用（新副本出现时补挂），
            //   这一行能一眼看出"这一次是新挂了、还是早就有了"。
            bool copyAlreadyPatched = false;
            int patchedOnThisCopy = 0;

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
                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                                              BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.DeclaredOnly);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var method in methods)
                {
                    if (!CandidateMethods.Contains(method.Name, StringComparer.Ordinal)) continue;
                    // 我们自己的方法不挂（否则自己探自己，噪音且可能递归）。
                    if (method.DeclaringType?.Assembly == typeof(PathProbe).Assembly) continue;
                    // 状态机的 MoveNext 单独由身份补丁处理，这里只记外层方法的进入。
                    if (method.Name == "MoveNext") continue;

                    string key = $"{method.DeclaringType?.FullName}.{method.Name}(" +
                                 string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name)) + ")";

                    // ★ 去重键必须带【副本身份】：同一签名在两个副本里各有一份，
                    //   只按签名去重会让第二份永远挂不上——那正是"只挂上了 1 份"的成因
                    //   （真机体检：Default 那份命中 16 个目标、执行副本 0 个）。
                    string copyKey = AssemblyInventory.ContextOf(assembly) + "｜" +
                                     AssemblyInventory.SafeLocation(assembly);
                    if (!seen.Add(copyKey + "｜" + key)) continue;

                    // 幂等：本进程早前已经给这个方法挂过探针就不再重复挂（Harmony 不替我们挡）。
                    if (HasPatch(method, entryPrefix))
                    {
                        copyAlreadyPatched = true;
                        patchedMethods.Add(method);
                        continue;
                    }

                    try
                    {
                        harmony.Patch(method,
                            prefix: new HarmonyMethod(entryPrefix),
                            postfix: new HarmonyMethod(exitPostfix),
                            finalizer: new HarmonyMethod(trap));
                        patched++;
                        patchedOnThisCopy++;
                        patchedMethods.Add(method);
                        Write($"已挂探针：{key}｜token=0x{TokenOf(method)}｜程序集 {assemblyName}" +
                              (IsPrivateSpecific(method.Name) ? "｜★私信专属" : ""));
                    }
                    catch (Exception e)
                    {
                        Write($"挂载失败：{key}｜{e.GetType().Name}: {e.Message}");
                    }
                }
            }

            // 每个副本单独记一行结论：这样"发现 N 份、已挂 M 份"是可核对的事实，
            // 而不是从别处推断出来的。
            if (patchedOnThisCopy > 0 || copyAlreadyPatched)
                patchedCopies.Add($"{assemblyName}（{AssemblyInventory.ContextOf(assembly)}｜" +
                                  $"本次新挂 {patchedOnThisCopy}｜早前已挂 {copyAlreadyPatched}）");
        }

        // 精确到类型的检查点（PrivateTurn 的字段赋值时间线）。
        int exactFound = 0, exactPatched = 0;
        // ★ 用【精确程序集名】的副本集合，而不是"名字含 NationalSpire"的模糊集合：
        //   后者会把我们自己（NationalSpireEconomy）也算进来，报告里就多出一份永远 0 命中的"副本"。
        foreach (var assembly in gameAssemblies)
        {
            string assemblyName = AssemblyInventory.SafeName(assembly);
            if (assemblyName.Length == 0) continue;

            foreach (var target in ExactTargets)
            {
                Type? type;
                try
                {
                    type = assembly.GetType(target.TypeName, throwOnError: false);
                }
                catch (Exception)
                {
                    continue;
                }
                if (type == null) continue;
                exactFound++;

                var method = type.GetMethod(target.MethodName, BindingFlags.Instance | BindingFlags.Public |
                                                            BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                             ?? type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                 .FirstOrDefault(m => m.Name == target.MethodName);
                string key = $"{target.TypeName}.{target.MethodName}";
                if (method == null)
                {
                    Write($"精确目标未找到方法：{key}（{target.TypeName} 里没有名为 {target.MethodName} 的成员）");
                    continue;
                }

                // 注意：这里刻意【不】用按名字匹配那份 seen 去重。
                // 理由：set_Context 与 PrivateMessagePrompts.Context 同名——
                // 按名字匹配会先命中后者并把 key 占掉，于是 PrivateTurn.set_Context 永远挂不上（实测踩过）。
                // 但仍然要按【副本身份】区分：两个副本各有一份 set_Context。
                string exactKey = "exact:" +
                                  AssemblyInventory.ContextOf(assembly) + "｜" +
                                  AssemblyInventory.SafeLocation(assembly) + "｜" + key;
                if (!seen.Add(exactKey)) continue;

                if (HasPatch(method, entryPrefix))
                {
                    // 早前已经挂过：只登记，不重复挂（副本摘要由上面那个按名字的循环给出）。
                    patchedMethods.Add(method);
                    continue;
                }

                try
                {
                    harmony.Patch(method,
                        prefix: new HarmonyMethod(entryPrefix),
                        postfix: new HarmonyMethod(exitPostfix),
                        finalizer: new HarmonyMethod(trap));
                    patched++;
                    exactPatched++;
                    patchedMethods.Add(method);
                    Write($"已挂探针：{key}｜token=0x{TokenOf(method)}｜程序集 {assemblyName}｜★私信时间线");
                }
                catch (Exception e)
                {
                    Write($"挂载失败：{key}｜{e.GetType().Name}: {e.Message}");
                }
            }
        }
        Write($"精确检查点：命中类型 {exactFound} 个／挂上 {exactPatched} 个（共 {ExactTargets.Length} 个目标）");

        lock (Gate)
        {
            PatchedCount = patchedMethods.Count;
            CopiesScanned = gameAssemblies.Count;
            foreach (string copy in patchedCopies) _patchedCopies.Add(copy);
            _installed = true;
            // InstallLogPath 的语义是"装配清单真正被写进的那个文件"：只有真的挂了新东西
            // （或这是本进程第一次装配）才更新它，否则会把读者指向一个只有运行痕迹、
            // 没有装配清单的新文件。
            if (patched > 0 || InstallLogPath.Length == 0) InstallLogPath = _path;
        }
        Write($"装配完成：本次新挂 {patched} 个候选方法｜当前带探针的方法共 {patchedMethods.Count} 个" +
              $"｜原版副本 {gameAssemblies.Count} 份｜已覆盖副本 {patchedCopies.Count} 份" +
              (patchedCopies.Count > 0 ? "：" + string.Join(" ／ ", patchedCopies) : ""));

        // ★ 每次装配都写一行显式的覆盖小结：真机判读时"私信时间线到底挂上没有"必须一眼可见，
        //   而不是要靠"本副本此前已挂"这类间接措辞去推。
        Write($"★私信时间线覆盖：精确检查点目标 {ExactTargets.Length} 个" +
              $"（{string.Join("、", ExactTargets.Select(t => t.TypeName + "." + t.MethodName))}）" +
              $"｜本副本命中类型 {exactFound} 个／本次新挂 {exactPatched} 个" +
              $"｜私信专属候选 {string.Join("、", PrivateSpecificMethods)}");
        // ★ 关键一步：把目标方法上现存的**全部**补丁（含别的模组装的）转储出来。
        //   如果有别的模组装了"返回 false 的前缀"，原版方法体连同我们的逻辑会被整体跳过——
        //   那正是"补丁挂上了却从未命中"的最可能原因，而这张表能直接指认它。
        DumpPatches(harmony, patchedMethods);

        // 返回值语义：<b>当前带我们探针的方法总数</b>（不是"本次新挂数"）。
        // 调用方问的是"探针装上没有"，而我们是可重复装配的，返回新挂数会让重复调用者以为失败。
        return PatchedCount;
    }

    private static bool IsPrivateSpecific(string methodName) =>
        PrivateSpecificMethods.Contains(methodName, StringComparer.Ordinal);

    /// <summary>
    /// 这个目标方法上是否已经有<b>我们自己的</b>补丁（幂等守卫）。
    /// <para>
    /// <b>实测事故（必须记住）</b>：本方法原先写成"只要该方法上有我们程序集的<em>任意</em>补丁就返回 true"。
    /// 而 <see cref="PathProbe"/> 会在启动早期给同一批方法挂<b>只读观测探针</b>——
    /// 于是第 7 步真正的注入前缀被误判成"已挂过"而<b>整个跳过</b>，
    /// 日志里表现为 <c>RequestPrivateAsync …（已挂过，跳过）</c>，注入前缀实际不存在。
    /// 所以现在必须按 <b>「目标方法 + 具体补丁方法」</b>精确判定：
    /// 观测探针在不在，与注入前缀该不该挂，是两件事。
    /// </para>
    /// </summary>
    public static bool HasPatch(MethodBase method, MethodInfo patchMethod)
    {
        try
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null) return false;

            return Contains(info.Prefixes, patchMethod) || Contains(info.Postfixes, patchMethod) ||
                   Contains(info.Transpilers, patchMethod) || Contains(info.Finalizers, patchMethod);
        }
        catch (Exception)
        {
            // 查不出来就当作没挂过：宁可多挂一次，也不要静默漏挂。
            return false;
        }
    }

    private static bool Contains(IEnumerable<Patch>? patches, MethodInfo patchMethod)
    {
        if (patches == null) return false;
        foreach (var patch in patches)
        {
            try
            {
                if (patch.PatchMethod == patchMethod) return true;
            }
            catch (Exception)
            {
                // 单个补丁读不出来就跳过它。
            }
        }
        return false;
    }

    /// <summary>
    /// 这个方法上是否有<b>任何</b>我们程序集的补丁（只用于诊断，<b>不要</b>拿它当幂等守卫）。
    /// </summary>
    public static bool HasAnyOfOurPatches(MethodBase method)
    {
        try
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null) return false;

            return IsOurs(info.Prefixes) || IsOurs(info.Postfixes) ||
                   IsOurs(info.Transpilers) || IsOurs(info.Finalizers);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsOurs(IEnumerable<Patch>? patches)
    {
        if (patches == null) return false;
        foreach (var patch in patches)
        {
            try
            {
                if (patch.PatchMethod?.DeclaringType?.Assembly == typeof(PathProbe).Assembly) return true;
            }
            catch (Exception)
            {
                // 单个补丁读不出来就跳过它。
            }
        }
        return false;
    }

    /// <summary>转储给定方法上现存的 Harmony 补丁。</summary>
    public static void DumpPatches(Harmony? harmony, IEnumerable<MethodBase> methods)
    {
        if (harmony == null) return;
        Write("===== Harmony 补丁表（谁在这些方法上装了东西）=====");

        foreach (var method in methods.Distinct())
        {
            Patches? info;
            try
            {
                info = Harmony.GetPatchInfo(method);
            }
            catch (Exception)
            {
                continue;
            }

            if (info == null) continue;

            int total = (info.Prefixes?.Count ?? 0) + (info.Postfixes?.Count ?? 0) +
                        (info.Transpilers?.Count ?? 0) + (info.Finalizers?.Count ?? 0);
            if (total == 0) continue;

            Write($"  {Describe(method)}：补丁 {total} 个（前缀 {info.Prefixes?.Count ?? 0}／" +
                  $"后缀 {info.Postfixes?.Count ?? 0}／转移器 {info.Transpilers?.Count ?? 0}／" +
                  $"终结器 {info.Finalizers?.Count ?? 0}）");

            foreach (var prefix in PatchList(info.Prefixes))
                Write($"      前缀 owner={prefix.owner}｜priority={prefix.priority}｜返回类型 " +
                      $"{DescribeReturn(prefix.PatchMethod)}" +
                      (ReturnsBoolean(prefix.PatchMethod) ? "（★返回 bool 的前缀可以整体跳过原版方法体！）" : ""));
            foreach (var postfix in PatchList(info.Postfixes))
                Write($"      后缀 owner={postfix.owner}｜priority={postfix.priority}" +
                      $"｜{Describe(postfix.PatchMethod)}");
            foreach (var transpiler in PatchList(info.Transpilers))
                Write($"      转移器 owner={transpiler.owner}｜priority={transpiler.priority}" +
                      $"｜{Describe(transpiler.PatchMethod)}（★可能替换了方法体）");
            foreach (var finalizer in PatchList(info.Finalizers))
                Write($"      终结器 owner={finalizer.owner}｜priority={finalizer.priority}");
        }

        Write("===== 补丁表转储结束 =====");
    }

    private static IEnumerable<Patch> PatchList(IReadOnlyCollection<Patch>? patches) =>
        patches ?? (IEnumerable<Patch>)[];

    /// <summary>转储给定方法名（在所有已加载类型里找）上的补丁。</summary>
    public static void DumpPatches(Harmony? harmony, IEnumerable<string> methodNames)
    {
        var wanted = new HashSet<string>(methodNames, StringComparer.Ordinal);
        var found = new List<MethodInfo>();

        foreach (var assembly in AssemblyInventory.AllAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var type in types)
            {
                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                                              BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.DeclaredOnly);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var method in methods)
                    if (wanted.Contains(method.Name)) found.Add(method);
            }
        }

        DumpPatches(harmony, found);
    }

    private static string DescribeReturn(MethodBase method)
    {
        try
        {
            return method is MethodInfo info ? info.ReturnType.Name : "?";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static bool ReturnsBoolean(MethodBase method) =>
        method is MethodInfo info && info.ReturnType == typeof(bool);

    // ─────────────────────────────────────────────────────────────────
    // 探针本体
    // ─────────────────────────────────────────────────────────────────

    /// <summary>方法进入。用 <c>__originalMethod</c> 拿到真实目标名，避免多副本时认错。</summary>
    public static void EntryPrefix(MethodBase __originalMethod, object[] __args)
    {
        try
        {
            string name = __originalMethod?.Name ?? "?";
            int count;
            lock (Gate)
            {
                Counts[name] = count = Counts.TryGetValue(name, out int old) ? old + 1 : 1;
            }

            Write($"进入 {Describe(__originalMethod)}" +
                  (IsPrivateSpecific(__originalMethod?.Name ?? "") ? "｜★私信专属" : "") +
                  $"｜参数 {__args?.Length ?? 0} 个｜" + DescribeArguments(__args));

            // ★ 首次命中就写一次"执行副本判据"：它证明的不只是"补丁挂上了"，
            //   而是"游戏执行的这一份上确实有我们的补丁"——这正是上一轮缺的那块证据。
            if (count == 1) ReportExecutingCopy("探针首次命中 " + name, __originalMethod);
        }
        catch (Exception)
        {
            // 探针绝不抛回原版。
        }
    }

    /// <summary>方法退出。这里只记录，不解析返回值（避免碰到可能为 null 的对象）。</summary>
    public static void ExitPostfix(MethodBase __originalMethod)
    {
        try
        {
            Write($"退出 {Describe(__originalMethod)}");
        }
        catch (Exception)
        {
        }
    }

    /// <summary>抛异常时记下来（这能解释"方法进去了但什么都没发生"）。</summary>
    public static Exception? Finalizer(Exception? __exception, MethodBase __originalMethod)
    {
        try
        {
            if (__exception != null)
                Write($"★异常 {Describe(__originalMethod)}" +
                      (IsPrivateSpecific(__originalMethod?.Name ?? "") ? "｜★私信专属" : "") +
                      $"｜{__exception.GetType().Name}: {__exception.Message}");
        }
        catch (Exception)
        {
        }
        return __exception;
    }

    private static string Describe(MethodBase? method) =>
        method == null
            ? "?"
            : $"{method.DeclaringType?.FullName}.{method.Name}({string.Join(",",
                method.GetParameters().Select(p => p.ParameterType.Name))})";

    /// <summary>把实参里"能一眼看出身份"的东西写出来：字符串、集合大小、类型名。</summary>
    private static string DescribeArguments(object[]? args)
    {
        if (args == null || args.Length == 0) return "(无参数)";

        var parts = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            object? value = args[i];
            switch (value)
            {
                case null:
                    parts.Add($"[{i}]null");
                    break;
                case string text:
                    parts.Add($"[{i}]\"{(text.Length > 60 ? text[..60] + "…" : text)}\"");
                    break;
                case System.Collections.ICollection collection:
                    parts.Add($"[{i}]{value.GetType().Name}({collection.Count})");
                    break;
                default:
                    parts.Add($"[{i}]{value.GetType().Name}");
                    break;
            }
        }
        return string.Join(" ", parts);
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

    private static string AssemblyOf(MethodBase method)
    {
        try
        {
            return method.Module.Assembly.GetName().Name ?? "?";
        }
        catch (Exception)
        {
            return "?";
        }
    }
}
