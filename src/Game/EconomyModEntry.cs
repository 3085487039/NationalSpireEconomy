using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Inject;
using NationalSpireEconomy.Trade;

namespace NationalSpireEconomy.Game;

/// <summary>
/// 模组入口。
/// <para>
/// 原版模组加载器通过 <see cref="ModInitializerAttribute"/> 找到本方法并在启动时调用一次。
/// 生命周期契约（<b>顺序不可颠倒</b>）：
/// <list type="number">
/// <item><b>建立文件保底日志通道</b>（纯 <c>System.IO</c>，不依赖 Godot）——
/// 它必须比一切都早，否则 Godot 未就绪时整个模组会静默。</item>
/// <item>建立日志通道——后面任何一步失败都要能看见原因。</item>
/// <item>解析数据根目录——配置与存档都从这里挂出去。</item>
/// <item>加载并校验配置。<b>有 Error 就拒绝启用</b>，绝不带着坏配置继续跑。</item>
/// <item>建立影子存档（只创建目录，不读不写原版存档）。</item>
/// <item>注册 Harmony。</item>
/// <item>阶段 2：显式加载 NationalSpire，挂 <c>PrivateStreamParser</c> 前缀补丁并自证。</item>
/// <item>阶段 3：建立影子存档运行时（<see cref="Runtime"/>），挂 S 段注入与日期推进两个补丁并自证。</item>
/// </list>
/// </para>
/// <para>
/// <b>纪律</b>：只写独立 DLL + Harmony 补丁，不改原版源码；不碰原版存档；
/// 初始化失败一律降级为"未启用"，绝不把异常抛回加载器。
/// </para>
/// </summary>
[ModInitializer(nameof(Init))]
public static class EconomyModEntry
{
    /// <summary>Harmony 实例 id。带 <c>.economy</c> 后缀，避免与别的模组撞 id。</summary>
    public const string HarmonyId = "nationalspire.economy";

    /// <summary>模组 id，必须与 NationalSpireEconomy.json 的 id 一致。</summary>
    public const string ModId = "NationalSpireEconomy";

    /// <summary><c>user://</c> 下的数据目录名。与诊断目录同级。</summary>
    public const string DataDirectoryName = "national_spire_economy";

    /// <summary>配置目录名（模组目录下）。</summary>
    public const string ConfigDirectoryName = "config";

    private static ModState? _state;
    private static Harmony? _harmony;

    private static readonly object ReinforceGate = new();

    /// <summary>程序集加载监听是否已经装过（进程内只装一次）。</summary>
    private static bool _assemblyLoadHooked;

    /// <summary>补挂动作（由 <see cref="Initialize"/> 在第 12 步装好；未初始化时为 null）。</summary>
    private static Action<string>? _reinforce;

    /// <summary>上一次补挂时看到的原版<b>精确</b>副本数。</summary>
    private static int _knownCopyCount = -1;

    private static int _reinforcing;

    /// <summary>补挂跑过几次。</summary>
    public static int ReinforceRuns { get; private set; }

    /// <summary>落点警卫是否已装好（装好之后才会自动补挂）。</summary>
    public static bool ReinforcementArmed
    {
        get
        {
            lock (ReinforceGate) return _reinforce != null;
        }
    }

    /// <summary>最近一次补挂的结论（诊断用）。</summary>
    public static string LastReinforceReport { get; private set; } = "";

    /// <summary>当前模组状态。未初始化时为 null。</summary>
    public static ModState? State => _state;

    /// <summary>Harmony 实例（阶段 2/3 的补丁都挂在它下面）。</summary>
    public static Harmony? HarmonyInstance => _harmony;

    /// <summary>
    /// 影子存档的运行时<b>（进程内单例）</b>。
    /// <para>
    /// 之所以是静态的：Harmony 补丁是静态方法，没有别的注入口；而账本与"已载入的世界"
    /// 必须在两次补丁调用之间保持同一份，否则每发一条私信都要重读一遍上万人的 JSON。
    /// 补丁读取它，为 null 表示未启用，此时一个字节都不改原版。
    /// </para>
    /// </summary>
    public static ShadowEconomyRuntime? Runtime { get; private set; }

