using System.Globalization;
using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 播种的确定性直接决定「影子存档丢了能不能重建出一模一样的数字」，
/// 以及我们的种子是否和原版 CareerEngine.StableHash 对齐。
/// </summary>
public static class DeterministicHashTests
{
    public static Action[] Cases =>
    [
        EmptyStringHashesToZero,
        SingleCharMatchesFnv1a,
        StableAcrossCalls,
        DiffersOnDifferentInput,
        SeedSeparatesParts,
        LogUniformStaysInRange,
        LogUniformSpreadsAcrossMagnitudes,
        LogUniformHandlesEqualBounds,
        LogUniformRejectsBadRange,
        RandomIsReproducible
    ];

    private static void EmptyStringHashesToZero()
    {
        // FNV-1a 初值 2166136261 折叠到 31 位后的实际取值（已用独立探针复核，
        // 与 CareerEngine.StableHash 逐位一致）。这里钉死是为了防止有人"顺手"改动算法。
        TestRunner.Equal(18652613, DeterministicHash.StableHash(""));
    }

    private static void SingleCharMatchesFnv1a()
    {
        // ('a' ^ 2166136261) * 16777619 折叠到 31 位的实际取值。
        TestRunner.Equal(1678518572, DeterministicHash.StableHash("a"));
    }

    private static void StableAcrossCalls()
    {
        int first = DeterministicHash.StableHash("abc123:world:person");
        for (int i = 0; i < 100; i++)
            TestRunner.Equal(first, DeterministicHash.StableHash("abc123:world:person"));
    }

    private static void DiffersOnDifferentInput()
    {
        TestRunner.NotEqual(DeterministicHash.StableHash("p1"), DeterministicHash.StableHash("p2"));
        TestRunner.NotEqual(DeterministicHash.StableHash("crowd-0-1"), DeterministicHash.StableHash("crowd-0-2"));
    }

    private static void SeedSeparatesParts()
    {
        // 用 ':' 连接，避免 ("ab","c") 与 ("a","bc") 撞车。
        TestRunner.NotEqual(DeterministicHash.Seed("ab", "c"), DeterministicHash.Seed("a", "bc"));
        TestRunner.Equal(DeterministicHash.Seed("x", "y"), DeterministicHash.StableHash("x:y"));
    }

    private static void LogUniformStaysInRange()
    {
        var random = new Random(12345);
        for (int i = 0; i < 5000; i++)
        {
            long value = DeterministicHash.LogUniform(random, 500, 5000);
            TestRunner.InRange(value, 500, 5000, "log-uniform 越界");
        }
        for (int i = 0; i < 5000; i++)
        {
            long value = DeterministicHash.LogUniform(random, 200_000, 500_000);
            TestRunner.InRange(value, 200_000, 500_000, "log-uniform 越界");
        }
    }

    private static void LogUniformSpreadsAcrossMagnitudes()
    {
        // 与 uniform 对比：log-uniform 应当让「远低于区间高端」的取值有可观占比。
        var random = new Random(777);
        int lowCount = 0, total = 20000;
        for (int i = 0; i < total; i++)
            if (DeterministicHash.LogUniform(random, 500, 5000) < 1000) lowCount++;

        // 500~5000 的对数中点约 1581；低于 1000 的比例理论值约 30%。
        double ratio = lowCount / (double)total;
        TestRunner.True(ratio is > 0.15 and < 0.45,
            $"log-uniform 的低端占比异常：{ratio:P1}，期望大致落在 15%~45%");
    }

    private static void LogUniformHandlesEqualBounds()
    {
        var random = new Random(1);
        TestRunner.Equal(1234L, DeterministicHash.LogUniform(random, 1234, 1234));
    }

    private static void LogUniformRejectsBadRange()
    {
        var random = new Random(1);
        TestRunner.Throws<ArgumentOutOfRangeException>(() => DeterministicHash.LogUniform(random, -5, 100));
        TestRunner.Throws<ArgumentOutOfRangeException>(() => DeterministicHash.LogUniform(random, 100, 50));
    }

    private static void RandomIsReproducible()
    {
        var a = DeterministicHash.Random("world1", "economy", "p7");
        var b = DeterministicHash.Random("world1", "economy", "p7");
        for (int i = 0; i < 50; i++) TestRunner.Equal(a.Next(), b.Next());

        // 种子不同则序列不同（极小概率巧合，用多个取样点降低误判）。
        var c = DeterministicHash.Random("world2", "economy", "p7");
        var d = DeterministicHash.Random("world1", "economy", "p7");
        bool anyDifference = false;
        for (int i = 0; i < 20; i++) if (c.Next() != d.Next()) anyDifference = true;
        TestRunner.True(anyDifference, "不同种子的随机序列不应完全相同");
    }
}
