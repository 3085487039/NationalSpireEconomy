# NationalSpireEconomy 阶段 4 交接文档（第一步：只读成交预览 + 双人确认）

> **本文用途**：让新会话接手**阶段 4（交易执行）**。第一步已落地并打包，**等甲方部署验证**。
> **前置**：阶段 3 已验收通过（落点 / 按需载入 / 收据裁剪三个真机缺陷全部闭环，见
> `docs/HANDOFF-PHASE-3-PROBE.md` 顶部的 19:5x / 20:1x / 20:2x 三节）。
> **铁律不变**：绝不改原版；影子存档按 WorldId 分片；MoneyUnits 是唯一换算出口；幂等一律走 `EconomyLedger`。

---

## 0. 一句话现状

**第一步只做"说清楚会发生什么 + 双方各投一票"，一个字节的余额都没动。**
真正扣款/加款的**结算**在第二步，`TradeConfirmationGate.SettlementWired` 恒为 `false`，并有测试钉着。

---

## 1. 这一步做了什么（4 个新文件 + 4 处接线）

| 文件 | 作用 |
| --- | --- |
| `src/Trade/TradePreview.cs` | **只读成交预览**：双方余额 before/→after、八条校验器逐条结论、幂等键、有效期、给玩家看的只读文案。契约 `MutatesLedger => false` |
| `src/Trade/TradeConfirmation.cs` | **双人确认闸门**：`confirm:{worldId}:{offerId}:npc` / `:player` 两个幂等键；`Confirm` / `Decline` / `Status`；**没有任何结算入口** |
| `src/Game/TradePreviewWiring.cs` | 游戏侧接线：模型流水里解析出交易标记 →（Reserve，不动钱）→ 只读预览 → 记进 V 段注记 / 弹窗；玩家私信指令 `确认 <单号>` 也走这里 |
| `src/Game/TradeConfirmDialog.cs` | **游戏自带官方弹窗**（`NModalContainer` + `NGenericPopup`）把预览摆给玩家，点「确认/拒绝」即投票；任何一步不满足就降级为聊天指令 |
| （改）`src/Game/TradeMarkerInterceptor.cs` | 每解析出一条交易标记就调 `TradePreviewWiring.HandleModelIntent` |
| （改）`src/Game/PrivateRequestInjectionPatch.cs` | 私信请求里解析玩家这一轮的指令（只回看最后两条 user 消息，跳过"当前日期"那条） |
| （改）`src/Inject/PromptInjectInput.cs` / `VolatileSegment.cs` | 新增 `ExtraVolatileNote`：**只读预览挂进 V 段**（它带金额，绝不能进零数字的 S 段） |
| （改）`src/Game/ShadowEconomyRuntime.cs` | `BuildInputFor` 把 `TradePreviewWiring.NoteFor(world)` 作为 V 段注记传下去 |

**两条确认通道（都调用同一个闸门）**：

1. **游戏弹窗**（首选）：NPC 点头后还没等到玩家那一票时，用游戏官方 `NGenericPopup` 弹预览；玩家点按钮即投票。
   关掉它的开关：环境变量 `NSE_CONFIRM_DIALOG=0`。
2. **聊天指令**（退路，且不依赖模型）：玩家在私信里回 `确认 <单号>` / `拒绝 <单号>`（单号可省，默认取本世界待确认单）。

**NPC 那一票怎么来**：由 NPC 自己发起的报价 = 它已经同意这些条款，直接记为 NPC 已确认；
玩家发起的报价则需要 NPC 在私信里答应（`[Deal: accept <单号>]` 这类标记由同一条流水解析路径识别，本步先留接口）。

---

## 2. UI 可行性（调研结论，决定"为什么用游戏自带弹窗"）

| 路线 | 结论 | 依据 |
| --- | --- | --- |
| **B：游戏自带弹窗**（本步采用） | ✅ 可行且最省事 | `NModalContainer.Instance.Add(popup, showBackstop)` + `NGenericPopup.Create()` 都是 `public`，游戏自己在 `NGame` 多处就是这么用的；加载的是**已预载**场景，不碰资源路径 |
| A：自绘 Godot 控件 | ✅ 可行（原版 NationalSpire 大规模实证），但代码量大 | `GameBridge.cs` 纯代码造 `Button`/`CareerScreen`；自绘确认框见 `CareerDialog.cs`（并注释明确：**不要新建独立 `Window`，会触发游戏"失焦静音"**） |
| C：用游戏私信界面 | ❌ 不存在 | `sts2.dll` 里搜 `PrivateMessage/ChatScreen/Conversation` 只命中 `PlatformBranch.PrivateBeta`；**私信窗口是 NationalSpire 自绘的**（`PrivateMessageScreen.cs`） |

