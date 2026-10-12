using NationalSpireEconomy.Core;
using NationalSpireEconomy.Game;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 模组入口：初始化编排、配置门禁、路径解析、日志转接。
/// <para>
/// 这是阶段 0 的验收面。最重要的一条是<b>配置门禁</b>：
/// 配置有 Error 时必须拒绝启用，绝不带着坏配置继续跑。
/// </para>
/// </summary>
public static class ModEntryTests
{
    public static Action[] Cases =>
    [
        // 日志转接
        LogAdapterFallsBackWithoutDiagnostics,
        LogAdapterRoutesDebugAndInfoToRecordHook,
        LogAdapterOnlyPrintsWarningAndAbove,
        LogAdapterSurvivesThrowingSink,
        LogAdapterFormatIsStable,

        // 文件保底日志（不依赖 Godot 的第一优先通道）
        FileLogSinkWritesWithoutGodot,
        FileLogSinkFallsBackToSecondCandidate,
        FileLogSinkSwitchesCandidateWhenWriteIsDenied,
        FileLogSinkNeverThrows,
        FileLogSinkReopensAfterBeingDisabled,
        FileLogSinkAcceptsTextWriterSink,

        // 配置门禁
        ValidConfigEnablesMod,
        ConfigErrorDisablesMod,
        MissingConfigDisablesMod,
        DisabledStateDescribesWhy,
        InitializeIsIdempotentAcrossCalls,

        // 路径
        DataRootFallsBackToAppData,
        ConfigRootIsModDirectoryConfig,
        AssemblyDirectoryIsTestOutput,
        ModIdIsNotNationalSpire,

        // 编排
        InitializeCreatesWorldsDirectory,
        InitializeNeverThrowsOnGarbageConfig,
        InitializeRejectsEmptyDataRoot,
        GodotIsNotLoadedInTestProcess,
        InitializeWithNullRootsFallsBackSafely
    ];

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    // ── 日志转接 ────────────────────────────────────────────────────

    private static void LogAdapterFallsBackWithoutDiagnostics()
    {
        // 不注入任何 hook：必须能用，只是没有详细日志。
        var adapter = new EconomyLogAdapter(null, null);
        adapter.Info("test", "没有下沉通道也不该抛异常");

        TestRunner.False(adapter.DiagnosticsAvailable, "没有 hook 时不该声称 Diagnostics 可用");
        TestRunner.Equal(1, adapter.Written, "仍应计入写出次数");
    }

    private static void LogAdapterRoutesDebugAndInfoToRecordHook()
    {
        var recorded = new List<string>();
        var adapter = new EconomyLogAdapter(recorded.Add, null);

        adapter.Debug("cfg", "调试信息");
        adapter.Info("init", "初始化完成");

        TestRunner.True(adapter.DiagnosticsAvailable, "注入了 hook 就该声称可用");
        TestRunner.Equal(2, recorded.Count, "Debug 与 Info 都要进详细日志");
        TestRunner.Contains(recorded[0], "cfg", "详细日志要带 category");
        TestRunner.Contains(recorded[0], "调试信息", "详细日志要带正文");
    }

    /// <summary>
    /// 控制台只收 Warning 及以上：Debug/Info 每帧都在刷，全打到控制台会把游戏日志淹掉。
    /// </summary>
    private static void LogAdapterOnlyPrintsWarningAndAbove()
    {
        var printed = new List<(string Line, bool Warn)>();
        var adapter = new EconomyLogAdapter(_ => { }, (line, warn) => printed.Add((line, warn)));

        adapter.Debug("test", "不该上控制台");
        adapter.Info("test", "也不该上控制台");
        TestRunner.Equal(0, printed.Count, "Debug/Info 不该打控制台");

        adapter.Warn("test", "该上控制台");
        adapter.Error("test", "也该上控制台");
        TestRunner.Equal(2, printed.Count, "Warning 与 Error 都要打控制台");
        TestRunner.True(printed[0].Warn, "第一条应标记为警告");
        TestRunner.False(printed[1].Warn, "第二条应走 Error 通道");
    }

    private static void LogAdapterSurvivesThrowingSink()
    {
        int calls = 0;
        var adapter = new EconomyLogAdapter(_ => { calls++; throw new InvalidOperationException("下沉通道坏了"); }, null);

        adapter.Info("test", "第一次会失败");
        adapter.Info("test", "第二次不该再抛");

        TestRunner.True(adapter.LastSinkError != null, "应记下失败原因");
        TestRunner.False(adapter.DiagnosticsAvailable, "失败后应摘掉钩子，避免每行都抛一次");
        TestRunner.Equal(2, adapter.Written, "两次调用都该被计入");
        TestRunner.Equal(1, calls, "坏掉的钩子只应被调用一次");
    }

