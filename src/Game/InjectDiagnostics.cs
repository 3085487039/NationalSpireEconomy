using System.Reflection;
using System.Text;
using HarmonyLib;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Inject;

namespace NationalSpireEconomy.Game;

/// <summary>稳定段（S 段）自证的结果。</summary>
public sealed record StableProof(
    bool Passed,
    int Length,
    string Fingerprint,
    bool HasDigits,
    bool HasHeader,
    bool BalanceLeaked,
    string Text,
    string? Error);

/// <summary>插入自证的结果。</summary>
public sealed record InsertionProof(
    bool Passed,
    string Placement,
    bool MessageCountUnchanged,
    bool Idempotent,
    string? Error);

/// <summary>影子推进自证的结果。</summary>
public sealed record TickProof(
    bool Passed,
    int Seeded,
    int FirstDueDay,
    int AdvancedTo,
    int SalariesPaid,
    bool Persisted,
    bool LedgerBalanced,
    bool NoEarlyPayout,
    string? Error);

/// <summary>易变段（V 段）自证的结果。</summary>
public sealed record VolatileProof(
    bool Passed,
    int Length,
    string Fingerprint,
    bool HasDigits,
    bool HasHeader,
    bool BalancePresent,
    bool PendingVoicePresent,
    string Text,
    string? Error);

/// <summary>易变段插入自证的结果。</summary>
public sealed record VolatileInsertionProof(
    bool Passed,
    string Placement,
    int MessageIndex,
    bool MessageCountUnchanged,
    bool Idempotent,
    bool PlayerMessageUntouched,
    string? Error);

/// <summary>
/// 注入与推进的启动期强制诊断。
/// <para>
/// <b>设计纪律（沿用阶段 2 的 <see cref="PatchDiagnostics"/>）</b>：
/// <list type="number">
/// <item>成功失败都要说话，而且打到 <b>Godot 控制台</b>（<c>logs\godot.log</c>）——
/// 模组初始化阶段唯一能看见的地方。</item>
/// <item>自证<b>完全不依赖 AI 配合</b>：它自己合成一段稳定段、自己拼一个模拟的原版 system、
/// 自己在一个临时世界推进到第 1 赛季结算日（含"结算日之前不发薪"的断言），全部是确定性断言。
/// 不要去游戏里诱导 NPC 输出标记。</item>
/// <item>每条结论都给出可复核的数字（长度、指纹、人数、天数），而不是一句"成功"。</item>
/// </list>
/// </para>
/// </summary>
public static class InjectDiagnostics
{
    /// <summary>控制台固定前缀。搜这个就能找到全部注入诊断结论。</summary>
    public const string ConsolePrefix = "[Economy-Inject]";

    /// <summary>易变段（V 段）专用的控制台前缀。与 S 段分开，便于分别搜。</summary>
    public const string ConsolePrefix_V = "[Economy-Inject-V]";

    /// <summary>自证用的假余额。刻意选一个会渲染成 <c>$123,456</c> 的值，便于断言它没有泄漏进稳定段。</summary>
    public const long ProbeBalanceUsd = 123_456;

    /// <summary>自证用的假世界 id 前缀。</summary>
    public const string ProbeWorldId = "selfcheck";

    public static string LastReport { get; private set; } = "";
    public static bool? SelfTestPassed { get; private set; }
    public static bool? TickSelfTestPassed { get; private set; }
    public static string? SelfTestError { get; private set; }

    public static StableProof? LastStableProof { get; private set; }
    public static InsertionProof? LastInsertionProof { get; private set; }
    public static TickProof? LastTickProof { get; private set; }
    public static VolatileProof? LastVolatileProof { get; private set; }
    public static VolatileInsertionProof? LastVolatileInsertionProof { get; private set; }

