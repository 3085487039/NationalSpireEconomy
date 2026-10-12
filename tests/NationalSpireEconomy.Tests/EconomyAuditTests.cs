using System.Text.Json;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Inject;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 审计：追加写 <c>audit/&lt;WorldId&gt;.jsonl</c>。
/// <para>
/// 四条契约：① 一行一条、只追加不重写；② 按 WorldId 分文件，绝不串档；
/// ③ 中文不转义（日志要能人肉读）；④ <b>写入失败绝不抛出</b>——审计是旁路，坏掉不该让游戏崩。
/// </para>
/// </summary>
public static class EconomyAuditTests
{
    public static Action[] Cases =>
    [
        AppendWritesOneLinePerRecord,
        AppendIsAdditiveNotRewriting,
        EachWorldGetsItsOwnFile,
        ChineseIsWrittenAsIsNotEscaped,
        BomIsWrittenOnlyOnceAtFileStart,
        LedgerRecordCarriesApplicationKindAndUsd,
        TradeRecordKeepsBalanceAfterSeparateFromAmount,
        MissingFileReadsAsEmpty,
        BrokenLineIsSkippedWithoutLosingTheRest,
        DisabledAuditWritesNothing,
        UnwritableRootReportsErrorWithoutThrowing,
        SafeworldIdPreventsDirectoryTraversal,
        EmptyWorldIdIsRejected,
        CountLinesMatchesAppendedRecords,
        IntentRecordCarriesDriftAndBand
    ];

    private static readonly DelegateEconomyLog Silent = new((_, _, _, _) => { });

    /// <summary>每个用例一个独立临时目录，避免互相干扰。</summary>
    private static string NewRoot() => Path.Combine(
        Path.GetTempPath(), "nse-audit-" + Guid.NewGuid().ToString("N"));