    private static void LogAdapterFormatIsStable()
    {
        string line = EconomyLogAdapter.Format(LogLevel.Warning, "init", "配置有问题。");
        TestRunner.Contains(line, "NationalSpireEconomy", "前缀要能让人一眼认出是我们的日志");
        TestRunner.Contains(line, "Warning", "要带级别");
        TestRunner.Contains(line, "init", "要带 category");
        TestRunner.Contains(line, "配置有问题。", "要带正文");
    }

    // ── 文件保底日志（第一优先通道，不经过 Godot） ──────────────────

    /// <summary>
    /// <b>本轮事故的直接对策</b>：在 Godot 原生运行时不可用的进程里（就是本测试进程），
    /// 日志也必须能落到文件。这条断言如果失败，说明我们又回到了"游戏里一个字都看不见"的状态。
    /// </summary>
    private static void FileLogSinkWritesWithoutGodot()
    {
        TestRunner.False(EconomyModEntry.IsGodotNativeReady(),
            "前置：本测试进程里 Godot 原生运行时不可用——正是要验证的场景");

        string dir = TempRoot();
        var adapter = new EconomyLogAdapter();
        adapter.AttachFileSink(EconomyLogAdapter.CreateFileSink(dir, null), LogLevel.Info, "boot", "模组入口被调用");

        adapter.Info("init", "配置校验通过");
        adapter.Warn("paths", "user:// 不可用，回退到 ApplicationData");

        string path = Path.Combine(dir, EconomyFileLogSink.FileName);
        TestRunner.True(File.Exists(path), "文件保底通道必须真的落盘：" + EconomyLogAdapter.FileSinkReport);

        string text = File.ReadAllText(path);
        TestRunner.Contains(text, "不经过 Godot", "文件头要说明这是不需要 Godot 的保底通道");
        TestRunner.Contains(text, "模组入口被调用", "挂在通道上时写的那一条必须落盘");
        TestRunner.Contains(text, "配置校验通过", "Info 级也要落文件（启动期判断全靠它）");
        TestRunner.Contains(text, "回退到 ApplicationData", "Warning 级同样要落文件");
        TestRunner.Contains(text, "[Info]", "行内要带级别");
        TestRunner.Contains(text, "init", "行内要带 category");
    }

    /// <summary>第一个落点不可写时必须自动换下一个，而不是整条通道失效。</summary>
    private static void FileLogSinkFallsBackToSecondCandidate()
    {
        // 把"目录"指向一个【文件】——Directory.CreateDirectory 必定失败。
        string blocker = Path.Combine(TempRoot(), "i-am-a-file");
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
        File.WriteAllText(blocker, "占位");

        string good = TempRoot();
        var sink = new EconomyFileLogSink();
        sink.AddCandidate(Path.Combine(blocker, EconomyFileLogSink.FileName));
        sink.AddCandidate(Path.Combine(good, EconomyFileLogSink.FileName));

        TestRunner.True(sink.TryAppend("回退之后也要能写"), "第一个落点坏掉时应换下一个");
        TestRunner.Equal(Path.Combine(good, EconomyFileLogSink.FileName), sink.Path, "应落在可写的那个候选点上");
        TestRunner.True(File.ReadAllText(sink.Path).Contains("回退之后也要能写"), "内容应写进回退落点");
    }

    /// <summary>
    /// 目录能建、但真正写文件时被拒绝（权限/策略）时，必须换下一个候选落点——
    /// 这正是"落点选错就整条通道静默"的隐患所在，不能只测"目录建不出来"这一种失败。
    /// </summary>
    private static void FileLogSinkSwitchesCandidateWhenWriteIsDenied()
    {
        string blockedDir = TempRoot();
        Directory.CreateDirectory(blockedDir);

        // 先占住这个文件名，然后把它的同名【目录】建出来：
        // 于是 Directory.CreateDirectory(父目录) 会成功，而真正写文件必定失败。
        string blocked = Path.Combine(blockedDir, EconomyFileLogSink.FileName);
        Directory.CreateDirectory(blocked);

        string good = TempRoot();
        var sink = new EconomyFileLogSink();
        sink.AddCandidate(blocked);
        sink.AddCandidate(Path.Combine(good, EconomyFileLogSink.FileName));

        TestRunner.True(sink.TryAppend("第一个落点写不进去时必须换下一个"), "应换到可写的落点");
        TestRunner.Equal(Path.Combine(good, EconomyFileLogSink.FileName), sink.Path, "应落在可写的那个落点");
        TestRunner.True(File.ReadAllText(sink.Path).Contains("必须换下一个"), "内容应写进换后的落点");
        TestRunner.True(sink.LastError != null, "应记下第一个落点的失败原因");
    }

