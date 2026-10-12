using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Inject;

/// <summary>审计记录类型。用常量而不是枚举，落盘就是可读字符串，方便人肉 grep。</summary>
public static class AuditKinds
{
    public const string TradeReserved = "trade_reserved";
    public const string TradeConfirmed = "trade_confirmed";
    public const string TradeDeclined = "trade_declined";
    public const string TradeInvalid = "trade_invalid";
    public const string TradeSoftDeclined = "trade_soft_declined";
    public const string TradeExpired = "trade_expired";
    /// <summary>一笔真实结算（阶段 4 第二步）。<b>钱真的动了</b>，这是对账时最先看的一类。</summary>
    public const string TradeSettled = "trade_settled";
    public const string LedgerEntry = "ledger_entry";
    public const string IntentDrift = "intent_drift";
}

/// <summary>
/// 一条审计记录。<b>只追加、不修改、不删除。</b>
/// <para>
/// 它记录的是"发生了什么"，而不是"当前状态"——所以余额变化要连同
/// <see cref="BalanceAfterUsd"/> 一起记，事后才能复原出完整的时间线。
/// </para>
/// </summary>
public sealed class AuditRecord
{
    /// <summary>UTC 时间戳（ISO-8601）。</summary>
    public string At { get; set; } = "";

    /// <summary>记录类型，见 <see cref="AuditKinds"/>。</summary>
    public string Kind { get; set; } = "";

    /// <summary>游戏内天数。</summary>
    public int Day { get; set; }
    public int Season { get; set; }

    /// <summary>涉及的 NPC。NPC↔NPC 时是主要一方。</summary>
    public string PersonId { get; set; } = "";
    public string CounterpartyId { get; set; } = "";

    /// <summary>幂等键。审计对账靠它把"同一件事"串起来。</summary>
    public string ReceiptKey { get; set; } = "";

    /// <summary>流水 id（若有）。</summary>
    public string LedgerEntryId { get; set; } = "";

    /// <summary>交易单 id（若有）。</summary>
    public string OfferId { get; set; } = "";

    /// <summary>业务科目，例如 赞助 / 转会费 / 冲正。</summary>
    public string Application { get; set; } = "";

    /// <summary>
    /// 资金方向（阶段 4 第三步补记）。
    /// <para>
    /// 之前审计行里没有方向，于是"回读审计给玩家看"时无法说明钱是<b>谁给谁</b>——
    /// 而首笔范围恰好就是靠方向闸限定的（只开通 NPC → 玩家），方向是对账时最该看见的一列。
    /// 老文件里没有这个字段，反序列化后是空串，展示端要容忍。
    /// </para>
    /// </summary>
    public string Direction { get; set; } = "";

    /// <summary>金额（美元）。一律美元整数，绝不放内部单位。</summary>
    public long AmountUsd { get; set; }

    /// <summary>记账之后的余额快照（美元）。NPC 侧余额（结算行也用它记 NPC 一侧）。</summary>
    public long BalanceAfterUsd { get; set; }

    /// <summary>记账之前的余额快照（美元，仅结算行用）。</summary>
    public long BalanceBeforeUsd { get; set; }

    /// <summary>玩家影子余额（结算行用；<b>它是我们管辖的那份钱</b>，与游戏自带数字不同）。</summary>
    public long PlayerBalanceBeforeUsd { get; set; }

    /// <summary>结算后玩家影子余额（结算行用）。</summary>
    public long PlayerBalanceAfterUsd { get; set; }

    /// <summary>结果码或状态。</summary>
    public string Status { get; set; } = "";

    /// <summary>说明。失败原因、拒绝台词等。</summary>
    public string Detail { get; set; } = "";

    /// <summary>意向漂移分数（仅 <see cref="AuditKinds.IntentDrift"/> 用）。</summary>
    public double DriftScore { get; set; }

    /// <summary>意向档位 id（仅 <see cref="AuditKinds.IntentDrift"/> 用）。</summary>
    public string BandId { get; set; } = "";
}

/// <summary>
/// 审计写入器：追加写 <c>audit/&lt;WorldId&gt;.jsonl</c>。
/// <para>
/// <b>为什么是 JSONL</b>：一行一条记录，追加是 O(1)，断电最多丢最后一行，
/// 而且可以用 <c>Select-String</c> / <c>grep</c> 直接查，不需要先解析整个文件。
/// 相比之下 JSON 数组必须整体重写，一旦损坏就全丢。
/// </para>
/// <para>
/// <b>失败策略：永不抛出。</b>审计是旁路，它坏掉不该让游戏崩。
/// 所有异常都会被记成一条 <c>Warning</c> 日志后吞掉——
/// 但会通过 <see cref="LastError"/> 暴露出来，便于自检。
/// </para>
/// </summary>
public sealed class EconomyAudit
{
    public const string DirectoryName = "audit";
    public const string FileSuffix = ".jsonl";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        // 中文不转义成 \uXXXX，日志要能人肉读。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly string _root;
    private readonly IEconomyLog _log;
    private readonly IEconomyClock _clock;
    private readonly object _gate = new();

    public EconomyAudit(string rootDirectory, IEconomyClock? clock = null, IEconomyLog? log = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("审计根目录不能为空。", nameof(rootDirectory));

        _root = rootDirectory;
        _clock = clock ?? SystemEconomyClock.Instance;
        _log = log ?? ConsoleEconomyLog.Instance;
    }

    /// <summary>是否启用。关掉之后所有写入都是空操作，方便玩家不想留痕。</summary>
    public bool Enabled { get; set; } = true;

    public string Directory => Path.Combine(_root, DirectoryName);