    /// <summary>
    /// 模组入口。原版加载器会调用一次。
    /// <b>本方法绝不抛异常</b>：初始化失败时降级为未启用状态，让游戏正常继续。
    /// </summary>
    public static void Init()
    {
        var log = new EconomyLogAdapter();

        // ── 第 -1 步（比文件通道还早）：监听"程序集被加载" ──
        // ★ 阶段 3 定案的关键补丁：真机实测游戏是在【我们初始化之后】才把 NationalSpire
        //   装进 IsolatedComponentLoadContext 的（ModManager 逐个模组加载并立刻初始化，
        //   我们排在它前面）。那一刻必须马上知道，否则补丁永远只挂在先出现的那份副本上。
        try
        {
            lock (ReinforceGate)
            {
                if (!_assemblyLoadHooked)
                {
                    _assemblyLoadHooked = true;
                    AppDomain.CurrentDomain.AssemblyLoad += OnGameAssemblyLoaded;
                }
            }
        }
        catch (Exception e)
        {
            log.Warn("probe", "程序集加载监听装配失败（不影响其它功能）：" + e.GetType().Name + ": " + e.Message);
        }

        // ── 第 0 步（比一切都早）：挂上文件保底通道 ──
        // 模组初始化时 Godot 原生运行时可能还没就绪，控制台通道与诊断文件通道都可能绑不上，
        // 那样整个模组在游戏里会一个字都出不来（实测事故）。文件通道不需要 Godot 原生函数，
        // 必须先于一切建立，后面任何一步失败才必定留痕。
        try
        {
            var sink = EconomyLogAdapter.CreateFileSink(ResolveDataRoot(log), AssemblyDirectory());
            log.AttachFileSink(sink, LogLevel.Info, "boot",
                $"模组入口被调用｜版本 {ModVersion()}｜程序集 {Assembly.GetExecutingAssembly().GetName().Version}" +
                $"｜Godot 原生就绪 {IsGodotNativeReady()}｜文件落点 {sink.Describe}");
        }
        catch (Exception e)
        {
            // 保底通道自己出问题也不能连累初始化。
            log.Warn("boot", "建立文件保底日志失败：" + e.GetType().Name + ": " + e.Message);
        }

        try
        {
            _state = Initialize(log);
        }
        catch (Exception e)
        {
            // 任何意外都不能把异常抛回原版加载器——那会让整个模组（甚至加载流程）崩掉。
            log.Error("init", "初始化抛出未预料的异常，已降级为未启用。", e);
            _state = ModState.Disabled(log, "unexpected", e.GetType().Name + ": " + e.Message);
        }

        log.Info("init", "NationalSpireEconomy " + ModVersion() + " → " + _state.Describe());
        if (!_state.Enabled)
            log.Warn("init", "经济系统未启用，所有经济功能保持关闭。请修正配置后重启游戏。");

        // 收尾必须落文件：这两个绑定结论本身就是诊断的一部分，
        // 而它们以前只能"写进日志"，恰恰是哑通道里最看不见的东西。
        log.Info("log", $"文件保底通道：{EconomyLogAdapter.FileSinkReport}");
        log.Info("log", $"诊断文件通道：{EconomyLogAdapter.LastBindingReport}");
        log.Info("log", $"控制台通道：{EconomyLogAdapter.LastPrintReport}");
    }

    /// <summary>
    /// <b>动钱的总开关（阶段 4 第二步）</b>：默认【关】。两条路任一打开即接线：
    /// ① 配置 <c>economy_config.json</c> 的 <c>trade.settlementWired = true</c>（推荐，Steam 启动也有效）；
    /// ② 环境变量 <c>NSE_SETTLEMENT=1</c>（从命令行启动游戏时方便）。
    /// <para>
    /// 为什么最终落到"配置"上：真机验证要走 Steam 启动，而 <b>Steam 启动拿不到我们临时设的环境变量</b>
    /// （直接跑 exe 又过不了 Steamworks 初始化——实测会弹"Steam初始化失败"）。
    /// 改配置 + 重启游戏最稳，而且开关状态每次启动都写进日志，可核对、可回退。
    /// </para>
    /// </summary>
    private static bool SettlementEnabled(ConfigLoadResult config, IEconomyLog log)
    {
        string? raw = Environment.GetEnvironmentVariable("NSE_SETTLEMENT");
        bool fromEnvironment = raw is "1" or "true" or "True" or "TRUE";
        bool fromConfig = config.Economy.Trade.SettlementWired;
        bool on = fromEnvironment || fromConfig;

        log.Info("init", on
            ? $"★结算已接线（{SettlementSwitchSource(fromEnvironment, fromConfig)}）：" +
              "双方确认后会【真的动钱】并写 audit jsonl。要恢复「不动钱」，把这一项关掉并重启游戏。"
            : "结算未接线（默认）：双方确认后停在「双方已确认，待结算」，一分钱都不会动。" +
              "要验结算，把 config/economy_config.json 里 trade.settlementWired 设为 true 后重启游戏" +
              "（或从命令行以 NSE_SETTLEMENT=1 启动）。");
        return on;
    }

    private static string SettlementSwitchSource(bool fromEnvironment, bool fromConfig)
    {
        if (fromEnvironment && fromConfig) return "环境变量 NSE_SETTLEMENT + 配置 trade.settlementWired";
        return fromEnvironment ? "环境变量 NSE_SETTLEMENT" : "配置 trade.settlementWired";
    }