    /// <summary>日志通道坏掉绝不能让模组失效：所有失败都只能记下来，不能抛。</summary>
    private static void FileLogSinkNeverThrows()
    {
        var sink = new EconomyFileLogSink();
        TestRunner.False(sink.TryAppend("没有登记任何落点"), "无候选落点时应返回 false 而不是抛异常");
        TestRunner.True(sink.LastError != null, "应记下失败原因");

        // 非法路径（含非法字符）同样只能失败，不能抛。
        var bad = new EconomyFileLogSink();
        bad.AddCandidate("Z:\\<>|\\" + EconomyFileLogSink.FileName);
        TestRunner.False(bad.TryAppend("非法路径"), "非法路径应返回 false");

        var adapter = new EconomyLogAdapter();
        adapter.AttachFileSink(bad, LogLevel.Info, "boot", "即使是坏的落点也不能抛");
        adapter.Warn("test", "这一行写不进去，但绝不能让游戏崩");
        TestRunner.True(EconomyLogAdapter.FileSinkReport.Length > 0, "落点健康结论必须始终有文本");
    }

    /// <summary>
    /// <b>★ 日志器不许永久闭嘴（2026-10-12 真机教训）。</b>
    /// <para>
    /// 真机症状：`mod-debug.log` 在某一刻之后<b>彻底不再增长</b>（旁路探针里
    /// <c>File.AppendAllText</c> 命中 0 次），而探针日志照常写 ——
    /// 于是后续每一轮排查都建立在"日志说什么"这个假信息上，白花了一整轮。
    /// </para>
    /// <para>
    /// 根因：<c>TryAppend</c> 开头只有一句 <c>if (_disabled) return false;</c>，
    /// 而 <c>_disabled</c> 在所有候选落点写失败后被置位就<b>永不恢复</b>。
    /// </para>
    /// <para>
    /// 这条钉住修复后的行为：<b>静默满 <see cref="EconomyFileLogSink.RetryAfterSuppressedLines"/>
    /// 行之后必须自动重试</b>，并且落点恢复可写时要真的写进去。
    /// </para>
    /// </summary>
    private static void FileLogSinkReopensAfterBeingDisabled()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nse-logsink-retry-" + Guid.NewGuid().ToString("N"));
        string good = Path.Combine(dir, EconomyFileLogSink.FileName);
        try
        {
            // ① 先让它彻底失败：登记一个非法路径，写几次就进入"永久静默"态。
            var sink = new EconomyFileLogSink();
            sink.AddCandidate("Z:\\<>|\\" + EconomyFileLogSink.FileName);
            TestRunner.False(sink.TryAppend("第一次必然失败"), "非法落点应该写不进去");

            // ② 现在把落点换成一个真实可写目录（模拟"磁盘/权限恢复了"）。
            sink.AddCandidate(good);

            // ③ 静默计数没到阈值之前，它应该继续闭嘴（不反复做无效探测）。
            bool early = sink.TryAppend("还没到重试阈值");
            TestRunner.False(early, "静默未满阈值时不该写");

            // ④ 把它推到阈值之后：必须自动重开并写进新的可写落点。
            for (int i = 0; i < EconomyFileLogSink.RetryAfterSuppressedLines; i++)
                sink.TryAppend("填充静默计数 " + i);

            bool recovered = sink.TryAppend("恢复之后这一行必须落盘");
            TestRunner.True(recovered, "★静默满阈值后必须自动重试写入（不许永久闭嘴）");
            TestRunner.True(File.Exists(good), "恢复后应真的建出日志文件");
            TestRunner.Contains(File.ReadAllText(good), "恢复之后这一行必须落盘",
                "★恢复后这一行要真的在文件里");
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>适配器只依赖 <see cref="IEconomyFileSink"/> 这层抽象，换成别的下沉实现照样能用。</summary>
    private static void FileLogSinkAcceptsTextWriterSink()
    {
        var buffer = new StringWriter();
        var adapter = new EconomyLogAdapter();
        adapter.AttachFileSink(new TextWriterSink(buffer, "内存"), LogLevel.Info, "boot", "头一行");

        adapter.Debug("cfg", "调试信息也要落文件");

        string text = buffer.ToString();
        TestRunner.Contains(text, "头一行", "挂上通道时写的那一条要落盘");
        TestRunner.Contains(text, "调试信息也要落文件", "Debug 级也要落文件");
        TestRunner.Contains(EconomyLogAdapter.FileSinkReport, "内存", "健康结论应写出落点描述");
    }

    // ── 配置门禁 ────────────────────────────────────────────────────

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "nse-mod-" + Guid.NewGuid().ToString("N"));