**我们踩过的线程约束**（照抄游戏做法）：非主线程不能建/挂节点（游戏自己的 `AddChildSafely` 用
`NGame.IsMainThread()` 分支 + `CallDeferred`）；`NModalContainer.Instance` 在它自己的 `_Ready` 里才赋值；
已有模态时 `Add` **静默丢弃**且无返回码 ⇒ 我们弹之前先判 `OpenModal == null`。
本步的弹窗全程 `try/catch`，失败只写一行日志并退化为聊天指令。

---

## 3. 验证（甲方部署后按这个顺序看）

**部署校验（先做）**：
```
mods\NationalSpireEconomy\NationalSpireEconomy.dll
  长度  ：403968
  SHA256：8165D00CE043B2B35549A88B6507862496B94D52CA654921ECFF48AE74626BD8
mods\NationalSpireEconomy\config\economy_config.json      ← 新增 trade.kindDirections
  长度  ：5892
  SHA256：8C35A50CB144D1F11FD192FC0BD6B52052AD22E3B2DF0BAD3EBE5F8176E78259
mods\NationalSpireEconomy\config\ai_prompt_economy.json   ← 交易协议说明（教模型发 [Trade:]，含"先问清数额"分支）
  长度  ：7420
  SHA256：F42C13C3F27F1C9483080994157356FC35422A89BC19A7AE78EBE6F09C6AAD1B
```

**操作**：进生涯，跟一个 NPC 聊到它提出一笔交易（模型输出 `[Trade: …]` 标记）。

**`mod-debug.log` 里搜**：

| 搜索词 | 期望 | 含义 |
| --- | --- | --- |
| `[Economy-Preview] 交易单` | 出现，带 `★只读预览，未动任何余额` | 预览生成、待确认单落地 |
| `成交预览·只读` | 后续若干行（金额 / 双方余额 → / 校验 8/8 / 幂等键） | 预览明细（同一份文案也会出现在游戏里） |
| `NSE_CONFIRM_DIALOG` | 不出现（除非你手动关过） | 弹窗开关状态 |
| `[Economy-UI] 已弹出` | 出现 ⇒ 弹窗路径通 | 游戏里应看到确认框；点「确认」后看下一行 |
| `[Economy-UI]` + `NModalContainer`/`非主线程`/`已有别的模态` | 出现 ⇒ 弹窗这次没弹 | **不算失败**：改用聊天指令（下面一条），并把这一行发我 |
| `[Economy-Confirm] 来源` | `确认弹窗·确认` 或 `私信指令` | 玩家那一票的来源 |
| `【双人确认】` | `NPC 已确认｜玩家 已确认｜✅ 双方已确认，待结算` | ★本步的终局状态 |
| `阶段 4 第二步` | 与上一行同时出现 | 明确说明**还没动钱** |

**钱不动的判据**（可独立核对）：
1. `mod-debug.log` 里**不该**出现 `[Economy-Tick]` 之外任何"余额变动"行，也**不该**出现 `流水`/`已执行` 字样；
2. `worlds\<WorldId>.json` 里 NPC 的 `balance` 与 `ledger` 条数在确认前后**一字不变**（收据 `receipts` 会多两条，那是幂等记录、不是钱）；
3. 想验证幂等：同样的指令/按钮再来一次，`收据` 条数不变，日志出现「重复确认，已按幂等忽略」。

**聊天指令退路**（弹窗不可用时的等价验证）：在私信里对那个 NPC 回一句
`确认 <单号>`（单号见 `[Economy-Preview] 交易单 xxx`），期望同样的 `【双人确认】…✅ 双方已确认，待结算`。

---

## 4. 本轮实测数字

```
零依赖测试（默认）：                    总计 311：通过 311，失败 0
含适配层+补丁层（GameRefsEnabled=true）：总计 424：通过 424，失败 0
Release 构建（两种 GameRefsEnabled）：   0 个警告 0 个错误
```