    /// <summary>
    /// 跑一遍完整诊断：挂载两个补丁 + 三项自证，并把结论写两处日志。
    /// </summary>
    /// <returns>本次成功挂上的补丁方法名，供 <see cref="ModState.Patches"/> 使用。</returns>
    public static IReadOnlyList<string> Run(Harmony? harmony, IEconomyLog? log, Action<string, bool>? console,
        ConfigLoadResult? config, string? dataRoot)
    {
        var patches = new List<string>();
        var report = new StringBuilder();

        Emit(console, log, $"{ConsolePrefix} 正在挂载 S 段注入与影子推进…");
        SelfTestError = null;

        if (config == null)
        {
            Emit(console, log, $"{ConsolePrefix} 失败：配置未加载，注入与推进都不挂载。");
            SelfTestPassed = null;
            TickSelfTestPassed = null;
            LastReport = "配置未加载";
            return patches;
        }

        // ── 1. 原版程序集（补丁目标在里面） ──
        Type? messagePrompts = FindType(PrivateMessagePatch.TargetTypeName);
        Type? careerEngine = FindType(CareerDayPatch.TargetTypeName);
        if (messagePrompts == null || careerEngine == null)
        {
            NationalSpireLocator.EnsureLoaded(null, null);
            messagePrompts ??= FindType(PrivateMessagePatch.TargetTypeName);
            careerEngine ??= FindType(CareerDayPatch.TargetTypeName);
        }

        if (messagePrompts == null)
            Emit(console, log, $"{ConsolePrefix} S 段注入失败：找不到类型 {PrivateMessagePatch.TargetTypeName}（原版改名或未加载）");

        if (careerEngine == null)
            Emit(console, log, $"{ConsolePrefix} 日期推进失败：找不到类型 {CareerDayPatch.TargetTypeName}（原版改名或未加载）");

        // ── 2. 方案 A：身份来源 + 注入挂载点 ──
        // 阶段 3-D 实测证明旧的 PrivateMessagePrompts.Compose 挂载点不在真实私信的执行路径上
        // （自证全过、私信真实发生，注入却 0 行）。方案 A 把干预点后移到最后一站，
        // 并且【只此一处】——旧的 Compose 注入补丁已按单轨原则退场（见 Initialize 里的 MarkRetired）。
        {
            // 2a. 身份来源：ProcessPrivateAsync 的异步状态机（那里同时有 CareerData 与人物 id）。
            // 元数据扫描已确认 CareerEngine 没有"当前生涯"的静态入口，所以这里是唯一可靠来源。
            var machines = PrivateIdentityTrackPatch.FindStateMachines();
            Emit(console, log, machines.Count == 0
                ? $"{ConsolePrefix} 身份来源：未找到 {PrivateIdentityTrackPatch.MethodName} 的异步状态机" +
                  $"（找不到就只能靠 Context/Compose 的身份槽，失败时会明确拒绝注入）"
                : $"{ConsolePrefix} 身份来源：发现 {machines.Count} 份 {PrivateIdentityTrackPatch.MethodName} 状态机");
            if (machines.Count > 0)
            {
                bool trackOk = PrivateIdentityTrackPatch.ApplyTo(harmony, machines, log);
                Emit(console, log, $"{ConsolePrefix} 身份来源挂载：{(trackOk ? "成功" : "失败")}｜" +
                                   $"{PrivateIdentityTrackPatch.Detail}");
                if (trackOk) patches.AddRange(PrivateIdentityTrackPatch.PatchedMethods);
            }

            // 2b. 注入挂载点：RequestPrivateAsync（消息序列化前的最后一站）。
            var requestTargets = PrivateRequestInjectionPatch.FindRequestPrivateMethods();
            Emit(console, log, requestTargets.Count == 0
                ? $"{ConsolePrefix} 方案 A 挂载失败：遍历全部已加载程序集都没找到 " +
                  $"{PrivateRequestInjectionPatch.MethodName}({PrivateRequestInjectionPatch.FirstParameterTypeName}, …)"
                : $"{ConsolePrefix} 方案 A 目标：发现 {requestTargets.Count} 份 " +
                  $"{PrivateRequestInjectionPatch.MethodName}({PrivateRequestInjectionPatch.FirstParameterTypeName}, …)");

            foreach (var method in requestTargets)
                Emit(console, log, $"{ConsolePrefix}   · {PrivateRequestInjectionPatch.SignatureOf(method)}");

            if (requestTargets.Count > 0)
            {
                bool ok = PrivateRequestInjectionPatch.ApplyTo(harmony, requestTargets, log, console);
                Emit(console, log, $"{ConsolePrefix} 方案 A 挂载：{(ok ? "成功" : "失败")}｜" +
                                   $"{PrivateRequestInjectionPatch.Detail}");
                if (ok) patches.AddRange(PrivateRequestInjectionPatch.PatchedMethods);

                // ★ 自证：把解析到的签名与 token 写进日志，避免"以为挂上了其实没挂"的静默失败。
                Emit(console, log, PrivateRequestInjectionPatch.Applied
                    ? $"{ConsolePrefix} 挂载点自证：已挂 {PrivateRequestInjectionPatch.PatchedMethods.Count} 份｜" +
                      $"第一份 token=0x{TokenOf(requestTargets[0])}｜{PrivateRequestInjectionPatch.SignatureOf(requestTargets[0])}"
                    : $"{ConsolePrefix} 挂载点自证：未挂上任何一份——注入不会生效，请把上面几行发我");
            }
        }

        // ── 3. 挂日期推进（影子存档的生命周期） ──
        if (careerEngine != null)
        {
            var advance = CareerDayPatch.FindAdvanceMethod(careerEngine);
            if (advance == null)
            {
                Emit(console, log,
                    $"{ConsolePrefix} 日期推进失败：{careerEngine.Name}.{CareerDayPatch.PrimaryMethodName}(CareerData) " +
                    $"与后备的 {CareerDayPatch.FallbackMethodName}(CareerData,int) 都不存在");
            }
            else if (harmony == null)
            {
                Emit(console, log, $"{ConsolePrefix} 日期推进失败：Harmony 实例未建立");
            }
            else
            {
                bool ok = CareerDayPatch.ApplyTo(harmony, careerEngine, advance, log);
                Emit(console, log, $"{ConsolePrefix} 日期推进挂载：{(ok ? "成功" : "失败")}｜{CareerDayPatch.Detail}");
                if (ok) patches.AddRange(CareerDayPatch.PatchedMethods);
            }
        }

        // ── 4. 稳定段自证（零数字 + 不含余额） ──
        if (!config.Economy.Injection.StableEnabled)
            Emit(console, log,
                $"{ConsolePrefix} 注意：injection.stableEnabled = false，稳定段不会真的注入" +
                "（自证照跑，用来验证关闭状态下的代码路径与模板是否仍然健康）。");

        var stable = ProveStableSegment(config);
        LastStableProof = stable;
        if (stable.Error != null) SelfTestError = stable.Error;

        Emit(console, log, stable.Passed
            ? $"{ConsolePrefix} S 段自证：长度 {stable.Length}｜指纹 {stable.Fingerprint}｜零数字 通过｜含标题 通过｜余额未泄漏 通过"
            : $"{ConsolePrefix} S 段自证：未通过｜长度 {stable.Length}｜零数字 {(stable.HasDigits ? "失败" : "通过")}" +
              $"｜含标题 {(stable.HasHeader ? "通过" : "失败")}｜余额泄漏 {(stable.BalanceLeaked ? "是" : "否")}" +
              (stable.Error != null ? $"｜{stable.Error}" : ""));

        // ── 5. 插入自证（位置 + 不新增 message + 幂等） ──
        var insertion = ProveInsertion(config, stable.Text);
        LastInsertionProof = insertion;
        if (insertion.Error != null) SelfTestError ??= insertion.Error;

        Emit(console, log, insertion.Passed
            ? $"{ConsolePrefix} 插入自证：锚点 命中｜位置 {insertion.Placement}｜message 条数 不变｜重复插入 幂等 通过"
            : $"{ConsolePrefix} 插入自证：未通过｜位置 {insertion.Placement}" +
              $"｜message 条数 {(insertion.MessageCountUnchanged ? "不变" : "变了")}" +
              $"｜幂等 {(insertion.Idempotent ? "通过" : "失败")}" +
              (insertion.Error != null ? $"｜{insertion.Error}" : ""));

        // ── 6. 影子推进自证（前 N 天不发薪 → 结算日准时发 → 落盘 → 账实一致） ──
        var tick = ProveTick(config, dataRoot, log, console);
        LastTickProof = tick;
        TickSelfTestPassed = tick.Passed;
        if (tick.Error != null) SelfTestError ??= tick.Error;

        Emit(console, log,
            $"{ConsolePrefix} 发薪规则：第 1 赛季按赛季末结算（第 {tick.FirstDueDay} 天）" +
            $"；第 2 赛季起按 salaryPayoutMode = {config.Economy.Season.SalaryPayoutMode}");

        Emit(console, log, tick.Passed
            ? $"{ConsolePrefix} 影子推进自证：播种 {tick.Seeded} 人｜前 {tick.FirstDueDay - 1} 天不发薪 通过" +
              $"｜第 {tick.AdvancedTo} 天发薪 {tick.SalariesPaid} 人｜落盘 通过｜账实一致 通过"
            : $"{ConsolePrefix} 影子推进自证：未通过｜播种 {tick.Seeded} 人｜水位线 {tick.AdvancedTo}" +
              $"｜前 {tick.FirstDueDay - 1} 天不发薪 {(tick.NoEarlyPayout ? "通过" : "失败")}" +
              $"｜落盘 {(tick.Persisted ? "通过" : "失败")}｜账实一致 {(tick.LedgerBalanced ? "通过" : "失败")}" +
              (tick.Error != null ? $"｜{tick.Error}" : ""));

        // ── 7. 易变段自证（含数字 + 含余额 + 含未竟之言） ──
        if (!config.Economy.Injection.VolatileEnabled)
            Emit(console, log,
                $"{ConsolePrefix_V} 注意：injection.volatileEnabled = false，易变段不会真的注入" +
                "（自证照跑，用来验证关闭状态下的模板是否仍然健康）。");

        var volatileProof = ProveVolatileSegment(config);
        LastVolatileProof = volatileProof;
        if (volatileProof.Error != null) SelfTestError ??= volatileProof.Error;

        Emit(console, log, volatileProof.Passed
            ? $"{ConsolePrefix_V} V 段自证：长度 {volatileProof.Length}｜指纹 {volatileProof.Fingerprint}" +
              $"｜含数字 通过｜含标题 通过｜含余额 通过｜含未竟之言 通过"
            : $"{ConsolePrefix_V} V 段自证：未通过｜长度 {volatileProof.Length}" +
              $"｜含数字 {(volatileProof.HasDigits ? "通过" : "失败")}" +
              $"｜含标题 {(volatileProof.HasHeader ? "通过" : "失败")}" +
              $"｜含余额 {(volatileProof.BalancePresent ? "通过" : "失败")}" +
              $"｜含未竟之言 {(volatileProof.PendingVoicePresent ? "通过" : "失败")}" +
              (volatileProof.Error != null ? $"｜{volatileProof.Error}" : ""));

        // ── 8. 易变段插入自证（落点 + 不新增 message + 幂等 + 不污染玩家那条消息） ──
        var volatileInsertion = ProveVolatileInsertion(config, volatileProof.Text);
        LastVolatileInsertionProof = volatileInsertion;
        if (volatileInsertion.Error != null) SelfTestError ??= volatileInsertion.Error;

        Emit(console, log, volatileInsertion.Passed
            ? $"{ConsolePrefix_V} 插入自证：锚点 命中｜位置 {volatileInsertion.Placement}" +
              $"｜message 下标 {volatileInsertion.MessageIndex}｜message 条数 不变｜重复插入 幂等 通过｜未污染玩家消息 通过"
            : $"{ConsolePrefix_V} 插入自证：未通过｜位置 {volatileInsertion.Placement}" +
              $"｜message 条数 {(volatileInsertion.MessageCountUnchanged ? "不变" : "变了")}" +
              $"｜幂等 {(volatileInsertion.Idempotent ? "通过" : "失败")}" +
              $"｜未污染玩家消息 {(volatileInsertion.PlayerMessageUntouched ? "通过" : "失败")}" +
              (volatileInsertion.Error != null ? $"｜{volatileInsertion.Error}" : ""));

        // ── 9. 总结 ──
        // S/V 段注入的判据现在是"方案 A 的挂载点是否真的挂上了"。
        // 旧的 Compose 挂载点已按单轨原则退场（它不在真实私信路径上），不再参与通过判据。
        bool tickOk = CareerDayPatch.Applied;
        bool injectOk = PrivateRequestInjectionPatch.Applied;
        bool allOk = stable.Passed && insertion.Passed && tick.Passed
                     && volatileProof.Passed && volatileInsertion.Passed;
        SelfTestPassed = allOk && tickOk && injectOk;

        string injectionState = injectOk
            ? $"方案 A 注入挂载 {PrivateRequestInjectionPatch.PatchedMethods.Count} 份"
            : "方案 A 注入【未挂上】";
        string summary = allOk && tickOk && injectOk
            ? $"{ConsolePrefix} 结果：成功｜{injectionState}｜日期推进 {CareerDayPatch.Detail}｜自证 通过"
            : $"{ConsolePrefix} 结果：失败｜{injectionState}｜日期推进 {(tickOk ? "已挂载" : "未挂载")}｜自证 未通过";

        Emit(console, log, summary);
        report.AppendLine(summary);
        report.AppendLine($"稳定段：长度 {stable.Length}，指纹 {stable.Fingerprint}，零数字 {!stable.HasDigits}");
        report.AppendLine($"S 段插入位置：{insertion.Placement}，message 条数不变 {insertion.MessageCountUnchanged}");
        report.AppendLine($"易变段：长度 {volatileProof.Length}，指纹 {volatileProof.Fingerprint}，" +
                          $"含数字 {volatileProof.HasDigits}，含余额 {volatileProof.BalancePresent}");
        report.AppendLine($"V 段插入位置：{volatileInsertion.Placement}，message 下标 {volatileInsertion.MessageIndex}，" +
                          $"message 条数不变 {volatileInsertion.MessageCountUnchanged}");
        report.AppendLine($"影子推进：第 1 赛季第 {tick.FirstDueDay} 天结算，水位线 {tick.AdvancedTo}，" +
                          $"发薪 {tick.SalariesPaid} 人，前 {tick.FirstDueDay - 1} 天不发薪 {tick.NoEarlyPayout}，落盘 {tick.Persisted}");
        LastReport = report.ToString();
        return patches;
    }

