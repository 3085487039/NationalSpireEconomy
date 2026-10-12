using NationalSpire;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Inject;

namespace NationalSpireEconomy.Game;

/// <summary>
/// 影子存档在游戏内的生命周期。
/// <para>
/// 它是"数据源"那一环：<b>没有它，S/V 段注入就是死的</b>——稳定段要档位与性格快照，
/// 易变段要最新余额，而这两样都只存在于影子存档里，必须由游戏内的日期推进驱动。
/// </para>
/// <para>
/// 三件事：
/// <list type="number">
/// <item><b>载入 + 增量播种</b>：按 <c>CareerData.WorldId</c> 分片载入账本；新出现的人
/// （新人登场、读档补人、转会）自动补一份初始资金。<c>EconomySeed</c> 本身幂等，
/// 已播过的人直接跳过，所以这一步可以反复调用。</item>
/// <item><b>推进</b>：把游戏的日期交给 <see cref="EconomyTick.AdvanceTo"/>，由它按幂等水位线
/// 补齐中间被跳过的每一天（发薪、跨赛季滚动都走 <see cref="EconomyLedger"/>）。</item>
/// <item><b>落盘</b>：推进之后立刻保存，保证"看到的余额"与"盘上的余额"一致。</item>
/// </list>
/// </para>
/// <para>
/// <b>进程内单例</b>：Harmony 补丁是静态方法，没有别的注入口，所以由
/// <see cref="EconomyModEntry.Runtime"/> 持有。加载好的账本在这里缓存，
/// 否则每发一条私信都要重读一遍上万人的 JSON。
/// </para>
/// </summary>
public sealed class ShadowEconomyRuntime
{
    private readonly EconomyStore _store;
    private readonly ConfigLoadResult _config;
    private readonly IEconomyLog _log;
    private readonly Action<string, bool>? _console;

    private readonly Dictionary<string, EconomyWorld> _worlds = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ShadowEconomyRuntime(EconomyStore store, ConfigLoadResult config, IEconomyLog log,
        Action<string, bool>? console = null, EconomyAudit? audit = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? ConsoleEconomyLog.Instance;
        _console = console;
        Audit = audit;

        // 共用同一个日志出口，便于把一段注入的全过程串起来看。
        Injector = new PromptInjector(_log);
    }

    /// <summary>
    /// 审计写入器（阶段 4 第二步：结算每笔一行写进 <c>audit/&lt;WorldId&gt;.jsonl</c>）。
    /// 为 null 时结算仍会执行，只是不留审计行（日志里会写明）。
    /// </summary>
    public EconomyAudit? Audit { get; }

    /// <summary>注入编排器。</summary>
    public PromptInjector Injector { get; }

    /// <summary>推进来源：玩家在游戏里真的走了日期。只有它才带"私信组装补丁是否命中"的提醒。</summary>
    public const string TickSourceGame = "游戏日期";

    /// <summary>推进来源：启动期自证。</summary>
    public const string TickSourceSelfCheck = "自证";

    public EconomyStore Store => _store;
    public ConfigLoadResult Config => _config;
    public IEconomyLog Log => _log;

    /// <summary>已经缓存的世界（测试与诊断用）。</summary>
    public EconomyWorld? CachedWorld(string worldId) =>
        worldId.Length > 0 && _worlds.TryGetValue(worldId, out var world) ? world : null;