新增用例（都是"不动钱"的钉子）：
- `TradePreviewTests`：`PreviewIsReadOnly`（前后逐字段一致）、
  **`PreviewDeltasMatchRealExecution`（预览的 delta 必须等于真跑一遍的余额差）**、
  `PreviewReportsBlockingValidator`、`PreviewTextCarriesNumbersAndIsNotStableSegment`；
- `TradeConfirmationTests`：`ConfirmIsIdempotentPerSide`、`BothSidesReadyToSettleWithoutMovingMoney`、
  `PlayerConfirmDoesNotCountAsNpc`、`ConfirmRefusesExpiredOffer`、`DeclineStopsTheDeal`、
  `SettlementIsDeliberatelyNotWired`；
- `PlanAInjectionTests`：`ModelIntentBecomesReadOnlyPreview`、
  `PlayerConfirmCommandIsHonouredWithoutMovingMoney`。

---

## 5. 第一轮真机反馈（2026-10-11 20:41 那一轮）暴露并已修的两件事

**现象**：模型确实发出了交易标记，但玩家侧看到的是"什么都没发生"：

```
[economy.trade] 识别出交易标记（第 1 条）：赞助 $5,000｜理由
[economy.trade] 意图被拒（BAD_SHAPE）：未知的交易方向：。
[economy.trade] [Economy-Preview] 交易标记未落单（BAD_SHAPE）：未知的交易方向：。｜赞助 $5,000
```

### ① 缺"意图补全"：协议层不猜方向，于是永远 `BAD_SHAPE`

`TradeProtocol` 刻意与对话上下文解耦，解析出的 `Direction`/`InitiatorId`/`CounterpartyId` 都是空串，
而游戏路径上<b>没有任何一层去补它们</b> ⇒ 每一条模型提出的交易都会被 `IntentShapeValidator` 拒掉。
**已修**：新增 `src/Trade/TradeIntentCompleter.cs`，方向按
**「标记里显式 `Direction:` ＞ 配置 `trade.kindDirections` 的 Kind→方向映射」** 补全，
双方按方向补成 `(NPC, 玩家)`；**都没配到就拒绝**并点名是哪个 Kind 没配（绝不猜钱的方向）。
配置新增（`economy_config.json`）：
```json
"kindDirections": { "赞助": "npcToPlayer", "打赏": "npcToPlayer", "奖金": "npcToPlayer",
                    "雇佣": "playerToNpc", "借款": "npcToPlayer", "转账": "playerToNpc" }
```
`TradeProtocol` 也新增解析显式 `Direction:` / `方向:` 字段。

### ② ★ 回声陷阱：模型那句其实是在<b>引用玩家打的字</b>，不能算它同意

那一轮玩家自己把 `[Trade: 赞助, Amount: 5000]` 打进消息里做测试，NPC 的回复原样引用了它
（原话："这行字你从哪抄来的，我认识你两个赛季了也没见过这套路"）。
如果按原先"模型提出即 NPC 已确认"的规则，就等于
**凭空替 NPC 同意了一笔它没同意的交易**——现在只是显示错，第二步就会变成真实的错误扣款。
**已修**：新增 `TradeIntentCompleter.LooksLikePlayerEcho`——
把标记原文与**玩家本轮说过的话**各自压掉空白后比对，命中即判定"模型只是在引用"，
不落单、不投票，并写一行 `已按「NPC 并未提议/同意」处理`。
玩家本轮的话由私信请求路径写进 `TradePreviewWiring.RememberPlayerTexts`
（它天然早于模型回复的流式输出），判据是**确定性**的，不依赖模型。
测试：`TradeIntentCompleterTests.EchoOfPlayerTextIsDetected`。

> 这两条都是"跑通交易流程"这一步应当暴露出来的东西——它们不改变"不动钱"的结论：
> 修复前后都**没有任何一处调用结算**，`BalanceSnapshot` 前后一字不变。

### ③ ★ 模型从未被教过 `[Trade: …]` 协议（**纯配置修复，DLL 未变**）

**第二轮真机（20:53）**：我们按上一轮的建议跑了一次，结论是**模型这次根本没提交易**
（`mod-debug.log` 里既没有 `识别出交易标记`，也没有 `Economy-Preview`——只有正常的注入与 Feed 命中）。
顺着这条线回查配置，发现真正的堵点：

