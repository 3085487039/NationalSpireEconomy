using System.Reflection;
using System.Runtime.Loader;
using MegaCrit.Sts2.Core.Modding;

namespace NationalSpireEconomy.Game;

/// <summary>
/// 定位并加载 NationalSpire 本体。
/// <para>
/// <b>为什么需要这个类</b>（阶段 2 实测出来的坑）：
/// 模组加载器是<b>按顺序</b>初始化各模组的，而 NationalSpireEconomy 排在 NationalSpire
/// <b>前面</b>（实测：我们 428 行初始化，NationalSpire 到 612 行才初始化）。
/// 于是我们初始化时 NationalSpire 根本还没被加载进 AppDomain，
/// <c>AccessTools.TypeByName("NationalSpire.PrivateStreamParser")</c> 必然返回 null，
/// 补丁静默装不上。
/// </para>
/// <para>
/// 另一个坑：NationalSpire 现在是从 <b>Steam 创意工坊</b>加载的
/// （<c>steamapps\workshop\content\2868840\3809501471\NationalSpire.dll</c>），
/// 而本地 <c>mods\NationalSpire\</c> 还留着旧版本（0.25.39）。
/// 两者 SHA256 不同——按编译期的 HintPath 去找会找错版本。
/// </para>
/// <para>
/// 所以这里优先通过 <see cref="ModManager"/> 的模组清单拿<b>真实路径</b>，
/// 那才是权威答案；拿不到才退回本地目录。
/// </para>
/// <para>
/// <b>★ 阶段 3 真机定案的根因与修法（改动本类之前必须先读懂）</b>：
/// 上一版用 <c>Assembly.LoadFrom</c> 加载，而它<b>永远装进 Default ALC</b>
/// （即使调用方在自定义 ALC 里——本机实测：自定义 ALC 里的代码调 <c>Assembly.LoadFrom</c>，
/// 结果落在 Default）。游戏自己的模组加载器则把同一个 DLL 装进
/// <c>IsolatedComponentLoadContext(…sts2.dll)</c>——那是<b>我们的模组所在的同一个 ALC</b>。
/// 于是同一份 DLL 在进程里有两份 <see cref="Assembly"/> 对象，
/// <c>FullName</c>/<c>Location</c> 完全相同，补丁全挂在 Default 那份死副本上，
/// 游戏执行的那份一个都没挂 ⇒ "挂载成功、token 都对，却永远不命中"。
/// </para>
/// <para>
/// 修法是把原版加载进 <b>我们自己所在的 ALC</b>（=<c>IsolatedComponentLoadContext</c>）：
/// ALC 内部按程序集身份去重（本机实测：同一 ALC 里第二次
/// <c>LoadFromAssemblyPath</c>／<c>LoadFromAssemblyName</c> 返回<b>同一个对象</b>），
/// 于是游戏加载器稍后来装同一个模组 DLL 时会直接拿到<b>我们已经打好补丁的那一份</b>。
/// 这样"我们挂的副本"与"游戏执行的副本"从根上是同一个对象。
/// </para>
/// </summary>
public static class NationalSpireLocator
{
    /// <summary>模组 id。</summary>
    public const string ModId = "NationalSpire";

    /// <summary>程序集简单名。</summary>
    public const string AssemblyName = "NationalSpire";

    /// <summary>数据目录下的程序集文件名。</summary>
    public const string FileName = "NationalSpire.dll";

    /// <summary>是否已经成功加载（诊断用）。</summary>
    public static bool Loaded { get; private set; }

    /// <summary>实际使用的路径（诊断用）。</summary>
    public static string LoadedFrom { get; private set; } = "";

    /// <summary>实际所在的 ALC 名（诊断用；"Default" 说明又踩了假副本的坑）。</summary>
    public static string LoadedContext { get; private set; } = "";

    /// <summary>加载失败原因（诊断用）。</summary>
    public static string FailureReason { get; private set; } = "";