    // ─────────────────────────────────────────────────────────────────
    // 三项自证（都可以单独调用，便于单测）
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 合成一段稳定段并断言三条硬性契约：非空、<b>零数字</b>、不含余额文本。
    /// </summary>
    public static StableProof ProveStableSegment(ConfigLoadResult config)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            var world = new EconomyWorld { WorldId = ProbeWorldId, LastTickedDay = 1, LastSeason = 1 };
            var npc = new NpcEconomy
            {
                PersonId = ProbeWorldId + "-npc",
                Balance = ProbeBalanceUsd,
                TierId = "professional",
                PersonalityKind = "热心同好",
                Season = 1
            };
            var facts = new NpcFacts(ProbeWorldId + "-npc", "职业选手", Rating: 1500, MaxAscension: 8,
                PersonalityKind: "热心同好", PersonalityVersion: 1);

            var input = new PromptInjectInput(world, npc, facts, "professional",
                config.Personality, config.Prompt, Favour: 42, Day: 30, Season: 1);

            var injector = new PromptInjector();
            var injection = injector.BuildStableOnly(input, commit: false);
            string text = injection.StableText;

            bool hasDigits = PrivateMessagePatch.ContainsDigit(text);
            bool hasHeader = config.Prompt.TryGet("stableHeader", out var header) && text.Contains(header, StringComparison.Ordinal);
            bool balanceLeaked = text.Contains(MoneyUnits.Format(ProbeBalanceUsd), StringComparison.Ordinal)
                                 || text.Contains(ProbeBalanceUsd.ToString(), StringComparison.Ordinal);

