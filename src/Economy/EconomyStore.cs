using System.Text.Json;
using System.Text.Json.Serialization;
using NationalSpireEconomy.Core;

namespace NationalSpireEconomy.Economy;

public enum WorldLoadStatus
{
    /// <summary>正常读取。</summary>
    Loaded,
    /// <summary>主文件有，但读失败，已从 .bak 恢复。</summary>
    RecoveredFromBackup,
    /// <summary>主文件与备份都不可用，已重建一份空账本（不删除坏文件）。</summary>
    RebuiltFromScratch,
    /// <summary>调用方要求的世界不存在，已创建。</summary>
    CreatedNew
}

public sealed class WorldLoadReport
{
    public required string WorldId { get; init; }
    public required WorldLoadStatus Status { get; init; }
    public required EconomyWorld World { get; init; }
    public string? Detail { get; init; }

    /// <summary>是否发生过数据丢失。为 true 时调用方应当提示玩家。</summary>
    public bool DataWasLost => Status is WorldLoadStatus.RebuiltFromScratch;
}

/// <summary>索引里的一条记录。</summary>
public sealed class WorldIndexEntry
{
    public string WorldId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string LastWrittenAt { get; set; } = "";
    public bool Archived { get; set; }
}

public sealed class EconomyIndex
{
    public int SchemaVersion { get; set; } = 1;
    public string? ActiveWorldId { get; set; }
    public List<WorldIndexEntry> Worlds { get; set; } = [];

    public WorldIndexEntry? Find(string worldId) =>
        Worlds.FirstOrDefault(w => string.Equals(w.WorldId, worldId, StringComparison.Ordinal));
}

/// <summary>
/// 影子存档的读写层。<b>只碰我们自己的目录，永不写原版存档。</b>
/// <para>三条硬性保证：</para>
/// <list type="number">
/// <item><b>不损坏</b>：写入走「临时文件 → 校验 → 轮换备份 → 原子替换」，
/// 任何一步失败都不会留下半个文件。</item>
/// <item><b>可恢复</b>：主文件坏了自动回落 <c>.bak</c>；两者都坏则重建，
/// 且坏文件一律改名保留而不是删除。</item>
/// <item><b>不串档</b>：一切都按 <c>WorldId</c> 分片，
/// 索引记录每个世界的存在与归档状态，新生涯不会读到旧生涯的钱。</item>
/// </list>
/// </summary>
public sealed class EconomyStore
{
    public const string DefaultDirectoryName = "national_spire_economy";
    public const string IndexFileName = "index.json";
    public const string WorldsDirectoryName = "worlds";
    public const string BackupSuffix = ".bak";
    public const string TempSuffix = ".tmp";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly string _root;
    private readonly IEconomyClock _clock;
    private readonly IEconomyLog _log;
    private readonly Dictionary<string, EconomyWorld> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastWrite = new(StringComparer.Ordinal);

    /// <summary>两次真实落盘之间的最小间隔，避免原版频繁保存把磁盘打满。</summary>
    public TimeSpan MinimumWriteInterval { get; set; } = TimeSpan.FromSeconds(2);

    public EconomyStore(string rootDirectory, IEconomyClock? clock = null, IEconomyLog? log = null)
    {
        _root = rootDirectory;
        _clock = clock ?? SystemEconomyClock.Instance;
        _log = log ?? ConsoleEconomyLog.Instance;
    }

    public string Root => _root;
    public string WorldsDirectory => Path.Combine(_root, WorldsDirectoryName);
    public string IndexPath => Path.Combine(_root, IndexFileName);

    public string WorldPath(string worldId) => Path.Combine(WorldsDirectory, SafeWorldId(worldId) + ".json");
    public string WorldBackupPath(string worldId) => WorldPath(worldId) + BackupSuffix;