    /// <summary>
    /// 保证 NationalSpire 程序集在 AppDomain 里，并返回它。
    /// 找不到或加载失败时返回 null，<b>不抛异常</b>。
    /// </summary>
    /// <param name="modsDir">mods 目录（回退查找用）。</param>
    /// <param name="dataDir">游戏程序集目录（回退查找用）。</param>
    public static Assembly? EnsureLoaded(string? modsDir = null, string? dataDir = null)
    {
        // 1) 我们自己所在的 ALC 里已经有了——这就是游戏执行的那一份，直接用它。
        var existing = FindInOurContext() ?? FindInAppDomain();
        if (existing != null)
        {
            Loaded = true;
            LoadedFrom = SafeLocation(existing);
            LoadedContext = AssemblyInventory.ContextOf(existing);
            return existing;
        }

        // 2) 按候选路径依次尝试。优先级见 CandidatePaths 的说明。
        foreach (string candidate in CandidatePaths(modsDir, dataDir))
        {
            var loaded = TryLoad(candidate);
            if (loaded != null)
            {
                Loaded = true;
                LoadedFrom = candidate;
                LoadedContext = AssemblyInventory.ContextOf(loaded);
                return loaded;
            }
        }

        Loaded = false;
        FailureReason = "在 AppDomain、模组清单、创意工坊与回退目录里都没找到 NationalSpire.dll。";
        return null;
    }

    /// <summary>
    /// 我们自己的模组 DLL 所在的 ALC。
    /// <para>
    /// 它<b>就是</b>游戏模组加载器用的那个 <c>IsolatedComponentLoadContext</c>：
    /// 游戏的 <c>ModManager</c> 在同一个 ALC 里装载并初始化所有模组，
    /// 所以"我们所在的 ALC"与"游戏执行 NationalSpire 的 ALC"是同一个。
    /// 拿不到（例如在 Godot 之外的单测进程里跑在 Default）时返回 Default。
    /// </para>
    /// </summary>
    public static AssemblyLoadContext OurContext
    {
        get
        {
            try
            {
                return AssemblyLoadContext.GetLoadContext(typeof(NationalSpireLocator).Assembly)
                       ?? AssemblyLoadContext.Default;
            }
            catch (Exception)
            {
                return AssemblyLoadContext.Default;
            }
        }
    }