            bool passed = text.Length > 0 && !hasDigits && hasHeader && !balanceLeaked;

            return new StableProof(passed, text.Length, injection.Fingerprint, hasDigits, hasHeader, balanceLeaked,
                text, passed ? null : DescribeStableFailure(text.Length, hasDigits, hasHeader, balanceLeaked));
        }
        catch (Exception e)
        {
            return new StableProof(false, 0, "", false, false, false, "",
                e.GetType().Name + ": " + e.Message);
        }
    }

    private static string DescribeStableFailure(int length, bool digits, bool header, bool leaked)
    {
        var reasons = new List<string>();
        if (length == 0) reasons.Add("稳定段是空的（模板缺失或 stableEnabled 关闭）");
        if (digits) reasons.Add("稳定段里出现了数字");
        if (!header) reasons.Add("缺少 stableHeader 标题，无法作为可搜索标记");
        if (leaked) reasons.Add("余额文本泄漏进了稳定段");
        return string.Join("；", reasons);
    }

    /// <summary>
    /// 在一个<b>模拟的</b>原版 system 消息上做插入，断言：插在锚点之前、message 条数不变、重复插入幂等。
    /// </summary>
    public static InsertionProof ProveInsertion(ConfigLoadResult config, string stableText)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            const string anchor = SystemPromptInsert.DefaultAnchor;
            string anchorEffective = config.Prompt.Get("stableAnchor", anchor);

            var system = "【模拟的原版 system：世界设定 + 私信协议串】\n"
                         + "…（此处是原版的 Protocol / SkillProtocol / MoodProtocol / MatchProtocol 等七串）…\n"
                         + anchor + "，性格字段描述可以变化的倾向；本次行为与表达遵循上述私信提示词。";

            var messages = new List<Dictionary<string, string>>
            {
                new() { ["role"] = "system", ["content"] = system },
                new() { ["role"] = "user", ["content"] = "【模拟的人物资料】" },
                new() { ["role"] = "user", ["content"] = "【模拟的当前上下文】" }
            };

            int before = messages.Count;
            var first = SystemPromptInsert.InsertIntoMessages(messages, stableText, anchorEffective);
            bool countUnchanged = messages.Count == before;

            string afterFirst = messages[0]["content"];
            bool inserted = first.Changed
                            && first.Placement == InsertPlacements.BeforeAnchor
                            && afterFirst.Contains(stableText, StringComparison.Ordinal);

            // 插在锚点之前：锚点必须仍然在正文里，且稳定段必须出现在锚点之前。
            bool beforeAnchor = inserted
                                && afterFirst.IndexOf(stableText, StringComparison.Ordinal)
                                < afterFirst.IndexOf(anchorEffective, StringComparison.Ordinal);

            var second = SystemPromptInsert.InsertIntoMessages(messages, stableText, anchorEffective);
            bool idempotent = !second.Changed
                              && second.Placement == InsertPlacements.AlreadyPresent
                              && messages[0]["content"] == afterFirst;

            bool passed = inserted && beforeAnchor && countUnchanged && idempotent;

            return new InsertionProof(passed, first.Placement, countUnchanged, idempotent,
                passed ? null : $"插入 {(inserted ? "成功" : "失败")}，位置 {(beforeAnchor ? "正确" : "不在锚点之前")}");
        }
        catch (Exception e)
        {
            return new InsertionProof(false, "", false, false, e.GetType().Name + ": " + e.Message);
        }
    }

    /// <summary>
    /// 影子推进自证：在一个<b>临时目录</b>里建世界 → 播种两人 →
    /// <b>推到第 1 赛季结算日的前一天（应一分钱不发）</b> → 推到结算日（应准时发薪）→
    /// 断言水位线、发薪、落盘、账实一致。
    /// <para>
    /// 为什么要分两步：第 1 赛季的工资是在<b>赛季末</b>结算的。如果只推一次到结算日，
    /// 那么"第 1 天就发薪"的旧缺陷与正确行为会给出<b>同样的发薪人数</b>，自证就白跑了。
    /// 前一步专门断言"结算日之前一分钱没发"，才真正锁住这条规则。
    /// </para>
    /// <para>
    /// 临时目录用完即删，<b>绝不碰真实的影子存档</b>——它跑在 <c>&lt;数据根&gt;/selfcheck/&lt;guid&gt;</c> 下。
    /// </para>
    /// </summary>
    public static TickProof ProveTick(ConfigLoadResult config, string? dataRoot, IEconomyLog? log,
        Action<string, bool>? console)
    {
        ArgumentNullException.ThrowIfNull(config);

        string baseRoot = string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : dataRoot!;
        string root = Path.Combine(baseRoot, "selfcheck", Guid.NewGuid().ToString("N"));

        try
        {
            var store = new EconomyStore(root, null, log);
            var runtime = new ShadowEconomyRuntime(store, config, log ?? ConsoleEconomyLog.Instance, console);

            var facts = new List<INpcFacts>
            {
                new NpcFacts(ProbeWorldId + "-a", "职业选手", Rating: 1500, MaxAscension: 8,
                    PersonalityKind: "热心同好", PersonalityVersion: 1),
                new NpcFacts(ProbeWorldId + "-b", "普通玩家", Rating: 900, MaxAscension: 3,
                    PersonalityKind: "看戏起哄者", PersonalityVersion: 1)
            };

            var world = runtime.EnsureWorld(ProbeWorldId, facts);
            if (world == null)
                return new TickProof(false, 0, 0, 0, 0, false, false, false, "无法建立自证世界。");

            int seeded = world.Npcs.Count;
            long balanceBefore = world.Npcs[ProbeWorldId + "-a"].Balance;
            int firstDueDay = EconomyTick.SalaryDueDay(config.Economy, EconomyTick.FirstSeason);
            long expectedSalary = EconomyTick.SalaryFor(config.Economy, "professional");

            // ① 结算日之前：一分钱都不许发，余额必须原封不动。
            var early = runtime.AdvanceTo(ProbeWorldId, facts, firstDueDay - 1);
            long balanceEarly = world.Npcs[ProbeWorldId + "-a"].Balance;
            bool noEarlyPayout = early.SalariesPaid == 0
                                 && balanceEarly == balanceBefore
                                 && world.LastTickedDay == firstDueDay - 1;

            // ② 结算日当天：准时发一次。这条路同时验证了水位线、EconomyLedger 入账与落盘。
            var settle = runtime.AdvanceTo(ProbeWorldId, facts, firstDueDay);

            long balanceAfter = world.Npcs[ProbeWorldId + "-a"].Balance;
            bool ledgerBalanced = balanceAfter == balanceBefore + expectedSalary;
            bool persisted = File.Exists(store.WorldPath(ProbeWorldId));
            bool advanced = world.LastTickedDay == firstDueDay;

            bool passed = seeded == 2 && noEarlyPayout && advanced && ledgerBalanced && persisted
                          && settle.SalariesPaid == 2;

            return new TickProof(passed, seeded, firstDueDay, world.LastTickedDay, settle.SalariesPaid, persisted,
                ledgerBalanced, noEarlyPayout,
                passed
                    ? null
                    : $"播种 {seeded} 人；第 {firstDueDay - 1} 天时发薪 {early.SalariesPaid} 人" +
                      $"、余额 {balanceEarly}（应为 {balanceBefore}）；第 {firstDueDay} 天发薪 {settle.SalariesPaid} 人" +
                      $"、余额 {balanceAfter}（期望 {balanceBefore + expectedSalary}）");
        }
        catch (Exception e)
        {
            return new TickProof(false, 0, 0, 0, 0, false, false, false, e.GetType().Name + ": " + e.Message);
        }
        finally
        {
            // 自证产物一律清掉：它是临时账本，不该留在玩家的数据目录里。
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception)
            {
                // 删不掉也不影响结论（例如文件被占用）。
            }
        }
    }

    /// <summary>
    /// 合成一段易变段并断言四条契约：非空、<b>含数字</b>（这正是 V 段存在的意义）、
    /// 含余额文本、含"未竟之言"。
    /// <para>
    /// 与 S 段自证刻意用同一个假余额：S 段必须"不含 $123,456"、V 段必须"含 $123,456"，
    /// 两条自证合起来就是"数字被隔离在 V 段"的可证事实。
    /// </para>
    /// </summary>
    public static VolatileProof ProveVolatileSegment(ConfigLoadResult config)
    {
        ArgumentNullException.ThrowIfNull(config);

        const string pendingVoice = "囊中羞涩，只能口头支持。";

        try
        {
            var world = new EconomyWorld { WorldId = ProbeWorldId, LastTickedDay = 84, LastSeason = 1 };
            var npc = new NpcEconomy
            {
                PersonId = ProbeWorldId + "-npc",
                Balance = ProbeBalanceUsd,
                TierId = "professional",
                PersonalityKind = "热心同好",
                Season = 1,
                PendingVoice = pendingVoice
            };
            npc.SeasonStats.SeasonIncome = 96_000;
            npc.SeasonStats.SalaryIncome = 72_000;
            npc.SeasonStats.WinBonusIncome = 24_000;
            npc.AppendLedger(new LedgerEntry
            {
                Id = "probe-last",
                Day = 80,
                Season = 1,
                Kind = "赞助",
                Title = "收到赞助",
                Amount = 20_000,
                BalanceAfter = ProbeBalanceUsd
            });

            var facts = new NpcFacts(ProbeWorldId + "-npc", "职业选手", Rating: 1500, MaxAscension: 8,
                PersonalityKind: "热心同好", PersonalityVersion: 1);

            var input = new PromptInjectInput(world, npc, facts, "professional",
                config.Personality, config.Prompt, Favour: 42, Day: 84, Season: 1);

            var injection = new PromptInjector().BuildVolatileOnly(input, commit: false);
            string text = injection.Text;

            bool hasDigits = HasDigit(text);
            bool hasHeader = config.Prompt.TryGet("volatileHeader", out var header)
                             && text.Contains(header, StringComparison.Ordinal);
            bool balancePresent = text.Contains(MoneyUnits.Format(ProbeBalanceUsd), StringComparison.Ordinal);
            bool voicePresent = text.Contains(pendingVoice, StringComparison.Ordinal);

            bool passed = injection.Enabled && text.Length > 0 && hasDigits && hasHeader && balancePresent
                          && voicePresent;

            var reasons = new List<string>();
            if (!injection.Enabled) reasons.Add("配置里 volatileEnabled 关闭了");
            if (text.Length == 0) reasons.Add("易变段是空的（模板缺失？）");
            if (!hasDigits) reasons.Add("易变段里没有数字——它本该承载全部数字");
            if (!hasHeader) reasons.Add("缺少 volatileHeader 标题，无法作为可搜索标记");
            if (!balancePresent) reasons.Add("余额没有出现在易变段里");
            if (!voicePresent) reasons.Add("未竟之言没有出现在易变段里");

            return new VolatileProof(passed, text.Length, injection.Fingerprint, hasDigits, hasHeader,
                balancePresent, voicePresent, text, passed ? null : string.Join("；", reasons));
        }
        catch (Exception e)
        {
            return new VolatileProof(false, 0, "", false, false, false, false, "",
                e.GetType().Name + ": " + e.Message);
        }
    }

    /// <summary>
    /// 在一个<b>模拟的</b>原版消息列表上做 V 段插入，断言：
    /// 落点在以锚点开头的那条消息、message 条数不变、重复插入幂等、
    /// 而且<b>玩家自己那条消息一个字都没被改</b>。
    /// </summary>
    public static VolatileInsertionProof ProveVolatileInsertion(ConfigLoadResult config, string volatileText)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            const string anchor = CurrentSegmentInsert.DefaultAnchor;
            string anchorEffective = config.Prompt.Get("volatileAnchor", anchor);

            // 与 Compose 的真实顺序一致：system → 人物资料 → 历史记录 → 上下文（Current）→ 玩家消息。
            var messages = new List<Dictionary<string, string>>
            {
                new() { ["role"] = "system", ["content"] = "【模拟的原版 system：世界设定 + 协议串】" },
                new() { ["role"] = "user", ["content"] = "【模拟的人物资料】" },
                new() { ["role"] = "user", ["content"] = "【模拟的历史记录：上一轮聊天】" },
                new()
                {
                    ["role"] = "user",
                    ["content"] = anchor + "第 1 赛季第 12 天。\n\n…档案…\n\n小李对你的关系\n好感42，关系为熟悉。\n\n"
                                  + "…近期赛程…\n\n…短期比赛状态：正常，当前等级0。"
                },
                new() { ["role"] = "user", ["content"] = "第 1 赛季第 12 天，玩家发给小李的消息\n你好" }
            };

            int before = messages.Count;
            string playerBefore = messages[4]["content"];
            string stableMessageBefore = messages[1]["content"];

            var first = CurrentSegmentInsert.InsertIntoMessages(messages, volatileText, anchorEffective);
            bool countUnchanged = messages.Count == before;
            bool landedInCurrent = first.Changed
                                   && first.Placement == VolatilePlacements.Appended
                                   && first.MessageIndex == 3
                                   && messages[3]["content"].Contains(volatileText, StringComparison.Ordinal);
            bool playerUntouched = messages[4]["content"] == playerBefore
                                   && messages[1]["content"] == stableMessageBefore;

            var second = CurrentSegmentInsert.InsertIntoMessages(messages, volatileText, anchorEffective);
            bool idempotent = !second.Changed
                              && second.Placement == VolatilePlacements.AlreadyPresent
                              && messages.Count == before;

            bool passed = landedInCurrent && countUnchanged && playerUntouched && idempotent;

            var reasons = new List<string>();
            if (!landedInCurrent) reasons.Add($"落点不对（位置 {first.Placement}，命中下标 {first.MessageIndex}）");
            if (!countUnchanged) reasons.Add("message 条数变了");
            if (!playerUntouched) reasons.Add("动到了不该动的消息");
            if (!idempotent) reasons.Add("重复插入不幂等");

            return new VolatileInsertionProof(passed, first.Placement, first.MessageIndex, countUnchanged,
                idempotent, playerUntouched, passed ? null : string.Join("；", reasons));
        }
        catch (Exception e)
        {
            return new VolatileInsertionProof(false, "", -1, false, false, false,
                e.GetType().Name + ": " + e.Message);
        }
    }

    /// <summary>文本里是否出现阿拉伯数字。稳定段必须为 false，易变段必须为 true。</summary>
    private static bool HasDigit(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, @"\d");

    /// <summary>元数据 token（诊断用；动态方法上会抛，所以必须兜住）。</summary>
    private static string TokenOf(MethodBase method)
    {
        try
        {
            return method.MetadataToken.ToString("X8");
        }
        catch (Exception)
        {
            return "不可用（动态方法？）";
        }
    }

    // ─────────────────────────────────────────────────────────────────

    private static Type? FindType(string fullName)
    {
        foreach (var assembly in AssemblyInventory.AllAssemblies())
        {
            Type? found;
            try
            {
                found = assembly.GetType(fullName, throwOnError: false);
            }
            catch (Exception)
            {
                continue;
            }
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>把结论同时写到控制台与诊断日志两处（控制台才是初始化阶段唯一能看见的地方）。</summary>
    private static void Emit(Action<string, bool>? console, IEconomyLog? log, string line)
    {
        try
        {
            console?.Invoke(line, false);
        }
        catch (Exception)
        {
            // 控制台不可用不影响结论。
        }

        log?.Info("inject", line.Replace(ConsolePrefix, "").Trim());
    }
}
