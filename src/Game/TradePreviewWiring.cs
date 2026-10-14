using NationalSpire;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Inject;
using NationalSpireEconomy.Trade;

namespace NationalSpireEconomy.Game;

/// <summary>
/// <b>阶段 4 第一步的游戏侧接线：只读成交预览 + 双人确认</b>（<b>不动一分钱</b>）。
/// <para>
/// 它把"模型在私信里提出的交易"变成：① 一张 <c>pending</c> 交易单（<b>Reserve，不动余额</b>）、
/// ② 一份只读预览（挂进 V 段，玩家在游戏自带的私信窗口里就能看到）、
/// ③ 两条确认票（NPC / 玩家），全部记在收据本上，幂等由收据本天然保证。
/// </para>
/// <para>
/// <b>本步刻意不做的事</b>：结算（真正扣款/加款）。<see cref="TradeConfirmationGate.SettlementWired"/>
/// 恒为 false；本类里没有任何一处调用 <c>TradeEngine.Confirm</c> 或执行器。
/// </para>
/// <para>
/// <b>为什么确认界面走游戏自带的私信窗口</b>：这是当前唯一<b>已被真机证明</b>能与玩家交互的
/// 原版界面（我们的 V 段注入正落在它里面）。玩家在那里回一句 <c>确认 &lt;单号&gt;</c> 即可，
/// 解析是纯文本匹配、不依赖模型——符合"确定性优先、不让 NPC 帮忙输出标记"的纪律。
/// 自绘 Godot 弹窗作为 1b 备选，但它不改变本类：两条界面都会调用同一个确认闸门。
/// </para>
/// </summary>
public static class TradePreviewWiring
{
    /// <summary>
    /// 玩家确认指令的前缀（大小写/全半角都容忍）。
    /// <b>解析实现已移到 <see cref="Trade.PlayerTradeCommand"/></b>（零依赖、可单测）；
    /// 这里保留同名数组只为不破坏既有引用。
    /// </summary>
    public static readonly string[] ConfirmWords = PlayerTradeCommand.ConfirmWords;

    /// <summary>玩家拒绝指令的前缀（同 <see cref="ConfirmWords"/>，实现已移出）。</summary>
    public static readonly string[] DeclineWords = PlayerTradeCommand.DeclineWords;

    private static readonly object GateLock = new();
    private static readonly Dictionary<string, TradePreview> PendingByWorld = new(StringComparer.Ordinal);

    /// <summary>
    /// 玩家<b>本轮</b>说过的话（按世界存）。用途只有一个：判断模型是不是在<b>引用玩家打的字</b>
    /// （见 <see cref="TradeIntentCompleter.LooksLikePlayerEcho"/>）。由私信请求路径写入。
    /// </summary>
    private static readonly Dictionary<string, string[]> PlayerTextsByWorld = new(StringComparer.Ordinal);

    /// <summary>记下玩家这一轮说的话（在私信请求组装前调用）。</summary>
    public static void RememberPlayerTexts(string worldId, IEnumerable<string>? texts)
    {
        if (string.IsNullOrWhiteSpace(worldId)) return;
        string[] snapshot = texts?.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? [];
        lock (GateLock) PlayerTextsByWorld[worldId] = snapshot;
    }

    private static string[] PlayerTextsFor(string worldId)
    {
        lock (GateLock)
        {
            return PlayerTextsByWorld.TryGetValue(worldId ?? "", out var texts) ? texts : [];
        }
    }

    /// <summary>最近一次预览（诊断/测试用）。</summary>
    public static TradePreview? LastPreview { get; private set; }

    /// <summary>最近一次确认结论（诊断/测试用）。</summary>
    public static TradeConfirmationStatus? LastConfirmation { get; private set; }

    /// <summary>最近一次结算结论（阶段 4 第二步；诊断/测试用）。</summary>
    public static SettlementOutcome? LastSettlement { get; private set; }

    /// <summary>本进程里处理过多少个来自模型流水的交易标记。</summary>
    public static int IntentsHandled { get; private set; }

    /// <summary>本进程里玩家下达过多少次确认/拒绝指令。</summary>
    public static int PlayerCommandsHandled { get; private set; }

    /// <summary>
    /// 供 V 段使用的<b>账本真相注记</b>（阶段 4 第三步）：待确认预览 / 最近一笔未成立 / 最近一笔已结算
    /// / 最近几笔结算（审计回读）+ 一条账本口径，<b>按这个顺序拼成一个字符串</b>；没有内容就返回 null。
    /// <para>
    /// <b>纯读</b>：预览是只读快照，未成立/已结算来自影子存档上的 <see cref="EconomyWorld.LastTrade"/>，
    /// 审计回读只读文件。这里<b>一个字节都不写</b>。
    /// </para>
    /// <para>
    /// <b>为什么账本口径那一行每轮都写</b>：真机三轮里模型都说"钱已经转过去了"而账上一分没动，
    /// 而它的正文会被原版存进聊天历史，于是下一轮它把"我说过要给"当成"我已经给了"。
    /// 只有把口径摆在它眼前，它才有一句可依的规则；这句话没有金额，但依然留在 V 段——
    /// 它描述的就是交易状态，放进 S 段还会让"零数字 + 逐字节稳定"多担一份风险。
    /// </para>
    /// </summary>
    public static string? NoteFor(EconomyWorld? world)
    {
        if (world == null || string.IsNullOrWhiteSpace(world.WorldId)) return null;

        var blocks = new List<string>();

        // ① 待确认预览（阶段 4 第一步的通路，一字不改）。
        var preview = PendingFor(world.WorldId);
        if (preview != null && preview.OfferExists)
            blocks.Add(preview.TextWith(TryStatus(preview, world)));

        // ② 最近一笔的真相：已结算（回执）或未成立（原因 + 该要多少钱）。
        var last = world.LastTrade;
        if (last != null && last.Receipt.Length > 0)
            blocks.Add(last.Receipt);
        else if (last != null && !last.Settled)
            blocks.Add(TradeNoteText.NotSettledLine(last, NpcLabelOf(world, last)));
        else if (blocks.Count == 0)
            blocks.Add(TradeNoteText.NoRecentTradeLine);

        // ③ 审计回读（§7.4）：最近几笔结算，与 audit/<WorldId>.jsonl 逐字一致。
        string? recent = RecentSettlementsOf(world.WorldId);
        if (recent != null) blocks.Add(recent);

        // ④ 账本口径：只有"已结算"才算真的给过钱。
        blocks.Add(TradeNoteText.LedgerRuleLine);

        return string.Join(Environment.NewLine, blocks);
    }

    /// <summary>某一侧的人类可读名字；查不到就返回 id（绝不返回空，否则文案里会缺一块）。</summary>
    private static string NpcLabelOf(EconomyWorld world, LastTradeNote note)
    {
        string id = note.NpcId;
        if (id.Length == 0) id = note.Direction == TradeDirections.PlayerToNpc ? "对方" : "";
        return id.Length > 0 ? id : "对方";
    }

