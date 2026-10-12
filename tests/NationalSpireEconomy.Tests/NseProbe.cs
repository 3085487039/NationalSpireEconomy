using System.Reflection;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 让测试进程能找到游戏程序集与 NationalSpire 本体。
/// <para>
/// 阶段 0/1 的测试要真的构造 <c>CareerPerson</c> 并断言投影结果，所以运行期必须有
/// <c>sts2.dll</c> / <c>NationalSpire.dll</c> 等。它们是 <c>&lt;Private&gt;false&lt;/Private&gt;</c>，
/// 不会复制到测试输出目录，因此这里在解析失败时按需从磁盘加载。
/// </para>
/// <para>
/// 环境变量 <c>NSE_GAME_DIR</c> 可覆盖游戏数据目录；未设置时用默认安装路径。
/// 两者都不存在时静默跳过——整套测试仍然可跑，只是那几个用例会因为依赖缺失而失败，
/// 而"缺依赖"本身就是个明确信号，不该伪装成随机异常。
/// </para>
/// </summary>
public static class NseProbe
{
    public const string DefaultDataDir =
        @"D:\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64";

    public const string DefaultModsDir =
        @"D:\Steam\steamapps\common\Slay the Spire 2\mods";

    /// <summary>解析到的游戏数据目录。为空表示环境不具备。</summary>
    public static string DataDirectory { get; private set; } = "";

    /// <summary>各程序集的探针路径（按名字）。</summary>
    public static IReadOnlyDictionary<string, string> SearchPaths => Paths;

    /// <summary>诊断：NationalSpire 实际从哪加载。为 null 表示还没加载。</summary>
    public static string? LoadedNationalSpireLocation
    {
        get
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "NationalSpire");
            try { return asm?.Location; } catch (Exception) { return "(无位置)"; }
        }
    }

    /// <summary>诊断：我们为 NationalSpire 选定的候选路径。</summary>
    public static string? ChosenNationalSpirePath =>
        Paths.TryGetValue("NationalSpire", out string? p) ? p : null;

    private static readonly Dictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase);
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;

        DataDirectory = Environment.GetEnvironmentVariable("NSE_GAME_DIR") is { Length: > 0 } custom
            ? custom
            : DefaultDataDir;
        string modsDir = Environment.GetEnvironmentVariable("NSE_MODS_DIR") is { Length: > 0 } customMods
            ? customMods
            : DefaultModsDir;

        Index("sts2", System.IO.Path.Combine(DataDirectory, "sts2.dll"));
        Index("0Harmony", System.IO.Path.Combine(DataDirectory, "0Harmony.dll"));
        Index("Steamworks.NET", System.IO.Path.Combine(DataDirectory, "Steamworks.NET.dll"));

        // NationalSpire 的解析顺序必须与编译期一致，也和游戏自己的加载仲裁一致：
        //   NSE_NATIONALSPIRE_PATH > 模组清单/创意工坊 > 本地 mods 目录
        //
        // 实测过的坑：本地 mods\NationalSpire\ 是 v0.25.39，而游戏实际加载创意工坊的 v0.26.11
        // （原版按 id 取版本号大者，日志里明确写了 disabling the local version）。
        // 如果这里 index 了本地那份，测试就会"编译期看到新版、运行期跑旧版"，
        // 表现为 MissingMethodException: PrivateStreamParser..ctor(string[])。
        foreach (string candidate in NationalSpireCandidates(modsDir))
        {
            Index("NationalSpire", candidate);
            break;
        }

        // GodotSharp 默认【不】纳入解析表。
        //
        // 原因是它会带来一次进程级崩溃：Godot.ProjectSettings 的静态构造函数会调 Godot 原生函数，
        // 在 Godot 之外访问它 → AccessViolationException（0xC0000005），而那是损坏状态异常，
        // catch (Exception) 拦不住，测试进程当场死。
        //
        // 我们生产代码里的 Godot 路径解析靠"原生运行时是否就绪"来判断，
        // 所以测试环境下让它保持未加载，正好走文件系统回退分支。
        if (Environment.GetEnvironmentVariable("NSE_ENABLE_GODOT") == "1")
            Index("GodotSharp", System.IO.Path.Combine(DataDirectory, "GodotSharp.dll"));

        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }

    /// <summary>NationalSpire.dll 的候选路径，按优先级排列（与构建期同一套规则）。</summary>
    public static IReadOnlyList<string> NationalSpireCandidates(string modsDir)
    {
        var list = new List<string>();

        if (Environment.GetEnvironmentVariable("NSE_NATIONALSPIRE_PATH") is { Length: > 0 } overridden)
            list.Add(overridden);

        // 创意工坊：扫 steamapps\workshop\content\<AppId>\*\NationalSpire.dll
        try
        {
            var dataDir = new DirectoryInfo(DataDirectory);
            for (int i = 0; i < 4 && dataDir != null; i++)
            {
                if (string.Equals(dataDir.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
                {
                    string contentDir = System.IO.Path.Combine(dataDir.FullName, "workshop", "content", SteamAppId);
                    if (Directory.Exists(contentDir))
                        list.AddRange(Directory.GetDirectories(contentDir)
                            .Select(d => System.IO.Path.Combine(d, "NationalSpire.dll"))
                            .Where(File.Exists)
                            .OrderByDescending(p => new FileInfo(p).LastWriteTimeUtc));
                    break;
                }
                dataDir = dataDir.Parent;
            }
        }
        catch (Exception)
        {
            // 扫不到就落到本地目录。
        }

        list.Add(System.IO.Path.Combine(modsDir, "NationalSpire", "NationalSpire.dll"));
        list.Add(System.IO.Path.Combine(DataDirectory, "NationalSpire.dll"));

        return list.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private const string SteamAppId = "2868840";

    /// <summary>某个依赖是否可用。测试据此给出"环境缺失"而不是随机失败。</summary>
    public static bool Has(string assemblyName) =>
        Paths.TryGetValue(assemblyName, out string? path) && File.Exists(path);

    private static void Index(string name, string path)
    {
        if (File.Exists(path)) Paths[name] = path;
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        string simpleName = new AssemblyName(args.Name).Name ?? "";
        if (!Paths.TryGetValue(simpleName, out string? path)) return null;

        try
        {
            return Assembly.LoadFrom(path);
        }
        catch (Exception)
        {
            // 加载失败就交回给运行时，让它抛出原本的异常——这里不该吞掉信息。
            return null;
        }
    }
}
