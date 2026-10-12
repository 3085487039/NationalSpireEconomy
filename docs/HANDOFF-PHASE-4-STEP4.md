# NationalSpireEconomy 阶段 4 第四步交接文档（第三步的真机验收）——**已验收通过**

> **本文用途**：记录**阶段 4 第三步（UI 与账本真相通道）的真机验收过程与结论**：
> 验收怎么做的、踩到哪两个落盘 bug、怎么修的、最后达成了什么、**以及哪一条原始设想被真机推翻了**。
> **撰写时间**：2026-10-12 凌晨起，**2026-10-12 12:0x 结案**。**本文所有数字、哈希均为本机实测。**
>
> **先读这一句（2026-10-12 结案）**：第三步**已真机验收通过**——
> ① 落盘修复被证明有效（`worlds\e35d0119.json` 里 `LastTrade` 键存在、`Status = already_settled`、回执完整）；
> ② 幂等拦截在真机成立（重复确认被状态闸拒，余额与 audit 一字未动，`[Error]` 全程 0）；
> ③ 模型确实读到了账本真相（它的思考与回复里都提到"已经结算/账本上写着"）；
> ④ 线上开关已按甲方拍板回到 `false`；线上 DLL 是含全部修复的 `C6F89483…`。
> **但有一条原始设想被推翻**：**V 段不会显示在游戏私信窗口里**（它只进发给模型的请求），
> 详见 §2"§7.4 的最终结论"；甲方 2026-10-12 明确表示**不做 UI 补充展示**。
>
> **➡️ 下一步接手请看 `docs/HANDOFF-PHASE-4-STEP5.md`（阶段 4 第五步：玩家 → NPC 方向与收尾）。**
> **上游文档**：第三步的完整记录（拍板落点、实施清单、坑位）在 `docs/HANDOFF-PHASE-4-STEP3.md`；
> 更早的第二步（真正动钱）在 `docs/HANDOFF-PHASE-4-STEP2.md`。

---

## 0. 三十秒速览

- **第三步做了什么**（一句话）：把"账本的真话"同时送到**玩家眼前**（V 段回执 + 当场结果框）与**模型眼前**
  （未成立注记 + 账本口径），从此"模型嘴上说转了"与"账上到底动没动"不再各说各话。
- **现在的状态**：代码完成 ✅ / 交付包重建 ✅ / 线上换版 ✅ / 两套基线全绿 ✅ / **真机三步验收 ⏳ 未做**。
- **本阶段唯一的目标**：跑完下面 §2 的三步，拿到"玩家不翻日志也知道成了"与"被拒时玩家与模型都知道为什么"的现场证据，
  然后执行 §3 的收尾（**把线上开关改回 `false`**，这是甲方拍板的长期默认值）。
- **绝对不要做**：不要在这个阶段顺手开新功能（尤其**不要**放开 `playerToNpc` 方向闸——那会让玩家的钱真的减少）。

---

## 1. 线上现状（换版后的权威快照）

```powershell
$dst = 'D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy'
(Get-Item "$dst\NationalSpireEconomy.dll").Length                        # 期望 441344
(Get-FileHash "$dst\NationalSpireEconomy.dll" -Algorithm SHA256).Hash     # 期望 C6F89483…
(Get-Content "$dst\config\economy_config.json" -Encoding utf8 | Select-String 'settlementWired')
```

| 项 | 值 |
| --- | --- |
| 线上 DLL（**已换版、已真机验收**） | `441344` 字节 / `C6F894834F1108FAAAC69C15DC21D2DF975F9B6D4B9A3F8CBCE30556A4D7279E` |
| 交付包 / `bin` | 同上（三处哈希一致 ✅）——**两条路**的落盘修复 + 账本口径哨兵 |
| 更早的线上 DLL（回滚用） | `427008` 字节 / `46E7B94DC8C7BDE059FEC5F6D004581546842E6F31A0AF561AD70EF913BF2069` |
| `C6F89483` 修掉了什么 | ① **两处落盘修复**（旧版永远写不出 `LastTrade`，见下文）；② 哨兵的账本口径（真机已验证生效） |
| 交付包 | `deploy/NationalSpireEconomy/`（含 `SHA256SUMS.txt`），包内 `settlementWired` = **false** |
| 线上开关 | **验收期间 = true**；验收完按 §3 改回 **false** |
| 世界 | `e35d0119`（1638 人、第 452 天 / 第 6 赛季） |
| 玩家影子余额 | `$197,320`（`Imported: true`） |
| `crowd-5-4`（游戏里叫「酸橙汽水」） | `$4,500` |
| `audit\e35d0119.jsonl` | **1 行**（`trade_settled`，流水 `3e237337…`） |
| 影子存档 | `%APPDATA%\SlayTheSpire2\national_spire_economy\worlds\e35d0119.json`（+`.bak`） |
| 第一现场日志 | 同目录 `mod-debug.log` |

> 上表是**换版那一刻**的快照。每次真机验收之后请在下面这张表里**追加一行**（别覆盖旧行）：