    /// <summary>
    /// 真正做事的初始化。拆成独立方法，便于单测直接注入日志与路径、绕开 Godot。
    /// </summary>
    /// <param name="log">日志出口。</param>
    /// <param name="dataRoot">数据根目录。null 表示运行时按 <c>user://</c> 解析。</param>
    /// <param name="configRoot">配置目录。null 表示运行时按模组目录解析。</param>
    public static ModState Initialize(IEconomyLog log, string? dataRoot = null, string? configRoot = null)
    {
        ArgumentNullException.ThrowIfNull(log);

        // 先清掉上一次的运行时：初始化可能被反复调用（测试、热重载），
        // 如果本次在中途失败，绝不能把上一份账本留给补丁继续用。
        Runtime = null;

        // ── 第 1 步：解析数据根目录 ──
        string? resolvedData = dataRoot ?? ResolveDataRoot(log);
        if (string.IsNullOrWhiteSpace(resolvedData))
            return ModState.Disabled(log, "paths", "无法解析数据根目录（user:// 未就绪，且无回退路径）。");

        // ── 第 2 步：解析配置目录 ──
        string? resolvedConfig = configRoot ?? ResolveConfigRoot(log);
        if (string.IsNullOrWhiteSpace(resolvedConfig))
            return ModState.Disabled(log, "paths", "无法解析配置目录。", dataRoot: resolvedData);

        // ── 第 3 步：加载并校验配置。有 Error 就拒绝启用 ──
        var loader = new ConfigLoader(resolvedConfig, log);
        ConfigLoadResult config = loader.Load();

        if (config.HasErrors)
        {
            string detail = string.Join(" | ", config.Errors.Select(e => e.ToString()));
            log.Error("init", $"配置存在 {config.Errors.Count()} 个错误，拒绝启用经济系统：{detail}");
            return ModState.Disabled(log, "config", detail, config, resolvedData);
        }

        if (!Directory.Exists(resolvedConfig))
        {
            // ConfigLoader 用的是内置默认值。不报错但必须警告：玩家多半是把包解压漏了。
            log.Warn("init", $"配置目录不存在，正在使用内置默认值：{resolvedConfig}");
        }

        // ── 第 4 步：建立影子存档（只创建目录） ──
        var store = new EconomyStore(resolvedData, null, log);
        try
        {
            Directory.CreateDirectory(store.WorldsDirectory);
        }
        catch (Exception e)
        {
            log.Error("init", $"无法创建影子存档目录 {store.WorldsDirectory}", e);
            return ModState.Disabled(log, "store", e.Message, config, resolvedData);
        }

        // ── 第 5 步：注册 Harmony ──
        if (!TryInstallHarmony(log, out string harmonyDetail))
            return ModState.Disabled(log, "harmony", harmonyDetail, config, resolvedData);

        // ── 旁路取证探针的【开关】：文件通道先建好，但装配必须等第 6 步之后 ──
        // ★ 本轮真机实测教训：原先在这里就调用 PathProbe.Install，
        //   而 NationalSpire 要到【第 6 步】才被显式加载——探针扫描早了一步，
        //   于是真机日志里出现"命中类型 0 个／挂上 0 个候选方法／补丁表为空"，
        //   看起来像"多副本隔离"，实际只是"扫的时候游戏程序集还没加载"。
        //   所以这里只开通道，装配挪到第 6 步之后。
        try
        {
            PathProbe.Enable(Path.Combine(resolvedData, "probe"));
            PathProbe.Write("程序集盘点（第 5 步，NationalSpire 尚未加载）：" + AssemblyInventory.Report());
        }
        catch (Exception e)
        {
            log.Warn("probe", "旁路取证通道开启失败（不影响其它功能）：" + e.GetType().Name + ": " + e.Message);
        }

        // ── 单轨原则（甲方拍板）：Compose 注入补丁退场 ──
        // 初始化可能被反复调用（测试、热重载），这里先把它重置成"未装配"，
        // 保证任何一次初始化之后都不存在"两处注入点"的可能。
        PrivateMessagePatch.MarkRetired("方案 A（RequestPrivateAsync）单轨接管，见 HANDOFF-PHASE-3-PROBE.md §4.2");
        PrivateRequestInjectionPatch.MarkRetired("每次初始化都要重新装配");
        PrivateIdentityTrackPatch.MarkRetired("每次初始化都要重新装配");

        // ── 第 6 步：确保 NationalSpire 已加载，再装配补丁 ──
        // 模组加载器按顺序初始化，我们排在 NationalSpire 前面，
        // 所以这里必须【显式】把它加载进来，否则类型一定找不到（阶段 2 实测）。
        var patches = new List<string>();
        string? modsDir = TryModsDirectory();
        string? dataDir = TryDataDirectory();

        PatchDiagnostics.Run(_harmony, log, ConsoleSinkOf(log), modsDir, dataDir);
        if (PrivateStreamPatch.Applied) patches.AddRange(PrivateStreamPatch.PatchedMethods);
        // 显示出口补丁是【另一个方法】，不并进上面的清单会让启动行只说一半实话 ——
        // 而"启动行没提它"正是上一轮没人发现标记泄漏的原因之一。
        if (PrivateReplyPatch.Applied) patches.Add("PrivateTurn.set_Reply");
        // 并行可见流（LeadingReasoningStream）是同日第二轮才发现的第二个流式出口；
        // 它也必须在启动行里出现，否则"日志看起来都装上了但界面还有标记"会再发生一次。
        if (LeadingStreamPatch.Applied) patches.AddRange(LeadingStreamPatch.PatchedMethods);

        // ── 第 6.5 步：现在才装配旁路取证探针 ──
        // 必须在这之后：第 6 步刚把 NationalSpire 显式加载进来，
        // 只有此刻进程里才存在那份"游戏真正会执行"的程序集。
        try
        {
            PathProbe.Write("程序集盘点（第 6 步之后，NationalSpire 应已加载）：" + AssemblyInventory.Report());
            int probed = PathProbe.Install(_harmony);
            log.Info("probe",
                $"旁路取证：已挂 {probed} 个候选方法｜原版程序集 {AssemblyInventory.GameAssemblies().Count} 份" +
                $"｜日志 {PathProbe.LogPath}");
        }
        catch (Exception e)
        {
            log.Warn("probe", "旁路取证探针装配失败（不影响其它功能）：" + e.GetType().Name + ": " + e.Message);
        }

        // ── 第 6.6 步：抓栈监听（回答"私信请求是谁发起的"） ──
        // 真实私信没有触发我们挂在 ProcessPrivateAsync / RequestPrivateAsync 上的探针，
        // 但游戏自己的诊断确实写了 private.request。那就监听那条诊断的写入：
        // 原版一写 private.request，立刻把调用栈打进旁路日志——谁发起的，栈里说得明明白白。
        try
        {
            PathProbe.InstallDiagnosticsListener(_harmony, log);
            PathProbe.InstallFileSinkListeners(_harmony, log);
        }
        catch (Exception e)
        {
            log.Warn("probe", "抓栈/文件写入监听装配失败（不影响其它功能）：" + e.GetType().Name);
        }

        // ── 第 7 步：影子存档的生命周期 + S 段注入（阶段 3） ──
        // 顺序有讲究：先建 Runtime，再挂补丁。补丁一旦挂上就可能被游戏调用，
        // 那时 Runtime 必须已经在位，否则第一次注入会因为"没有数据源"而空转。
        //
        // 阶段 4 第二步新增两件：① 审计写入器（audit/<WorldId>.jsonl）；② 动钱开关（NSE_SETTLEMENT）。
        var audit = new EconomyAudit(resolvedData, null, log) { Enabled = config.Economy.Trade.AuditEnabled };
        TradeConfirmationGate.WireSettlement(SettlementEnabled(config, log));

        var runtime = new ShadowEconomyRuntime(store, config, log, ConsoleSinkOf(log), audit);
        Runtime = runtime;
        patches.AddRange(InjectDiagnostics.Run(_harmony, log, ConsoleSinkOf(log), config, resolvedData));

        // ── 第 8 步：挂载点体检（方案 D，只观测不改数据） ──
        // 放在最后：它要观测的是"注入补丁是否真的被调用"，所以必须在注入补丁之后装。
        patches.AddRange(HookProbe.Run(_harmony, log, ConsoleSinkOf(log), config.Economy.Injection.ProbeEnabled));

        // ── 第 9 步：把"私信链路上到底挂了哪些补丁、谁挂的"写进旁路日志 ──
        // 到这里注入（第 7 步）与体检（第 8 步）都已装配完毕，这张表才是最终态：
        // 它能一眼看出方案 A 的前缀是否真的在，以及有没有别的模组的补丁在同一批方法上。
        try
        {
            PathProbe.DumpPatches(_harmony, new[]
            {
                "RequestPrivateAsync", "ProcessPrivateAsync", "EnterPrivateQueue",
                "Context", "Compose", "Feed", "SendAsync", "SendTransportAsync"
            });
        }
        catch (Exception e)
        {
            log.Warn("probe", "补丁表最终转储失败（不影响其它功能）：" + e.GetType().Name);
        }

        // ── 第 10 步：活性自检（只在真机跑） ──
        // 它回答本轮最关键的问题："探针挂上了"是否等于"探针会被执行"。
        // 如果自检报告"没被执行"，那么真机上再发私信也不会有痕迹——必须换机制，而不是换挂载点。
        try
        {
            PathProbe.RunLiveSelfCheck();
            if (PathProbe.LiveSelfCheckPassed == false)
                log.Warn("probe", "活性自检未通过：补丁在本进程内不会被调用，详见 path-probe.log");
        }
        catch (Exception e)
        {
            log.Warn("probe", "活性自检抛异常（不影响其它功能）：" + e.GetType().Name);
        }

        // 顺便把诊断分类计数写一次（基线段：此刻应只有我们自己的分类）。
        PathProbe.DumpCategoryCounts("启动完成时");

        // ── 第 11 步：补丁落点体检 ──
        // 真机事故：同一个 DLL 被两个 ALC 各加载一次时，FullName 与 Location 完全相同，
        // 我们按名字去重后只挂到了其中一份，而游戏执行的是另一份（症状=挂载成功却永不命中）。
        // 这份体检直接列出"每份副本上我们的补丁命中几个方法"，一眼看出有没有挂错副本。
        try
        {
            PathProbe.DumpPatchLocationAudit(new[]
            {
                "RequestPrivateAsync", "ProcessPrivateAsync", "EnterPrivateQueue",
                "Context", "Compose", "Feed", "Record", "Configure"
            });
        }
        catch (Exception e)
        {
            log.Warn("probe", "补丁落点体检失败（不影响其它功能）：" + e.GetType().Name);
        }

        // ── 第 11.5 步：把"发现 N 份、已挂 M 份"写成一行 ──
        // 这一行是甲方真机判据的第一行：副本数、每份的 ALC/路径、我们的补丁覆盖了几份。
        try
        {
            var copies = AssemblyInventory.GameAssemblies();
            int covered = PathProbe.LastPatchedCopyCount;
            log.Info("probe", $"[Economy-Locator] 原版副本 {copies.Count} 份" +
                              (copies.Count > 0
                                  ? "（" + string.Join(" ／ ", copies.Select(AssemblyInventory.Describe)) + "）"
                                  : "（★一份都没有）") +
                              $"｜我们的补丁覆盖 {covered}／{copies.Count} 份｜带探针的方法 {PathProbe.PatchedCount} 个" +
                              $"｜加载入口 ALC {NationalSpireLocator.LoadedContext}");
            PathProbe.Write("[Economy-Locator] 原版副本 " + copies.Count + " 份｜补丁覆盖 " + covered + " 份｜" +
                            "带探针方法 " + PathProbe.PatchedCount + " 个｜入口 ALC " + NationalSpireLocator.LoadedContext);
        }
        catch (Exception e)
        {
            log.Warn("probe", "落点摘要写出失败（不影响其它功能）：" + e.GetType().Name);
        }

        // ── 第 12 步：装"落点警卫" ──
        // 游戏可能在我们之后才加载 NationalSpire（真机实测就是），那时必须自动补挂。
        // 触发源有两个，都是【已被真机证明会被游戏流量触发】的：
        //   ① AppDomain.AssemblyLoad（游戏模组加载器装载该程序集时）
        //   ② 原版写 national-spire.log 的那一刻（见 PathProbe.FileSinkPrefix）
        // 补挂本身幂等（每个补丁都按「目标方法 + 具体补丁方法」判定），且绝不抛异常。
        try
        {
            var capturedLog = log;
            var capturedConsole = ConsoleSinkOf(log);
            _reinforce = reason => ReinforcePatchesCore(reason, capturedLog, capturedConsole);

            lock (ReinforceGate)
            {
                _knownCopyCount = AssemblyInventory.GameAssemblies().Count;
                ReinforceRuns = 0;
            }

            log.Info("probe", $"[Economy-Reinforce] 落点警卫已装：当前原版副本 {_knownCopyCount} 份，" +
                              "之后每出现一个新副本都会自动补挂全部补丁");
        }
        catch (Exception e)
        {
            log.Warn("probe", "落点警卫装配失败（不影响其它功能）：" + e.GetType().Name);
        }

        // 程序集加载进来之后，诊断日志的钩子通常就能接上了，补一次绑定。
        if (log is EconomyLogAdapter adapter && adapter.Rebind())
            log.Debug("init", "诊断日志下沉通道已补上绑定。");

        log.Info("init", $"配置校验通过（警告 {config.Warnings.Count()} 条），Harmony 已登记：{harmonyDetail}");
        return ModState.Ready(config, store, log, resolvedData, patches);
    }