    /// <summary>
    /// 审计回读（§7.4）：<c>audit/&lt;WorldId&gt;.jsonl</c> 里最近几笔 <c>trade_settled</c>。
    /// <b>纯读</b>，失败一律返回 null（审计是旁路，读不到不该影响注入）。
    /// </summary>
    private static string? RecentSettlementsOf(string worldId)
    {
        try
        {
            var audit = EconomyModEntry.Runtime?.Audit;
            if (audit == null) return null;
            return TradeNoteText.RecentSettlements(audit.Read(worldId));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>当前世界的待确认预览（测试与诊断用）。</summary>
    public static TradePreview? PendingFor(string worldId)
    {
        lock (GateLock)
        {
            return PendingByWorld.TryGetValue(worldId ?? "", out var preview) ? preview : null;
        }
    }

    /// <summary>最近一笔交易结论（只读投影，测试与诊断用）。</summary>
    public static LastTradeNote? LastNoteOf(EconomyWorld? world) =>
        world == null ? null : world.LastTrade;

    /// <summary>
    /// 记下"这笔交易未成立 / 玩家拒绝了"（阶段 4 第三步 §7.1）。
    /// <para>
    /// <see cref="HandleModelIntent"/> 的两个拒绝分支与玩家拒绝分支都调它，
    /// 于是<b>玩家与模型都能看到同一句话</b>：钱没动、原因是什么、该要多少钱。
    /// </para>
    /// <para>
    /// <b>它绝不覆盖一条"已结算"的回执</b>。理由是真机症状 3 的对偶面：已经动过的钱是终局事实，
    /// 而结算之后还会出现新的"未成立"（例如同一张单再来一次会命中幂等闸、
    /// 弹窗再点一次也会走到这里）。若让后者把回执盖掉，玩家看到的就是
    /// "刚才弹窗说成了，现在私信说没成"——两句话都出自我们，却互相打脸。
    /// </para>
    /// <para>它只改影子存档里的一个节点（随下一次落盘一起写下去），绝不自己写盘。</para>
    /// </summary>
    /// <returns>是否真的写进去了（被已结算回执挡下时返回 false，诊断用）。</returns>
    public static bool RecordNote(EconomyWorld? world, string status, string kind, string direction,
        long amountUsd, string reason, string offerId = "", string npcId = "")
    {
        if (world == null) return false;
        try
        {
            if (world.LastTrade is { } settled && settled.Settled)
                return false;

            world.LastTrade = new LastTradeNote
            {
                Day = world.LastTickedDay,
                OfferId = offerId ?? "",
                Kind = kind ?? "",
                Direction = direction ?? "",
                AmountUsd = Math.Abs(amountUsd),
                Status = status ?? TradeNoteStatuses.Refused,
                Reason = reason ?? "",
                NpcId = npcId ?? ""
            };
            return true;
        }
        catch (Exception)
        {
            // 记真相失败不该影响交易流程本身。
            return false;
        }
    }

    /// <summary>清掉某个世界的待确认预览（交易结束后调用）。</summary>
    public static void Clear(string worldId)
    {
        lock (GateLock)
        {
            PendingByWorld.Remove(worldId ?? "");
        }
    }

    /// <summary>
    /// <b>模型流水里解析出一条交易意向时调用</b>（由 <c>TradeMarkerInterceptor</c> 触发）。
    /// 步骤：解析身份 → 取/按需载入世界 → Reserve（<b>不动钱</b>）→ 只读预览 → 记下。
    /// <para>任何异常都只写日志，绝不抛回原版流式解析。</p>
    /// </summary>
    public static void HandleModelIntent(TradeIntent? intent, IEconomyLog? log = null)
    {
        if (intent == null || intent.ParseFailed) return;
        try
        {
            var runtime = EconomyModEntry.Runtime;
            if (runtime == null) return;

            var identity = PrivateIdentitySlot.Peek();
            if (identity == null)
            {
                (log ?? runtime.Log).Warn("trade",
                    "[Economy-Preview] 收到交易标记，但此刻没有可用身份（没带世界/人物），已忽略。");
                return;
            }

            var world = ResolveWorld(runtime, identity);
            if (world == null)
            {
                (log ?? runtime.Log).Warn("trade",
                    $"[Economy-Preview] 世界 <{identity.WorldId}> 尚未载入且无法按需载入，已忽略本次交易标记。");
                return;
            }

            var logger = log ?? runtime.Log;

            // ① 回声判定：模型可能只是在【引用玩家刚打的字】，那不能算它提出了交易、
            //    更不能算它同意（真机实测：NPC 原话"这行字你从哪抄来的"）。
            if (TradeIntentCompleter.LooksLikePlayerEcho(intent, PlayerTextsFor(world.WorldId)))
            {
                logger.Info("trade",
                    "[Economy-Preview] 这个交易标记与玩家本轮说的话逐字相同（模型只是在引用），" +
                    "已按「NPC 并未提议/同意」处理，不落单。");
                return;
            }

            // ② 意图补全：协议层不猜方向与双方，这里按「标记显式 > 配置 kindDirections」补，
            //    补不出来就拒绝并点名（见 TradeIntentCompleter 的说明）。
            var (completed, reason) = TradeIntentCompleter.Complete(intent, identity.PersonId,
                EconomyTier.PlayerId, runtime.Config.Economy.Trade);
            if (completed == null)
            {
                logger.Info("trade", $"[Economy-Preview] 交易标记无法补全：{reason}｜{intent.Kind} " +
                                     $"{MoneyUnits.Format(intent.AmountUsd)}");
                // ★ 阶段 4 第三步 §7.1：补全失败也要落成"未成立"，否则模型下一轮会宣称已经给了钱，
                //   而玩家这边一声不吭（真机三轮就是这么来的）。
                RecordNote(world, TradeNoteStatuses.Incomplete, intent.Kind, intent.Direction,
                    intent.AmountUsd, reason ?? "交易标记的关键信息不全", npcId: identity.PersonId);
                runtime.SaveQuietly(world, "记录未成立（补全失败）");
                return;
            }

            var ctx = BuildContext(runtime, world, identity, completed);
            if (ctx == null)
            {
                logger.Warn("trade", "[Economy-Preview] 组装交易上下文失败（人物/钱包缺失），已忽略。");
                RecordNote(world, TradeNoteStatuses.Refused, completed.Kind, completed.Direction,
                    completed.AmountUsd, "组装交易上下文失败（人物或钱包缺失）", npcId: identity.PersonId);
                runtime.SaveQuietly(world, "记录未成立（上下文失败）");
                return;
            }

            var engine = EngineOf(runtime);

            // ③ Reserve：校验通过才落 pending 单；它按既有测试与注释【绝不动余额】。
            var reserve = engine.Submit(ctx, completed);
            if (!reserve.Ok || reserve.OfferId == null)
            {
                string code = reserve.SoftDeclined ? "软失败" : reserve.Check.Code;
                logger.Info("trade",
                    $"[Economy-Preview] 交易标记未落单（{code}）：" +
                    $"{reserve.Check.Message}｜{completed.Kind} {MoneyUnits.Format(completed.AmountUsd)}" +
                    $"｜方向 {completed.Direction}（{completed.InitiatorId} → {completed.CounterpartyId}）");
                // ★ 同上：被拒也必须让玩家与模型看见原因（含该要多少钱这类可操作信息）。
                RecordNote(world, TradeNoteStatuses.Refused, completed.Kind, completed.Direction,
                    completed.AmountUsd, $"{code}：{reserve.Check.Message}",
                    npcId: identity.PersonId);

                runtime.SaveQuietly(world, "记录未成立（未落单）");

                return;
            }

            var offer = world.Offers[reserve.OfferId];

            // ④ 只读预览（不写任何东西）。
            var preview = TradePreviewBuilder.ForOffer(ctx, offer, engine);
            IntentsHandled++;

            // ⑤ NPC 那一票：模型在自己的回复里明确提出这笔交易 ⇒ 视为它同意了这些条款。
            //    （回声已在 ① 被拦掉，所以这里不会把"引用玩家的话"误当成同意。）
            var status = TradeConfirmationGate.Confirm(ctx, offer.Id, TradeConfirmationSide.Npc,
                $"模型在第 {identity.Day} 天的回复里提出（方向来自 {completed.Direction}）");

            lock (GateLock) PendingByWorld[world.WorldId] = preview;
            LastPreview = preview;
            LastConfirmation = status;

            // ★ 阶段 4 第二步：待确认单【强制落盘】。
            //   真机实测教训（2026-10-11 22:12）：单子原先只在内存里，玩家重启后拿着旧单号来确认，
            //   只会得到一句"找不到交易单"，而模型那边还留着"我给过你钱"的历史——
            //   两边对不上。落盘之后单子跨重启仍然在，`Offers` 不再是空的。
            runtime.SaveQuietly(world, $"落单 {offer.Id}");

            logger.Info("trade",
                $"[Economy-Preview] 交易单 {offer.Id}｜{preview.Title}｜{MoneyUnits.Format(preview.AmountUsd)}｜" +
                $"校验 {preview.CheckSummary}｜覆盖 {preview.WouldSucceed}｜NPC 票 {(status.NpcAccepted ? "已投" : "未投")}" +
                $"｜玩家票 {(status.PlayerConfirmed ? "已投" : "未投")}｜★只读预览，未动任何余额；" +
                "结算见阶段 4 第二步");
            foreach (string line in preview.ReadOnlyText.Split('\n'))
                logger.Info("trade", "[Economy-Preview] " + line.TrimEnd('\r'));
            logger.Info("trade", "[Economy-Preview] " + status.DetailText);
            logger.Info("trade",
                $"[Economy-Preview] 玩家如何确认：在私信里回一句「确认 {offer.Id}」（或「拒绝 {offer.Id}」），" +
                $"或直接在游戏弹窗上点确认。{ConfirmationEffectLine}");

            // ⑥ 双人确认界面：NPC 已经点头、还差玩家这一票时，用游戏自带弹窗问玩家。
            //    弹不出来（非主线程/模态层未就绪/已有弹窗…）也没关系——聊天指令那条路照样能用。
            if (!status.PlayerConfirmed && status.NpcAccepted && !status.ReadyToSettle)
                TradeConfirmDialog.TryAsk(world.WorldId, offer.Id, preview, logger);
        }
        catch (Exception e)
        {
            try
            {
                (log ?? EconomyModEntry.Runtime?.Log)?.Error("trade",
                    "[Economy-Preview] 生成只读预览时抛异常（已忽略，不影响游戏）：" + e.GetType().Name +
                    "：" + e.Message, e);
            }
            catch (Exception)
            {
                // 日志都失败就只能放弃。
            }
        }
    }

    /// <summary>
    /// <b>阶段 4 B 项（形态 b）：玩家发起交易</b> —— 玩家在私信里打 <c>[Trade: …]</c>。
    /// <para>
    /// 与 <see cref="HandleModelIntent"/> 的<b>唯一差别</b>：落单之后
    /// <b>不投 NPC 那一票</b>。NPC 的票要等模型在回复里写 <c>[Deal: accept]</c>
    /// （见 <see cref="HandleDealAccepted"/>），这样 NPC 才有"拒绝玩家的提议"的能力 ——
    /// 否则玩家一开口 NPC 就必须答应。
    /// </para>
    /// <para>
    /// <b>为什么它不违反回声保护</b>：回声保护拦的是"模型复读玩家打的标记、被误当成 NPC 提议"。
    /// 玩家自己打的标记本来就**应该**被当成玩家提议，走的是这条路，与模型那条互不干扰。
    /// </para>
    /// <para>仍然不动钱：落单只是 Reserve。</para>
    /// </summary>
    /// <returns>本次是否落下了一张单（供日志与测试判断）。</returns>
    public static bool HandlePlayerIntent(string rawText, IEconomyLog? log = null)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return false;
        try
        {
            var runtime = EconomyModEntry.Runtime;
            if (runtime == null) return false;

            // 只认完整、可解析的标记；残缺/畸形的原样留给原版显示（绝不猜）。
            var intents = TradeProtocol.ParseAll(rawText);
            if (intents.Count == 0) return false;

            var identity = PrivateIdentitySlot.Peek();
            if (identity == null)
            {
                (log ?? runtime.Log).Warn("trade",
                    "[Economy-PlayerTrade] 玩家打了交易标记，但此刻没有可用身份，已忽略。");
                return false;
            }

            var world = ResolveWorld(runtime, identity);
            if (world == null) return false;

            var logger = log ?? runtime.Log;
            bool any = false;

            foreach (var intent in intents)
            {
                if (intent.ParseFailed)
                {
                    logger.Warn("trade",
                        $"[Economy-PlayerTrade] 玩家标记解析失败：{intent.ParseError}｜原文 {intent.RawText}");
                    continue;
                }

                // 意图补全：与模型那条完全同一套（标记显式 > 配置 kindDirections，都不行就拒绝）。
                var (completed, reason) = TradeIntentCompleter.Complete(intent, identity.PersonId,
                    EconomyTier.PlayerId, runtime.Config.Economy.Trade);
                if (completed == null)
                {
                    logger.Info("trade",
                        $"[Economy-PlayerTrade] 玩家标记无法补全：{reason}｜{intent.Kind} " +
                        $"{MoneyUnits.Format(intent.AmountUsd)}");
                    RecordNote(world, TradeNoteStatuses.Incomplete, intent.Kind, intent.Direction,
                        intent.AmountUsd, reason ?? "交易标记的关键信息不全", npcId: identity.PersonId);
                    runtime.SaveQuietly(world, "记录未成立（玩家标记补全失败）");
                    continue;
                }

                var ctx = BuildContext(runtime, world, identity, completed);
                if (ctx == null) continue;

                var engine = EngineOf(runtime);
                var reserve = engine.Submit(ctx, completed);
                if (!reserve.Ok || reserve.OfferId == null)
                {
                    string code = reserve.SoftDeclined ? "软失败" : reserve.Check.Code;
                    logger.Info("trade",
                        $"[Economy-PlayerTrade] 玩家标记未落单（{code}）：{reserve.Check.Message}｜" +
                        $"{completed.Kind} {MoneyUnits.Format(completed.AmountUsd)}");
                    RecordNote(world, TradeNoteStatuses.Refused, completed.Kind, completed.Direction,
                        completed.AmountUsd, $"{code}：{reserve.Check.Message}", npcId: identity.PersonId);
                    runtime.SaveQuietly(world, "记录未成立（玩家标记未落单）");
                    continue;
                }

                var offer = world.Offers[reserve.OfferId];
                var preview = TradePreviewBuilder.ForOffer(ctx, offer, engine);

                // ★ 这里是形态 b 的全部要害：**不投 NPC 那一票**。
                //   单子停在 pending，等 HandleDealAccepted 收到模型的 accept 才补上。
                lock (GateLock) PendingByWorld[world.WorldId] = preview;
                LastPreview = preview;
                LastConfirmation = TradeConfirmationGate.Status(ctx, offer.Id);
                any = true;

                runtime.SaveQuietly(world, $"玩家落单 {offer.Id}");
                logger.Info("trade",
                    $"[Economy-PlayerTrade] 玩家提出的交易单 {offer.Id}｜{preview.Title}｜" +
                    $"{MoneyUnits.Format(preview.AmountUsd)}｜校验 {preview.CheckSummary}｜" +
                    "★只读预览、未动任何余额；NPC 那一票要等模型回 [Deal: accept]");
            }

            return any;
        }
        catch (Exception e)
        {
            try
            {
                (log ?? EconomyModEntry.Runtime?.Log)?.Error("trade",
                    "[Economy-PlayerTrade] 处理玩家交易标记时抛异常（已忽略）：" +
                    e.GetType().Name + "：" + e.Message, e);
            }
            catch (Exception)
            {
                // 日志都失败就只能放弃。
            }
            return false;
        }
    }

    /// <summary>
    /// <b>模型回了 <c>[Deal: accept]</c></b>：把 NPC 那一票投到本世界最近一张待确认单上。
    /// <para>
    /// <b>只认最近一张</b>：私信里玩家通常一次只谈一笔；多张 pending 时按单号猜是错的
    /// （猜错就等于替 NPC 同意了另一笔）。宁可拒绝也不猜，日志里写清楚。
    /// </para>
    /// <para>仍然不动钱 —— 投完票若双方到齐，走的是既有的结算四闸。</para>
    /// </summary>
    /// <returns>本次是否投上了 NPC 那一票。</returns>
    public static bool HandleDealAccepted(IEconomyLog? log = null)
    {
        try
        {
            var runtime = EconomyModEntry.Runtime;
            if (runtime == null) return false;

            var identity = PrivateIdentitySlot.Peek();
            if (identity == null) return false;

            var world = ResolveWorld(runtime, identity);
            if (world == null) return false;

            var logger = log ?? runtime.Log;

            // 最近一张由玩家发起、且仍待确认的单。
            // 判据刻意窄：只认 pending，且**发起方是玩家**（模型自己提议的那条
            // 在 HandleModelIntent 里就已经投过 NPC 票了，不需要 accept）。
            TradeOffer? target = null;
            foreach (var offer in world.Offers.Values)
            {
                if (!TradeOfferStates.IsPending(offer.State)) continue;
                if (!string.Equals(offer.Direction, TradeDirections.PlayerToNpc, StringComparison.Ordinal))
                    continue;
                if (target == null || offer.CreatedDay >= target.CreatedDay)
                    target = offer;
            }

            if (target == null)
            {
                logger.Info("trade",
                    "[Economy-PlayerTrade] 模型回了 [Deal: accept]，但本世界没有「玩家发起且仍待确认」的单，已忽略。");
                return false;
            }

            var ctx = BuildContext(runtime, world, identity, intent: null);
            if (ctx == null) return false;

            var status = TradeConfirmationGate.Confirm(ctx, target.Id, TradeConfirmationSide.Npc,
                "模型回了 [Deal: accept]");
            LastConfirmation = status;
            logger.Info("trade",
                $"[Economy-PlayerTrade] 模型同意玩家提出的交易单 {target.Id}｜{status.DetailText}");

            if (status.ReadyToSettle)
            {
                logger.Info("trade", SettlingLine(target.Id));
                TrySettle(runtime, world, target.Id, "模型 accept", logger);
            }

            return status.NpcAccepted;
        }
        catch (Exception e)
        {
            try
            {
                (log ?? EconomyModEntry.Runtime?.Log)?.Error("trade",
                    "[Economy-PlayerTrade] 处理 [Deal: accept] 时抛异常（已忽略）：" +
                    e.GetType().Name + "：" + e.Message, e);
            }
            catch (Exception)
            {
            }
            return false;
        }
    }

    /// <summary>
    /// <b>玩家侧确认指令</b>：从玩家发出去的消息里找「确认 &lt;单号&gt;」/「拒绝 &lt;单号&gt;」。
    /// 纯文本匹配、不依赖模型；命中就投玩家那一票（<b>不动钱</b>）。
    /// </summary>
    /// <returns>本次处理了几个指令。</returns>
    public static int HandlePlayerCommands(PrivateIdentity identity, IEnumerable<string> playerTexts,
        IEconomyLog? log = null)
    {
        try
        {
            var runtime = EconomyModEntry.Runtime;
            if (runtime == null || identity == null) return 0;

            var world = ResolveWorld(runtime, identity);
            if (world == null) return 0;

            var logger = log ?? runtime.Log;
            int handled = 0;
            foreach (string text in playerTexts)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                var parsed = ParseCommand(text);
                if (parsed == null) continue;

                string offerId = parsed.Value.OfferId;
                if (offerId.Length == 0)
                {
                    // 没写单号：用本世界当前待确认的那张单，玩家少打几个字。
                    var pending = PendingFor(world.WorldId);
                    if (pending == null || !pending.OfferExists) continue;
                    offerId = pending.OfferId;
                }

                var ctx = BuildContext(runtime, world, identity, intent: null);
                if (ctx == null) continue;

                var status = parsed.Value.Confirm
                    ? TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, "私信指令")
                    : TradeConfirmationGate.Decline(ctx, offerId, TradeConfirmationSide.Player, "私信指令");

                PlayerCommandsHandled++;
                handled++;
                LastConfirmation = status;

                logger.Info("trade", $"[Economy-Confirm] 玩家指令「{text.Trim()}」→ 交易单 {offerId}｜{status.DetailText}");
                if (status.ReadyToSettle)
                    logger.Info("trade", SettlingLine(offerId));

                // 与弹窗那条路对称：聊天指令确认到齐也要触发结算（开关没开时是空操作）。
                if (parsed.Value.Confirm && status.ReadyToSettle)
                    TrySettle(runtime, world, offerId, "私信指令", logger);

                if (parsed.Value.Confirm)
                {
                    // ★★ 2026-10-12 01:31 真机暴露的第二个落盘漏洞（第一次修在 TradeSettlement，
                    //    但那条路要求 ReadyToSettle；**重复确认一张已结算的单根本走不到 TrySettle**）。
                    //    真机现象：日志明写"交易单 o0001 已是 confirmed 状态，确认被拒绝"，
                    //    而存档里连 LastTrade 这个键都没有 ⇒ 重启后看不到"已结算"回执（§7.4 落不了地）。
                    //    这里补上"玩家对一张已终结的单表态"这条最容易被忽略的出口。
                    NoteClosedTrade(world, offerId, runtime, logger);
                }
                else
                {
                    // ★ 与弹窗那条路对称：玩家在私信里拒绝也要落成"未成立"（§7.1）。
                    RecordNote(world, TradeNoteStatuses.Declined, KindOf(world, offerId),
                        DirectionOf(world, offerId), AmountOf(world, offerId), "你在私信里拒绝了这一笔",
                        offerId, NpcOf(world, offerId));
                    runtime.SaveQuietly(world, $"记录未成立（私信拒绝 {offerId}）");
                    Clear(world.WorldId);
                }
            }

            if (handled == 0) ReportUnparsed(playerTexts, logger);
            return handled;
        }
        catch (Exception e)
        {
            try
            {
                (log ?? EconomyModEntry.Runtime?.Log)?.Error("trade",
                    "[Economy-Confirm] 处理玩家确认指令时抛异常（已忽略）：" + e.GetType().Name, e);
            }
            catch (Exception)
            {
                // 同上。
            }
            return 0;
        }
    }

    /// <summary>
    /// 一条指令都没解析出来时的可见性兜底（<b>2026-10-11 22:12 真机事故</b>）：
    /// 玩家打了「确认 o0001」，而模组一声不吭 —— 既没有日志也没有反馈，
    /// 只能靠翻源码猜"到底是没听懂，还是玩家没说话"。
    /// <para>
    /// 现在只要玩家文本里出现指令字样却没解析成，就写一行<b>警告</b>（含原文开头）；
    /// 确实没有指令时才写 Debug。这一行就是下次真机能一眼定位的那条证据。
    /// </para>
    /// </summary>
    private static void ReportUnparsed(IEnumerable<string> playerTexts, IEconomyLog? logger)
    {
        if (logger == null) return;
        try
        {
            var texts = playerTexts.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
            foreach (string text in texts)
            {
                if (!PlayerTradeCommand.LooksLikeCommandAttempt(text)) continue;
                logger.Warn("trade",
                    "[Economy-Confirm] 玩家文本里出现确认/拒绝字样，但没能解析成指令" +
                    $"（原版可能包了壳）｜原文 <{Head(text)}>");
                return;
            }

            if (texts.Length > 0)
                logger.Debug("trade", $"[Economy-Confirm] 本轮玩家文本 {texts.Length} 条，未含确认/拒绝指令。");
        }
        catch (Exception)
        {
            // 诊断失败不影响任何流程。
        }
    }

    /// <summary>日志里的一行摘要：换行折成字面量，太长的截断。</summary>
    private static string Head(string text)
    {
        string flat = (text ?? "").Replace("\r", "").Replace("\n", "\\n").Trim();
        return flat.Length > 60 ? flat[..60] + "…" : flat;
    }

    /// <summary>
    /// <b>投玩家那一票</b>（界面/聊天指令两条路都走这里）。
    /// 只登记幂等收据 + 写日志，<b>不动钱</b>。
    /// </summary>
    public static TradeConfirmationStatus? ApplyPlayerAnswer(string worldId, string offerId, bool confirm,
        IEconomyLog? log = null, string source = "界面")
    {
        try
        {
            var runtime = EconomyModEntry.Runtime;
            if (runtime == null) return null;

            var world = runtime.CachedWorld(worldId);
            if (world == null) return null;

            var logger = log ?? runtime.Log;

            // ★ 这里刻意【不依赖私信身份槽】（真机事故 2026-10-11 21:36）：
            //   弹窗是 21:30:55 弹的，玩家 21:36:08 才点——隔了 5 分 13 秒，
            //   而身份槽保鲜期是 5 分钟，于是那一票被"身份已过期"挡掉、交易卡在未就绪。
            //   但登记一票其实只需要"哪个世界、哪张单"：玩家 id 是常量，开票日期在单子上，
            //   天数用影子存档自己的水位线即可。人的思考时间不该被 TTL 限制。
            var ctx = BuildContextForVote(runtime, world, offerId);

            var status = confirm
                ? TradeConfirmationGate.Confirm(ctx, offerId, TradeConfirmationSide.Player, source)
                : TradeConfirmationGate.Decline(ctx, offerId, TradeConfirmationSide.Player, source);

            LastConfirmation = status;
            logger.Info("trade", $"[Economy-Confirm] 来源 {source}｜交易单 {offerId}｜{status.DetailText}");
            if (status.ReadyToSettle)
                logger.Info("trade", SettlingLine(offerId));
            if (!confirm)
            {
                // ★ 阶段 4 第三步 §7.1：玩家自己拒绝了也要落成"未成立"，
                //   否则 NPC 那边只记得自己说过"行吧转你了"，下一轮还会重复报价。
                RecordNote(world, TradeNoteStatuses.Declined, KindOf(world, offerId),
                    DirectionOf(world, offerId), AmountOf(world, offerId), "你在界面/私信里拒绝了这一笔",
                    offerId, NpcOf(world, offerId));
                runtime.SaveQuietly(world, $"记录未成立（玩家拒绝 {offerId}）");
                Clear(worldId);
            }
            else if (!status.ReadyToSettle)
            {
                // ★★ 与私信指令那条路完全对称（2026-10-12 01:31 真机踩到的坑）：
                //    在弹窗上对一张【已经终结】的单再点一次确认时 ReadyToSettle 也是 false，
                //    于是 TrySettle 不会被调用、补在 TradeSettlement 里的落盘永远轮不到。
                //    这里把"这一笔已经结算过"的结论补进存档，保证重启后私信里仍能看到回执。
                NoteClosedTrade(world, offerId, runtime, logger);
            }

            // ★ 阶段 4 第二步：确认到齐之后立刻尝试结算（开关没开时这一步是空操作，
            //   行为与第一步完全一致）。失败/未接线都只写日志，绝不让按钮回调抛异常。
            if (confirm && status.ReadyToSettle) TrySettle(runtime, world, offerId, source, logger);

            return status;
        }
        catch (Exception e)
        {
            try
            {
                (log ?? EconomyModEntry.Runtime?.Log)?.Error("trade",
                    "[Economy-Confirm] 登记玩家那一票时抛异常（已忽略）：" + e.GetType().Name, e);
            }
            catch (Exception)
            {
                // 日志失败也只能放弃。
            }
            return null;
        }
    }

    /// <summary>
    /// 为"登记一票"构造上下文：<b>不需要私信身份槽</b>。
    /// <para>
    /// 世界来自运行时缓存，天/赛季取影子存档自己的水位线（过期判定用的就是它），
    /// 钱包只是 <see cref="TradeContext"/> 的必填项（确认闸门不读钱），
    /// 所以它一律是<b>只读快照</b>——不碰能动的钱包，也就不可能动钱。
    /// </para>
    /// <para>
    /// <b>2026-10-12：优先用影子余额（<see cref="WalletFor"/>）而不是预览里那个快照。</b>
    /// 预览快照只是"登记一票"的兜底（私信身份槽过期时拿不到 <c>CareerData</c>）；
    /// 影子余额才是真值，而且三条路（预览/投票/结算）看同一个来源才不会互相打脸。
    /// </para>
    /// </summary>
    private static TradeContext BuildContextForVote(ShadowEconomyRuntime runtime, EconomyWorld world,
        string offerId)
    {
        var preview = PendingFor(world.WorldId);
        long previewBalance = preview != null && preview.OfferId == offerId
            ? preview.Player.BalanceBeforeUsd
            : 0;

        // ★ 三条路（预览 / 投票 / 结算）必须看同一个余额来源，否则它们会互相打脸。
        //   这里仍然一定是只读的：WalletFor 在已导入时返回 ReadOnlySnapshot。
        IPlayerWallet wallet = world.Player.Imported
            ? WalletFor(world, data: null)
            : new InMemoryPlayerWallet(previewBalance);

        return MakeContext(runtime, world, wallet);
    }

    /// <summary>
    /// "玩家确认之后会发生什么"这一句，<b>按动钱开关分叉</b>。
    /// <para>
    /// <b>为什么必须分叉（2026-10-12 真机验收②的教训）</b>：这类句子原先无条件拼上
    /// <see cref="TradeConfirmationGate.SettlementIsNextPhase"/>（"…尚未接线…一分钱不动"），
    /// 而开关打开时那句是<b>纯粹的错误信息</b>。
    /// </para>
    /// <para>
    /// <b>这一处是第三处，前两处（确认时的两条路）在同一天早些时候已修</b> ——
    /// 也就是说"同一句假话"在同一个文件里打了三个地方。教训：改措辞要<b>全工程搜同一个常量</b>
    /// （`grep SettlementIsNextPhase`），而不是"改我看到的那一处"。
    /// 搜完的结论：全工程 4 处引用中，<c>TradeSettlement</c> 与 <c>TradeConfirmDialog</c>
    /// 那两处都在 <c>if (!SettlementWired)</c> 分支里（诚实），只有日志这处是假的。
    /// </para>
    /// </summary>
    private static string ConfirmationEffectLine => TradeConfirmationGate.SettlementWired
        ? "（结算已接线：双方确认到齐后会立即真的结算——扣款/加款并写审计。）"
        : TradeConfirmationGate.SettlementIsNextPhase;

    /// <summary>
    /// "双方已确认"那一行日志的措辞，<b>按动钱开关分叉</b>。
    /// <para>
    /// <b>为什么不能一律带上那句"尚未接线"（2026-10-12 真机验收①挖出的噪音）</b>：
    /// 那两处日志原先是无条件拼上 <see cref="TradeConfirmationGate.SettlementIsNextPhase"/>
    /// （"…尚未接线…本阶段到「双方已确认，待结算」为止"）的，而开关打开时
    /// <b>紧接着</b>结算就真的发生了。真机日志于是长这样：
    /// </para>
    /// <code>13:20:00.052 交易单 o0003 双方已确认，待结算。…尚未接线…一分钱不动。
    /// 13:20:00.060 [Economy-Settle] 交易单 o0003｜Applied｜雇佣 $3,000｜…</code>
    /// <para>
    /// 同一瞬间说了两句互相打脸的话。看日志的人（和下一个接手的人）会先信哪一句？
    /// 开关打开时那句"尚未接线"是<b>纯粹的错误信息</b>，必须去掉。
    /// </para>
    /// </summary>
    private static string SettlingLine(string offerId) =>
        TradeConfirmationGate.SettlementWired
            ? $"[Economy-Confirm] 交易单 {offerId} 双方已确认，立即结算（trade.settlementWired = true）。"
            : $"[Economy-Confirm] 交易单 {offerId} 双方已确认，待结算。" +
              TradeConfirmationGate.SettlementIsNextPhase;

    /// <summary>
    /// <b>预览/投票该用哪个钱包</b>：能读影子余额就一律用影子余额的只读快照。
    /// <para>
    /// <b>为什么（2026-10-12 真机验收①挖出的 bug）</b>：弹窗原先显示的是
    /// <c>PlayerWallet</c> 读出来的<b>原版 <c>CareerData.Credits</c></b>，而结算扣的是
    /// <see cref="ShadowPlayerWallet"/> 里的<b>影子余额</b>。这两个数字从第二步
    /// "一次性只读导入"之后就按甲方拍板的 1(a) <b>合法地分叉</b>了；
    /// 而玩家花钱只从影子扣、原版那两个字段我们<b>只读不写</b>，
    /// 于是弹窗上那个数字会<b>永远停在导入那一刻</b> ——
    /// 真机实测偏差 $1,200（弹窗 $196,820 vs 实际扣款起点 $198,020）。
    /// </para>
    /// <para>
    /// 它还是 <c>BalanceValidator</c> 判断"玩家的钱够不够"的依据，
    /// 所以这个分歧不只是显示问题：预览说"够"、结算可能说"不够"（反过来也一样）。
    /// 换成一个来源之后，两者不可能再分叉。
    /// </para>
    /// <para>
    /// <b>它一定是只读的</b>：用 <see cref="ShadowPlayerWallet.ReadOnlySnapshot"/>，拿到的
    /// 是 <c>InMemoryPlayerWallet</c> —— 于是"预览/投票不可能动钱"是
    /// <b>类型层面</b>的保证，不靠"我记得没调 TryAdd"。
    /// </para>
    /// <para>
    /// <b>尚未导入时回退到原版数字</b>：这不是"用错钱包"，而是当时唯一有意义的数字
    /// （影子是 0，显示 0 会让预览说"你一定付不起"）。正常流程下
    /// <c>WorldFor</c> 会先做导入，所以这条兜底只在一键注入等异常路径上出现。
    /// </para>
    /// </summary>
    private static IPlayerWallet WalletFor(EconomyWorld world, CareerData? data)
    {
        if (world.Player.Imported)
            return new ShadowPlayerWallet(world).ReadOnlySnapshot();

        return data != null ? new PlayerWallet(data) : new InMemoryPlayerWallet(0);
    }

    /// <summary>
    /// <b>结算用的上下文</b>：与投票那份唯一的差别是<b>钱包换成真正能动的影子钱包</b>。
    /// <para>
    /// 为什么必须分开：投票那条路刻意用只读快照（<c>InMemoryPlayerWallet</c>），于是"确认绝不动钱"
    /// 是结构上成立的；而结算必须动钱，只能用 <see cref="ShadowPlayerWallet"/>——
    /// 它自己还有"未导入就拒绝动钱"的纪律兜着。
    /// </para>
    /// </summary>
    private static TradeContext BuildContextForSettlement(ShadowEconomyRuntime runtime, EconomyWorld world) =>
        MakeContext(runtime, world, new ShadowPlayerWallet(world));

    private static TradeContext MakeContext(ShadowEconomyRuntime runtime, EconomyWorld world,
        IPlayerWallet wallet) => new()
    {
        World = world,
        Config = runtime.Config.Economy,
        Personality = runtime.Config.Personality,
        Prompt = runtime.Config.Prompt,
        Counterparty = null,
        Favour = 0,
        Day = world.LastTickedDay,
        Season = world.LastSeason,
        Wallet = wallet
    };

    /// <summary>
    /// 尝试结算（阶段 4 第二步）。<b>开关没开时直接返回 null 且一行日志都不写</b>，
    /// 保证阶段 4 第一步的行为与日志逐字不变。
    /// <para>
    /// 它只负责"组装上下文 + 落盘/审计回调 + 把结果告诉玩家"，真正的判定与动账全在
    /// <see cref="TradeSettlement.Settle"/>（零依赖、可单测）。
    /// </para>
    /// <para>
    /// <b>阶段 4 第三步 §7.2</b>：结算完当场弹一次结果框（成功/失败各一次）。
    /// 弹不出来只写日志——<b>绝不能因为弹窗失败而影响已经发生的结算</b>，
    /// 下一轮私信的 V 段里那条回执照样在。
    /// </para>
    /// </summary>
    private static SettlementOutcome? TrySettle(ShadowEconomyRuntime runtime, EconomyWorld world,
        string offerId, string source, IEconomyLog logger)
    {
        if (!TradeConfirmationGate.SettlementWired) return null;

        try
        {
            var ctx = BuildContextForSettlement(runtime, world);
            var settlement = new TradeSettlement(EngineOf(runtime), logger,
                persist: w => runtime.SaveQuietly(w, "结算"),
                audit: runtime.Audit);

            var outcome = settlement.Settle(ctx, offerId, source);
            LastSettlement = outcome;
            logger.Info("trade", $"[Economy-Settle] {offerId}｜{outcome.ReceiptText}");
            if (outcome.Settled) Clear(world.WorldId);

            // ★ 结果送到玩家眼前（V 段回执已由 world.LastTrade 承担，这里是"当场"那次）。
            string label = NpcOf(world, offerId);
            TradeConfirmDialog.TryShowResult(world.WorldId, outcome, label, logger);
            return outcome;
        }
        catch (Exception e)
        {
            // 结算在按钮回调里跑，绝不能抛出去。
            logger.Error("settle", "[Economy-Settle] 组装/调用结算时抛异常（已忽略）：" + e.GetType().Name, e);

            // ★ 必须给玩家与模型留下一条结论。
            //
            // 为什么：`TradeSettlement.Settle` 内部有自己的 try/catch 与 `Finish`，
            // 所以"钱动没动"这件事它自己会落盘。但**异常抛在 Settle 之前**（组装上下文、
            // 构造结算器…）时，Settle 根本没被调用 ⇒ 一个字都不会写。
            // 于是一笔玩家点了确认的交易在界面上**毫无痕迹**：既没有回执、也没有"没成"，
            // 而模型下一轮还会照着"玩家确认过了"往下编。
            //
            // 这里补一条【未成立】并把原因写清楚，同时**明说钱的状态无法核实**——
            // 因为我们确实不知道异常发生在动账之前还是之后。
            try
            {
                string who = NpcOf(world, offerId);
                var crashed = new LastTradeNote
                {
                    Day = world.LastTickedDay,
                    OfferId = offerId,
                    Kind = KindOf(world, offerId),
                    Direction = DirectionOf(world, offerId),
                    AmountUsd = AmountOf(world, offerId),
                    NpcId = who,
                    Status = TradeNoteStatuses.Failed,
                    Reason = $"结算过程抛出异常（{e.GetType().Name}），没有走完。" +
                             "这一笔是否动过钱无法确定，请核对审计文件。",
                    LedgerEntryId = ""
                };
                crashed.Receipt = TradeNoteText.NotSettledLine(crashed, who);
                crashed.Detail = "核对 audit/<WorldId>.jsonl 里有没有这笔单号：" +
                                 "有就是真的结算过，没有就是没结算。";
                world.LastTrade = crashed;
                runtime.SaveQuietly(world, $"记录结算异常（{offerId}）");
                TradeConfirmDialog.TryShowResult(world.WorldId,
                    new SettlementOutcome(SettlementStatus.Failed, offerId, crashed.Kind, crashed.Direction,
                        crashed.AmountUsd, who, 0, 0, 0, 0, "", "", crashed.Reason),
                    who, logger);
            }
            catch (Exception nested)
            {
                // 连记录结论都失败：那至少别把异常吞得无声无息。
                logger.Error("settle",
                    "[Economy-Settle] 记录结算异常时又抛了一次（已放弃）：" + nested.GetType().Name, nested);
            }

            return null;
        }
    }

    // ── 单子上那几个字段的只读小工具（记录"未成立"时要点名）────────────

    /// <summary>
    /// <b>玩家对一张"已经结算过"的单又表了一次态</b>时，把那条已结算的真相重新写进世界并落盘。
    /// <para>
    /// 这条出口真机踩过两次（`2026-10-12 01:19` 与 `01:31`）：日志明写
    /// `交易单 o0001 已是 confirmed 状态，确认被拒绝`，而影子存档里**连 <c>LastTrade</c> 这个键都没有**——
    /// 因为幂等命中时 <c>TradeConfirmationGate.Confirm</c> 直接返回、**根本走不到 <c>TrySettle</c>**，
    /// 于是 `TradeSettlement` 那边补的落盘永远轮不到。
    /// </para>
    /// <para>
    /// <b>它是纯记账，绝不动钱</b>：账本余额、流水、审计一概不碰，只把"这笔确实结算过"写进存档，
    /// 让玩家重启后还能在私信里看到回执（§7.4）。
    /// </para>
    /// </summary>
    private static void NoteClosedTrade(EconomyWorld world, string offerId, ShadowEconomyRuntime runtime,
        IEconomyLog logger)
    {
        try
        {
            // 只处理"这张单确实已终结"的情形：找不到单子 / 单子还没终结 ⇒ 什么都不做。
            // 这条前置很重要——ApplyPlayerAnswer 里 ReadyToSettle 为 false 也可能是
            // "还差 NPC 那一票"，那时既没有回执可补、也绝不该覆盖别的单的结论。
            if (!world.Offers.TryGetValue(offerId, out var offer)) return;
            if (!string.Equals(offer.State, TradeOfferStates.Confirmed, StringComparison.Ordinal)) return;

            // ★ 证据闸：`Confirmed` 这个状态**本身不足以证明钱动过**。
            //
            // 为什么：`TradeEngine.Confirm` 里把 State 置成 Confirmed 的地方有两处 ——
            // 一处是真正动账成功之后（TradeEngine.cs:247），另一处是**幂等命中**
            // （TradeEngine.cs:230，`ExecStatus.AlreadyApplied`）。而幂等命中只看收据本，
            // 收据是会被裁剪的（ReceiptBook 的双规则裁剪 + 账本流水本身是每 NPC 64 条的环形，
            // 见 NpcEconomy.MaxLedgerEntries）。于是一张"很久以前结算过"的单，
            // 它的流水可能早就被挤出环形，只剩下 State=Confirmed 与一个悬空的 LedgerEntryId。
            //
            // 旧写法到这里就直接按"此前结算过"重建回执了 —— 那等于**仅凭一个状态位
            // 就向玩家宣称钱动过**。这正是本项目最忌讳的形状（模型嘴上说转了 vs 账上到底动没动）。
            //
            // 现在要求"钱的去向"能对上：流水 id 必须**真的还在**至少一方的流水里。
            // 对不上就【不写"已结算"】，改写一条诚实的"无法核实"，并明确告诉玩家去哪儿查。
            if (!MoneyActuallyMoved(world, offer, out string evidence))
            {
                logger.Warn("trade",
                    $"[Economy-Confirm] 交易单 {offerId} 是 confirmed，但账上找不到对应的流水" +
                    $"（{evidence}）——不宣称「已结算」，改写「无法核实」。");
                string who = NpcOf(world, offerId);
                var unverified = new LastTradeNote
                {
                    Day = world.LastTickedDay,
                    OfferId = offerId,
                    Kind = offer.Kind,
                    Direction = offer.Direction,
                    AmountUsd = Math.Abs(offer.Amount),
                    NpcId = who,
                    Status = TradeNoteStatuses.Unverified,
                    Reason = "这张单标记为已确认，但账本里找不到对应的流水（很可能已被流水环形裁剪），" +
                             "无法核实这笔钱到底动没动。",
                    LedgerEntryId = offer.LedgerEntryId
                };
                unverified.Receipt = TradeNoteText.NotSettledLine(unverified, who);
                unverified.Detail = "请核对 audit/<WorldId>.jsonl 里有没有这笔单号：" +
                                    "有就是真的结算过，没有就是其实没结算。别只看这句话下结论。";
                world.LastTrade = unverified;
                runtime.SaveQuietly(world, $"记录无法核实（{offerId}）");
                return;
            }

            // ① 手上已经有【同一张单】的已结算结论：只补一次落盘，绝不重复记。
            if (world.LastTrade is { } note && note.Settled &&
                string.Equals(note.OfferId, offerId, StringComparison.Ordinal))
            {
                runtime.SaveQuietly(world, $"补记已结算结论的落盘（{offerId}）");
                return;
            }

            // ② 手上没有（跨重启 / 注记丢了 / 记的是别的单）：
            //    从**这张已 confirmed 的单**上重建一条最小回执 ——
            //    余额取账本现值（绝不编数字），措辞与 AlreadyApplied 那条出口完全同源。
            string npcId = NpcOf(world, offerId);
            long npcBalance = world.Npcs.TryGetValue(npcId, out var npcEconomy) ? npcEconomy.Balance : 0;
            long playerBalance = 0;
            try { playerBalance = new ShadowPlayerWallet(world).BalanceUsd; }
            catch (Exception) { /* 未导入时按 0 —— 回执里只说"没再动钱"，不给假数字 */ }

            var rebuilt = new SettlementOutcome(
                SettlementStatus.AlreadyApplied, offerId, offer.Kind, offer.Direction, offer.Amount,
                npcId, npcBalance, npcBalance, playerBalance, playerBalance,
                new EconomyLedger(world).Key("trade", offerId),
                offer.LedgerEntryId,
                "这张单已经处于 confirmed 状态（此前结算过），本次未再动钱。");

            world.LastTrade = new LastTradeNote
            {
                Day = world.LastTickedDay,
                OfferId = offerId,
                Kind = offer.Kind,
                Direction = offer.Direction,
                AmountUsd = Math.Abs(offer.Amount),
                NpcId = npcId,
                Status = TradeNoteStatuses.AlreadySettled,
                Reason = rebuilt.Detail,
                Receipt = TradeNoteText.ReceiptLine(rebuilt, npcId),
                Detail = TradeNoteText.ReceiptDetail(rebuilt, npcId),
                LedgerEntryId = offer.LedgerEntryId
            };

            runtime.SaveQuietly(world, $"补记已结算结论（{offerId}）");
            logger.Info("trade",
                $"[Economy-Confirm] 交易单 {offerId} 之前已结算，已把「已结算」结论补记进影子存档" +
                $"（重启后私信里仍能看到回执）｜{world.LastTrade.Receipt}");
        }
        catch (Exception e)
        {
            logger.Warn("trade", $"[Economy-Confirm] 补记已结算结论失败（已忽略，不影响账）：{e.GetType().Name}");
        }
    }

    /// <summary>某张单子的对手方（NPC）id；单子不在就返回空串。</summary>
    private static string NpcOf(EconomyWorld world, string offerId)
    {
        if (!world.Offers.TryGetValue(offerId ?? "", out var offer)) return "";
        bool playerFirst = string.Equals(offer.InitiatorId, EconomyTier.PlayerId, StringComparison.Ordinal);
        return playerFirst ? offer.CounterpartyId : offer.InitiatorId;
    }

    /// <summary>
    /// <b>这张单有没有"钱真的动过"的证据</b>：它的流水 id 必须真的还在某一方的流水里。
    /// <para>
    /// 为什么不能只看 <c>State == Confirmed</c>：那个状态在**幂等命中**时也会被置上
    /// （<c>TradeEngine.Confirm</c> 的 <c>AlreadyApplied</c> 分支），而流水是每 NPC 64 条的
    /// 环形（<see cref="NpcEconomy.MaxLedgerEntries"/>）、收据也会被裁剪 ——
    /// 所以一张老单完全可能只剩状态位与一个**悬空的** <c>LedgerEntryId</c>。
    /// 拿它去重建"已结算"回执，就是仅凭一个状态位向玩家宣称钱动过。
    /// </para>
    /// <para>
    /// 判据是<b>保守</b>的：找不到流水就返回 false（宁可说"无法核实"），
    /// 而且绝不抛异常 —— 它是纯读，任何意外都按"没有证据"处理。
    /// </para>
    /// </summary>
    private static bool MoneyActuallyMoved(EconomyWorld world, TradeOffer offer, out string evidence)
    {
        try
        {
            string entryId = offer.LedgerEntryId ?? "";
            if (entryId.Length == 0)
            {
                evidence = "单子上没有流水 id";
                return false;
            }

            // 两侧都可能记着这条流水：出钱方与收钱方。任一侧还在就算有证据。
            string npcId = NpcOf(world, offer.Id);
            if (npcId.Length > 0 && world.Npcs.TryGetValue(npcId, out var npc) &&
                npc.Ledger.Any(e => string.Equals(e.Id, entryId, StringComparison.Ordinal)))
            {
                evidence = $"流水 {Short(entryId)} 在 {npcId} 的流水里";
                return true;
            }

            if (world.Player.Ledger.Any(e => string.Equals(e.Id, entryId, StringComparison.Ordinal)))
            {
                evidence = $"流水 {Short(entryId)} 在玩家的流水里";
                return true;
            }

            evidence = $"流水 {Short(entryId)} 不在 {npcId} 或玩家的流水里（环形上限 " +
                       $"{NpcEconomy.MaxLedgerEntries} 条，很可能已被裁剪）";
            return false;
        }
        catch (Exception e)
        {
            evidence = "核对流水时出错：" + e.GetType().Name;
            return false;
        }
    }

    /// <summary>日志里用的短 id（长哈希只留前 8 位，与项目里其它地方的引用习惯一致）。</summary>
    private static string Short(string id) =>
        string.IsNullOrEmpty(id) ? "<无>" : (id.Length <= 8 ? id : id[..8]);

    private static string KindOf(EconomyWorld world, string offerId) =>
        world.Offers.TryGetValue(offerId ?? "", out var offer) ? offer.Kind : "";

    private static string DirectionOf(EconomyWorld world, string offerId) =>
        world.Offers.TryGetValue(offerId ?? "", out var offer) ? offer.Direction : "";

    private static long AmountOf(EconomyWorld world, string offerId) =>
        world.Offers.TryGetValue(offerId ?? "", out var offer) ? Math.Abs(offer.Amount) : 0;

    /// <summary>
    /// 解析一句玩家指令。命中返回 <c>(是否确认, 单号)</c>；不成指令返回 null。
    /// <para>接受的形式：<c>确认 o0001</c>、<c>确认</c>、<c>拒绝 o0001</c>、<c>[Deal: confirm o0001]</c>，
    /// 以及被原版包了壳的 <c>第 6 赛季第 32 天，玩家发给苹果的消息\n确认 o0001</c>。</para>
    /// <para>实现见 <see cref="PlayerTradeCommand.Parse"/>（零依赖层，进 329 那套基线）。</para>
    /// </summary>
    public static (bool Confirm, string OfferId)? ParseCommand(string? text) => PlayerTradeCommand.Parse(text);

    // ── 内部 ─────────────────────────────────────────────────────────

    private static TradeEngine EngineOf(ShadowEconomyRuntime runtime) =>
        new(runtime.Config.Economy, runtime.Config.Personality, runtime.Config.Prompt, runtime.Log);

    private static EconomyWorld? ResolveWorld(ShadowEconomyRuntime runtime, PrivateIdentity identity)
    {
        var cached = runtime.CachedWorld(identity.WorldId);
        if (cached != null) return cached;

        // 与私信注入同一条"按需载入"路径：身份槽带着权威 CareerData 时直接载入/播种。
        if (identity.Data != null &&
            string.Equals(identity.Data.WorldId, identity.WorldId, StringComparison.Ordinal))
            return runtime.WorldFor(identity.Data);

        return null;
    }

    private static TradeContext? BuildContext(ShadowEconomyRuntime runtime, EconomyWorld world,
        PrivateIdentity identity, TradeIntent? intent)
    {
        string npcId = intent != null
            ? (identity.PersonId.Length > 0 ? identity.PersonId : intent.CounterpartyId)
            : identity.PersonId;
        if (npcId.Length == 0 || !world.Npcs.TryGetValue(npcId, out var npc)) return null;

        CareerPerson? person = null;
        try
        {
            var data = identity.Data;
            if (data != null)
                person = data.People?.FirstOrDefault(p =>
                    string.Equals(p.Id, npcId, StringComparison.Ordinal));
        }
        catch (Exception)
        {
            // 拿不到原版人物只影响性格适配与好感，不影响预览的余额部分。
        }

        var facts = person != null ? NpcFactsProjection.Project(person) : null;
        int favour = 0;
        try
        {
            if (identity.Data != null)
                favour = PrivateMessages.Relation(identity.Data, npcId).Favour;
        }
        catch (Exception)
        {
            // 好感读不到按 0 处理。
        }

        // 钱包快照：只用于"预览里显示你的余额"与余额校验。
        // ★★ 2026-10-12 真机验收①挖出的 bug 的修复点（见 WalletFor 的说明）：
        //    原先这里固定用 PlayerWallet（原版 Credits），而结算用的是影子余额——
        //    两者从"一次性导入"之后就分叉了，于是弹窗给玩家看的数字与实际扣款不符。
        IPlayerWallet wallet;
        try
        {
            wallet = WalletFor(world, identity.Data);
        }
        catch (Exception)
        {
            wallet = new InMemoryPlayerWallet(0);
        }

        return new TradeContext
        {
            World = world,
            Config = runtime.Config.Economy,
            Personality = runtime.Config.Personality,
            Prompt = runtime.Config.Prompt,
            Counterparty = facts,
            Favour = favour,
            Day = identity.Day,
            Season = identity.Season,
            Wallet = wallet
        };
    }

    /// <summary>
    /// 清掉"最近一笔交易"的结论（<b>只给测试与诊断用</b>）。
    /// <para>
    /// 存在的理由：<see cref="RecordNote"/> 刻意不覆盖一条"已结算"的回执，
    /// 而测试套件里多个用例共用一个世界号，于是需要一个显式的复位入口，
    /// 让"这一条用例从零开始"是<b>写明的</b>，而不是靠重新构造一个世界。
    /// </para>
    /// </summary>
    public static void ResetNote(EconomyWorld? world)
    {
        if (world == null) return;
        world.LastTrade = null;
    }

    private static TradeConfirmationStatus? TryStatus(TradePreview preview, EconomyWorld world)
    {
        try
        {
            var runtime = EconomyModEntry.Runtime;
            var identity = PrivateIdentitySlot.Peek();
            if (runtime == null || identity == null) return null;
            var ctx = BuildContext(runtime, world, identity, intent: null);
            return ctx == null ? null : TradeConfirmationGate.Status(ctx, preview.OfferId);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