| 时间 | 动作 | DLL | 结果（余额变化 / V 段 / 弹窗 / `[Error]`） |
| --- | --- | --- | --- |
| 2026-10-12 00:42 | 第三步换版 | `77D6ADB0…` | 基线 376/498 全绿；真机三步待做 |
| 2026-10-12 00:55 | **游戏重启（人启动）** | `77D6ADB0…` | ✅ 新 DLL 已加载：启动行 `★结算已接线`、`配置校验通过`、`[Error]` 0 |
| 2026-10-12 00:57 | 给 `tour-c60-5` 发私信 | `77D6ADB0…` | ✅ **真相通道已在线上跑**：V 段 `长度 262`（第三步前只有 76），即多了 `【账本口径】` 那一行；`含数字 是`、`零数字 是`（S 段仍零数字） |
| 2026-10-12 01:19 | **人**在游戏内重复 `确认 o0001`（`crowd-5-4`） | `77D6ADB0…` | ✅ 幂等拦截成立、余额与 audit 一字未动、`[Error]` 0；**同时暴露 `LastTrade` 从不落盘**（存档里连键都没有） |
| 2026-10-12 01:09:54 | **人**在游戏内给 `tour-c60-5` 发第二条私信 | `77D6ADB0…` | ✅ 注入仍成功：`message 11`（比上一条多 2 条历史）`V 段 长度 262`、`含数字 是`、`S 段 零数字 是`；玩家文本里**未含**确认/拒绝指令（`[Economy-Confirm] …未含确认/拒绝指令`）；`[Error]` 仍 0 |
| 2026-10-12 01:14 | 面板仍活着 | `77D6ADB0…` | 探针逐帧轮询 `Conversation`/`CanChat`/`CoachLineups.Context`（说明游戏仍在运行、DLL 仍被占用） |
| 2026-10-12 01:26 | **游戏退出 → 换版** | `8324E085…` | ✅ 独占写判据通过；线上 DLL 现在是 `440320` / `8324E085ECA0736E3CD77DA34FB83BF2E76B0E2C8A59576D3C44FFD0CB2BE14D` |
| 2026-10-12 01:26 | **按拍板关掉动钱开关** | `8324E085…` | ✅ `trade.settlementWired: true → false`；用"压平成键值再逐键比对"验过：**语义差异仅此一条**；备份 `economy_config.json.bak-before-off-20261012-012657`（放在 `config\` 外面，避免被打包吃掉） |
| 2026-10-12 01:2x | 换版待做的复验 | `8324E085…` | ⏳ 重启游戏 → 复验 §7.4 的"重启后仍能看到回执"（三条判据见下） |
| 2026-10-12 01:29 / 01:37 | 人两次重启并重复确认 | `8324E085…` | ❌ 该版**修不好**这条路：`LastTrade` 仍不落盘（存档里连键都没有）。根因见下"第二个更深的洞" |
| 2026-10-12 01:26~03:5x | **两次落盘修复 + 哨兵改进** | `C6F89483…` | 交付包 / `bin` 一致；基线 383/383 与 509/509、三构建 0 警告 |
| 2026-10-12 11:16 | 游戏重启（换版后） | `C6F89483…` | ✅ 启动行 `结算未接线（默认）`（开关已 false） |
| 2026-10-12 11:19 | 重复 `确认 o0001` | `C6F89483…` | ✅ **落盘修好了**：`LastTrade` 键出现、`Status=already_settled`、完整回执、写入时间 11:19:17；余额与 audit 未动 |
| 2026-10-12 12:22 | 验收开局（开关临时置 `true`） | `C6F89483…` | ✅ 启动行 `★结算已接线` |
| 2026-10-12 12:25 / 12:28 | **① 被拒**（`fan1_1`，报 $500） | `C6F89483…` | ✅ `交易标记未落单（PERSONALITY_LOW_EMOTION）情绪值 0.71 < 1.00`（窗口是 $700~$731，报低了）；**一次弹窗都没弹**是正确的（没落单就没有确认框） |
| 2026-10-12 12:30:38 | **② 落单**（`fan1_1` 报 $700） | `C6F89483…` | ✅ `交易单 o0002｜校验 7/7` + `[Economy-UI] 已弹出第 1 次确认框（o0002）` —— **确认框真机可见** |
| 2026-10-12 12:31:16 | **② 成交** | `C6F89483…` | ✅ `[Economy-Settle] o0002｜Applied｜$700｜fan1_1 $7,000 → $6,300｜玩家 $197,320 → $198,020`；存档 `LastTrade.Status=settled`；audit 多一行且含 `Direction=npc_to_player` |
| 2026-10-12 12:31:16 | ② 的结果框 | `C6F89483…` | ⚠ `[Economy-UI] 已有别的模态在开着，不抢（结果只写进 V 段与日志）` —— **降级生效**，账与状态不受影响 |
| 2026-10-12 12:33:39 | **③ 幂等**（再发 `确认 o0002`） | `C6F89483…` | ✅ `交易单 o0002 已是 confirmed 状态，确认被拒绝` + `补记已结算结论的落盘（o0002）`；**余额仍 $198,020 / $6,300、audit 仍 2 行**（一分未动） |
| 2026-10-12 12:33:57 | **新哨兵首次真机生效** | `C6F89483…` | ✅ `模型正文里说了「到账了」…⇒ 本轮不会产生任何交易单｜**账本：fan1_1 已给过你 $700（o0002，钱确实动过）**` —— 把"复述真事"与"编"分开这件事**真机成立** |
| 2026-10-12 12:3x | **验收通过 → 关开关** | `C6F89483…` | ✅ `settlementWired: true → false`；与交付包**逐键比对语义差异 0**；备份 `economy_config.json.bak-before-off2-20261012-*` |
| 2026-10-12 | `[Error]` 全轮 | `C6F89483…` | ✅ **0 条** |

### ✅ 三步验收结论（2026-10-12 12:31~12:33，`C6F89483`）

| 步骤 | 结论 | 关键证据 |
| --- | --- | --- |
| ① 被拒 | ✅ | `PERSONALITY_LOW_EMOTION`（0.71 < 1.00）；没有落单、没有动钱、没有弹窗（都是正确行为） |
| ② 成交 | ✅ | `Applied`；NPC `$7,000 → $6,300`、玩家 `$197,320 → $198,020`；确认框弹出且可点；audit 新增一行含 `Direction`；存档 `LastTrade.Status = settled` |
| ③ 幂等 | ✅ | `已是 confirmed 状态，确认被拒绝`；**余额与 audit 一字未动**；补记回执落盘（`already_settled`） |
| 全程 | ✅ | `[Error]` **0**；**模型侧确实读到了账本真相**（新哨兵那行就是铁证）；开关已按拍板回 `false` |

**仍未达成的一项（甲方已明确"不需要"）**：玩家**看不到**"已结算"的即时回执。
两条路都碰壁——V 段不面向 UI（见 §2）、结果框被私信模态挡住（`已有别的模态在开着，不抢`，12:31:16）。
所以玩家只能从 NPC 的话里间接得知。这条记在 §6 待办第 1 条，含三条备选做法。

### 换版后要复验的三条（`8324E085` 起）

这是**落盘修复的现场验收**，也是本阶段唯一还缺的证据。开关现在是 **false**，所以这一步不会动钱：

1. **重启游戏**，`mod-debug.log` 启动行应为 `结算未接线（默认）`（不再是 `★结算已接线`）；
2. 给 **`crowd-5-4`（酸橙汽水）** 发一句 `确认 o0001`：
   - 日志出现 `交易单 o0001 已是 confirmed 状态，确认被拒绝`；
   - **打开 `worlds\e35d0119.json`，里面应出现 `LastTrade` 键**，且 `Status` 是 `already_settled`
     （换版前这份文件里连 `LastTrade` 这个词都没有 —— 这就是修好的判据）；
   - 世界文件的写入时间应该变成**刚才那一刻**（不再是 `23:47:52`）。
3. **再打开一次 `crowd-5-4` 的私信**，V 段里应看到
   `【已结算】…｜单号 o0001｜流水 3e237337` + `【最近结算】共 1 笔…` + `【账本口径】…`——
   **这一眼就是 §7.4 的验收**（V 段原文不落任何日志，只能这样看）。

> ⚠ **第 3 条已被真机否定，别按它验收**：见下面"§7.4 的最终结论"——
> **V 段文本根本不会出现在私信窗口里**（它只进发给模型的请求），所以"用肉眼看私信"这条路不成立。

---

## ✅ 2026-10-12 11:19 —— 真机最终结论（落盘修复被证明有效）

人退出游戏 → 换版 → 重启，三段证据齐了：

```
11:16:14 启动行：结算未接线（默认）                       ← 新 DLL 已加载 + 开关已关
11:19:17 [Economy-Confirm] 交易单 o0001 之前已结算，已把「已结算」结论补记进影子存档（重启后私信里仍能看到回执）
11:19:17 economy.store: 世界 e35d0119 已落盘（补记已结算结论（o0001））
11:19:17 inject-v: 注入成功｜位置 appended｜message 下标 13｜长度 305｜指纹 27b095d1｜含数字 是
```

存档 `worlds\e35d0119.json`（**这是判据**）：

```
LastTrade key present: True
  OfferId: o0001 | Status: already_settled | Settled: True
  AmountUsd: 500 | Direction: npc_to_player | NpcId: crowd-5-4
  LedgerEntryId: 3e237337ee231d4e2e4c82457def88d7
  Receipt: 【已结算】这笔之前就结算过了，本次没有再动钱｜你的影子余额 $197,320｜crowd-5-4 $4,500｜单号 o0001｜流水 3e237337