    /// <summary>从日志出口里挖出控制台通道。不是适配器实现时返回 null。</summary>
    private static Action<string, bool>? ConsoleSinkOf(IEconomyLog log) =>
        (log as EconomyLogAdapter)?.ConsoleSink;

    // ─────────────────────────────────────────────────────────────────
    // 落点警卫：新副本出现时自动补挂（阶段 3 卡点的正面修复）
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 程序集加载事件的处理器。
    /// <para>
    /// <b>为什么这条通道是关键</b>：真机实测 ModManager 把每个模组 DLL 装进
    /// <c>IsolatedComponentLoadContext</c> 后<b>立刻</b>调用它的初始化方法，
    /// 而 NationalSpire 排在我们后面——也就是说，我们初始化时进程里根本没有
    /// "游戏会执行的那一份"。等它出现时（本事件）立刻补挂，才能保证落点正确。
    /// </para>
    /// </summary>
    private static void OnGameAssemblyLoaded(object? sender, AssemblyLoadEventArgs e)
    {
        try
        {
            if (!AssemblyInventory.IsGameAssembly(e.LoadedAssembly)) return;

            PathProbe.Write("程序集加载事件：" + AssemblyInventory.Describe(e.LoadedAssembly));
            ReinforceIfNewCopies("检测到 NationalSpire 程序集被加载进 " +
                                 AssemblyInventory.ContextOf(e.LoadedAssembly));
        }
        catch (Exception)
        {
            // 加载事件的处理器绝不抛回加载器。
        }
    }

