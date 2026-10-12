using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Inject;

/// <summary>
/// 前缀缓存的守卫：判断"稳定段这次是否真的变了"。
/// <para>
/// 它存在的理由：稳定段的文本是<b>算出来的</b>，而调用方每轮都会重新算一遍。
/// 如果没有指纹比对，任何"顺手重写一下"都会把 <c>StableSegmentFingerprint</c>
/// 变成一个新值，于是下游会以为缓存失效并重发整段——前缀缓存就白做了。
/// </para>
/// <para>
/// <b>语义约定</b>：<see cref="Refresh"/> 只在"指纹发生了变化"时返回 true。
/// 首次写入（原指纹为空）返回 false，因为那时还没有缓存可供失效。
/// </para>
/// </summary>
public static class CacheGuard
{
    /// <summary>计算指纹。同样的输入必须给出同样的指纹。</summary>
    public static string Fingerprint(PromptInjectInput input, string? bandId = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        string stableText = StableSegment.Build(input, bandId);
        return FingerprintOf(stableText);
    }

    /// <summary>对已经算好的稳定段文本取指纹。</summary>
    public static string FingerprintOf(string stableText)
    {
        // 只依赖稳定段正文本身：正文一样，指纹就必须一样。
        return DeterministicHash.StableHash(stableText).ToString("x8");
    }

    /// <summary>
    /// 稳定段是否需要重写。
    /// <list type="bullet">
    /// <item>原指纹为空（首次）→ false，并把指纹写进去。</item>
    /// <item>新指纹与原指纹相同 → false，且<b>不</b>改动存档字段。</item>
    /// <item>新指纹不同 → true，并更新指纹。</item>
    /// </list>
    /// </summary>
    public static bool Refresh(EconomyWorld world, PromptInjectInput input, string? bandId = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(input);
        return RefreshFingerprint(world, Fingerprint(input, bandId));
    }

    /// <summary>同上，但直接给指纹。</summary>
    public static bool RefreshFingerprint(EconomyWorld world, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (string.Equals(world.StableSegmentFingerprint, fingerprint, StringComparison.Ordinal))
            return false;

        bool hadPrevious = world.StableSegmentFingerprint.Length > 0;
        world.StableSegmentFingerprint = fingerprint;

        // 首次建立指纹不算"缓存失效"——那时还没有缓存可言。
        return hadPrevious;
    }

    /// <summary>只判断是否变化，<b>不改动</b>存档。用于"先问再决定"的场景。</summary>
    public static bool HasChanged(EconomyWorld world, PromptInjectInput input, string? bandId = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        return !string.Equals(world.StableSegmentFingerprint, Fingerprint(input, bandId), StringComparison.Ordinal);
    }
}