LastWrittenAt: 2026-10-12T03:19:17Z（本地 11:19:17）
player: 197320 | crowd-5-4: 4500 | audit: 仍 1 行 | [Error]: 全程 0
```

**这意味着两次落盘 bug 都真的修好了**（`LastTrade` 从"只存在内存"变成"盘上有完整回执"），
而且**没有任何钱被动过**（余额与 audit 一字未变）。线上 DLL 为
`441344` / `C6F894834F1108FAAAC69C15DC21D2DF975F9B6D4B9A3F8CBCE30556A4D7279E`，开关 `settlementWired: false`。

### ⚠ §7.4 的最终结论：V 段不面向玩家界面（**本阶段的一个重要认知修正**）

真机截图（2026-10-12 12:0x）证明：**玩家在私信窗口里只看到自己发的 `确认 o0001` 与 NPC 的回复，
看不到我们注入的 `【当前经济】…【已结算】…【账本口径】…`**。

原因是结构性的，不是我写错了格式：**V 段注入的是"发给模型的 messages 数组"，
不是"游戏私信窗口渲染的那份对话记录"**（前者由 `AiService.RequestPrivateAsync` 的入参构成，
后者由原版自己的 `PrivateConversation` 渲染）。所以：

- ✅ 第三步的"**送到模型眼前**"**成立**：模型确实读到了（真机截图里它的思考写着
  "系统提示钱已经结算过了"、回复里说"账本也写上了"）；
- ❌ 第三步最初设想的"玩家在私信里能看到回执"**不成立**——**这条我从第一步就继承了一个未经验证的假设**
  （`TradePreview` 的注释写着"玩家在私信窗口里就能看到它"），直到 2026-10-12 才被真机推翻。
- **玩家侧目前唯一的官方 UI 通道是"复用官方弹窗"**（`TradeConfirmDialog`），它是被真机验证过的：
  `23:46:15 [Economy-UI] 已弹出第 1 次确认框`，那次人在游戏里看到了框。

**甲方 2026-10-12 的决定：不做 UI 补充展示**（"不需要"）。
所以本阶段**到此验收通过**，剩下的是记录在 §6 待办里的"玩家侧可见性"改进（需要时再做）。

### ⚠⚠ 2026-10-12 01:29~01:31 —— `8324E085` 换上了，**但那条修复没生效**（第二个更深的洞）

你按第 1 步重启了，日志证明**新版本确实加载了、开关也确实关了**：

```
2026-10-12 01:29:28.096 启动时间：2026-10-12 01:29:28.096｜进程 32296
2026-10-12 01:29:28.513 [Info] economy.init: 结算未接线（默认）：双方确认后停在「双方已确认，待结算」，一分钱都不会动。…
2026-10-12 01:29:29.287 [Info] economy.init: 配置校验通过（警告 0 条）
2026-10-12 01:31:11.731 [Info] economy.inject: [Economy-Inject-A] 注入成功｜context=<private/e35d0119/crowd-5-4/…>｜…｜V 段 注入成功｜长度 245
2026-10-12 01:31:13.959 [Warning] [Economy-Trade] 模型正文里说了「到账了」…⇒ **本轮**不会产生任何交易单…
```

（最后那行里的"**本轮**"是新 build 才有的措辞 —— 这是"新版已加载"的第二重证据。）

但 01:31 那次 `确认 o0001` 之后：

```
2026-10-12 01:31:11.724 [Economy-Confirm] …｜交易单 o0001 已是 confirmed 状态，确认被拒绝。
（worlds\e35d0119.json 依然：含 LastTrade = False，写入时间仍是 2026-10-11 23:47:52）
```

**根因（我上一轮修错了地方）**：`HandlePlayerCommands` 触发结算的条件是 `status.ReadyToSettle`，
而**重复确认一张已结算的单时 `ReadyToSettle` 恒为 false** —— 于是 `TrySettle` 根本不会被调用，
我补在 `TradeSettlement.Finish` 里的落盘**永远轮不到**。真机这条出口与单测里走的那条**不是同一条路**。

**第二次修法**（已实现、已过测试）：

- 新增 `TradePreviewWiring.NoteClosedTrade`：玩家对一张**已终结**的单表态时——
  手上已有同一张单的已结算结论 ⇒ 只补一次落盘；手上没有（跨重启/注记丢了）而单子在存档里是
  `confirmed` ⇒ **从单子上重建一条"已结算"回执**（`Status = already_settled`，
  余额取**账本现值**，绝不编数字）并落盘；
- 在 `HandlePlayerCommands` 的"确认"分支里调用它（拒绝分支原本就会写 `Declined`）；
- 回归用例 `ReConfirmingSettledOfferRePersistsTheReceipt`：结算一次 → **清掉内存注记（模拟重启后的旧存档）**
  → 再发一次 `确认 <单号>` → 断言 ① 结论被补记成 `already_settled`；② **余额一分未动**；
  ③ **换 store 重新读盘仍能看到回执**。

**这一版：`441344` / `C6F894834F1108FAAAC69C15DC21D2DF975F9B6D4B9A3F8CBCE30556A4D7279E`**
（基线：零依赖 **383/383**、含游戏引用 **509/509**、三构建 0 警告）。**又得等你退出游戏才能换上去。**

> 上一轮补在 `TradeSettlement.Finish` 里的落盘**不是白做的**：它覆盖"程序化调用 `Settle`"与
> "方向未接线 / 还没到齐"那几条出口（有单测钉着）；只是**游戏里重复确认这条路绕过了它**。
> 现在两条路都有闸了。

**当前线上 DLL 还是 `77D6ADB0…`**（第三步的核心都在里面：未成立注记 / 已结算回执 / 结果框 / 账本口径 / 审计回读），
**但它有一个已确认的落盘 bug**（见下一节）：它永远写不出 `LastTrade`，所以"重启后仍能看到回执"在线上还不成立。
**待换的新包是 `8324E085…`**（落盘修复 + 账本口径哨兵），**必须等游戏退出**才能换上。

### ⚠ 2026-10-12 01:19 —— 真机抓到一个**真 bug**（已在库层修掉，待换版）

**先说好消息：这一步的真机证据这次是真的**（`01:19:07`，新 build `77D6ADB0`，`crowd-5-4`）：

```
2026-10-12 01:19:07.924 [Info] economy.trade: [Economy-Confirm] 玩家指令「第 6 赛季第 32 天，尖塔之神发给酸橙汽水的消息
确认 o0001」→ 交易单 o0001｜【双人确认】交易单 o0001｜NPC 已确认｜玩家 已确认｜⏳ 尚未就绪（交易单已是 confirmed 状态。）｜交易单 o0001 已是 confirmed 状态，确认被拒绝。
2026-10-12 01:19:07.925 [Debug] economy.inject-v: 注入成功｜位置 appended｜message 下标 7｜长度 245｜指纹 21a2f53f｜含数字 是
2026-10-12 01:19:10.719 [Warning] economy.trade: [Economy-Trade] 模型正文里说了「到账了」，但本轮【没有】交易标记⇒ 不会产生任何交易单、账上一分未动｜原文 <到账了到账了，你这确认键是卡住了还是怎么着，两回都按一个编号发过来。行了钱在账上跑不了…>
```

- ✅ **幂等拦截在真机成立**：重复确认已结算的单被状态闸拒（`已是 confirmed 状态，确认被拒绝`）；
- ✅ **账本分毫未动**：`Player=197320`、`crowd-5-4=4500`、`audit` 仍 **1 行**（`23:47:52`）、`[Error]` **0**；
- ✅ **哨兵抓到幻觉**（这次是 `到账了` 而不是 `转过去了`）——**这正是第三步要治的症状**；
- ⚠ `V 段 245` 与 `00:57` 的 `262` 不同，说明 V 段确实随账本内容变（**但 V 段原文仍然没落任何日志**，见下）。

**再说那个真 bug（这条才是本轮最大的收获）：`LastTrade` 从来没有被写进磁盘。**

| 检查项 | 实测 |
| --- | --- |
| 线上影子存档里有没有 `LastTrade` 键 | **完全没有**（整份 JSON 里搜不到 `LastTrade` 字样） |
| 世界文件最后写入时间 | 仍是 `23:47:52`（结算那一笔），`01:19` 这次**一个字节都没写** |
| `crowd-5-4` 的 V 段里应该有的"已结算"回执 | 只能靠内存，**重启就没了** |

根因（两条，都读代码确认，并用测试复现）：

1. `TradeSettlement.Finish` 写内存里的 `LastTrade`，但**幂等命中那段出口（`AlreadyApplied`）自己 never 落盘**——
   `SettleCore` 的落盘清单是 `Applied / AlreadyApplied / Failed`，看起来覆盖了，但真机走的是
   "在 `ReadyToSettle` 判定处提前 return" 那条路，**它根本没走到落盘那一步**；
2. 更关键的是**顺序**：`Applied` 那条出口是"先落盘、再写注记"（`Persist` 在 `Finish` 之前），
   于是**第一次结算时 LastTrade 也不在盘上**——所以存档里连键都没有。

修法（已提交、已过测试）：

- **顺序反过来**：先 `Finish`（写注记）→ 再 `Persist`；
- **补上没管的那几条出口**：`NotReady`（含幂等命中/找不到单子/还没到齐）与 `DirectionNotWired` 现在也落盘；
  `NotWired`（开关没开）**依旧一次盘都不写**（有测试钉着：`没结算就别碰存储`）。
- 新增回归用例 `AlreadySettledNoteSurvivesReload`：走一次 `Applied` → 再走一次 `AlreadyApplied` →
  **换一个 store 重新读盘**，`LastTrade` 必须还在且 `Settled == true`。
- 顺带把 `PlayerToNpcDirectionIsNotWiredYet` 的旧断言（"没结算就不该落盘"）改成
  "**钱一分没动**（原断言保留）**且**'未成立'的结论要落盘"——两条现在是并列的。

**修复后的交付包 / `bin`：`440320` / `8324E085ECA0736E3CD77DA34FB83BF2E76B0E2C8A59576D3C44FFD0CB2BE14D`**
（比线上那版还多了"账本口径哨兵"）。**它必须等游戏退出才能换上去**——这是一个自举问题：
修好了"落盘"，但修好的那份现在换不上去，而线上那版永远写不出 `LastTrade`。

> **换版后的复验（关键一步）**：重启游戏 → 再发一次 `确认 o0001` → 检查
> ① `mod-debug.log` 出现 `已是 confirmed 状态，确认被拒绝`；
> ② `worlds\e35d0119.json` 里**出现 `LastTrade` 键**且 `Status` 是 `already_settled`；
> ③ 再打开 `crowd-5-4` 的私信，V 段应出现 `【已结算】…｜单号 o0001` 与 `【最近结算】…`。
> 三条齐了，§7.4（"重启后仍能看到"）才算真过。

### 关于 01:19 之前那份报告

在此之前有人提交过一份"三步验收已全部触发"的报告。逐条核对的结果：**其中的幂等拦截行与哨兵行
来自 `2026-10-11 23:50`（换版前的旧 DLL 会话 23:44:32 / 进程 15376）**，
而且 `$838,000` 这个数在日志与存档里都不存在（实测影子余额 `197320`）。
**所以那份报告不能作为第三步的验收证据**；`01:19` 这次才是（而且它当场暴露了上面那个 bug）。

（详见上一节的逐条核对。）

### 顺带确认的一件事（对第三步有利）

报告里描述的模型行为——把玩家打的 `确认 o0001` 当成"确认收到 $500"，并在正文里再宣称"500 已经转过去了"——
正好是**这条哨兵要抓的形状**。在待换的 `8324E085` 里它会被写成
`｜账本：crowd-5-4 已给过你 $500（o0001，钱确实动过）`，
于是"模型在复述一件真事"与"模型在编"在同一行里就能分开。**这也是这一版值得立刻换上去的理由之一。**

### 已知的现场证据（2026-10-12 00:55~00:58，从 `mod-debug.log` 抄录）

```
2026-10-12 00:55:36.496 启动时间：2026-10-12 00:55:36.496｜进程 45052
2026-10-12 00:55:36.851 [Info] economy.init: ★结算已接线（配置 trade.settlementWired）：双方确认后会【真的动钱】并写 audit jsonl。
2026-10-12 00:55:37.597 [Info] economy.init: NationalSpireEconomy v0.1.0 → 已启用 | … | 错误 0 警告 0 | 补丁 Feed + Finish + …
2026-10-12 00:55:37.596 [Info] economy.init: 配置校验通过（警告 0 条）
2026-10-12 00:57:57.238 [Debug] economy.inject-v: 注入成功｜位置 appended｜message 下标 7｜长度 262｜指纹 11b6e306｜含数字 是
2026-10-12 00:57:57.239 [Info] economy.inject: [Economy-Inject-A] 注入成功｜context=<private/e35d0119/tour-c60-5/…>｜…｜S 段 注入成功｜长度 456｜零数字 是｜V 段 注入成功｜长度 262｜含数字 是
```

- `V 段 长度 262` 是**本阶段最省事的一条线上证据**：第三步前这个数字是 **76**，
  多出来的部分就是 `【账本口径】…`（外加"还没有任何交易记录"的兜底行）。
- 想自己核对 V 段原文，就在**同一台机器上**跑一次最小化的注入（会写进沙箱临时目录，不动存档）：

  ```powershell
  $h = 'D:\蓝色大肥鱼\sandbox\dotnethome'
  $env:APPDATA = "$h\Roaming"; $env:USERPROFILE = $h
  $env:DOTNET_CLI_HOME = $h; $env:NUGET_PACKAGES = "$h\packages"; $env:DOTNET_NOLOGO = '1'
  & dotnet run --project 'D:\蓝色大肥鱼\NationalSpireEconomy\tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj' -c Release -p:GameRefsEnabled=true 2>&1 |
      Select-String -Pattern 'V 段注记'
  ```

  它会把「未成立」与「已结算」两种 V 段原文逐字打出来（就是 §2 两步要看到的形状）。

---

## 2. 真机验收三步（本阶段的核心，约 15 分钟）

**准备**：游戏**从 Steam 启动**（不要直接跑 exe，会弹「Steam初始化失败」）；
开关确认是 `true`（**改完开关要重启游戏**，启动日志会明说 `★结算已接线（…）` 或 `结算未接线（默认）`）。

### 步骤 ① 被拒一次 —— 验「未成立」回灌（真机症状 1+2）

1. 先在**游戏外的**沙箱里挑人 + 看金额窗口（工具会给人名，别用 id 去游戏里找）：

   ```powershell
   $h = 'D:\蓝色大肥鱼\sandbox\dotnethome'
   $env:APPDATA = "$h\Roaming"; $env:USERPROFILE = $h
   $env:DOTNET_CLI_HOME = $h; $env:NUGET_PACKAGES = "$h\packages"; $env:DOTNET_NOLOGO = '1'
   & 'C:\Users\Li\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe' `
       'D:\蓝色大肥鱼\NationalSpireEconomy\tools\pick-sponsor.py' --top 10
   ```

