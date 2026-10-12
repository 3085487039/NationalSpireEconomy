using System.Reflection;
using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Game;

/// <summary>
/// 把 <see cref="IEconomyLog"/> 转接到原版 NationalSpire 的 <c>Diagnostics</c> 与 Godot 控制台。
/// <para>
/// <b>刻意走反射而不是直接调用</b>：原版 <c>Diagnostics</c> 的签名随时可能调整。
/// 用反射的话，签名一变只会让详细日志降级，模组本身与控制台输出仍然照常工作——
/// 日志通道坏掉不该让模组失效。
/// </para>
/// <para>
/// 目标文件：<c>%APPDATA%\SlayTheSpire2\national_spire_diagnostics\national-spire.log</c>
/// （由原版 <c>Entry.Init</c> 里 <c>Diagnostics.Configure</c> 的调用决定，我们只往里写）。
/// </para>
/// </summary>
public sealed class EconomyLogAdapter : IEconomyLog
{
    private Action<string>? _recordHook;
    private Action<string, bool>? _printHook;
    private IEconomyFileSink? _fileSink;

    /// <summary>详细日志是否成功接到了原版 Diagnostics。false 表示只剩控制台输出。</summary>
    public bool DiagnosticsAvailable => _recordHook != null;

    /// <summary>控制台通道是否可用（Godot 未初始化时为 false）。</summary>
    public bool ConsoleAvailable => _printHook != null;

    /// <summary>最近一次转接失败的原因，便于自检。</summary>
    public string? LastSinkError { get; private set; }

    /// <summary>
    /// 诊断文件通道（<c>NationalSpire.Diagnostics.Record</c>）的绑定结论。
    /// <para>
    /// <b>实测教训</b>：这条通道曾经"看起来绑上了"但其实从未生效——诊断文件里一条 <c>economy.*</c> 都没有。
    /// 所以现在把结论写成一行文本，由挂载点体检打在启动日志里，不再靠猜。
    /// </para>
    /// </summary>
    public static string LastBindingReport { get; private set; } = "尚未尝试绑定";

    /// <summary>控制台通道（<c>Godot.GD.Print</c>）的绑定结论。</summary>
    public static string LastPrintReport { get; private set; } = "尚未尝试绑定";

    /// <summary>控制台通道当前状态。</summary>
    public static PrintBindStatus PrintStatus { get; private set; } = PrintBindStatus.NotAttempted;

    /// <summary>控制台通道一共重试过几次（每次写出日志时机会性重试一次）。</summary>
    public static int PrintRebindAttempts { get; private set; }

    /// <summary>是否已经挂上文件保底通道（由 <see cref="AttachFileSink"/> 置位）。</summary>
    public static bool FileSinkEnabled { get; private set; }

    /// <summary>文件保底通道的落点与健康结论。</summary>
    public static string FileSinkReport { get; private set; } = "尚未启用文件日志（保底通道）";

    /// <summary>懒重试次数上限：原版 <c>Diagnostics</c> 可能比我们晚初始化。</summary>
    private const int MaxLazyRebinds = 5;
    private int _printRebinds;
    private int _lazyRebinds;

    /// <summary>
    /// 是否允许"懒重试绑定"。<b>只有运行时构造（无参构造）才允许</b>。
    /// <para>
    /// 注入式构造（单测用）刻意不自动绑定：那两条测试的契约是"没给 hook 就没有 hook"、
    /// "坏掉的 hook 摘掉后不再重绑"。懒重试如果对它们也生效，会把契约悄悄改掉。
    /// </para>
    /// </summary>
    private readonly bool _autoBind;

    /// <summary>
    /// 控制台通道。暴露出来是为了让补丁诊断能<b>绕过诊断日志</b>直接写 Godot 控制台——
    /// 模组初始化阶段诊断日志的钩子可能还没接上（它依赖 NationalSpire 已加载），
    /// 而控制台一定可用。阶段 2 的第一次交付就栽在这上面。
    /// </summary>
    public Action<string, bool>? ConsoleSink => _printHook;

    /// <summary>
    /// 尝试重新绑定日志下沉通道。
    /// <para>
    /// 必要性：<see cref="TryBindRuntimeHooks"/> 在构造时跑一次，那时 NationalSpire
    /// 可能还没被模组加载器载入，于是 <c>Diagnostics.Record</c> 找不到，诊断日志静默失效。
    /// 在确保程序集已加载之后调用本方法就能补上。
    /// </para>
    /// </summary>
    public bool Rebind()
    {
        TryBindRecordHook();
        if (_printHook == null) TryRetryPrintBind();
        return _recordHook != null;
    }

