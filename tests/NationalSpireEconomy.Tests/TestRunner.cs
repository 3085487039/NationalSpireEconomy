namespace NationalSpireEconomy.Tests;

/// <summary>
/// 零依赖测试运行器。本机 NuGet 出网被沙箱拦截，无法还原 xunit/nunit，
/// 因此用自建断言 + 退出码来保证测试是真能跑起来的，而不是纸面设计。
/// </summary>
public static class TestRunner
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = [];

    public static void Run(params (string Suite, Action[] Cases)[] suites)
    {
        foreach (var (suite, cases) in suites)
        {
            Console.WriteLine();
            Console.WriteLine($"── {suite} ──");
            foreach (var test in cases)
            {
                string name = test.Method.Name;
                try
                {
                    test();
                    _passed++;
                    Console.WriteLine($"  PASS  {name}");
                }
                catch (Exception e)
                {
                    _failed++;
                    Failures.Add($"{suite}.{name}: {e.Message}");
                    Console.WriteLine($"  FAIL  {name}");
                    Console.WriteLine($"        {e.GetType().Name}: {e.Message}");
                    // 前几帧足够定位到具体调用点，比反复读代码快得多。
                    if (e.StackTrace != null)
                        foreach (string frame in e.StackTrace.Split('\n').Take(4))
                            Console.WriteLine("          " + frame.Trim());
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"总计 {_passed + _failed}：通过 {_passed}，失败 {_failed}");
        if (_failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败明细：");
            foreach (string failure in Failures) Console.WriteLine("  - " + failure);
        }
    }

    public static int ExitCode => _failed == 0 ? 0 : 1;

    // ── 断言 ──────────────────────────────────────────────────────────

    public static void True(bool condition, string message = "断言失败：期望为 true")
    {
        if (!condition) throw new Exception(message);
    }

    public static void False(bool condition, string message = "断言失败：期望为 false")
    {
        if (condition) throw new Exception(message);
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{message ?? "值不相等"}：期望 <{expected}>，实际 <{actual}>");
    }

    public static void NotEqual<T>(T unexpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            throw new Exception($"{message ?? "值不应相等"}：两者都是 <{actual}>");
    }

    public static void Throws<TException>(Action action, string? message = null) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception e)
        {
            throw new Exception($"{message ?? "异常类型不符"}：期望 {typeof(TException).Name}，实际 {e.GetType().Name}（{e.Message}）");
        }
        throw new Exception($"{message ?? "未抛异常"}：期望 {typeof(TException).Name}");
    }

    public static void InRange(long value, long min, long max, string? message = null)
    {
        if (value < min || value > max)
            throw new Exception($"{message ?? "超出区间"}：{value} 不在 [{min}, {max}] 内");
    }

    public static void Contains(string haystack, string needle, string? message = null)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
            throw new Exception($"{message ?? "未包含子串"}：在 <{haystack}> 中找不到 <{needle}>");
    }

    public static void NotContains(string haystack, string needle, string? message = null)
    {
        if (haystack.Contains(needle, StringComparison.Ordinal))
            throw new Exception($"{message ?? "不应包含子串"}：<{haystack}> 中出现了 <{needle}>");
    }
}