2. 进游戏，给**同一个人**发私信，正文里带一个**必然被拒**的金额（例如他的上限是 $1,300，就写 `[Trade: 赞助, Amount: 9999999]`），
   或者干脆像真机那样让它自己提一个过大的数。
3. **期望看到**（按下述顺序核对）：
   - `mod-debug.log` 出现 `[Economy-Preview] 交易标记未落单（<拒绝码>）：<原因>`；
   - **下一轮**给这个人再发一条私信，V 段（`【当前经济】` 那一段）里必须多出一行：
     `【未成立】赞助 $… （<人名> → 你）没有落单：<拒绝码>：<原因>｜钱一分没动。` + 一行 `【账本口径】…`；
   - **模型不再宣称"钱已经转过去了"**（这是本阶段的头号验收点）；哨兵行
     `[Economy-Trade] 模型正文里说了「…」，但本轮【没有】交易标记` **应明显变少**；
   - `audit\<WorldId>.jsonl` **行数不变**，两边余额**一分不动**；`[Error]` 为 0。

### 步骤 ② 成交一次 —— 验「已结算」回执 + 结果框（真机症状 3）

1. 用 `pick-sponsor.py` 算一个**一次到位**的金额（记住规则：`金额 ÷ max(500, 余额×10%) ≥ 1`，
   且 `金额 ≤ 余额 × tierCapRatio × give × (1 − 0.4×thrift)`），**换一个人**（被拒过的人 7 天内更冷）。
