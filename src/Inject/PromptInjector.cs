using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Inject;

/// <summary>一次注入的产物。三段分开给出，调用方按自己的拼接点放。</summary>
public sealed record PromptInjection(
    string StableText,
    string VolatileText,
    string Text,
    string Fingerprint,
    bool StableChanged,
    bool PendingVoiceInjected);

/// <summary>
/// <b>只合成易变段</b>的产物（阶段 3-B）。
/// <para>
/// 刻意不复用 <see cref="PromptInjection"/>：那个记录的 <c>Fingerprint</c> 是<b>稳定段</b>的指纹，
/// 会被写进存档参与缓存判定；而易变段的指纹只是诊断用的，两者混在一个字段里迟早会被误用。
/// </para>
/// </summary>
public sealed record VolatileInjection(
    string Text,
    string Fingerprint,
    bool PendingVoiceInjected,
    bool Enabled)
{
    /// <summary>是否真的产出了易变段正文。</summary>
    public bool HasText => Text.Length > 0;
}

/// <summary>
/// Prompt 注入的编排层：把 <see cref="StableSegment"/> 与 <see cref="VolatileSegment"/>
/// 合成，并按 <see cref="CacheGuard"/> 的判定决定稳定段是否重写。
/// <para>
/// <b>纯逻辑</b>：不碰 Godot、不碰补丁、不碰 UI。它只接受一个
/// <see cref="PromptInjectInput"/> 并返回字符串，所以整段注入行为都可以单测。
/// </para>
/// <para>
/// <b>三段切分</b>（保护前缀缓存）：
/// <list type="table">
/// <item><term>S（稳定）</term><description>量级档位、收入来源、金钱观、意向措辞。零数字，每 7 天或跨赛季才动。</description></item>
/// <item><term>V（易变）</term><description>精确余额、最近一笔流水、本季累计收支、PendingVoice。每笔交易都变。</description></item>
/// <item><term>I（意向）</term><description>意向<b>类型</b>进 S（低频），意向<b>细节</b>由 V 承载。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class PromptInjector
{
    private readonly IEconomyLog _log;

    public PromptInjector(IEconomyLog? log = null) => _log = log ?? ConsoleEconomyLog.Instance;

    /// <summary>
    /// 合成注入文本。
    /// </summary>
    /// <param name="input">求值输入。</param>
    /// <param name="commit">
    /// 为 true 时提交副作用：更新 <c>StableSegmentFingerprint</c>，
    /// 并在 <c>PendingVoice</c> 确实被注入之后清空它。
    /// 单测里传 false 可以反复观察同一个状态。
    /// </param>
    public PromptInjection Build(PromptInjectInput input, bool commit = true)
    {
        ArgumentNullException.ThrowIfNull(input);

        bool stableEnabled = input.Prompt.Get("stableEnabled", "true") is not "false";
        bool pendingVoiceWillBeInjected = VolatileSegment.WillIncludePendingVoice(input);

        string stableText = stableEnabled ? StableSegment.Build(input) : "";
        string fingerprint = CacheGuard.FingerprintOf(stableText);
        string volatileText = VolatileSegment.Build(input);
        string text = Compose(stableText, volatileText);

        bool changed = false;
        if (commit)
        {
            changed = CacheGuard.RefreshFingerprint(input.World, fingerprint);
            ClearPendingVoice(input.Npc, pendingVoiceWillBeInjected);
        }
        else
        {
            // 不提交时也要如实回答"会不会变"，否则调用方无法据此决定是否重发。
            changed = !string.Equals(input.World.StableSegmentFingerprint, fingerprint, StringComparison.Ordinal)
                && input.World.StableSegmentFingerprint.Length > 0;
        }

        return new PromptInjection(stableText, volatileText, text, fingerprint, changed, pendingVoiceWillBeInjected);
    }

    /// <summary>
    /// <b>只合成稳定段</b>（阶段 3 第一步的接线路径）。
    /// <para>
    /// 为什么不能直接复用 <see cref="Build"/>：它在提交副作用时会清空
    /// <c>PendingVoice</c>，而"未竟之言"是由<b>易变段</b>承载的。这一轮只注入 S 段、
    /// 不注入 V 段，若照旧清空就等于把软失败留下的那句话悄悄吞掉——
    /// 模型永远看不到它，而存档里也没有了。
    /// </para>
    /// <para>
    /// 返回的 <see cref="PromptInjection.VolatileText"/> 恒为空字符串，
    /// <see cref="PromptInjection.PendingVoiceInjected"/> 恒为 false，
    /// 于是"这一轮没有注入易变段"这件事在类型上就写明了，不会被误当成已注入。
    /// </para>
    /// </summary>
    public PromptInjection BuildStableOnly(PromptInjectInput input, bool commit = true)
    {
        ArgumentNullException.ThrowIfNull(input);

        bool stableEnabled = input.Prompt.Get("stableEnabled", "true") is not "false";
        string stableText = stableEnabled ? StableSegment.Build(input) : "";
        string fingerprint = CacheGuard.FingerprintOf(stableText);

        bool changed;
        if (commit)
        {
            changed = CacheGuard.RefreshFingerprint(input.World, fingerprint);
        }
        else
        {
            // 与 Build 的语义保持一致：不提交时也要如实回答"会不会变"。
            changed = !string.Equals(input.World.StableSegmentFingerprint, fingerprint, StringComparison.Ordinal)
                && input.World.StableSegmentFingerprint.Length > 0;
        }

        return new PromptInjection(stableText, "", stableText, fingerprint, changed, false);
    }

    /// <summary>
    /// 只算稳定段在"给定输入下"的指纹，不改任何状态。
    /// 用于接线层判断"是否需要重写系统提示的前半段"。
    /// </summary>
    public static string FingerprintOf(PromptInjectInput input) => CacheGuard.Fingerprint(input);

    /// <summary>
    /// <b>只合成易变段</b>（阶段 3-B 的接线路径）。
    /// <para>
    /// 与 <see cref="BuildStableOnly"/> 对称，但有三处<b>刻意不同</b>：
    /// <list type="number">
    /// <item>它读的全是易变数据（余额、本赛季收支、最近一笔流水、<c>PendingVoice</c>），
    /// 所以<b>数字只允许出现在它产出的文本里</b>。</item>
    /// <item>它<b>不碰任何指纹</b>：易变段没有缓存命中可言，写存档只会误导后人。</item>
    /// <item>清空 <c>PendingVoice</c> 这件事<b>只有注入真的发生了才做</b>，
    /// 所以走 <c>commit: false</c> 先问一遍、插入成功后再调 <see cref="ClearPendingVoice"/>。
    /// 直接给 <c>commit: true</c> 只用于"确定会插入"的场景与单测。</item>
    /// </list>
    /// </para>
    /// </summary>
    public VolatileInjection BuildVolatileOnly(PromptInjectInput input, bool commit = false)
    {
        ArgumentNullException.ThrowIfNull(input);

        bool enabled = input.Prompt.Get("volatileEnabled", "true") is not "false";
        string text = enabled ? VolatileSegment.Build(input) : "";
        bool willInjectVoice = enabled && VolatileSegment.WillIncludePendingVoice(input);

        if (commit) ClearPendingVoice(input.Npc, willInjectVoice);

        return new VolatileInjection(text, VolatileSegment.FingerprintOf(text), willInjectVoice, enabled);
    }

    /// <summary>拼接两段。稳定段为空时不留多余空行。</summary>
    public static string Compose(string stableText, string volatileText)
    {
        if (stableText.Length == 0) return volatileText;
        if (volatileText.Length == 0) return stableText;
        return stableText + "\n\n" + volatileText;
    }

    /// <summary>
    /// 注入之后清空"未竟之言"。
    /// <para>
    /// 只清这一次真正被注入的那句：如果 <c>PendingVoice</c> 在读完之后又被别处改了
    /// （例如同一轮里又发生了一次软失败），那不是我们该抹掉的。
    /// </para>
    /// </summary>
    public void ClearPendingVoice(NpcEconomy npc, bool wasInjected)
    {
        if (!wasInjected) return;
        if (npc.PendingVoice.Length == 0) return;
        npc.PendingVoice = "";
    }
}