    // ─────────────────────────────────────────────────────────────────
    // 载入与播种
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 拿到（必要时载入并播种）某个世界的账本。失败返回 null——<b>绝不抛回补丁</b>。
    /// </summary>
    public EconomyWorld? EnsureWorld(string worldId, IReadOnlyList<INpcFacts> people)
    {
        if (string.IsNullOrWhiteSpace(worldId))
        {
            _log.Warn("world", "WorldId 为空，跳过影子存档（读档中/未开局时会这样）。");
            return null;
        }

        lock (_gate)
        {
            if (!_worlds.TryGetValue(worldId, out var world))
            {
                try
                {
                    var report = _store.LoadWorld(worldId);
                    world = report.World;
                    _worlds[worldId] = world;
                    _log.Info("world",
                        $"载入影子存档 {worldId}：{report.Status}｜{world.Npcs.Count} 人｜水位线第 {world.LastTickedDay} 天。");

                    // 世界初始化这里必须说清发薪语义，理由有两个：
                    // ① 水位线是 0 且从没结算过赛季 = 全新世界，此时第 1 赛季的工资是
                    //    【赛季末】结算的（唯一判据在 EconomyTick.SalaryDueDay，这里不复制公式）；
                    // ② 因此播种绝不能把 LastSalaryPaidSeason 预置成 1——那会让第 1 赛季
                    //    永远拿不到工资。EconomySeed 保持默认的 0 正是为了第 84 天能发出来。
                    if (world.LastTickedDay == 0 && world.LastSeason == 0)
                    {
                        int firstDueDay = EconomyTick.SalaryDueDay(_config.Economy, EconomyTick.FirstSeason);
                        _log.Info("world",
                            $"世界 {worldId} 是新建的：第 1 赛季工资在第 {firstDueDay} 天（赛季末）结算，" +
                            $"第 {firstDueDay - 1} 天之前不发薪；第 2 赛季起按 salaryPayoutMode = " +
                            $"{_config.Economy.Season.SalaryPayoutMode} 结算。");
                    }

                    if (report.DataWasLost)
                        _log.Warn("world", $"影子存档 {worldId} 数据曾丢失并重建，此前资金记录不可恢复。");
                }
                catch (Exception e)
                {
                    _log.Error("world", $"载入影子存档 {worldId} 失败，本次不注入经济信息。", e);
                    return null;
                }
            }

            // 增量播种：已播过的人会被 EconomySeed 跳过，所以这里可以放心反复调用。
            // 但先做一次"有没有缺人"的廉价检查——EconomySeed 每次都会写一行播种日志，
            // 而本方法每天推进都会被调用，不检查的话日志会被"新增 0 人"刷满。
            try
            {
                if (HasUnseededPeople(world, people))
                {
                    var seeded = EconomySeed.Seed(world, people, _config.Economy, _log);
                    if (seeded.SeededCount > 0)
                    {
                        SaveQuietly(world, $"播种 {seeded.SeededCount} 人");
                        _log.Info("world",
                            $"世界 {worldId} 新增播种 {seeded.SeededCount} 人，账本共 {world.Npcs.Count} 人，" +
                            $"总额 {MoneyUnits.Format(world.SeededTotalUsd)}。");
                    }
                }
            }
            catch (Exception e)
            {
                // 播种失败不该让补丁崩掉：账本仍然是可用的，只是某些人没有余额。
                _log.Error("world", $"世界 {worldId} 播种失败：{e.GetType().Name}: {e.Message}");
            }

            return world;
        }
    }

    /// <summary>是否有还没播种的人。玩家 id 由 <c>EconomySeed</c> 自己跳过，这里也要排除。</summary>
    private static bool HasUnseededPeople(EconomyWorld world, IReadOnlyList<INpcFacts> people)
    {
        foreach (var person in people)
        {
            string id = person.Id;
            if (id.Length == 0) continue;
            if (id == EconomyTier.PlayerId) continue;
            if (!world.Npcs.ContainsKey(id)) return true;
        }
        return false;
    }

    /// <summary>从原版生涯数据取账本：跳过真人（他们的钱在 <c>CareerData.Credits</c> 里）。</summary>
    public EconomyWorld? WorldFor(CareerData data)
    {
        if (data == null) return null;

        // ★ 2026-10-12 甲方拍板的方案①：顺手清掉存档里【已经固化】的 [Trade: …] 标记。
        //   为什么挂在这里：私信按需载入、跳天、界面预览最终都会走到 WorldFor，
        //   是进程里最早、也最全地拿到"游戏正在用的那份 CareerData"的地方。
        //   同一份数据只清一次（按对象身份记），所以这条热路径上不会反复遍历会话树。
        //
        //   ★ 无论结果如何都必须留一行日志：第一版清理器在"拿不到结构"时静默返回，
        //     于是真机上"清理器没跑"与"跑了但没找到标记"完全无法区分。
        //     这里的 ProbeStructure 会把三层结构的真实名字打出来（Life / Mailbox / Turns）。
        bool cleaned = MailboxSanitizer.Sanitize(data, _log);
        if (MailboxSanitizer.Ran)
            _log.Info("trade", "[Economy-Marker] 存档结构探测：" + MailboxSanitizer.ProbeStructure(data) +
                              (cleaned ? "｜本次有清理" : "｜本次无改动"));

        IReadOnlyList<INpcFacts> people;
        try
        {
            people = NpcFactsProjection.ProjectLedgerPeople(data);
        }
        catch (Exception e)
        {
            _log.Error("world", "投影原版人物失败：" + e.Message);
            return null;
        }
        var world = EnsureWorld(data.WorldId ?? "", people);
        if (world != null) ImportPlayerWalletOnce(world, data);
        return world;
    }

