namespace NationalSpireEconomy.Tests;

/// <summary>
/// 零依赖测试运行器。本机 NuGet 出网被沙箱拦截，无法还原 xunit/nunit，
/// 因此用自建断言 + 退出码来保证测试是真能跑起来的，而不是纸面设计。
/// </summary>
public static class TestRunner
{
    private static int _passed;
    private static int _failed;
    private static int _skipped;
    private static readonly List<string> Failures = [];
    private static readonly List<string> Skips = [];

    public static void Run(params (string Suite, Action[] Cases)[] suites)
    {
        foreach (var (suite, cases) in suites)
        {
            Console.WriteLine();
            Console.WriteLine($"── {suite} ──");
            foreach (var test in cases)
            {
                string name = test.Method.Name;
                _currentSuite = suite;
                _currentTest = name;
                _skipReason = null;
                try
                {
                    test();
                    if (_skipReason != null)
                    {
                        // 用例主动声明"环境不具备，这次没验到"。
                        _skipped++;
                        Skips.Add($"{suite}.{name}: {_skipReason}");
                        Console.WriteLine($"  SKIP  {name}");
                        Console.WriteLine($"        {_skipReason}");
                    }
                    else
                    {
                        _passed++;
                        Console.WriteLine($"  PASS  {name}");
                    }
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
                finally
                {
                    _currentSuite = null;
                    _currentTest = null;
                    _skipReason = null;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"总计 {_passed + _failed + _skipped}：通过 {_passed}，失败 {_failed}，跳过 {_skipped}");
        if (_skipped > 0)
        {
            // ★ 跳过必须显眼：这是"最贵的回归测试在干净环境里悄悄变成空操作"的解药。
            //   以前用例只能 `return`，输出上与 PASS 毫无区别，于是没人知道它没验到。
            Console.WriteLine();
            Console.WriteLine("跳过明细（环境不具备，这些用例这次【没有】验证任何东西）：");
            foreach (string skip in Skips) Console.WriteLine("  - " + skip);
        }
        if (_failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败明细：");
            foreach (string failure in Failures) Console.WriteLine("  - " + failure);
        }
    }

    public static int ExitCode => _failed == 0 ? 0 : 1;

    // ── 跳过 ──────────────────────────────────────────────────────────

    private static string? _currentSuite;
    private static string? _currentTest;
    private static string? _skipReason;

    /// <summary>
    /// 声明"本用例需要的环境不具备，这次没有验证任何东西"，然后<b>立即返回</b>。
    /// <para>
    /// <b>为什么需要它</b>：以前这几条用例只能裸 <c>return</c>，输出上与 <c>PASS</c>
    /// 完全一样 —— 于是"多副本挂载"这种最贵的回归测试，在没有上游 DLL 的干净环境里
    /// （比如 CI）会**静默变成空操作**，而没人看得出来。跳过统计与明细就是为了让这件事可见。
    /// </para>
    /// <example>
    /// <code>
    /// if (probe == null) { TestRunner.Skip("找不到可用于多副本验证的 DLL"); return; }
    /// </code>
    /// </example>
    /// </summary>
    public static void Skip(string reason)
    {
        _skipReason = reason;
    }

    /// <summary>本进程累计跳过的用例数（供"跳过会不会太多"这类自查用）。</summary>
    public static int SkippedCount => _skipped;

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