    /// <summary>
    /// 只有在"原版副本数变了"时才补挂。可以放心地在热路径上反复调用
    /// （例如每次原版写诊断文件时）——副本数没变就只是一次程序集枚举。
    /// </summary>
    public static void ReinforceIfNewCopies(string reason)
    {
        try
        {
            if (_reinforce == null) return;   // 还没初始化完，补挂清单还没装好

            int copies = AssemblyInventory.GameAssemblies().Count;
            if (copies == _knownCopyCount) return;

            ReinforcePatches(reason + $"（副本数 {_knownCopyCount} → {copies}）");
        }
        catch (Exception)
        {
            // 判断本身出问题也不能影响游戏。
        }
    }

    /// <summary>
    /// 强制把所有补丁在所有原版副本上补齐（不判断副本数）。
    /// <b>可重入保护 + 全程 try/catch</b>：它可能从探针内部（文件写入层）被调用。
    /// </summary>
    public static void ReinforcePatches(string reason)
    {
        var install = _reinforce;
        if (install == null) return;

        if (Interlocked.Exchange(ref _reinforcing, 1) == 1) return;   // 防重入（补挂会写日志 → 又触发补挂判断）
        try
        {
            lock (ReinforceGate) ReinforceRuns++;
            install(reason);
        }
        catch (Exception e)
        {
            LastReinforceReport = "补挂抛异常：" + e.GetType().Name + ": " + e.Message;
        }
        finally
        {
            try
            {
                _knownCopyCount = AssemblyInventory.GameAssemblies().Count;
            }
            catch (Exception)
            {
                // 拿不到副本数就把水位线设成 -1，下次还会再试一遍（宁可多试）。
                _knownCopyCount = -1;
            }
            Interlocked.Exchange(ref _reinforcing, 0);
        }
    }