    /// <summary>
    /// 玩家影子钱包的<b>首次导入</b>（阶段 4 第二步）：把原版 <c>Credits + CreditFraction</c>
    /// <b>只读</b>搬进影子存档一次，之后玩家的钱由我们的经济系统管辖（甲方拍板的方案 A）。
    /// <para>
    /// 为什么必须在这里做：<see cref="Economy.ShadowPlayerWallet"/> 在"未导入"状态下<b>拒绝任何动钱</b>
    /// （免得凭空造钱、或让后来的导入覆盖已变动的余额）。所以结算之前必须有一次成功的导入。
    /// </para>
    /// <para>
    /// 失败不抛出：这一条路径被游戏补丁直接调用。导入失败时余额保持 0 且水位线仍是"未导入"，
    /// 于是<b>结算会被拒绝</b>（钱不动），并在日志里留一行 Error——安全的失败方式。
    /// </para>
    /// </summary>
    private void ImportPlayerWalletOnce(EconomyWorld world, CareerData data)
    {
        if (world.Player.Imported) return;

        try
        {
            var snapshot = new PlayerWallet(data).Snapshot();
            var outcome = ShadowPlayerWallet.ImportOnce(world, snapshot.Credits, snapshot.Fraction,
                data.Day, data.Season, null, _log);
            if (outcome.Imported)
            {
                _log.Info("wallet", "[Economy-Wallet] " + outcome.Detail);
                SaveQuietly(world, "首次导入玩家影子钱包");
            }
        }
        catch (Exception e)
        {
            _log.Error("wallet",
                "玩家影子钱包首次导入失败（余额保持 0，结算会被拒绝——钱不会动）：" +
                $"{e.GetType().Name}: {e.Message}", e);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // 推进
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 把游戏当前日期推给影子存档。由 <c>CareerEngine.MoveToNextDay</c> 的后缀补丁调用，
    /// 于是"跳天/跳四周/下个赛季/赛后自动推进"全都覆盖到，且每推进一天只触发一次。
    /// </summary>
    public TickResult AdvanceTo(CareerData data)
    {
        if (data == null) return TickResult.Skipped;

        var world = WorldFor(data);
        if (world == null) return TickResult.Skipped;

        return AdvanceTo(world, data.Day, TickSourceGame);
    }

    /// <summary>给测试与自证用的显式入口：直接按 WorldId + 人群推进。</summary>
    public TickResult AdvanceTo(string worldId, IReadOnlyList<INpcFacts> people, int targetDay)
    {
        var world = EnsureWorld(worldId, people);
        if (world == null) return TickResult.Skipped;
        return AdvanceTo(world, targetDay, TickSourceSelfCheck);
    }

    /// <summary>
    /// 推进账本到 <paramref name="targetDay"/> 并落盘。
    /// <para>
    /// 幂等由 <see cref="EconomyTick"/> 的水位线负责：重复推进同一天、
    /// 回档后重进、读档重放都不会重复发薪。这里只负责"推进之后落盘 + 说清楚发生了什么"。
    /// </para>
    /// </summary>
    public TickResult AdvanceTo(EconomyWorld world, int targetDay, string source)
    {
        ArgumentNullException.ThrowIfNull(world);

        int before = world.LastTickedDay;
        int seasonBefore = world.LastSeason;
        TickResult result;
        try
        {
            result = EconomyTick.AdvanceTo(world, _config.Economy, targetDay, _log);
        }
        catch (Exception e)
        {
            _log.Error("tick", $"第 {targetDay} 天推进失败，账本保持原状（水位线第 {before} 天）。", e);
            return TickResult.Skipped;
        }

        // 幂等：水位线没动就说明这次什么都没发生（重复推进/更早的日期）。
        if (world.LastTickedDay == before) return TickResult.Skipped;

        SaveQuietly(world, $"推进到第 {world.LastTickedDay} 天");

        int advanced = world.LastTickedDay - before;
        // 只有赛季号真的变了才说"跨赛季"。两个坑：
        // ① 新世界的水位线是 0，第一次推进必然走进"赛季 1"，照着 TickResult.SeasonChanged
        //    打会得到一句误导人的"跨赛季已滚动"；
        // ② 所以还要排除 seasonBefore == 0 这一次——那是首次建立水位线，不是真的跨赛季。
        string seasonNote = world.LastSeason != seasonBefore && seasonBefore > 0
            ? $"，跨赛季 第 {seasonBefore} → 第 {world.LastSeason} 赛季"
            : "";
        string line = $"[Economy-Tick] 第 {before} 天 → 第 {world.LastTickedDay} 天" +
                      $"（第 {world.LastSeason} 赛季）：推进 {advanced} 天" +
                      seasonNote +
                      (result.SalariesPaid > 0
                          ? $"，发薪 {result.SalariesPaid} 人共 {MoneyUnits.Format(result.SalaryTotalUsd)}"
                          : "") +
                      $"｜{source}" + ComposeHintFor(source, PrivateMessagePatch.Invocations) + ProbeNoteFor(source);
        Emit(line);
        _log.Info("tick", line.Replace("[Economy-Tick] ", ""));

        return result;
    }

    /// <summary>
    /// 只要私信组装补丁<b>一次都没被调用过</b>，就在玩家推进日期时附一句提醒。
    /// <para>
    /// 它把"补丁没被调用"与"补丁被调用但条件不满足"一眼分开——阶段 3-A 正是前者无法区分。
    /// 命中过一次之后这句自动消失（不会长期刷日志）；自证用的推进不带它，免得启动日志里多一句噪音。
    /// </para>
    /// <para>
    /// 写成"纯函数 + 显式传入计数"是为了能直接单测，而不必依赖"进程里还没发生过一次注入"这种顺序前提。
    /// </para>
    /// </summary>
    public static string ComposeHintFor(string source, int composeInvocations) =>
        source == TickSourceGame && composeInvocations == 0
            ? "｜私信组装补丁 0 次（已发过私信的话，说明挂载点没被调用）"
            : "";

    /// <summary>
    /// 挂载点体检的计数摘要，附在**玩家推进日期**时的那一行上。
    /// <para>
    /// 放在这里是因为它是"玩家自己触发的低频日志"：点一次跳天就能看到四个探针各命中多少次，
    /// 不需要翻整个 godot.log。体检关掉时（<c>probeEnabled = false</c>）这一句消失。
    /// </para>
    /// </summary>
    private static string ProbeNoteFor(string source) =>
        source == TickSourceGame && HookProbe.Enabled ? "｜探针 " + HookProbe.Summary : "";

    // ─────────────────────────────────────────────────────────────────
    // 注入输入
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 组装一次注入所需的全部输入。<b>账本里没有的人返回 null</b>——
    /// 真人（<c>HumanIds</c>）本来就不进账本，于是他们不会被注入任何经济信息。
    /// </summary>
    public PromptInjectInput? BuildInput(CareerData data, CareerPerson? person, EconomyWorld world)
    {
        if (data == null) return null;
        return BuildInputFor(world, person, data.Day, data.Season, data);
    }

    /// <summary>
    /// 与上面等价，但<b>不要求调用方持有 <c>CareerData</c></b>：直接给世界、人物、日期/赛季，
    /// 以及组装私信时快照下来的那份 <paramref name="data"/>（可能为 null，只影响好感修正）。
    /// <para>
    /// 方案 A 需要它：<c>RequestPrivateAsync</c> 只拿得到 <c>AiOptions</c>，
    /// 世界/人物/天数全部来自身份槽。
    /// </para>
    /// </summary>
    public PromptInjectInput? BuildInputFor(EconomyWorld world, CareerPerson? person, int day, int season,
        CareerData? data = null)
    {
        if (person == null || world == null) return null;

        string personId = person.Id ?? "";
        if (personId.Length == 0) return null;
        if (!world.Npcs.TryGetValue(personId, out var npc)) return null;

        var facts = NpcFactsProjection.Project(person);
        int favour = 0;
        if (data != null)
        {
            try
            {
                favour = PrivateMessages.Relation(data, personId).Favour;
            }
            catch (Exception e)
            {
                // 好感读不到不该让整段注入失效：它只影响性格的施舍敏感度修正。
                _log.Warn("inject", $"读取 {personId} 的好感失败，按 0 处理：" + e.Message);
            }
        }

        string tierId = npc.TierId.Length > 0
            ? npc.TierId
            : facts != null
                ? EconomyTier.ResolveId(facts, _config.Economy.Seeding)
                : EconomySeed.FallbackTierId;

        return new PromptInjectInput(
            world,
            npc,
            facts,
            tierId,
            _config.Personality,
            _config.Prompt,
            favour,
            day,
            season,
            // 阶段 4 第一步：把"待确认交易的只读预览"挂进易变段，
            // 玩家就能在游戏自带的私信窗口里看到它（金额只能在 V 段）。
            // 阶段 4 第三步：同一条通路现在还带上"最近一笔交易到底怎么样了"（未成立原因 / 已结算回执）
            // 与一条账本口径 —— 模型只看得见 Prompt，看不见日志，所以真相必须走这里。
            TradePreviewWiring.NoteFor(world));
    }

    // ─────────────────────────────────────────────────────────────────
    // 内部
    // ─────────────────────────────────────────────────────────────────

    /// <summary>保存，但把失败降级成日志——补丁路径上不允许抛出。</summary>
    public bool SaveQuietly(EconomyWorld world, string reason)
    {
        try
        {
            bool written = _store.SaveWorld(world, force: true);
            if (written) _log.Debug("store", $"世界 {world.WorldId} 已落盘（{reason}）。");
            return written;
        }
        catch (Exception e)
        {
            _log.Error("store", $"世界 {world.WorldId} 落盘失败（{reason}）：{e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>同时写控制台与诊断日志。控制台才是模组早期唯一能看见的地方。</summary>
    public void Emit(string line)
    {
        try
        {
            _console?.Invoke(line, false);
        }
        catch (Exception)
        {
            // 控制台不可用不影响账本。
        }
    }
}
