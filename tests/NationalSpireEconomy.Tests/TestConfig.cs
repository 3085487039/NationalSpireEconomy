namespace NationalSpireEconomy.Tests;

/// <summary>
/// 找仓库里那份<b>真实的</b> <c>config/</c> 目录。
/// <para>
/// 为什么要有这么一个共享类：这段"从 <see cref="AppContext.BaseDirectory"/> 逐级向上找
/// <c>config/economy_config.json</c>"的代码原先在 <b>19 个测试文件里各抄了一份</b>
/// （有的返回目录、有的返回 <c>ConfigLoadResult</c>、有的只返回 <c>EconomyConfig</c>）。
/// 副本之间已经开始漂移，而且谁都不会注意到。收敛到这里之后只有一处需要维护。
/// </para>
/// <para>
/// 用真实配置是有意的：这些用例同时是"配置自洽性"的回归测试 —— 仓库里的
/// <c>config/*.json</c> 改了、坏了，测试会当场变红，而不是只验一堆构造出来的假对象。
/// </para>
/// </summary>
internal static class TestConfig
{
    /// <summary>仓库里 <c>config/</c> 的绝对路径。找不到就抛（那是环境问题，不该静默）。</summary>
    public static string Directory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "config");
            if (File.Exists(Path.Combine(candidate, NationalSpireEconomy.Core.ConfigLoader.EconomyFileName)))
                return candidate;
            directory = directory.Parent;
        }
        throw new Exception("找不到 config/ 目录（测试需要仓库里的真实配置）");
    }

    /// <summary>加载仓库里的三份真实配置。</summary>
    public static NationalSpireEconomy.Core.ConfigLoadResult Load()
        => new NationalSpireEconomy.Core.ConfigLoader(Directory()).Load();

    /// <summary>只取经济配置那一份（最常用的简写）。</summary>
    public static NationalSpireEconomy.Core.EconomyConfig Economy() => Load().Economy;

    /// <summary>
    /// 把三份真实配置拷进一个临时目录，返回该目录。
    /// <para>给那些需要"改配置再加载"的用例用（例如临时打开探针开关），
    /// 这样它们就能改副本而不动仓库里的真文件。</para>
    /// </summary>
    public static string CopyTo(string target)
    {
        System.IO.Directory.CreateDirectory(target);
        string source = Directory();
        foreach (string name in new[]
                 {
                     NationalSpireEconomy.Core.ConfigLoader.EconomyFileName,
                     NationalSpireEconomy.Core.ConfigLoader.PersonalityFileName,
                     NationalSpireEconomy.Core.ConfigLoader.PromptFileName
                 })
            File.Copy(Path.Combine(source, name), Path.Combine(target, name), overwrite: true);
        return target;
    }
}