    /// <summary>
    /// 挂上文件保底通道，并<b>立刻</b>写一条头行。
    /// <para>
    /// 它必须比一切其它日志都早：模组初始化时的 Godot 原生运行时就绪状态是不确定的，
    /// 只要这条通道在位，无论控制台/诊断文件通道能不能绑上，排查都不会再摸黑。
    /// </para>
    /// </summary>
    public void AttachFileSink(IEconomyFileSink sink, LogLevel level, string category, string firstLine)
    {
        _fileSink = sink;
        FileSinkEnabled = true;
        FileSinkReport = sink.Describe;
        WriteToFile(level, category, firstLine);
    }

    /// <summary>已经写出的行数（本次进程内）。</summary>
    public int Written { get; private set; }

    /// <summary>默认构造：运行时自己解析原版 <c>Diagnostics.Record</c> 与 Godot 的 GD.Print。</summary>
    public EconomyLogAdapter()
    {
        _autoBind = true;
        TryBindRuntimeHooks();
    }

    /// <summary>
    /// 注入式构造，供单测使用。两个 hook 都可以为 null，表示那个通道不可用。
    /// </summary>
    /// <param name="recordHook">详细日志通道，收到 <c>category + " " + message</c>。</param>
    /// <param name="printHook">控制台通道，第二参数为 true 表示警告级别。</param>
    public EconomyLogAdapter(Action<string>? recordHook, Action<string, bool>? printHook = null)
    {
        _recordHook = recordHook;
        _printHook = printHook;
    }

    public void Write(LogLevel level, string category, string message, Exception? error = null)
    {
        string text = message;
        if (error != null) text += " | " + error.GetType().Name + ": " + error.Message;

        // 0) 懒重试：原版 Diagnostics 可能比我们晚初始化（我们排在 NationalSpire 前面）。
        //    只对运行时构造生效；注入式构造（单测）的契约是"没给 hook 就没有 hook"。
        //    控制台通道同理：Godot 原生运行时可能到我们这里才开始可用，也必须能补上——
        //    "一次判定为未就绪就永远不再尝试"正是上一轮全通道静默的根因之一。
        if (_autoBind)
        {
            if (_recordHook == null && _lazyRebinds < MaxLazyRebinds)
            {
                _lazyRebinds++;
                TryBindRecordHook();
            }

            if (_printHook == null) TryRetryPrintBind();
        }

        // 1) 保底通道：文件。第一优先，纯 System.IO，与 Godot 是否就绪无关。
        WriteToFile(level, category, text);

        // 2) 详细日志（含 Debug/Info）→ 原版诊断文件，供事后排查。
        if (_recordHook != null)
        {
            try
            {
                _recordHook(category + " " + text);
            }
            catch (Exception e)
            {
                // 单次失败就摘掉钩子，避免每行日志都抛一次异常把游戏卡住。
                LastSinkError = e.Message;
                _recordHook = null;
            }
        }

        // 3) 控制台：Warning 及以上一定要让开发者立刻看见。
        //    诊断文件是异步刷盘的，排查"模组到底加载了没有"时控制台更快。
        if (level >= LogLevel.Warning && _printHook != null)
        {
            try
            {
                _printHook(Format(level, category, text), level == LogLevel.Warning);
            }
            catch (Exception e)
            {
                LastSinkError = e.Message;
                _printHook = null;
            }
        }

        Written++;
    }

    public static string Format(LogLevel level, string category, string message) =>
        $"[NationalSpireEconomy] [{level}] {category}: {message}";

    private void TryBindRuntimeHooks()
    {
        TryBindRecordHook();
        TryBindPrintHook();
    }