    /// <summary>WorldId 只允许作为安全文件名，防目录穿越。</summary>
    public static string SafeWorldId(string worldId)
    {
        if (string.IsNullOrWhiteSpace(worldId)) throw new ArgumentException("WorldId 不能为空。", nameof(worldId));
        var builder = new System.Text.StringBuilder(worldId.Length);
        foreach (char c in worldId)
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return builder.ToString();
    }

    // ─────────────────────────────────────────────────────────────────
    // 索引
    // ─────────────────────────────────────────────────────────────────

    public EconomyIndex LoadIndex()
    {
        if (!File.Exists(IndexPath))
        {
            _log.Info("store", "索引不存在，将新建。");
            return new EconomyIndex();
        }
        try
        {
            var index = JsonSerializer.Deserialize<EconomyIndex>(File.ReadAllText(IndexPath), Json);
            return index ?? new EconomyIndex();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // 索引是可重建的派生数据，坏了不该阻塞玩家；备份一份后重建。
            PreserveBroken(IndexPath);
            _log.Warn("store", "索引读取失败，已保留原文件并重建。", e);
            return new EconomyIndex();
        }
    }

    public void SaveIndex(EconomyIndex index)
    {
        index.SchemaVersion = 1;
        WriteAtomic(IndexPath, JsonSerializer.Serialize(index, Json), backup: false);
    }

    public void SetActiveWorld(string worldId)
    {
        var index = LoadIndex();
        index.ActiveWorldId = worldId;
        Upsert(index, worldId, archived: false);
        SaveIndex(index);
    }

    public void MarkArchived(string worldId)
    {
        var index = LoadIndex();
        var entry = index.Find(worldId);
        if (entry == null) index.Worlds.Add(new WorldIndexEntry { WorldId = worldId, Archived = true });
        else entry.Archived = true;
        if (index.ActiveWorldId == worldId) index.ActiveWorldId = null;
        SaveIndex(index);
        _log.Info("store", $"世界 {worldId} 已标记归档（余额保留，不再作为现役生涯）。");
    }

    private void Upsert(EconomyIndex index, string worldId, bool archived)
    {
        string stamp = Stamp();
        var entry = index.Find(worldId);
        if (entry == null) index.Worlds.Add(new WorldIndexEntry { WorldId = worldId, CreatedAt = stamp, LastWrittenAt = stamp, Archived = archived });
        else { entry.LastWrittenAt = stamp; if (!archived) entry.Archived = false; }
    }

    // ─────────────────────────────────────────────────────────────────
    // 世界读写
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 读取世界。主文件缺失且有备份时从备份恢复；两者都不可用则重建空账本。
    /// <b>绝不删除任何文件</b>，坏文件一律改名保留。
    /// </summary>
    public WorldLoadReport LoadWorld(string worldId, bool createIfMissing = true)
    {
        string path = WorldPath(worldId);

        if (File.Exists(path))
        {
            if (TryRead(path, worldId, out var world, out string? error))
            {
                _cache[worldId] = world!;
                return new WorldLoadReport { WorldId = worldId, Status = WorldLoadStatus.Loaded, World = world! };
            }

            _log.Error("store", $"世界 {worldId} 主文件损坏：{error}");
            string preserved = PreserveBroken(path);

            if (File.Exists(WorldBackupPath(worldId)) && TryRead(WorldBackupPath(worldId), worldId, out var recovered, out _))
            {
                _log.Warn("store", $"世界 {worldId} 已从备份恢复。坏文件保留在 {preserved}");
                _cache[worldId] = recovered!;
                return new WorldLoadReport
                {
                    WorldId = worldId,
                    Status = WorldLoadStatus.RecoveredFromBackup,
                    World = recovered!,
                    Detail = "主文件损坏，已从最近备份恢复。原文件已另存。"
                };
            }

            _log.Error("store", $"世界 {worldId} 与备份均不可读，将重建空账本。坏文件保留在 {preserved}");
            var rebuilt = NewWorld(worldId);
            _cache[worldId] = rebuilt;
            return new WorldLoadReport
            {
                WorldId = worldId,
                Status = WorldLoadStatus.RebuiltFromScratch,
                World = rebuilt,
                Detail = "账本与备份均无法读取，已重建；此前资金记录丢失。"
            };
        }

        if (File.Exists(WorldBackupPath(worldId)) && TryRead(WorldBackupPath(worldId), worldId, out var fromBackup, out _))
        {
            _log.Warn("store", $"世界 {worldId} 主文件缺失，已从备份恢复。");
            _cache[worldId] = fromBackup!;
            return new WorldLoadReport
            {
                WorldId = worldId,
                Status = WorldLoadStatus.RecoveredFromBackup,
                World = fromBackup!,
                Detail = "主文件缺失，已从备份恢复。"
            };
        }

        if (!createIfMissing)
        {
            var empty = NewWorld(worldId);
            _cache[worldId] = empty;
            return new WorldLoadReport { WorldId = worldId, Status = WorldLoadStatus.CreatedNew, World = empty };
        }

        var created = NewWorld(worldId);
        _cache[worldId] = created;
        _log.Info("store", $"世界 {worldId} 不存在，已创建空账本。");
        return new WorldLoadReport { WorldId = worldId, Status = WorldLoadStatus.CreatedNew, World = created };
    }