> **我们的提示词里从来没有 `[Trade: …]` 协议。** 上一轮之所以出现标记，只是模型在
> **复读玩家自己打进去的那行字**（它也当场说了"这行字你从哪抄来的"）。
> 也就是说：**在自然对话里，模型永远不会主动提出交易**——阶段 4 在真机上根本没有入口。

**已修（只改 `config/ai_prompt_economy.json`，不动 DLL）**：在稳定段收尾句里补上交易协议说明
（`stableBlockSuffix`），要点：
- 想为对方花钱时，在回复**最后单独写一行** `[Trade: 赞助, Amount: 金额, Direction: npcToPlayer]`；
- `Direction` 只允许 `npcToPlayer`（你给对方）或 `playerToNpc`（对方给你）；金额不得超过可用余额；
- 对方提出交易而它同意时，再写一行 `[Deal: accept]`；
- **零数字自查通过**（S 段纪律：`ConfigLoader` 用 `\d` 拦；所以示例金额写成「金额」而不是具体数）。

> **为什么这条修复不需要重编译**：协议的"教"与"解析"是分开的——解析器（`TradeProtocol` +
> `TradeIntentCompleter`）早已在 DLL 里，缺的只是让模型知道该发什么。这也意味着
> **甲方只需要替换这一个 JSON 文件**（DLL 哈希与上一版完全相同）。

**`[Deal: accept]` 的现状**：目前"NPC 提出即视为它同意"；玩家发起的交易等它回 `[Deal: accept]`
这条路径**已在协议说明里写出，但解析代码留到下一轮**（本轮先让 NPC 能主动发起，验证预览 + 弹窗）。

### ④ 协议确实到达了模型；它不发标记是因为"玩家没给金额"

**第三轮真机（21:22）**：配置生效 ✅（`S 段自证：长度 131 → 333`，零数字仍通过）。
但模型仍然没发 `[Trade:]`。**决定性证据在它自己的推理里**（`national-spire.log` 的
`private.response.reasoning`，这是我们最好的诊断入口）：

> 「…而且赞助要写 `[Trade: 赞助, Amount: 金额, Direction: npcToPlayer]`。
> 我应该怎么处理？玩家说"最近手头紧，能赞助我一点吗"。**金额没提**」

⇒ 协议**已经被模型读到并复述**了；它没写标记，是因为：① 玩家没给数额；② 那个 NPC 是
"尖刻挑刺者 + 好感 0"，于是它按人设先反问要多少。**这不是缺陷，是我们的协议文字没覆盖"没给金额"这一支。**

**已修（仍是纯配置）**：协议里补上分支——
「如果对方开口要钱但没说具体数额，**先问清数额与用途**；等对方说清之后，你愿意就按那个数额写标记给钱，
不愿意就照你的性格拒绝（**不要自己编一个数额**）」。零数字自查通过（长度 314）。

**给甲方的测试建议**：**在私信里把金额说清楚**（例如"赞助我 5000 块，我要交房租"）；
**不要自己打 `[Trade: …]` 标记**——那会被回声守卫（见 ②）正确地跳过。

---

## 6. 第二步预告（**甲方明确要求：本步验收通过后才做**）

1. `TradeEngine.Confirm` 接进结算路径（真正的扣款/加款），并在动账前**重跑完整校验链**；
2. 审计落盘：把 `[Economy-Audit]` 的成交审计写进影子存档（幂等键 `trade:{world}:{offerId}` 已是现成的对账锚点）；
3. 把 `TradeConfirmationGate.SettlementWired` 翻成 `true`（它同时是"这一步允许动钱"的唯一开关）；
4. UI 侧：弹窗从"确认"升级为"确认并执行"，并把执行结果回写到私信/日志。

---

## 7. 部署与回滚

见 `deploy/DEPLOY.md`（§2 构建与产物、§3 部署、§4 验证、§5 回滚）。
本次只新增了**一个**环境变量开关：`NSE_CONFIRM_DIALOG=0` 关掉确认弹窗（聊天指令仍然有效）。

*本文档结束。与本地代码不符时以本地代码为准，并顺手更新本文档与 `DEPLOY.md`。*