2. 发私信让他提这笔交易 → 游戏里应弹**确认框**（标题「交易确认（确认并执行）」）→ 点**确认**。
3. **期望看到**：
   - **当场弹出一个结果框**，标题 `✅ 交易已结算（钱已到账）`，正文含金额、**双方余额前后**、流水号、幂等键、
     以及"这笔已写进审计文件"；**只有一个按钮**（用 `NVerticalPopup.HideNoButton()` 藏掉了取消键）；
   - 日志里 `[Economy-UI] 已弹出第 N 次结果框（交易单 …｜Applied）`，紧跟其后逐行打印正文；
     若没弹出来，后面一定有一行**说清原因**（非主线程 / 已有模态 / 场景没预载…）——**这不算失败**；
   - `[Economy-Settle] 交易单 …｜Applied｜…｜npc …→…｜玩家 …→…`；
   - **再打开一次这个人的私信**，V 段里应看到：
     `【已结算】赞助 $…（<人名> → 你）｜你的影子余额 A → B｜…｜单号 oXXXX｜流水 XXXXXXXX`
     + `【最近结算】共 N 笔，最近 K 笔：…` + `【账本口径】…`；
   - `audit\<WorldId>.jsonl` 追加一行 `trade_settled`，且**含 `Direction`（`npc_to_player`）**；
   - `[Error]` 为 0。

