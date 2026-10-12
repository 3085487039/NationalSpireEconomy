using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Game;

/// <summary>
/// 模组的运行时状态。整个进程一份，由 <see cref="EconomyModEntry"/> 建立。
/// <para>
/// <b>只在这里持有跨模块的引用</b>：补丁层、注入层、交易层都从这里取配置与存档，
/// 而不是各自 new 一份。否则"配置热重载"会只改到其中一份，出现两套并存的配置。
/// </para>
/// <para>
/// <see cref="Enabled"/> 为 false 时所有下游功能都必须停摆。它只在
/// <b>配置有 Error</b> 或 <b>初始化抛异常</b>时为 false——绝不带着坏配置继续跑。
/// </para>
/// </summary>
public sealed class ModState
{
    private ModState() { }

    /// <summary>配置加载结果。未启用时也可能是最后一次的加载结果（用于展示错误）。</summary>
    public ConfigLoadResult? Config { get; private init; }
    public EconomyStore? Store { get; private init; }
    public required IEconomyLog Log { get; init; }

    /// <summary>存档根目录（已解析成真实路径）。未启用时为空串。</summary>
    public string DataRoot { get; private init; } = "";

    /// <summary>模组是否可用。false 时下游必须全部停摆。</summary>
    public bool Enabled { get; private init; }

    /// <summary>初始化失败的阶段名，便于定位。</summary>
    public string FailureStage { get; private init; } = "";

    /// <summary>初始化失败的说明。</summary>
    public string FailureDetail { get; private init; } = "";

    /// <summary>已经装上的 Harmony 补丁。</summary>
    public IReadOnlyList<string> Patches { get; private init; } = [];

    public EconomyConfig? Economy => Config?.Economy;
    public PersonalityConfig? Personality => Config?.Personality;
    public PromptConfig? Prompt => Config?.Prompt;

    /// <summary>配置校验里的 Error 条数。</summary>
    public int ErrorCount => Config?.Errors.Count() ?? 0;

    /// <summary>配置校验里的 Warning 条数。</summary>
    public int WarningCount => Config?.Warnings.Count() ?? 0;

    /// <summary>构造一个"已就绪"的状态。</summary>
    public static ModState Ready(ConfigLoadResult config, EconomyStore store, IEconomyLog log, string dataRoot,
        IReadOnlyList<string>? patches = null) =>
        new()
        {
            Config = config,
            Store = store,
            Log = log,
            DataRoot = dataRoot,
            Enabled = true,
            Patches = patches ?? []
        };

    /// <summary>
    /// 构造一个"未启用"的状态。<paramref name="config"/> 可以为 null
    /// （连配置都没读出来时），但要把失败阶段与原因带上，便于日志定位。
    /// </summary>
    public static ModState Disabled(IEconomyLog log, string stage, string detail,
        ConfigLoadResult? config = null, string dataRoot = "") =>
        new()
        {
            Config = config,
            Log = log,
            DataRoot = dataRoot,
            Enabled = false,
            FailureStage = stage,
            FailureDetail = detail
        };

    /// <summary>诊断摘要，用于启动时打一行日志。</summary>
    public string Describe()
    {
        if (!Enabled) return $"未启用（阶段 {FailureStage}）：{FailureDetail}";
        string patches = Patches.Count > 0 ? string.Join(" + ", Patches) : "无";
        return $"已启用 | 数据 {DataRoot} | 配置 {Config!.RootDirectory} | " +
               $"错误 {ErrorCount} 警告 {WarningCount} | 补丁 {patches}";
    }
}