    public string PathFor(string worldId) =>
        Path.Combine(Directory, EconomyStore.SafeWorldId(worldId) + FileSuffix);

    /// <summary>最近一次失败原因。为 null 表示一切正常。</summary>
    public string? LastError { get; private set; }

    /// <summary>已经写出的记录条数（本次进程内）。</summary>
    public int WrittenCount { get; private set; }

    // ─────────────────────────────────────────────────────────────────
    // 写入
    // ─────────────────────────────────────────────────────────────────

    /// <summary>追加一条记录。返回是否真的写成功。</summary>
    public bool Append(string worldId, AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!Enabled) return false;
        if (string.IsNullOrWhiteSpace(worldId)) return false;

        record.At = _clock.UtcNow.ToString("O");

        try
        {
            string path = PathFor(worldId);
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);

            string line = JsonSerializer.Serialize(record, Json);
            lock (_gate)
            {
                // 追加 + 立即冲刷：宁可慢一点，也不要"崩了才发现最后几十条还在缓冲区"。
                //
                // 编码必须带 BOM，且**只在新建文件时**写一次：
                // Windows PowerShell 5.1 与老版记事本不会自动嗅探 UTF-8，
                // 无 BOM 时中文会显示成"鎵撹祻"这类乱码——而这个文件存在的唯一意义
                // 就是给人看，读不了等于没写。行中间绝不重复插 BOM，否则会污染 JSON。
                //
                // 注意用 Exists + Length 两步判断，不能直接读 FileInfo.Length：
                // 文件不存在时那个属性会抛 FileNotFoundException。
                //
                // BOM 由 StreamWriter 自己写（encoderShouldEmitUTF8Identifier），
                // 这里**不要**再手工 Write('\uFEFF')——那会写出两个 BOM，首行 JSON 直接废掉。
                var info = new FileInfo(path);
                bool needsBom = !info.Exists || info.Length == 0;

                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: needsBom));
                writer.WriteLine(line);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            WrittenCount++;
            LastError = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                      or System.Security.SecurityException or ArgumentException)
        {
            LastError = e.Message;
            _log.Warn("audit", $"审计写入失败（已忽略，不影响游戏）：{e.Message}", e);
            return false;
        }
    }

    /// <summary>读取某个世界的全部记录。供自检与测试；坏行会被跳过而不是让整个文件读不出来。</summary>
    public IReadOnlyList<AuditRecord> Read(string worldId)
    {
        string path = PathFor(worldId);
        var records = new List<AuditRecord>();
        if (!File.Exists(path)) return records;

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<AuditRecord>(line, Json) is { } parsed) records.Add(parsed);
            }
            catch (JsonException)
            {
                // 半行（断电）或手工编辑坏了：跳过这一行，其余照读。
            }
        }
        return records;
    }

    /// <summary>记录条数。文件不存在时返回 0。</summary>
    public int CountLines(string worldId)
    {
        string path = PathFor(worldId);
        if (!File.Exists(path)) return 0;

        int count = 0;
        foreach (string line in File.ReadLines(path))
            if (!string.IsNullOrWhiteSpace(line)) count++;
        return count;
    }

    // ─────────────────────────────────────────────────────────────────
    // 便捷构造
    // ─────────────────────────────────────────────────────────────────

    /// <summary>记一条流水。金额与科目都来自 <see cref="LedgerEntry"/>，不重新解释。</summary>
    public bool Ledger(string worldId, string personId, LedgerEntry entry, string status = "applied")
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Append(worldId, new AuditRecord
        {
            Kind = AuditKinds.LedgerEntry,
            Day = entry.Day,
            Season = entry.Season,
            PersonId = personId,
            CounterpartyId = entry.CounterpartyId,
            LedgerEntryId = entry.Id,
            OfferId = entry.OfferId,
            Application = string.IsNullOrEmpty(entry.ApplicationKind) ? entry.Kind : entry.ApplicationKind,
            AmountUsd = entry.Amount,
            BalanceAfterUsd = entry.BalanceAfter,
            Status = status,
            Detail = entry.Reason
        });
    }

    /// <summary>
    /// 记一次交易状态变化。
    /// <para>
    /// <paramref name="balanceAfterUsd"/> 是<b>记账之后</b>的余额快照，由调用方从账本取出。
    /// 不要拿 <c>offer.Amount</c> 顶上——金额是"动了多少"，余额是"动完剩多少"，
    /// 混用会让审计线完全无法复原。
    /// </para>
    /// </summary>
    public bool Trade(string worldId, TradeOffer offer, string kind, string status,
        string detail = "", long balanceAfterUsd = 0)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return Append(worldId, new AuditRecord
        {
            Kind = kind,
            Day = offer.CreatedDay,
            Season = offer.Season,
            PersonId = offer.InitiatorId,
            CounterpartyId = offer.CounterpartyId,
            OfferId = offer.Id,
            LedgerEntryId = offer.LedgerEntryId,
            Application = offer.Kind,
            AmountUsd = offer.Amount,
            BalanceAfterUsd = balanceAfterUsd,
            Status = status,
            Detail = detail.Length > 0 ? detail : offer.Report
        });
    }

    /// <summary>记一次意向漂移。</summary>
    public bool Intent(string worldId, string personId, string kind, string detail, double driftScore,
        string bandId, int day, int season)
    {
        return Append(worldId, new AuditRecord
        {
            Kind = AuditKinds.IntentDrift,
            Day = day,
            Season = season,
            PersonId = personId,
            Application = kind,
            Status = bandId,
            Detail = detail,
            DriftScore = driftScore,
            BandId = bandId
        });
    }
}
