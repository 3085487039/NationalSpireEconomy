using System.Text;

namespace NationalSpireEconomy.Game;

/// <summary>
/// 日志的下沉出口。刻意只吃"已经渲染好的文本行"，不认识日志级别，
/// 这样单测可以直接塞一个记录用的假实现，不需要碰真实文件系统。
/// </summary>
public interface IEconomyFileSink
{
    /// <summary>落点描述（写进日志头，便于事后确认到底写到了哪里）。</summary>
    string Describe { get; }

    /// <summary>追加一行。<b>永远不抛异常</b>；写不进去就返回 false。</summary>
    bool TryAppend(string line);
}

/// <summary>
/// 把一行文本写进一个"我们自己的" <see cref="TextWriter"/>。
/// <para>
/// 供单测使用：测试可以在内存里接一个 <see cref="StringWriter"/>，
/// 也可以自己打开文件流来验证真实落盘。
/// </para>
/// </summary>
public sealed class TextWriterSink : IEconomyFileSink
{
    private readonly TextWriter _writer;

    public TextWriterSink(TextWriter writer, string describe)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        Describe = describe;
    }

    public string Describe { get; }

    public bool TryAppend(string line)
    {
        try
        {
            _writer.WriteLine(line);
            _writer.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// <b>保底日志通道</b>：纯 <see cref="File"/> 追加写，<b>完全不经过 Godot</b>。
/// <para>
/// <b>为什么必须有它（实测事故）</b>：模组初始化时 Godot 原生运行时可能还没就绪，
/// 于是控制台通道（<c>GD.Print</c>）绑不上、诊断文件通道也接不上，
/// 结果整个模组<b>一个字都出不来</b>——本轮排查被迫摸黑了好几轮。
/// 写文件不需要 Godot 原生函数，也不需要管理员权限，是最不可能再断的一条通道。
/// </para>
/// <para>
/// <b>落点策略</b>：影子存档目录优先（<c>%APPDATA%\SlayTheSpire2\national_spire_economy\mod-debug.log</c>），
/// 其次模组目录（需要管理员权限），最后系统临时目录。
/// <b>某个落点写失败会自动换下一个</b>；第一个写成功的落点会被一直沿用，<b>不双写</b>，
/// 排查时不需要再猜"看哪一份"。
/// </para>
/// <para>
/// <b>永不抛异常</b>：日志通道坏掉绝不能让模组失效。所有 IO 都在 try/catch 里，
/// 失败只记 <see cref="LastError"/>。
/// </para>
/// </summary>
public sealed class EconomyFileLogSink : IEconomyFileSink
{
    /// <summary>日志文件名。</summary>
    public const string FileName = "mod-debug.log";

    /// <summary>滚动阈值：超过就重命名为 <c>mod-debug.log.1</c>（覆盖旧的备份）。</summary>
    public const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>线程锁：日志可能来自游戏主线程、补丁回调、AI 回调等多个线程。</summary>
    private readonly object _gate = new();

    private readonly List<string> _candidatePaths = [];
    private int _chosen = -1;
    private bool _disabled;

    /// <summary>真正在用的落点（尚未写出第一行时为 null）。</summary>
    public string Path { get; private set; } = "";

    /// <summary>最近一次失败原因。null 表示没出过错。</summary>
    public string? LastError { get; private set; }

    public string Describe => string.IsNullOrEmpty(Path)
        ? (_disabled ? "未启用（无可用路径）" : "尚未写出第一行")
        : Path;

    /// <summary>按优先级登记候选落点。空串/null 会被忽略。</summary>
    public void AddCandidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_gate)
        {
            if (!_candidatePaths.Contains(path)) _candidatePaths.Add(path);
        }
    }

    /// <summary>
    /// 写一行。<b>永远不抛异常</b>。
    /// <para>
    /// 第一次调用时写一条头行（时间戳 + 进程号），并在<b>真正可写</b>的落点上定下来。
    /// 某个落点写失败时会<b>逐个换下一个候选</b>重试（例如影子存档目录被策略挡住时，
    /// 退到模组目录、再退到系统临时目录），全部失败才彻底放弃。
    /// </para>
    /// </summary>
    public bool TryAppend(string line)
    {
        // ★ 2026-10-12 真机挖出的坑：这里原先只有一句 `if (_disabled) return false;`，
        //   而 `_disabled` 一旦在所有候选落点都写失败时被置位，就【永远不会】再打开。
        //   真机症状：`mod-debug.log` 在某一刻之后彻底不再增长（探针里 File.AppendAllText
        //   命中 0 次），而旁路探针日志照常写 —— 于是后续每一轮排查都建立在"日志说什么"
        //   这个假信息上，白花了整整一轮。
        //   现在的策略：失败不永久放弃，改为【按行数冷却重试】；
        //   并且把"已静默多少次"记下来，让恢复那一刻能一眼看出中间丢了多少。
        if (_disabled)
        {
            lock (_gate)
            {
                if (_disabled)
                {
                    _suppressed++;
                    if (_suppressed < RetryAfterSuppressedLines) return false;

                    // 冷却到期：把失败标记清掉，重新尝试所有候选落点。
                    _disabled = false;
                    _failed.Clear();
                    LastError = (LastError ?? "") + $"（已静默 {_suppressed} 行后自动重试）";
                    _suppressed = 0;
                }
            }
        }

        lock (_gate)
        {
            // 候选点数量有限，一轮遍历就够了；已选定的失败点会被跳过。
            for (int round = 0; round <= _candidatePaths.Count; round++)
            {
                int target = SelectTarget();
                if (target < 0)
                {
                    _disabled = true;
                    LastError ??= "所有候选落点都不可写";
                    return false;
                }

                try
                {
                    string path = _candidatePaths[target];
                    bool fresh = target != _chosen || Path.Length == 0;
                    Path = path;
                    _chosen = target;

                    if (fresh)
                    {
                        RotateIfNeeded(path);
                        File.AppendAllText(path, Header(), Utf8NoBom);
                    }

                    File.AppendAllText(path, line + Environment.NewLine, Utf8NoBom);
                    return true;
                }
                catch (Exception e)
                {
                    // 这个落点写不进去：把它划掉，下一轮换下一个候选点。
                    LastError = e.GetType().Name + ": " + e.Message;
                    _failed.Add(target);
                }
            }

            _disabled = true;
            return false;
        }
    }

    /// <summary>已被证明写不进去的候选落点（下标）。</summary>
    private readonly HashSet<int> _failed = [];

    /// <summary>
    /// "已静默"的行数：写到全部候选落点都失败之后，每来一行就加一。
    /// 到达 <see cref="RetryAfterSuppressedLines"/> 就自动清掉失败标记、重新尝试
    /// —— 这样日志<b>不会永久闭嘴</b>（2026-10-12 真机教训）。
    /// </summary>
    private int _suppressed;

    /// <summary>
    /// 静默多少行之后自动重试写入。
    /// <para>
    /// 取 200 是折中：太小会在磁盘暂时不可写时反复做无效的目录/文件探测（每行一次），
    /// 太大则一次故障会吞掉几千行日志。200 行大约相当于一次私信往返的量级。
    /// </para>
    /// </summary>
    public const int RetryAfterSuppressedLines = 200;

    /// <summary>挑一个能写的落点：已选定的优先（可建目录），其次第一个没失败过的候选。</summary>
    private int SelectTarget()
    {
        if (_chosen >= 0 && _chosen < _candidatePaths.Count && !_failed.Contains(_chosen))
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_candidatePaths[_chosen])!);
                return _chosen;
            }
            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                _failed.Add(_chosen);
                _chosen = -1;
            }
        }

        for (int i = 0; i < _candidatePaths.Count; i++)
        {
            if (_failed.Contains(i)) continue;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_candidatePaths[i])!);
                return i;
            }
            catch (Exception e)
            {
                // 换下一个候选落点。全部失败时由调用方收尾。
                LastError = e.GetType().Name + ": " + e.Message;
                _failed.Add(i);
            }
        }

        return -1;
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static string Header() =>
        "===== NationalSpireEconomy 文件日志（保底通道，不经过 Godot）=====" + Environment.NewLine +
        $"启动时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}｜进程 {Environment.ProcessId}" + Environment.NewLine;

    /// <summary>超过阈值就把当前文件挪成 <c>.1</c>（覆盖旧备份），避免无限增长。</summary>
    private static void RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;
            if (File.Exists(path + ".1")) File.Delete(path + ".1");
            File.Move(path, path + ".1");
        }
        catch (Exception)
        {
            // 滚动失败无所谓：继续往原文件追加即可。
        }
    }
}