    /// <summary>
    /// 把候选路径按优先级列出来。
    /// <para>
    /// <b>优先级顺序很关键</b>：原版按模组 id 取<b>版本号大者</b>，实测日志明确写着
    /// 「Steam version (v0.26.11) is greater than local version (v0.25.39),
    /// so we are disabling the local version」——本地那份是被禁用的死代码。
    /// 所以创意工坊版必须排在本地 mods 目录之前。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> CandidatePaths(string? modsDir = null, string? dataDir = null)
    {
        var paths = new List<string>();

        // ① 显式覆盖：玩家/开发者最清楚装在哪。
        string? overridden = Environment.GetEnvironmentVariable("NSE_NATIONALSPIRE_PATH");
        if (!string.IsNullOrWhiteSpace(overridden)) paths.Add(overridden!);

        // ② 模组清单登记的真实路径（权威答案）。
        string? fromManager = PathFromModManager();
        if (!string.IsNullOrWhiteSpace(fromManager)) paths.Add(fromManager!);

        // ③ 创意工坊目录里最新的那一份。
        paths.AddRange(WorkshopCandidates());

        // ④ 本地 mods 目录（通常是被禁用的旧版，只作兜底）。
        if (!string.IsNullOrWhiteSpace(modsDir))
            paths.Add(Path.Combine(modsDir!, ModId, FileName));

        // ⑤ 游戏程序集目录。
        if (!string.IsNullOrWhiteSpace(dataDir))
            paths.Add(Path.Combine(dataDir!, FileName));

        return paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// 扫创意工坊目录，按修改时间从新到旧返回 NationalSpire.dll。
    /// <para>
    /// 放在这里而不是写死一个 published file id：工坊条目的 id 每一份都不一样，
    /// 写死了换台机器就失效。扫目录能自动跟上。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> WorkshopCandidates()
    {
        var found = new List<string>();
        try
        {
            string? steamApps = SteamAppsDirectory();
            if (steamApps == null) return found;

            string contentDir = Path.Combine(steamApps, "workshop", "content", SteamAppId);
            if (!Directory.Exists(contentDir)) return found;

            foreach (string sub in Directory.GetDirectories(contentDir))
            {
                string candidate = Path.Combine(sub, FileName);
                if (File.Exists(candidate)) found.Add(candidate);
            }

            return found
                .OrderByDescending(p => new FileInfo(p).LastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception e)
        {
            FailureReason = "扫描创意工坊目录失败：" + e.Message;
            return found;
        }
    }

    /// <summary>Slay the Spire 2 的 Steam AppId。创意工坊内容按它分目录。</summary>
    public const string SteamAppId = "2868840";

    /// <summary>
    /// 从游戏自己的程序集位置反推 steamapps 目录。
    /// 例：<c>D:\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64</c>
    /// → <c>D:\Steam\steamapps</c>。
    /// </summary>
    private static string? SteamAppsDirectory()
    {
        string? dataDir = null;
        try
        {
            var sts2 = AssemblyInventory.AllAssemblies().FirstOrDefault(a => a.GetName().Name is "sts2");
            if (sts2?.Location is { Length: > 0 } location) dataDir = Path.GetDirectoryName(location);
        }
        catch (Exception)
        {
            // 拿不到就放弃这条路径。
        }

        if (string.IsNullOrWhiteSpace(dataDir)) return null;

        // ……\steamapps\common\Slay the Spire 2\data_xxx → 上溯三级到 steamapps
        var dir = new DirectoryInfo(dataDir!);
        for (int i = 0; i < 4 && dir != null; i++)
        {
            if (string.Equals(dir.Name, "steamapps", StringComparison.OrdinalIgnoreCase)) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>从 <see cref="ModManager.Mods"/> 里读 NationalSpire 的真实路径。</summary>
    private static string? PathFromModManager()
    {
        try
        {
            foreach (var mod in ModManager.Mods)
            {
                if (mod?.manifest == null) continue;
                if (!string.Equals(mod.manifest.id, ModId, StringComparison.Ordinal)) continue;
                if (string.IsNullOrWhiteSpace(mod.path)) continue;
                return Path.Combine(mod.path!, FileName);
            }
        }
        catch (Exception)
        {
            // 模组清单不可用（早期初始化、原版改结构……）时交给回退路径。
        }
        return null;
    }

    private static Assembly? FindInAppDomain()
    {
        try
        {
            foreach (var assembly in AssemblyInventory.GameAssemblies())
                return assembly;
        }
        catch (Exception)
        {
            // 枚举程序集失败不该让补丁流程崩掉。
        }
        return null;
    }

    /// <summary>
    /// 在我们自己所在的 ALC 里找 NationalSpire。
    /// <para>
    /// <b>必须优先找这一份</b>：游戏执行的就是它。只按"AppDomain 里第一份"去找，
    /// 会命中别处（例如别的模组或我们自己早期 <c>Assembly.LoadFrom</c> 造出来的 Default 假副本），
    /// 那样补丁又会挂到死副本上。
    /// </para>
    /// </summary>
    private static Assembly? FindInOurContext()
    {
        try
        {
            var context = OurContext;
            foreach (var assembly in context.Assemblies)
                if (AssemblyInventory.IsGameAssembly(assembly)) return assembly;
        }
        catch (Exception)
        {
            // 拿不到就交给 FindInAppDomain。
        }
        return null;
    }

    /// <summary>
    /// 把 NationalSpire.dll 装进<b>我们自己所在的 ALC</b>。
    /// <para>
    /// <b>这里绝不能再用 <c>Assembly.LoadFrom</c></b>：它总是装进 Default ALC
    /// （本机实测：即使调用方在自定义 ALC 里也照样落 Default）。
    /// 那样会造出一份游戏<b>永远不会执行</b>的假副本，
    /// 于是"补丁挂载成功、token 全对、真实私信零命中"（阶段 3 那个卡点）。
    /// </para>
    /// <para>
    /// 装进自己的 ALC 之后，ALC 会按程序集身份去重：游戏加载器稍后来装同一个模组 DLL 时，
    /// <c>LoadFromAssemblyPath</c>／<c>LoadFromAssemblyName</c> 会返回
    /// <b>我们这一份已经打好补丁的对象</b>（本机实测过该行为）。
    /// </para>
    /// </summary>
    private static Assembly? TryLoad(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            full = path;
        }

        // 首选：装进我们自己所在的 ALC（= 游戏执行模组的那个 ALC）。
        try
        {
            return OurContext.LoadFromAssemblyPath(full);
        }
        catch (Exception e)
        {
            // 落到兜底之前必须留痕：装错 ALC 就是本轮的根因，绝不能静默。
            FailureReason = $"装进 {AssemblyInventory.ContextOf(typeof(NationalSpireLocator).Assembly)} " +
                            $"所在的 ALC 失败（{e.GetType().Name}: {e.Message}），改用兜底加载。";
        }

        // 兜底：只有在上面的路径真的不可用时才用 LoadFrom。
        // 注意它总是装进 Default——那份可能不是游戏执行的副本，所以这条路径
        // 必须靠 EconomyModEntry 的"落点警卫"（新副本出现时自动补挂）兜住。
        try
        {
            return Assembly.LoadFrom(full);
        }
        catch (Exception e)
        {
            FailureReason = $"{path} 加载失败：{e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    private static string SafeLocation(Assembly assembly)
    {
        try
        {
            return assembly.Location;
        }
        catch (Exception)
        {
            return "(未知位置)";
        }
    }
}