    /// <summary>
    /// 补挂的实际动作：在<b>每一份</b>原版副本上把我们的补丁补齐。
    /// <para>
    /// <b>刻意不跑任何自证</b>：自证会喂探针文本、写 selfcheck 账本，
    /// 补挂只该"把缺的补丁挂上"，不该产生业务副作用。
    /// 每个补丁自身都按「目标方法 + 具体补丁方法」幂等（<see cref="PathProbe.HasPatch"/>），
    /// 所以重复调用安全。
    /// </para>
    /// </summary>
    private static void ReinforcePatchesCore(string reason, IEconomyLog log, Action<string, bool>? console)
    {
        var harmony = _harmony;
        if (harmony == null)
        {
            LastReinforceReport = "Harmony 未建立，补挂跳过";
            return;
        }

        var copies = AssemblyInventory.GameAssemblies();
        PathProbe.Write($"===== 补挂补丁（{reason}）｜原版副本 {copies.Count} 份 =====");
        log.Info("inject", $"[Economy-Reinforce] {reason}：发现原版副本 {copies.Count} 份，逐份补齐");

        // ① 流补丁（[Trade:] 标记抽取）+ 显示出口补丁（渲染前剥标记）——按副本逐个挂。
        //    两个必须一起补：只补流补丁会出现"标记被识别、单也落了，但玩家界面上照样看得见"
        //    （2026-10-12 真机实测，见 PrivateReplyPatch 的说明）。
        foreach (var copy in copies)
        {
            try
            {
                var type = copy.GetType(PrivateStreamPatch.TargetTypeName, throwOnError: false);
                if (type == null)
                {
                    PathProbe.Write($"补挂：{AssemblyInventory.Describe(copy)} 里没有 {PrivateStreamPatch.TargetTypeName}");
                }
                else
                {
                    var feed = AccessTools.Method(type, PrivateStreamPatch.FeedMethodName, [typeof(string)]);
                    var finish = AccessTools.Method(type, PrivateStreamPatch.FinishMethodName, Type.EmptyTypes);
                    PrivateStreamPatch.ApplyTo(harmony, type, feed, finish, log);
                    PathProbe.Write($"补挂：流补丁 {type.FullName}｜ALC {AssemblyInventory.ContextOf(copy)}｜" +
                                    PrivateStreamPatch.Detail);
                }

                var turn = copy.GetType(PrivateReplyPatch.TargetTypeName, throwOnError: false);
                if (turn == null)
                {
                    PathProbe.Write($"补挂：{AssemblyInventory.Describe(copy)} 里没有 {PrivateReplyPatch.TargetTypeName}");
                }
                else
                {
                    PrivateReplyPatch.ApplyTo(harmony, turn, log);
                    PathProbe.Write($"补挂：显示出口补丁 {turn.FullName}｜ALC {AssemblyInventory.ContextOf(copy)}｜" +
                                    PrivateReplyPatch.Detail);
                }

                // 并行可见流：与上面两个出口一样按副本逐个补齐，任何一条漏挂都会让标记重新出现。
                var leading = copy.GetType(LeadingStreamPatch.TargetTypeName, throwOnError: false);
                if (leading == null)
                {
                    PathProbe.Write($"补挂：{AssemblyInventory.Describe(copy)} 里没有 {LeadingStreamPatch.TargetTypeName}");
                }
                else
                {
                    LeadingStreamPatch.ApplyTo(harmony, leading, log);
                    PathProbe.Write($"补挂：并行可见流补丁 {leading.FullName}｜ALC {AssemblyInventory.ContextOf(copy)}｜" +
                                    LeadingStreamPatch.Detail);
                }
            }
            catch (Exception e)
            {
                PathProbe.Write($"补挂补丁失败：{e.GetType().Name}: {e.Message}");
            }
        }

        // ② 取证探针 + 抓栈监听（都可重复调用，按方法幂等）。
        try
        {
            PathProbe.Install(harmony);
        }
        catch (Exception e)
        {
            PathProbe.Write("补挂观测探针失败：" + e.GetType().Name + ": " + e.Message);
        }

        try
        {
            PathProbe.InstallDiagnosticsListener(harmony, log);
        }
        catch (Exception e)
        {
            PathProbe.Write("补挂抓栈监听失败：" + e.GetType().Name + ": " + e.Message);
        }

        // ③ 方案 A 的两块：（内部会遍历全部副本，且各自带精确幂等守卫）
        try
        {
            PrivateIdentityTrackPatch.ApplyTo(harmony, log);
            PathProbe.Write("补挂：身份来源 " + PrivateIdentityTrackPatch.Detail);
        }
        catch (Exception e)
        {
            PathProbe.Write("补挂身份来源失败：" + e.GetType().Name + ": " + e.Message);
        }

        try
        {
            PrivateRequestInjectionPatch.ApplyTo(harmony, log, console);
            PathProbe.Write("补挂：注入前缀 " + PrivateRequestInjectionPatch.Detail);
        }
        catch (Exception e)
        {
            PathProbe.Write("补挂注入前缀失败：" + e.GetType().Name + ": " + e.Message);
        }

        // ④ 日期推进（影子存档的生命周期）——按副本逐个挂。
        foreach (var copy in copies)
        {
            try
            {
                var engine = copy.GetType(CareerDayPatch.TargetTypeName, throwOnError: false);
                if (engine == null) continue;

                var advance = CareerDayPatch.FindAdvanceMethod(engine);
                if (advance == null)
                {
                    PathProbe.Write($"补挂：{AssemblyInventory.Describe(copy)} 里找不到 {CareerDayPatch.PrimaryMethodName}" +
                                    "（该副本上的日期推进只能靠活动补丁，见日志说明）");
                    continue;
                }

                CareerDayPatch.ApplyTo(harmony, engine, advance, log);
                PathProbe.Write($"补挂：日期推进 {CareerDayPatch.Detail}｜ALC {AssemblyInventory.ContextOf(copy)}");
            }
            catch (Exception e)
            {
                PathProbe.Write($"补挂日期推进失败：{e.GetType().Name}: {e.Message}");
            }
        }

        // ⑤ 诊断日志下沉通道重绑（原来那条可能绑在了旧副本上、或压根没绑上）。
        try
        {
            if (log is EconomyLogAdapter adapter && adapter.Rebind())
                log.Debug("init", "补挂之后重新绑定诊断日志通道：成功。");
        }
        catch (Exception e)
        {
            PathProbe.Write("补挂后重绑诊断通道失败：" + e.GetType().Name + ": " + e.Message);
        }

        // ⑥ 落点体检（第一判据）：补挂完必须能自证"多份副本都已挂上"。
        try
        {
            PathProbe.DumpPatchLocationAudit(new[]
            {
                "RequestPrivateAsync", "ProcessPrivateAsync", "EnterPrivateQueue",
                "Context", "Compose", "Feed", "Record", "Configure"
            });
        }
        catch (Exception e)
        {
            PathProbe.Write("补挂后落点体检失败：" + e.GetType().Name + ": " + e.Message);
        }

        LastReinforceReport = $"发现副本 {copies.Count} 份｜补丁覆盖 {PathProbe.LastPatchedCopyCount} 份" +
                              $"｜方案 A 注入前缀 {PrivateRequestInjectionPatch.PatchedMethods.Count} 份" +
                              $"｜带探针方法 {PathProbe.PatchedCount} 个";
        PathProbe.Write("补挂完成：" + LastReinforceReport);
        log.Info("inject", "[Economy-Reinforce] 补挂完成：" + LastReinforceReport);
    }