### 步骤 ③ 幂等再确认一次 —— 验"钱不会动第二次"

1. 拿**步骤 ② 同一个单号**，在私信里回 `确认 <单号>`（或再点一次弹窗）。
2. **期望看到**：
   - 日志 `[Economy-Settle] 交易单 …｜AlreadyApplied`（**不是** `Applied`）；
   - **两边余额一分不动**、NPC 流水不新增；`audit` **不新增**行；
   - 玩家侧**不出现**"这笔又结算了一次"的说法：V 段仍是那条 `【已结算】` 回执
     （第三步特意加了闸：**已结算回执不许被后续的"未成立"覆盖**）。

### 判据速查（`mod-debug.log`）

| 搜索词 | 期望 |
| --- | --- |
| `★结算已接线` / `结算未接线（默认）` | 每次启动一行（改开关后必须重启才生效） |
| `识别出交易标记` | 第 2 条起才是模型真的提交易（**第 1 条永远是启动自检的"诊断 $5,000"**） |
| `[Economy-Preview] 交易单` | 带 `★只读预览，未动任何余额`（此时**单子已经落盘**） |
| `交易标记未落单（<码>）` | 步骤 ① 必出现；**但别指望在私信窗口里看到它**（V 段不面向 UI，见上） |
| `[Economy-UI] 已弹出第 N 次结果框` | 结算那条路应出现一次；没出现要看紧跟其后的原因行 |
| `[Economy-Confirm] 交易单 … 之前已结算，已把「已结算」结论补记进影子存档` | **重复确认**那条路的关键证据（第三步后期新增），出现即说明补记走通 |
| `[Economy-Settle] …｜Applied` / `｜AlreadyApplied` | 结算路 / 幂等路各一条 |
| `[Economy-Trade] 模型正文里说了「…」，但本轮【没有】交易标记` | **第三步之后应明显变少** |
| `audit\<WorldId>.jsonl` 的 `trade_settled` 行 | 每笔结算一行（含双方余额前后、`Direction`、幂等键、流水 id） |
| `worlds\<WorldId>.json` 里的 `LastTrade` 键 | **落盘判据**：它的存在与 `Status` 就是"结论是否落地"的唯一硬标准 |
| `[Error]` | 0 |

**真机会话结束后**，把结果追加到 §1 的表里，并在这张表下面写清"哪一条判据没达成"。

