namespace NationalSpireEconomy.Core;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// 日志出口。Core 层不依赖 Godot，所以这里只定义接口；
/// 接线层（第 7 步）会实现一个转接到原版 <c>Diagnostics.Record</c> 与 <c>GD.PushWarning</c> 的实现。
/// </summary>
public interface IEconomyLog
{
    void Write(LogLevel level, string category, string message, Exception? error = null);
}

/// <summary>默认实现：写标准输出。用于单测与无游戏环境下的自检。</summary>
public sealed class ConsoleEconomyLog : IEconomyLog
{
    public static readonly ConsoleEconomyLog Instance = new();

    public void Write(LogLevel level, string category, string message, Exception? error = null)
    {
        string line = $"[{DateTime.UtcNow:HH:mm:ss}] [{level}] [{category}] {message}";
        if (error != null) line += Environment.NewLine + error;
        if (level >= LogLevel.Warning) Console.Error.WriteLine(line);
        else Console.WriteLine(line);
    }
}

/// <summary>把日志转发给任意委托，便于测试断言与接线层适配。</summary>
public sealed class DelegateEconomyLog : IEconomyLog
{
    private readonly Action<LogLevel, string, string, Exception?> _sink;

    public DelegateEconomyLog(Action<LogLevel, string, string, Exception?> sink) => _sink = sink;

    public void Write(LogLevel level, string category, string message, Exception? error = null)
        => _sink(level, category, message, error);
}

/// <summary>便捷扩展，统一前缀成 <c>economy.*</c>，与原版 Diagnostics 的命名风格一致。</summary>
public static class EconomyLogExtensions
{
    public static void Debug(this IEconomyLog log, string category, string message)
        => log.Write(LogLevel.Debug, "economy." + category, message);

    public static void Info(this IEconomyLog log, string category, string message)
        => log.Write(LogLevel.Info, "economy." + category, message);

    public static void Warn(this IEconomyLog log, string category, string message, Exception? error = null)
        => log.Write(LogLevel.Warning, "economy." + category, message, error);

    public static void Error(this IEconomyLog log, string category, string message, Exception? error = null)
        => log.Write(LogLevel.Error, "economy." + category, message, error);
}