    public EconomyWorld GetOrLoad(string worldId) => LoadWorld(worldId).World;

    /// <summary>
    /// 保存世界。内部先做自检；<paramref name="force"/> 为 false 时受
    /// <see cref="MinimumWriteInterval"/> 节流，但<b>待确认交易有变动时会被强制放行</b>。
    /// </summary>
    public bool SaveWorld(EconomyWorld world, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (string.IsNullOrWhiteSpace(world.WorldId)) throw new ArgumentException("WorldId 不能为空。", nameof(world.WorldId));

        // ★ 兜底裁剪（真机实测事故，2026-10-11 20:0x）：任何来源导致的收据膨胀
        //   都不该让"整个世界写不进去"。那一轮的后果是：自检拒绝写入 ⇒
        //   影子存档永远停在播种那天（磁盘 lastTickedDay=0），而内存里已经跑到第 398 天，
        //   所有日期推进、发薪全部丢失。这里在自检【之前】把收据裁到上限内。
        //   只裁幂等收据、不动余额与水位线；Pending 交易引用的键受保护。
        TrimReceiptsInPlace(world);

        var problems = Validate(world);
        if (problems.Count > 0)
        {
            foreach (string problem in problems) _log.Error("store", $"世界 {world.WorldId} 自检失败：{problem}");
            throw new InvalidDataException($"世界 {world.WorldId} 未通过自检，拒绝写入：{string.Join("；", problems)}");
        }

        string worldId = world.WorldId;
        if (!force && _lastWrite.TryGetValue(worldId, out var last) && _clock.UtcNow - last < MinimumWriteInterval)
        {
            _cache[worldId] = world;
            return false;
        }

        // 先栽临时文件并回读校验，再轮换备份、最后原子替换。
        // 顺序不可颠倒：任何一步崩溃都不会留下半个正式文件。
        string path = WorldPath(worldId);
        Directory.CreateDirectory(WorldsDirectory);

        world.LastWrittenAt = Stamp();
        string json = JsonSerializer.Serialize(world, Json);

        string temp = path + TempSuffix;
        File.WriteAllText(temp, json);

        if (!TryRead(temp, worldId, out _, out string? verifyError))
        {
            File.Delete(temp);
            _log.Error("store", $"临时文件回读校验失败，已放弃写入：{verifyError}");
            throw new InvalidDataException("写入自检失败，已放弃保存以保证原档不受影响。");
        }

        if (File.Exists(path)) File.Copy(path, WorldBackupPath(worldId), overwrite: true);
        File.Move(temp, path, overwrite: true);

        _cache[worldId] = world;
        _lastWrite[worldId] = _clock.UtcNow;

        var index = LoadIndex();
        Upsert(index, worldId, archived: false);
        SaveIndex(index);

        _log.Info("store", $"世界 {worldId} 已保存（{world.Npcs.Count} 人，{world.Offers.Count} 笔交易，{world.Receipts.Count} 条收据）。");
        return true;
    }