---

## 3. 收尾（**已于 2026-10-12 01:26 执行完毕**）

```powershell
# ① 把线上开关改回 false（甲方拍板的长期默认值：要验钱时再打开）
$live = 'D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy'
# 用 JSON 改写而不是字符串替换，并逐行 diff 确认只改了该改的那一行（当心 BOM，见 STEP3 §10 第 15 条）
$path = "$live\config\economy_config.json"
Copy-Item $path "$path.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"      # 备份放 config\ 外面的话更稳（打包会吃 config\*.json）
$node = Get-Content $path -Raw -Encoding utf8 | ConvertFrom-Json
$node.trade.settlementWired = $false
$node | ConvertTo-Json -Depth 20 | Set-Content $path -Encoding utf8
Get-Content $path -Encoding utf8 | Select-String 'settlementWired'      # 期望 false

# ② 核对交付包与线上 DLL 一致（三处一致：bin / 交付包 / 线上）
(Get-FileHash "$live\NationalSpireEconomy.dll" -Algorithm SHA256).Hash
(Get-FileHash 'D:\蓝色大肥鱼\NationalSpireEconomy\deploy\NationalSpireEconomy\NationalSpireEconomy.dll' -Algorithm SHA256).Hash

# ③ 回归：两套基线（含游戏引用那次放最后，bin 会停在完整版）
$h = 'D:\蓝色大肥鱼\sandbox\dotnethome'
$env:APPDATA = "$h\Roaming"; $env:USERPROFILE = $h
$env:DOTNET_CLI_HOME = $h; $env:NUGET_PACKAGES = "$h\packages"; $env:DOTNET_NOLOGO = '1'
$root = 'D:\蓝色大肥鱼\NationalSpireEconomy'
& dotnet run --project "$root\tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj" -c Release
& dotnet run --project "$root\tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj" -c Release -p:GameRefsEnabled=true
```

**验收完不要重打包**（除非改了代码）：`bin` 现在停在"含游戏引用"的完整版，
再跑一次零依赖测试会把 `bin` 覆盖成缩减版，破坏"bin / 交付包 / 线上三处一致"（STEP3 §10 第 14 条，
以及本文 §7 第 6 条：**这就是那两条"看似回归"的红条是怎么来的**）。

---

## 4. 换版与回滚

```powershell
$root = 'D:\蓝色大肥鱼\NationalSpireEconomy'
$stg  = "$root\deploy\NationalSpireEconomy"
$dst  = 'D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy'

# 换版（**游戏必须完全退出**；判据是"目标文件能否独占写"，不是"有没有同名进程"）
Copy-Item "$stg\NationalSpireEconomy.dll" "$dst\NationalSpireEconomy.dll" -Force
Copy-Item "$stg\config\ai_prompt_economy.json" "$dst\config\ai_prompt_economy.json" -Force
# ⚠ 不要随手覆盖 config\economy_config.json：那会把手动开的 settlementWired 退回 false
```

- 回滚到第三步之前：用 §1 里那个 `427008` / `46E7B94D…` 的备份（备份名见 `docs/HANDOFF-PHASE-4-STEP2.md` §1① 的全哈希表）。
- **沙箱看不到用户的进程**，也**杀不掉**它；换版前由人把游戏完全退出。

---

## 5. 本阶段**不许**顺手做的事

1. **不要放开 `playerToNpc` 方向闸**。甲方拍板的首笔范围是"只开通 NPC → 玩家"，
   那一条判定就在 `src/Trade/TradeSettlement.cs`（`DirectionNotWired`）。
   放开它 = 玩家的钱真的会减少，属于**另一个阶段 + 需要单独的验收**。
2. **不要把 `settlementWired` 的默认值改成 `true`**。`TradeSettlementTests.SettlementSwitchIsOffByDefault`
   会直接拦下；更重要的是"随手一次确认就真的动钱"是真机事故。
3. **不要为了"让弹窗好看"去改原版本地化词条**（铁律 1：绝不改原版）。
   结果框已经用官方 `HideNoButton()` 做成单按钮，不需要新造词条。
4. **不要在验收中途重打包/换版**：一次真机会话只对应一个 DLL 哈希，否则日志对不上账。

---

## 6. 遗留待办（**不阻塞**，甲方已同意延后）

1. **玩家侧可见性（本阶段最重要的遗留）**：V 段不面向 UI（见 §2"§7.4 的最终结论"），
   而甲方 2026-10-12 明确说**不做 UI 补充展示**。所以目前玩家只能从 NPC 的话里间接得知"结算了"。
   将来若要真正"送到玩家眼前"，可选做法（都已讨论过）：
   - **A. 复用官方弹窗**（`TradeConfirmDialog.TryShowResult` 已经有了）：玩家再确认一张已终结的单时当场弹一次回执——
     这条通道被真机验证过（`23:46:15` 弹过确认框），代价只是一次换版；
   - **B. 自绘一个常驻小面板**：读 `LastTrade` / `audit` 显示最近结算，代价是自绘 UI（可行性未调研到证据级别）；
   - **C. 什么都不做**：账本与模型口径已经正确，玩家通过 NPC 的对话得知（甲方当前的选择）。
2. **预览里玩家那行出现两次**（一次作"对手方"、一次作"你"）——甲方说先不急着改；
3. `trade.kindDirections` 的值写成 `npcToPlayer`，而程序常量是 `npc_to_player`（两者都被接受，运行无碍）；
4. `[Deal: accept]` 的**解析**（玩家发起交易 → NPC 同意）——协议文字已写，代码留待接线；
5. **玩家 → NPC 方向**（`playerToNpc`）：路径已写、被方向闸挡着（见 §5 第 1 条）；
6. **结算结果弹窗按钮文字**是原版的「好的」（`GENERIC_POPUP.confirm`）；要改成「知道了」就得改原版词条 ⇒ 不做。
7. **审计行没有 `Day`**（老世界的水位线是 0 时）——新世界会正常；如果哪天要给玩家看"第几天成交"，再补一次迁就。
8. ⚠ **"V 段在私信窗口里可见"这个假设已经被真机推翻**：`TradePreview` 与 V 段相关注释里那句
   "玩家在游戏自带的私信窗口里就能看到它"**是错的**，接手时不要据此做设计（已在 §2 记明）。