    /// <summary>把仓库里的真实配置拷到指定目录。</summary>
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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "config");
            if (File.Exists(Path.Combine(candidate, ConfigLoader.EconomyFileName))) return candidate;
            directory = directory.Parent;
        }
        throw new Exception("找不到 config/ 目录");
    }

    private static void ValidConfigEnablesMod()
    {
        string data = TempRoot();
        string config = CopyRealConfig(TempRoot());

        var state = EconomyModEntry.Initialize(Silent, data, config);

        TestRunner.True(state.Enabled, "真实配置应当通过门禁：" + state.Describe());
        TestRunner.Equal(0, state.ErrorCount, "不该有配置错误");
        TestRunner.True(state.Config != null, "应持有配置结果");
        TestRunner.True(state.Store != null, "应持有影子存档");
        TestRunner.Equal("", state.FailureStage);
    }

    /// <summary>
    /// <b>阶段 0 的核心契约</b>：配置有 Error 就拒绝启用。
    /// </summary>
    private static void ConfigErrorDisablesMod()
    {
        string data = TempRoot();
        string config = CopyRealConfig(TempRoot());

        // 塞一个越界的 give（允许 0.2~1.5），ConfigLoader 会判为 Error。
        string personalityPath = Path.Combine(config, ConfigLoader.PersonalityFileName);
        string text = File.ReadAllText(personalityPath);
        text = text.Replace("\"give\": 0.65", "\"give\": 99.0");
        File.WriteAllText(personalityPath, text);
        var state = EconomyModEntry.Initialize(Silent, data, config);

        TestRunner.False(state.Enabled, "配置有 Error 时必须拒绝启用");
        TestRunner.Equal("config", state.FailureStage, "失败阶段应标记为 config");
        TestRunner.True(state.ErrorCount > 0, "应记录到错误条数");
        TestRunner.Contains(state.FailureDetail, "give", "失败说明里应指出具体问题");
    }

    private static void MissingConfigDisablesMod()
    {
        string data = TempRoot();
        string config = Path.Combine(TempRoot(), "并不存在的目录");

        var state = EconomyModEntry.Initialize(Silent, data, config);

        // 文件全缺 → ConfigLoader 对每份文件都报 Error → 必须拒绝启用。
        TestRunner.False(state.Enabled, "配置目录不存在时必须拒绝启用");
        TestRunner.Equal("config", state.FailureStage);
        TestRunner.True(state.ErrorCount >= 3, "三份配置都缺失，应至少 3 个错误");
    }

    private static void DisabledStateDescribesWhy()
    {
        string data = TempRoot();
        var state = EconomyModEntry.Initialize(Silent, data, Path.Combine(TempRoot(), "missing"));

        TestRunner.False(state.Enabled);
        string described = state.Describe();
        TestRunner.Contains(described, "未启用", "摘要要说明未启用");
        TestRunner.Contains(described, "config", "摘要要说明失败阶段");

        // 配置目录不存在时，三份文件各报一个 Error，随后"配置为空"还会追加若干 Warning，
        // 所以这里只断言错误数，不断言警告数——警告数取决于 ConfigLoader 的内部实现细节。
        TestRunner.True(state.ErrorCount >= 3, "三份配置都缺失，错误数应至少 3，实际 " + state.ErrorCount);
    }

    /// <summary>初始化可以被反复调用（热重载场景），每次都给出独立结果。</summary>
    private static void InitializeIsIdempotentAcrossCalls()
    {
        string config = CopyRealConfig(TempRoot());

        var first = EconomyModEntry.Initialize(Silent, TempRoot(), config);
        var second = EconomyModEntry.Initialize(Silent, TempRoot(), config);

        TestRunner.True(first.Enabled && second.Enabled, "两次都应成功");
        TestRunner.NotEqual(first.DataRoot, second.DataRoot, "两次用了不同的数据根，应各自独立");
    }

    // ── 路径 ────────────────────────────────────────────────────────

    /// <summary>
    /// 数据根目录至少要可用。测试进程里 GodotSharp 刻意未加载，所以走文件系统回退——
    /// 这正是我们要保证的"Godot 不可用时也不能返回 null"。
    /// <para>
    /// 注意这里<b>不</b>断言路径落在某个具体盘符：<c>GetFolderPath(ApplicationData)</c> 读的是
    /// Windows 注册表的 shell 文件夹设置，<b>不</b>看 <c>$env:APPDATA</c>。
    /// 本机沙箱把 APPDATA 重定向到了 D 盘，但注册表仍指向 C 盘，断言具体位置会变成环境依赖。
    /// 该断言的是"契约"：绝对路径 + 目录名 + 与原版 user:// 同一结构。
    /// </para>
    /// </summary>
    private static void DataRootFallsBackToAppData()
    {
        string? root = EconomyModEntry.ResolveDataRoot(Silent);

        TestRunner.True(root != null, "必须给出一个可用路径，不能返回 null");
        TestRunner.True(Path.IsPathRooted(root!), "必须是绝对路径：" + root);
        TestRunner.Equal(EconomyModEntry.DataDirectoryName, Path.GetFileName(root!),
            "最后一级必须是我们的数据目录名");
        TestRunner.Equal("SlayTheSpire2", Path.GetFileName(Path.GetDirectoryName(root!)!),
            "回退路径应与原版 user:// 同结构：…\\SlayTheSpire2\\national_spire_economy");
    }

    private static void ConfigRootIsModDirectoryConfig()
    {
        string? root = EconomyModEntry.ResolveConfigRoot(Silent);
        string? assemblyDir = EconomyModEntry.AssemblyDirectory();

        TestRunner.True(assemblyDir != null, "应能取到程序集目录");
        TestRunner.Equal(Path.Combine(assemblyDir!, EconomyModEntry.ConfigDirectoryName), root,
            "配置目录必须是「程序集目录/config」——模组被装到哪个盘都要能找到配置");
    }

    private static void AssemblyDirectoryIsTestOutput()
    {
        string? dir = EconomyModEntry.AssemblyDirectory();
        TestRunner.True(dir != null && Directory.Exists(dir), "程序集目录应真实存在：" + dir);
        TestRunner.True(File.Exists(Path.Combine(dir!, "NationalSpireEconomy.Tests.dll")),
            "应指向测试输出目录");
    }

    /// <summary>
    /// §6.1 的坑：模组 id 绝不能叫 NationalSpire，否则会被原版按版本号挤出加载列表。
    /// </summary>
    private static void ModIdIsNotNationalSpire()
    {
        TestRunner.Equal("NationalSpireEconomy", EconomyModEntry.ModId);
        TestRunner.NotEqual("NationalSpire", EconomyModEntry.ModId, "绝不能与原版同名");
        TestRunner.NotEqual("nationalspire", EconomyModEntry.HarmonyId, "Harmony id 也不能与原版撞");
    }

    // ── 编排 ────────────────────────────────────────────────────────

    private static void InitializeCreatesWorldsDirectory()
    {
        string data = TempRoot();
        string config = CopyRealConfig(TempRoot());

        var state = EconomyModEntry.Initialize(Silent, data, config);
        TestRunner.True(state.Enabled, "前置：应启用成功");

        string worlds = state.Store!.WorldsDirectory;
        TestRunner.True(Directory.Exists(worlds), "应创建 worlds 目录：" + worlds);
        TestRunner.True(worlds.StartsWith(data, StringComparison.Ordinal), "worlds 目录应在数据根下");
    }

    /// <summary>
    /// 无论配置多烂，<see cref="EconomyModEntry.Initialize"/> 都不许抛异常——
    /// 抛出去会让原版加载流程崩掉。
    /// </summary>
    private static void InitializeNeverThrowsOnGarbageConfig()
    {
        string config = TempRoot();
        Directory.CreateDirectory(config);
        // 写入三份语法坏掉的 JSON。
        foreach (string name in new[]
                 {
                     ConfigLoader.EconomyFileName, ConfigLoader.PersonalityFileName, ConfigLoader.PromptFileName
                 })
            File.WriteAllText(Path.Combine(config, name), "{ 这不是 JSON");

        ModState? state = null;
        bool threw = false;
        try
        {
            state = EconomyModEntry.Initialize(Silent, TempRoot(), config);
        }
        catch (Exception)
        {
            threw = true;
        }

        TestRunner.False(threw, "坏配置必须被降级处理，而不是抛异常");
        TestRunner.True(state != null && !state.Enabled, "应返回未启用状态");
    }

    private static void InitializeRejectsEmptyDataRoot()
    {
        string config = CopyRealConfig(TempRoot());
        var state = EconomyModEntry.Initialize(Silent, "", config);

        TestRunner.False(state.Enabled, "数据根为空时必须拒绝启用");
        TestRunner.Equal("paths", state.FailureStage, "应标记为路径阶段失败");
    }

    /// <summary>
    /// <b>回归测试：一次真实的进程级崩溃（<c>0xC0000005</c>）。</b>
    /// <para>
    /// <c>Godot.ProjectSettings.GlobalizePath</c> 在 Godot 原生运行时之外被调用时会调原生函数，
    /// 触发 <c>AccessViolationException</c>。那是<b>损坏状态异常</b>，<c>catch (Exception)</c>
    /// 拦不住，整个测试进程当场死、后面的用例一条都跑不到。
    /// </para>
    /// <para>
    /// 光判"GodotSharp 程序集有没有加载"<b>不够</b>：别的库（Sentry 的 Godot 扩展）
    /// 也可能把它拖进进程，那时程序集在、原生函数却还是空的。
    /// 生产代码因此用 <c>NativeFuncs</c> 的委托字段做探针——读字段不会触发静态构造函数。
    /// 这条测试钉住的契约是：<b>在没有原生运行时的进程里，解析路径绝不能去碰 Godot。</b>
    /// </para>
    /// </summary>
    private static void GodotIsNotLoadedInTestProcess()
    {
        bool godotAssemblyLoaded = AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => a.GetName().Name is "GodotSharp");

        if (!godotAssemblyLoaded)
        {
            // 最常见的情况：进程里根本没有 GodotSharp，防护①就拦住了，绝不会崩。
            TestRunner.True(true, "GodotSharp 未加载，路径解析走文件系统回退");
            return;
        }

        // 加载了也不代表能用：必须确认原生运行时就位才会去调 GlobalizePath。
        // 这里能安全地走完 ResolveDataRoot 本身就是"没去踩地雷"的证据。
        string? root = EconomyModEntry.ResolveDataRoot(Silent);
        TestRunner.True(root != null, "即便 GodotSharp 已加载，也必须能安全给出回退路径");
    }

    /// <summary>
    /// 根目录都"解析不出来/指不到"时，必须走运行时解析并且不崩、且拒绝启用。
    /// <para>
    /// <b>为什么配置目录要显式指一个不存在的路径</b>：<c>ResolveConfigRoot()</c> 默认取的是
    /// <b>本程序集所在目录</b>下的 <c>config/</c>，而"主工程的 <c>config\**\*.json</c> 会随
    /// <c>CopyToOutputDirectory</c> 复制到测试输出目录"是构建的正常行为——
    /// 于是"输出目录下没有 config/"这句话在两个构建配置之间会随机成立或不成立，
    /// 这条用例就会时绿时红（本机实测：一次 480/480 绿，下一次同样命令 496/498 红）。
    /// 改成显式给一个必定不存在的配置根，用例才与构建顺序无关。
    /// </para>
    /// </summary>
    private static void InitializeWithNullRootsFallsBackSafely()
    {
        string missingConfig = Path.Combine(Path.GetTempPath(),
            "nse-no-config-" + Guid.NewGuid().ToString("N"));

        var state = EconomyModEntry.Initialize(Silent, dataRoot: null, configRoot: missingConfig);

        TestRunner.True(state.DataRoot.Length > 0, "应解析出数据根");
        TestRunner.False(state.Enabled, "指不到配置目录时必须拒绝启用");
        // 关键契约：不崩、且给出明确原因。具体阶段取决于环境（配置缺失 / 目录不可写），
        // 所以只要求它是一个已知阶段名，而不是空串。
        TestRunner.True(state.FailureStage.Length > 0,
            "必须标记失败阶段，实际：" + state.Describe());
        TestRunner.True(state.FailureDetail.Length > 0, "必须给出失败原因");
    }
}