    /// <summary>丢弃缓存，强制下次从磁盘重读。</summary>
    public void Invalidate(string? worldId = null)
    {
        if (worldId == null) { _cache.Clear(); _lastWrite.Clear(); }
        else { _cache.Remove(worldId); _lastWrite.Remove(worldId); }
    }

    public bool IsCached(string worldId) => _cache.ContainsKey(worldId);

    // ─────────────────────────────────────────────────────────────────
    // 自检
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 写入前的兜底裁剪：收据超过上限时裁到上限内（保留最近的、保护 Pending 交易引用的键）。
    /// <para>
    /// 它<b>不改余额、不改任何水位线</b>，只收拾幂等收据本，所以不影响"钱只发一次"的语义；
    /// 而它挡住的是更糟的后果：整个世界因为收据膨胀而被自检拒绝、一个字都写不出去。
    /// </para>
    /// </summary>
    private void TrimReceiptsInPlace(EconomyWorld world)
    {
        if (world.Receipts.Count <= EconomyWorld.MaxReceipts) return;

        var ledger = new EconomyLedger(world);
        int before = world.Receipts.Count;
        ledger.PruneReceipts();
        ledger.FlushReceipts();

        if (world.Receipts.Count < before)
            _log.Warn("store",
                $"世界 {world.WorldId} 收据兜底裁剪：{before} → {world.Receipts.Count} 条（上限 {EconomyWorld.MaxReceipts}）。" +
                "只裁幂等收据，余额与水位线未动。");
    }

    /// <summary>
    /// 写入前的自检。宁可拒绝写入也不要写出一份垃圾账本——
    /// 前一层 <c>MoneyUnits</c> 已经在金额上设了闸，这一层守住引用完整性。
    /// </summary>
    public static IReadOnlyList<string> Validate(EconomyWorld world)
    {
        var problems = new List<string>();

        foreach (var (key, npc) in world.Npcs)
        {
            if (npc == null) { problems.Add($"npc[{key}] 为 null"); continue; }
            if (string.IsNullOrWhiteSpace(npc.PersonId)) problems.Add($"npc[{key}] 缺少 PersonId");
            else if (npc.PersonId != key) problems.Add($"npc[{key}] 的 PersonId 是 {npc.PersonId}，与字典键不一致");
            if (npc.Balance < 0) problems.Add($"npc[{key}] 余额为负：{npc.Balance}");
            if (npc.Ledger.Count > NpcEconomy.MaxLedgerEntries)
                problems.Add($"npc[{key}] 流水条数 {npc.Ledger.Count} 超过上限 {NpcEconomy.MaxLedgerEntries}");
            if (npc.IntentTriggers.Count > NpcEconomy.MaxIntentTriggers)
                problems.Add($"npc[{key}] 意向触发器条数 {npc.IntentTriggers.Count} 超过上限 {NpcEconomy.MaxIntentTriggers}");
        }

        // 玩家影子钱包（阶段 4 第二步）。三条不变量：
        // ① 余额不为负；② "没导入却有余额"是自相矛盾（钱只能来自导入或结算）；
        // ③ 流水不超上限。它们保证"玩家那一侧的钱"和 NPC 侧受同样的自检约束。
        var player = world.Player;
        if (player == null)
        {
            problems.Add("player 节点为 null");
        }
        else
        {
            if (player.Balance < 0) problems.Add($"玩家影子余额为负：{player.Balance}");
            if (!player.Imported && player.Balance != 0)
                problems.Add($"玩家影子余额为 {player.Balance}，但导入水位线是 false（钱不可能凭空出现）");
            if (player.Ledger.Count > PlayerEconomy.MaxLedgerEntries)
                problems.Add($"玩家流水条数 {player.Ledger.Count} 超过上限 {PlayerEconomy.MaxLedgerEntries}");
        }

        foreach (var (key, offer) in world.Offers)
        {
            if (offer == null) { problems.Add($"offer[{key}] 为 null"); continue; }
            if (string.IsNullOrWhiteSpace(offer.Id)) problems.Add($"offer[{key}] 缺少 Id");
            else if (offer.Id != key) problems.Add($"offer[{key}] 的 Id 是 {offer.Id}，与字典键不一致");
            if (offer.Amount < 0) problems.Add($"offer[{key}] 金额为负：{offer.Amount}");

            // 双向引用一致性：交易单指向的人必须在账本里存在。
            foreach (string person in new[] { offer.InitiatorId, offer.CounterpartyId })
            {
                if (person.Length == 0 || person == "player") continue;
                if (!world.Npcs.ContainsKey(person))
                    problems.Add($"offer[{key}] 引用了不存在的人物 {person}");
            }

            if (TradeOfferStates.IsTerminal(offer.State) && offer.LedgerEntryId.Length == 0
                && offer.State == TradeOfferStates.Confirmed)
                problems.Add($"offer[{key}] 已确认但缺少流水号");
        }

        if (world.Receipts.Count > EconomyWorld.MaxReceipts * 2)
            problems.Add($"收据条数 {world.Receipts.Count} 异常膨胀，可能未裁剪");

        if (world.Offers.Count > EconomyWorld.MaxOffers * 2)
            problems.Add($"交易单条数 {world.Offers.Count} 异常膨胀，可能未裁剪");

        return problems;
    }