    private static EconomyAudit Open(string root, bool enabled = true) =>
        new(root, new FixedEconomyClock(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero)), Silent)
        {
            Enabled = enabled
        };

    private static AuditRecord Sample(string kind = AuditKinds.LedgerEntry) => new()
    {
        Kind = kind,
        Day = 30,
        Season = 3,
        PersonId = "npc",
        CounterpartyId = "player",
        ReceiptKey = "trade:w:o0001",
        LedgerEntryId = "e1",
        OfferId = "o0001",
        Application = "赞助",
        AmountUsd = 50_000,
        BalanceAfterUsd = 450_000,
        Status = "applied",
        Detail = "这赛季刚拿工资，正好赞助你一点"
    };

    // ── 追加写 ──────────────────────────────────────────────────────

    private static void AppendWritesOneLinePerRecord()
    {
        string root = NewRoot();
        var audit = Open(root);

        TestRunner.True(audit.Append("w1", Sample()), "写入应成功");
        TestRunner.True(audit.Append("w1", Sample()), "第二次写入也应成功");

        TestRunner.Equal(2, audit.CountLines("w1"), "两行 = 两条记录，一行一条");
        TestRunner.Equal(2, audit.Read("w1").Count, "读回来也应是两条");

        string path = audit.PathFor("w1");
        TestRunner.True(File.Exists(path), "文件应落在 audit/<WorldId>.jsonl");
        TestRunner.True(path.EndsWith(".jsonl", StringComparison.Ordinal), "扩展名必须是 .jsonl");
        TestRunner.True(path.Contains(EconomyAudit.DirectoryName), "应放在 audit/ 子目录下");
    }

    private static void AppendIsAdditiveNotRewriting()
    {
        string root = NewRoot();
        var audit = Open(root);

        audit.Append("w1", Sample());
        string firstContent = File.ReadAllText(audit.PathFor("w1"));

        audit.Append("w1", Sample());
        string secondContent = File.ReadAllText(audit.PathFor("w1"));

        TestRunner.True(secondContent.StartsWith(firstContent, StringComparison.Ordinal),
            "第二次写入必须追加在原内容之后，而不是整体重写");
        TestRunner.True(secondContent.Length > firstContent.Length, "内容应只增不减");
    }

    private static void EachWorldGetsItsOwnFile()
    {
        string root = NewRoot();
        var audit = Open(root);

        audit.Append("worldA", Sample());
        audit.Append("worldB", Sample());
        audit.Append("worldB", Sample());

        TestRunner.Equal(1, audit.CountLines("worldA"), "A 世界只该有自己那条");
        TestRunner.Equal(2, audit.CountLines("worldB"), "B 世界该有两条");
        TestRunner.NotEqual(audit.PathFor("worldA"), audit.PathFor("worldB"), "两个世界必须是不同文件");

        foreach (var record in audit.Read("worldA"))
            TestRunner.Equal("npc", record.PersonId, "A 世界不该读到 B 世界的内容");
    }

    private static void ChineseIsWrittenAsIsNotEscaped()
    {
        string root = NewRoot();
        var audit = Open(root);
        audit.Append("w1", Sample());

        string raw = File.ReadAllText(audit.PathFor("w1"));
        TestRunner.Contains(raw, "赞助", "中文必须原样写出，不能转义成 \\uXXXX");
        TestRunner.NotContains(raw, "\\u", "不该出现任何 Unicode 转义序列");
        TestRunner.Contains(raw, "这赛季刚拿工资，正好赞助你一点", "说明文字也要原样保留");

        // Windows PowerShell 5.1 不会嗅探无 BOM 的 UTF-8，中文会显示成乱码。
        // 这个文件存在的唯一意义就是给人看，所以必须带 BOM。
        var bytes = File.ReadAllBytes(audit.PathFor("w1"));
        TestRunner.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "日志应带 UTF-8 BOM，否则 Windows 工具读出来是乱码");
    }

    /// <summary>
    /// BOM 只能出现在文件开头一次。若每次追加都写一遍，行中间就会插进 U+FEFF，
    /// 那些行会解析失败，整份审计直接废掉。
    /// </summary>
    private static void BomIsWrittenOnlyOnceAtFileStart()
    {
        string root = NewRoot();
        var audit = Open(root);
        for (int i = 0; i < 5; i++) audit.Append("w1", Sample());

        var bytes = File.ReadAllBytes(audit.PathFor("w1"));
        TestRunner.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "开头应有 BOM");

        int bomCount = 0;
        for (int i = 0; i + 2 < bytes.Length; i++)
            if (bytes[i] == 0xEF && bytes[i + 1] == 0xBB && bytes[i + 2] == 0xBF) bomCount++;

        TestRunner.Equal(1, bomCount, "整份文件只应有 1 个 BOM");
        TestRunner.Equal(5, audit.Read("w1").Count, "5 条记录都必须能解析出来");
        // 每行仍必须是合法 JSON，BOM 不能把首行弄坏。
        foreach (string line in File.ReadLines(audit.PathFor("w1")))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string trimmed = line.TrimStart('\uFEFF');
            TestRunner.True(trimmed.StartsWith('{') && trimmed.EndsWith('}'), "每行都应是合法 JSON 对象：" + trimmed);
        }
    }

    // ── 记录内容 ────────────────────────────────────────────────────

    private static void LedgerRecordCarriesApplicationKindAndUsd()
    {
        string root = NewRoot();
        var audit = Open(root);

        // 冲正流水：Kind 是"冲正"，但 ApplicationKind 保留被冲正的科目。
        // 审计必须记后者，否则事后完全看不出这笔到底撤销了什么。
        var entry = new LedgerEntry
        {
            Id = "rev1", Day = 31, Season = 3, Kind = EconomyLedger.ReversalKind,
            ApplicationKind = "转会费", Title = "冲正：支付转会费",
            Amount = 40_000, BalanceAfter = 460_000, CounterpartyId = "seller", Reason = "卖方入账失败"
        };

        TestRunner.True(audit.Ledger("w1", "buyer", entry), "记账审计应写入成功");
        var record = audit.Read("w1").Single();

        TestRunner.Equal("转会费", record.Application, "应记被冲正的科目，而不是「冲正」二字");
        TestRunner.Equal(40_000L, record.AmountUsd, "金额一律美元整数");
        TestRunner.Equal(460_000L, record.BalanceAfterUsd, "应带上记账后的余额快照");
        TestRunner.Equal("rev1", record.LedgerEntryId);
        TestRunner.Equal("seller", record.CounterpartyId);
    }

    /// <summary>
    /// 金额是"动了多少"，余额是"动完剩多少"，两者绝不能混用。
    /// </summary>
    private static void TradeRecordKeepsBalanceAfterSeparateFromAmount()
    {
        string root = NewRoot();
        var audit = Open(root);

        var offer = new TradeOffer
        {
            Id = "o0007", Kind = TradeKinds.Sponsor, Direction = TradeDirections.NpcToPlayer,
            InitiatorId = "npc", CounterpartyId = "player", Amount = 50_000,
            CreatedDay = 30, Season = 3, State = TradeOfferStates.Confirmed, LedgerEntryId = "e7"
        };

        audit.Trade("w1", offer, AuditKinds.TradeConfirmed, "confirmed", balanceAfterUsd: 450_000);
        var record = audit.Read("w1").Single();

        TestRunner.Equal(50_000L, record.AmountUsd, "金额是「动了多少」");
        TestRunner.Equal(450_000L, record.BalanceAfterUsd, "余额是「动完剩多少」");
        TestRunner.NotEqual(record.AmountUsd, record.BalanceAfterUsd, "两者不该被写成同一个值");
        TestRunner.Equal(AuditKinds.TradeConfirmed, record.Kind);
        TestRunner.Equal("o0007", record.OfferId);
        TestRunner.Equal("e7", record.LedgerEntryId);
    }

    private static void IntentRecordCarriesDriftAndBand()
    {
        string root = NewRoot();
        var audit = Open(root);

        audit.Intent("w1", "npc", DriftTriggers.SoftDeclined, "打赏", driftScore: -0.35,
            bandId: DriftBands.Resentful, day: 50, season: 3);
        var record = audit.Read("w1").Single();

        TestRunner.Equal(AuditKinds.IntentDrift, record.Kind);
        TestRunner.Equal(-0.35, record.DriftScore);
        TestRunner.Equal(DriftBands.Resentful, record.BandId);
        TestRunner.Equal(DriftTriggers.SoftDeclined, record.Application);
    }

    // ── 健壮性 ──────────────────────────────────────────────────────

    private static void MissingFileReadsAsEmpty()
    {
        var audit = Open(NewRoot());
        TestRunner.Equal(0, audit.Read("never-written").Count, "不存在的世界应读成空列表");
        TestRunner.Equal(0, audit.CountLines("never-written"));
    }

    /// <summary>断电会留下半行。半行不该让整个文件读不出来。</summary>
    private static void BrokenLineIsSkippedWithoutLosingTheRest()
    {
        string root = NewRoot();
        var audit = Open(root);
        audit.Append("w1", Sample());
        audit.Append("w1", Sample());

        // 手工插一行坏数据，模拟断电或被编辑器改坏。
        File.AppendAllText(audit.PathFor("w1"), "{ 这不是合法的 JSON" + Environment.NewLine);

        var records = audit.Read("w1");
        TestRunner.Equal(2, records.Count, "两条好记录必须仍然读得出来");
        TestRunner.Equal(1, audit.CountLines("w1") - records.Count,
            "行数比可解析条数多 1 —— 差的就是那个坏行");
    }

    private static void DisabledAuditWritesNothing()
    {
        string root = NewRoot();
        var audit = Open(root, enabled: false);

        TestRunner.False(audit.Append("w1", Sample()), "关掉之后不该写任何东西");
        TestRunner.Equal(0, audit.CountLines("w1"), "不该产生文件内容");
        TestRunner.False(File.Exists(audit.PathFor("w1")), "连文件都不该创建");
    }

    /// <summary>
    /// 审计是旁路：目录不可写时只记一条警告，<b>绝不抛出</b>。
    /// 用一个"父路径是文件"的根目录来构造必然失败的情况。
    /// </summary>
    private static void UnwritableRootReportsErrorWithoutThrowing()
    {
        string root = NewRoot();
        Directory.CreateDirectory(root);
        string blocker = Path.Combine(root, "blocker");
        File.WriteAllText(blocker, "我是一个文件，不是目录");

        // 让 audit 目录的父级是一个文件 → CreateDirectory 必然失败。
        var audit = new EconomyAudit(blocker, null, Silent);

        bool threw = false;
        bool ok = false;
        try
        {
            ok = audit.Append("w1", Sample());
        }
        catch (Exception)
        {
            threw = true;
        }

        TestRunner.False(threw, "审计失败必须被吞掉，绝不能把异常抛给游戏");
        TestRunner.False(ok, "写入应报告失败");
        TestRunner.True(audit.LastError != null, "应通过 LastError 暴露原因，便于自检");
    }

    private static void SafeworldIdPreventsDirectoryTraversal()
    {
        var audit = Open(NewRoot());
        string path = audit.PathFor("../../evil");

        TestRunner.Equal("______evil" + EconomyAudit.FileSuffix, Path.GetFileName(path),
            "应是 EconomyStore.SafeWorldId 的同一套规则：非字母数字一律变下划线");
        // 父目录必须是 audit/ 本身，而不是被穿越到上层。
        TestRunner.Equal(audit.Directory, Path.GetDirectoryName(path), "文件必须落在 audit/ 目录内");
    }

    /// <summary>
    /// WorldId 为空时不该写出一个名叫 <c>.jsonl</c> 的怪文件，
    /// 也不该抛异常——直接拒绝即可。
    /// </summary>
    private static void EmptyWorldIdIsRejected()
    {
        var audit = Open(NewRoot());
        TestRunner.False(audit.Append("", Sample()), "空 WorldId 应被拒绝");
        TestRunner.False(audit.Append("   ", Sample()), "空白 WorldId 应被拒绝");
    }

    private static void CountLinesMatchesAppendedRecords()
    {
        string root = NewRoot();
        var audit = Open(root);

        for (int i = 0; i < 25; i++) audit.Append("w1", Sample());
        TestRunner.Equal(25, audit.CountLines("w1"));
        TestRunner.Equal(25, audit.WrittenCount, "进程内计数应与实际写入一致");
        TestRunner.True(audit.LastError == null, "正常写入时不该有错误残留");
    }
}