    /// <summary>
    /// 往文件保底通道写一行。<b>永远不抛异常</b>。
    /// <para>刻意<b>不过滤级别</b>：启动期判断"模组到底跑到哪一步"靠的就是 Info/Debug。</para>
    /// </summary>
    private void WriteToFile(LogLevel level, string category, string text)
    {
        if (_fileSink == null) return;

        try
        {
            bool ok = _fileSink.TryAppend($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {category}: {text}");
            FileSinkReport = ok
                ? _fileSink.Describe
                : $"写入失败（{_fileSink.Describe}）｜{(_fileSink as EconomyFileLogSink)?.LastError ?? "未知原因"}";
        }
        catch (Exception e)
        {
            // 保底通道自己坏掉也只能记下来——绝不能让它把模组带崩。
            FileSinkReport = "写入异常：" + e.GetType().Name + ": " + e.Message;
            _fileSink = null;
        }
    }

    /// <summary>
    /// 造一个文件保底通道。落点按"最可能需要、最不依赖权限"排序，逐个尝试：
    /// <list type="number">
    /// <item>影子存档目录（<c>%APPDATA%\SlayTheSpire2\national_spire_economy\</c>）——正常首选。</item>
    /// <item>模组目录——开发机上常常可写；正式安装通常需要管理员权限。</item>
    /// <item>系统临时目录——权限受限时的最后保底（不双写，够用就行）。</item>
    /// </list>
    /// </summary>
    public static EconomyFileLogSink CreateFileSink(string? dataRoot, string? assemblyDirectory)
    {
        var sink = new EconomyFileLogSink();
        if (!string.IsNullOrWhiteSpace(dataRoot)) sink.AddCandidate(Path.Combine(dataRoot, EconomyFileLogSink.FileName));
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            sink.AddCandidate(Path.Combine(assemblyDirectory, EconomyFileLogSink.FileName));

        // 最后保底：临时目录。任何进程都写得进去，避免"主落点不可写就直接静默"。
        try
        {
            sink.AddCandidate(Path.Combine(Path.GetTempPath(), EconomyFileLogSink.FileName));
        }
        catch (Exception)
        {
            // 连临时目录都问不出来：那就只剩前面两个候选点了。
        }

        return sink;
    }

    /// <summary>
    /// 机会性重试绑定控制台通道：每次写出日志时问一次。
    /// <para>
    /// 重试次数上限刻意放宽（见 <see cref="MaxPrintRebinds"/>）：每次重试只是读一个字段，
    /// 而"过早放弃"会让整个模组在游戏里彻底沉默——两边的代价完全不对称。
    /// </para>
    /// </summary>
    private void TryRetryPrintBind()
    {
        if (_printHook != null) return;
        if (PrintStatus == PrintBindStatus.AlreadyBound) return;
        if (_printRebinds >= MaxPrintRebinds) return;

        PrintRebindAttempts++;
        _printRebinds++;
        TryBindPrintHook();
    }

    /// <summary>控制台通道机会性重试的次数上限。</summary>
    private const int MaxPrintRebinds = 200;

    /// <summary>
    /// 绑定诊断文件通道。
    /// <para>
    /// <b>不用 <c>Type.GetType("…, NationalSpire")</c></b>：那会让运行时按程序集名去解析，
    /// 极可能把 NationalSpire <b>装进 Default ALC</b>——那正是阶段 3 造出"游戏永不执行的
    /// 假副本"的机制之一。改成只认已经加载好的副本，并且<b>优先游戏执行的那一份</b>
    /// （我们自己 ALC 里的、由 <see cref="NationalSpireLocator"/> 定位/装载的那份）。
    /// </para>
    /// <para>结论一律写进 <see cref="LastBindingReport"/>，由启动期体检打进日志。</para>
    /// </summary>
    private void TryBindRecordHook()
    {
        if (_recordHook != null) return;

        try
        {
            var diagnostics = FindLoadedType("NationalSpire.Diagnostics");
            if (diagnostics == null)
            {
                LastBindingReport = "未绑定：找不到类型 NationalSpire.Diagnostics（尚未加载原版程序集）";
                return;
            }

            var record = diagnostics.GetMethod("Record", BindingFlags.Public | BindingFlags.Static, null,
                             [typeof(string), typeof(object)], null)
                         ?? diagnostics.GetMethods(BindingFlags.Public | BindingFlags.Static)
                             .FirstOrDefault(m => m.Name == "Record" && m.GetParameters().Length == 2);

            if (record == null)
            {
                LastBindingReport = $"未绑定：{diagnostics.FullName} 里没有 Record(string, object)";
                return;
            }

            _recordHook = payload => record.Invoke(null, ["economy", payload]);
            string signature = string.Join(", ", record.GetParameters().Select(p => p.ParameterType.Name));
            LastBindingReport = $"已绑定 {diagnostics.FullName}.Record({signature})" +
                                $"｜程序集 {diagnostics.Assembly.GetName().Name}" +
                                $"｜ALC {AssemblyInventory.ContextOf(diagnostics.Assembly)}";
        }
        catch (Exception e)
        {
            LastSinkError = "绑定 Diagnostics 失败：" + e.Message;
            LastBindingReport = "绑定异常：" + e.GetType().Name + ": " + e.Message;
        }
    }

    /// <summary>
    /// 绑定控制台通道。
    /// <para>
    /// <b>普通信息必须走 <c>GD.Print</c></b>：我们曾经把非警告行也丢给 <c>PushError</c>，
    /// 于是 godot.log 里每条诊断都带上 <c>ERROR:</c> 前缀和一段调用栈（实测），
    /// 既像报错又刷屏。<c>PushError</c> 现在只作为最后兜底。
    /// </para>
    /// </summary>
    private void TryBindPrintHook()
    {
        if (_printHook != null)
        {
            PrintStatus = PrintBindStatus.AlreadyBound;
            return;
        }

        // 先问"原生运行时是否就绪"：GodotSharp 程序集在进程里 ≠ 原生函数可用。
        // 别的库（例如 Sentry 的 Godot 扩展）会把 GodotSharp 拖进进程，
        // 那时调用 GD.Print / GD.PushError 会直接杀死进程（0xC0000005，catch 拦不住）。
        if (!EconomyModEntry.IsGodotNativeReady())
        {
            // ★ 关键：这种情况必须【允许以后再试】。初始化时判定为未就绪，
            //   不代表整局游戏都不就绪；早先把这条一次性判死，正是全通道静默的根因。
            PrintStatus = PrintBindStatus.NotReadyYet;
            LastPrintReport = "跳过：Godot 原生运行时尚未就绪（绑定过早，稍后自动重试）" +
                              $"｜已试 {PrintRebindAttempts} 次";
            return;
        }

        try
        {
            var gd = FindLoadedType("Godot.GD") ?? Type.GetType("Godot.GD, GodotSharp", throwOnError: false);
            var print = gd?.GetMethod("Print", BindingFlags.Public | BindingFlags.Static, null, [typeof(string)], null);
            var warn = gd?.GetMethod("PushWarning", BindingFlags.Public | BindingFlags.Static, null, [typeof(string)], null);
            var error = gd?.GetMethod("PushError", BindingFlags.Public | BindingFlags.Static, null, [typeof(string)], null);

            if (print == null && warn == null && error == null)
            {
                // 完全拿不到方法：不值得重试（是 API 形状问题，不是时机问题）。
                PrintStatus = PrintBindStatus.Unavailable;
                LastPrintReport = "未找到 Godot.GD（游戏外运行？）";
                return;
            }

            _printHook = (line, isWarning) =>
            {
                if (isWarning && warn != null) { warn.Invoke(null, [line]); return; }
                if (print != null) { print.Invoke(null, [line]); return; }
                error?.Invoke(null, [line]);
            };

            PrintStatus = PrintBindStatus.Bound;
            LastPrintReport = print != null
                ? "已绑定 Godot.GD.Print（普通信息不再走 PushError）"
                : warn != null || error != null
                    ? "只找到 PushWarning/PushError：普通信息仍会带 ERROR 前缀"
                    : "未找到 Godot.GD（游戏外运行？）";
        }
        catch (Exception e)
        {
            LastSinkError = "绑定 Godot 日志失败：" + e.Message;
            PrintStatus = PrintBindStatus.NotReadyYet; // 反射失败多半是时机问题，允许再试
            LastPrintReport = "绑定异常：" + e.GetType().Name + ": " + e.Message;
        }
        finally
        {
            if (_printHook != null) PrintStatus = PrintBindStatus.Bound;
        }
    }

    /// <summary>
    /// 在已加载的程序集里按完整类型名找类型。找不到返回 null。
    /// <para>
    /// <b>优先原版副本里"游戏执行的那一份"</b>（我们自己 ALC 里的那份），
    /// 其次其它原版副本，最后才是全进程——多副本时顺序错了就会绑到死副本上。
    /// </para>
    /// </summary>
    private static Type? FindLoadedType(string fullName)
    {
        var ordered = AssemblyInventory.GameAssemblies()
            .OrderByDescending(PathProbe.InOurContext)
            .Concat(AssemblyInventory.AllAssemblies());

        foreach (var assembly in ordered)
        {
            try
            {
                var found = assembly.GetType(fullName, throwOnError: false);
                if (found != null) return found;
            }
            catch (Exception)
            {
                // 个别程序集枚举类型会抛（动态程序集等），跳过即可。
            }
        }
        return null;
    }
}

/// <summary>
/// 控制台（Godot）日志通道的绑定状态。
/// <para>
/// <see cref="NotReadyYet"/> 与 <see cref="Unavailable"/> 必须分开：
/// 前者是"时机不对"，要允许以后再试；后者是"这条路根本没有"（例如在游戏外跑单测），
/// 再试也没意义。上一轮把两者混为一谈（一次判死），代价是整个模组在游戏里沉默。
/// </para>
/// </summary>
public enum PrintBindStatus
{
    /// <summary>还没试过。</summary>
    NotAttempted,

    /// <summary>原生运行时尚未就绪：允许稍后自动重试。</summary>
    NotReadyYet,

    /// <summary>拿不到 Godot.GD 的日志方法：重试无意义。</summary>
    Unavailable,

    /// <summary>已经绑上（含由注入式构造直接给定 hook 的情况）。</summary>
    AlreadyBound,

    /// <summary>本次绑定成功。</summary>
    Bound
}