    // ─────────────────────────────────────────────────────────────────
    // 内部
    // ─────────────────────────────────────────────────────────────────

    private EconomyWorld NewWorld(string worldId) => new()
    {
        WorldId = worldId,
        CreatedAt = Stamp(),
        LastWrittenAt = Stamp()
    };

    private bool TryRead(string path, string expectedWorldId, out EconomyWorld? world, out string? error)
    {
        world = null;
        error = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<EconomyWorld>(File.ReadAllText(path), Json);
            if (parsed == null) { error = "反序列化结果为空"; return false; }
            if (string.IsNullOrWhiteSpace(parsed.WorldId)) { error = "缺少 WorldId"; return false; }
            if (parsed.SchemaVersion > EconomyWorld.CurrentSchemaVersion)
            {
                error = $"账本来自更新的版本（schema {parsed.SchemaVersion} > {EconomyWorld.CurrentSchemaVersion}）";
                return false;
            }
            // 串档防护：文件名与内容里的 WorldId 必须一致。
            if (!string.Equals(SafeWorldId(parsed.WorldId), SafeWorldId(expectedWorldId), StringComparison.Ordinal))
            {
                error = $"内容里的 WorldId 是 {parsed.WorldId}，与请求的 {expectedWorldId} 不符";
                return false;
            }
            var problems = Validate(parsed);
            if (problems.Count > 0) { error = string.Join("；", problems); return false; }

            world = parsed;
            return true;
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>把坏文件改名保留，绝不删除。返回保留后的路径。</summary>
    private string PreserveBroken(string path)
    {
        try
        {
            string broken = $"{path}.broken-{_clock.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, broken, overwrite: true);
            return broken;
        }
        catch (IOException e)
        {
            _log.Warn("store", $"保留坏文件失败：{path}", e);
            return path;
        }
    }

    private void WriteAtomic(string path, string content, bool backup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + TempSuffix;
        File.WriteAllText(temp, content);
        if (backup && File.Exists(path)) File.Copy(path, path + BackupSuffix, overwrite: true);
        File.Move(temp, path, overwrite: true);
    }

    private string Stamp() => _clock.UtcNow.ToString("O");
}