---

## 7. 坑位（第三步新踩到的；前两步的在 `docs/HANDOFF-PHASE-4-STEP3.md` §10）

1. **回执是终局事实**：结算之后同一张单再来一次必然产生一条"未成立"（幂等闸）。
   若允许它覆盖 `World.LastTrade`，玩家会看到"弹窗说成了、私信说没成"。
   ⇒ `RecordNote` 与 `TradeSettlement.Finish` 各有一道"已结算优先"的闸；测试复位用 `TradePreviewWiring.ResetNote`。
2. **单测共用一个世界号 = 真机不会出现的串档**：`EnsureWorld` 跨用例缓存同一个 `EconomyWorld`，
   上一组用例留下的幂等收据 / 每日计数 / 回声文本会让下一条用例在第一道闸就被挡掉，
   排查时**看起来像"接线没生效"**。规矩：端到端用例要么用独立世界号，要么 `Clear` + `RememberPlayerTexts(…, [])` + `ResetNote`。
3. **金额决策必须按"这一局的余额"算**：同一个档位在不同世界里余额差一倍，写死 $10,000 会在一个世界里过闸、
   另一个世界里撞 `PERSONALITY_CAP` 或 `DAILY_LIMIT`。用例里要**断言预览金额 == 预期金额**，
   否则"前置失败"会伪装成"结算失败"（这次就这么被骗了三轮）。
4. **别猜原版 API，读元数据**：`NVerticalPopup.HideNoButton()` 是公开方法，
   用 `System.Reflection.Metadata` 读 `sts2.dll`（不加载程序集，5 分钟）就能确认；
   `MetadataLoadContext` 需要 NuGet 包（本机出网被拦），所以走 `PEReader` 那条路。
5. **`config\economy_config.json` 不要随手覆盖**：交付包内是 `false`，线上验收时要 `true`，
   一次 `Copy-Item config\*` 就会把开关退回默认值。
6. **两套基线的先后顺序是硬约束，不是习惯**：零依赖跑一遍会把主工程的 `bin` 变成
   "不含 `src/Game/` 的缩减版"，而**含游戏引用那一次只是重编译一次、不会把缩减版删掉的东西补回来**——
   于是紧跟其后的 game-refs 用例会红两条（`ModEntryTests` 里一条 + `TradeMarkerInterceptorTests` 里一条），
   看起来像代码坏了。正确姿势：**零依赖先跑，含游戏引用最后跑**；跑完再核对
   `bin\Release\net9.0\NationalSpireEconomy.dll` 的哈希等于交付包（**最新一版见 §1**）。
7. **两条"环境敏感"用例已修**（2026-10-12）：`InitializeWithNullRootsFallsBackSafely` 改成显式传
   "不存在的配置根"；`TradeMarkerInterceptorTests` 在 `Program.cs` 里挪到会建立 Runtime 的套件**之前**。
   两条都不是功能缺陷，而是"用例依赖了它不控制的全局状态"——再看到类似红条时先按这个思路查。
8. ⚠ **"我测过的那条路"不等于"真机走的那条路"**（本次最大的教训，同一个 bug 因此漏了两次）：
   我两次都只按设想的出口写测试——第一次补在 `TradeSettlement.Finish`（真机走的出口根本不经过它），
   第二次漏了弹窗那条对称路径。真机的判据是"**玩家怎么操作会走到哪一行代码**"，不是"我以为会走哪里"。
   ⇒ 对策：`EveryTruthWriteSurvivesAReload` 这条不变量，**只从磁盘读**来判，
   覆盖"被拒 / 玩家拒绝 / 对已终结单再表态"三条写路径；谁再写出"只记内存没落盘"的代码，它当场变红。
9. ⚠ **注入进 Prompt ≠ 显示在界面上**：`AiService.RequestPrivateAsync` 的入参（我们注入的 S/V 段）
   与游戏私信窗口渲染的那份记录**是两回事**。任何"让玩家看到"的需求都必须走 **UI 通道**
   （官方弹窗 / 自绘控件），不能靠"写进 V 段"——本阶段的原始设想正是在这一点上错了。

---

## 8. 文档地图

| 文档 | 内容 |
| --- | --- |
| `docs/HANDOFF-PHASE-4-STEP4.md`（本文） | **接手入口**：第三步的真机验收清单、线上现状、收尾命令、坑位 |
| `docs/HANDOFF-PHASE-4-STEP3.md` | 第三步（UI 与账本真相通道）完整记录：拍板落点、实施清单 7.1~7.6、判据表、20 条坑位 |
| `docs/HANDOFF-PHASE-4-STEP2.md` | 第二步（真正动钱 + 审计落盘）：12 轮真机记录、五次换版全哈希 |
| `docs/PLAN-PHASE-4-STEP2.md` | 第二步方案全文（§8 甲方四项决策） |
| `docs/HANDOFF-PHASE-4-TRADE.md` | 第一步交接 + 三轮真机反馈 |
| `docs/HANDOFF-PHASE-3-PROBE.md` | 阶段 3：落点根因、收据裁剪、按需载入三大缺陷 |
| `deploy/DEPLOY.md` | 构建、部署、回滚、真机判据表 |
| `docs/HANDOFF-PHASE-0-2.md` | 阶段 0~2 完整记录 |

*本文档结束。与本地代码不符时以本地代码为准，并顺手更新本文档与 `docs/HANDOFF-PHASE-4-STEP3.md`。*
