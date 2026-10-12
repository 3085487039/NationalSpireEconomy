namespace NationalSpireEconomy.Core;

/// <summary>
/// 确定性随机。与 NationalSpire 的 <c>CareerEngine.StableHash</c>（FNV-1a，32 位，取正）逐位等价，
/// 这样同一个 <c>WorldId + PersonId</c> 在我们的模组里和在原模组里会得到相同的种子，
/// 影子存档丢失后可以确定性重放出一模一样的初始资金。
/// </summary>
public static class DeterministicHash
{
    public static int StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in value)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)(hash & 0x7fffffff);
        }
    }

    /// <summary>把若干片段拼成一个稳定种子。片段之间用 <c>:</c> 分隔，避免歧义。</summary>
    public static int Seed(params string[] parts) => StableHash(string.Join(":", parts));

    public static Random Random(params string[] parts) => new(Seed(parts));

    /// <summary>
    /// log-uniform 取整分布：让结果在 [min, max] 上按数量级均匀铺开，
    /// 而不是像 uniform 那样挤在区间高端。用于初始资金播种。
    /// </summary>
    public static long LogUniform(Random random, long min, long max)
    {
        if (min < 0) throw new ArgumentOutOfRangeException(nameof(min), "金额不能为负。");
        if (max < min) throw new ArgumentOutOfRangeException(nameof(max), "上限不能小于下限。");
        if (min == max) return min;
        if (min == 0) min = 1;

        double lo = Math.Log(min);
        double hi = Math.Log(max);
        double value = Math.Exp(lo + random.NextDouble() * (hi - lo));

        long rounded = (long)Math.Round(value);
        // 对金额取整到「10 的整数次幂量级上的一档」，避免出现 137942 这种假精确。
        long step = MagnitudeStep(min, max);
        rounded = rounded / step * step;
        return Math.Clamp(rounded, min, max);
    }

    /// <summary>按区间量级挑一个取整步长：小钱取整到 100，大钱取整到 10000。</summary>
    private static long MagnitudeStep(long min, long max)
    {
        return max switch
        {
            < 1_000 => 10,
            < 10_000 => 100,
            < 100_000 => 500,
            < 1_000_000 => 1_000,
            _ => 10_000
        };
    }
}
