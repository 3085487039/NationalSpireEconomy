using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Inject;

/// <summary>
/// 一次 Prompt 注入所需的全部输入。
/// <para>
/// 刻意把"稳定段用得到的东西"与"易变段用得到的东西"放在同一个对象里：
/// 这样 <see cref="StableSegment"/> 只需要读它的一部分，
/// 而"稳定段是否被易变数据污染"这件事就变成了可断言的<b>输入隔离</b>问题，
/// 而不是"靠调用方自觉"。
/// </para>
/// </summary>
public sealed record PromptInjectInput(
    EconomyWorld World,
    NpcEconomy Npc,
    INpcFacts? Facts,
    string TierId,
    PersonalityConfig Personality,
    PromptConfig Prompt,
    int Favour,
    int Day,
    int Season,
    /// <summary>
    /// 追加进<b>易变段</b>的额外只读注记（例如"待确认交易的只读预览"）。
    /// <para>
    /// 之所以挂在 V 段而不是 S 段：预览里一定有金额（数字），而 S 段受"零数字"纪律约束
    /// （见 <c>ConfigLoader.ValidatePrompt</c>）。调用方负责保证这里的内容<b>只读</b>。
    /// </para>
    /// </summary>
    string? ExtraVolatileNote = null);