    /// <summary>mods 目录（补丁目标的回退查找用）。拿不到返回 null。</summary>
    private static string? TryModsDirectory()
    {
        try
        {
            string? assemblyDir = AssemblyDirectory();
            if (!string.IsNullOrWhiteSpace(assemblyDir))
            {
                // 模组装配在 mods\<ModId>\，上一级就是 mods。
                string? parent = Path.GetDirectoryName(assemblyDir);
                if (!string.IsNullOrWhiteSpace(parent)) return parent;
            }
        }
        catch (Exception)
        {
            // 拿不到就交给 Locator 的其他候选路径。
        }
        return null;
    }

    /// <summary>游戏程序集目录（补丁目标的回退查找用）。拿不到返回 null。</summary>
    private static string? TryDataDirectory()
    {
        try
        {
            // 游戏自己的程序集（sts2）所在目录就是 data_sts2_*，
            // 某些安装形态下 NationalSpire.dll 也可能被放在那里。
            var sts2 = AssemblyInventory.AllAssemblies()
                .FirstOrDefault(a => a.GetName().Name is "sts2");
            if (sts2?.Location is { Length: > 0 } location) return Path.GetDirectoryName(location);
        }
        catch (Exception)
        {
            // 同上。
        }
        return null;
    }

    /// <summary>
    /// 建立 Harmony 实例并装配补丁。
    /// </summary>
    private static bool TryInstallHarmony(IEconomyLog log, out string detail)
    {
        try
        {
            _harmony = new Harmony(HarmonyId);
            detail = HarmonyId;
            return true;
        }
        catch (Exception e)
        {
            log.Error("init", "创建 Harmony 实例失败", e);
            detail = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }

    /// <summary>
    /// 数据根目录。运行时是 <c>user://national_spire_economy</c>，
    /// 与 NationalSpire 自己的 <c>user://national_spire_diagnostics</c> 同级。
    /// <para>
    /// 放在 <c>user://</c> 而不是模组目录，是因为模组目录在 Steam 安装路径下、
    /// 可能没有写权限；而 <c>user://</c> 一定可写、且不随模组更新被覆盖。
    /// </para>
    /// <para>
    /// Godot 不可用时的回退是 <c>%APPDATA%\SlayTheSpire2\national_spire_economy</c>——
    /// 那正是原版 <c>user://</c> 在 Windows 上的实际落点（见 §2.1 的路径表），
    /// 所以回退之后行为与在游戏内一致。
    /// </para>
    /// </summary>
    public static string? ResolveDataRoot(IEconomyLog? log = null)
    {
        string? user = Globalize("user://" + DataDirectoryName);
        if (!string.IsNullOrWhiteSpace(user)) return user;

        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.UserProfile
                 })
        {
            try
            {
                string basePath = Environment.GetFolderPath(folder);
                if (string.IsNullOrWhiteSpace(basePath)) continue;

                log?.Warn("paths", $"user:// 不可用，回退到 {folder}。");
                return Path.Combine(basePath, "SlayTheSpire2", DataDirectoryName);
            }
            catch (Exception e)
            {
                log?.Warn("paths", $"解析 {folder} 失败：" + e.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// 配置目录：模组自己目录下的 <c>config/</c>。
    /// 装配位置是 <c>mods\NationalSpireEconomy\config\</c>，DLL 与配置文件在一起。
    /// </summary>
    public static string? ResolveConfigRoot(IEconomyLog? log = null)
    {
        // 优先用 DLL 自己的位置——这样无论模组被装到哪个盘都能找到配置。
        string? assemblyDir = AssemblyDirectory();
        if (!string.IsNullOrWhiteSpace(assemblyDir))
            return Path.Combine(assemblyDir, ConfigDirectoryName);

        // 退路：从原版 ModManager 已经载入的模组清单里找我们自己的 path。
        try
        {
            var mod = ModManager.Mods.FirstOrDefault(m =>
                string.Equals(m.manifest?.id, ModId, StringComparison.Ordinal));
            if (mod != null && !string.IsNullOrWhiteSpace(mod.path))
                return Path.Combine(mod.path, ConfigDirectoryName);
        }
        catch (Exception e)
        {
            log?.Warn("paths", "从 ModManager 解析模组目录失败：" + e.Message);
        }

        return null;
    }

    /// <summary>本程序集所在目录。Godot 动态加载时它指向模组目录。</summary>
    public static string? AssemblyDirectory()
    {
        try
        {
            string? location = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrWhiteSpace(location)) return null;
            return Path.GetDirectoryName(location);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 把 Godot 的虚拟路径转成真实路径。
    /// <para>
    /// <b>这里有一个会让进程直接崩掉的坑，务必看懂再改</b>：
    /// <c>Godot.ProjectSettings</c> 的静态构造函数会去调 Godot 原生函数
    /// （<c>godotsharp_string_new_with_utf16_chars</c>）。在 Godot 之外调用它时会触发
    /// <c>AccessViolationException</c>（实测退出码 <c>0xC0000005</c>）——
    /// 那是<b>损坏状态异常</b>，<c>catch (Exception)</c> <b>拦不住</b>，整个进程当场死。
    /// </para>
    /// <para>
    /// 所以这里做三道防护：
    /// <list type="number">
    /// <item><b>GodotSharp 没加载就直接放弃</b>——读 AppDomain 的程序集清单不碰任何原生函数。</item>
    /// <item><b>再用一个"不触发静态构造函数"的探针确认原生运行时真的就位</b>
    /// （见 <see cref="IsGodotNativeReady"/>）。光看程序集是不够的：
    /// 别的库（例如 Sentry 的 Godot 扩展）也可能把 GodotSharp 拖进进程，
    /// 那时程序集在、原生函数却还是空的，正好踩中这个坑。</item>
    /// <item><b>只用一次</b>，失败就记进 <see cref="_globalizeFailed"/>，之后永不重试。</item>
    /// </list>
    /// </para>
    /// </summary>
    private static string? Globalize(string virtualPath)
    {
        if (_globalizeFailed) return null;
        if (!IsGodotNativeReady()) return null;

        try
        {
            _globalizeMethod ??= Type.GetType("Godot.ProjectSettings, GodotSharp", throwOnError: false)
                ?.GetMethod("GlobalizePath", BindingFlags.Public | BindingFlags.Static,
                    null, [typeof(string)], null);

            if (_globalizeMethod == null)
            {
                _globalizeFailed = true;
                return null;
            }

            if (_globalizeMethod.Invoke(null, [virtualPath]) is string resolved && resolved.Length > 0 &&
                !resolved.StartsWith("user://", StringComparison.Ordinal))
                return resolved;
        }
        catch (Exception)
        {
            // 走到这里说明异常是可捕获的。记下来不再重试，交给调用方的回退逻辑。
            _globalizeFailed = true;
        }

        return null;
    }

    private static MethodInfo? _globalizeMethod;
    private static bool _globalizeFailed;

    /// <summary>
    /// Godot 的原生运行时是否已经就位。
    /// <para>
    /// 判据是 GodotSharp 的 <c>NativeFuncs</c> 里那个委托字段<b>非空</b>。
    /// 读一个字段是绝对安全的——<b>不</b>会像访问 <c>Godot.ProjectSettings</c> 那样
    /// 触发静态构造函数、进而调到还没绑定的原生函数。
    /// 原生运行时没绑定时那些委托全是 null，于是这里返回 false，我们就不会去踩地雷。
    /// </para>
    /// <para>
    /// 注意必须先确认 GodotSharp 已加载：否则 <c>Type.GetType</c> 会把程序集加载进来，
    /// 而加载一个引用了未绑定原生函数的程序集并不是我们想主动做的事。
    /// </para>
    /// <para>
    /// <b>本方法对日志适配器也是公开的</b>：<c>GD.Print</c> / <c>GD.PushError</c> 在没有原生运行时的
    /// 进程里调用同样是进程级崩溃（实测过 <c>0xC0000005</c>），所以绑定控制台通道前必须先问这一句。
    /// </para>
    /// </summary>
    public static bool IsGodotNativeReady()
    {
        if (!IsGodotAssemblyLoaded()) return false;

        try
        {
            var nativeFuncs = Type.GetType("Godot.NativeInterop.NativeFuncs, GodotSharp", throwOnError: false);
            var field = nativeFuncs?.GetField("godotsharp_string_new_with_utf16_chars",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);

            // 字段不存在（Godot 改了内部命名）时保守地返回 false：
            // 宁可退回 %APPDATA% 也不用一次进程级崩溃去赌。
            return field?.GetValue(null) != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 当前进程里是否已经加载了 GodotSharp。
    /// <para>
    /// 这是"我们是否跑在 Godot 里"的<b>安全</b>判据：读 AppDomain 里已加载的程序集清单
    /// 不碰任何原生函数。反之，直接访问 <c>Godot.ProjectSettings</c> 的成员
    /// 会触发它的静态构造函数，在 Godot 之外就是一次进程级崩溃。
    /// </para>
    /// </summary>
    private static bool IsGodotAssemblyLoaded()
    {
        foreach (var assembly in AssemblyInventory.AllAssemblies())
            if (assembly.GetName().Name is "GodotSharp") return true;
        return false;
    }

    /// <summary>读清单里的版本号，仅用于日志。</summary>
    private static string ModVersion()
    {
        try
        {
            var mod = ModManager.Mods.FirstOrDefault(m =>
                string.Equals(m.manifest?.id, ModId, StringComparison.Ordinal));
            if (mod?.manifest?.version is { Length: > 0 } version) return "v" + version;
        }
        catch (Exception)
        {
            // 日志用的信息，拿不到就不写版本号。
        }
        return "v?";
    }
}
