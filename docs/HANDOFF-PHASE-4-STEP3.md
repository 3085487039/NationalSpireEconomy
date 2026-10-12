# NationalSpireEconomy 阶段 4 第三步交接文档（UI 与账本真相通道）

> **本文用途**：记录**阶段 4 第三步**（把「账本的真话」同时送到**玩家眼前**与**模型眼前**）的
> 现状、代码落点、验收证据与真机结果。
> **撰写时间**：2026-10-11 23:5x；**代码完成 + 换版时间：2026-10-12 凌晨**。
> **本文所有数字、哈希、日志原文均为本机实测。**
>
> **先读这一句**：阶段 4 第一步（只读预览 + 双人确认）与第二步（**真正动钱 + 审计落盘**）都已在真机验收通过
> —— 真机成交过一笔 $500，NPC 余额真的减少、玩家影子余额真的增加、`audit\<WorldId>.jsonl` 真的生成、
> 幂等复验也通过。**第三步要做的不是"能不能动钱"，而是"让所有参与者看到同一本账"**，
> 它现在**已实现、已换版、两套基线全绿**，剩余动作只有一个：**真机三步验收**（见 `docs/HANDOFF-PHASE-4-STEP4.md`）。
>
> **接手入口**：下一步要做什么在 **`docs/HANDOFF-PHASE-4-STEP5.md`**（阶段 4 第五步：玩家 → NPC 方向与收尾）；
> 第三步的真机验收记录（含两个落盘 bug）在 **`docs/HANDOFF-PHASE-4-STEP4.md`**。本文保留为第三步的实施记录。

---

## 0. 三十秒速览

- 链路已通：模型 `[Trade: …]` → 意图补全 → Reserve（落 **pending** 单，**并强制落盘**）→ 只读预览（进 V 段）→
  官方弹窗（标题「确认并执行」）→ 双方确认 → **结算**（NPC 扣款 / 玩家影子加款）→ **强制落盘 + 审计一行**
  → **结算结果当场弹一次结果框 + 写进 V 段回执**（第三步新增）。
- 真机铁证（2026-10-11 23:47）：`crowd-5-4`（酸橙汽水）$500 ⇒ NPC `$5,000 → $4,500`、玩家影子 `$196,820 → $197,320`、
  交易单 `state=confirmed`（流水 `3e237337…`）、`audit\e35d0119.jsonl` 第一行 `trade_settled`、`[Error]` 0。
- **第三步解决的三个真机症状**（都已被实测撞到）：
  1. **模型会替我们宣布"钱已经转过去了"**，而账上什么都没发生（被闸拒时尤其明显）——
     它的正文还会被原版存进聊天历史，于是下一轮它把"我说过要给"当成"我已经给了"；
     ⇒ 现在 V 段固定写一条**账本口径**，并在被拒时写明「【未成立】…钱一分没动」；
  2. **被我们拒掉时，玩家完全不知情**（他只看到模型说"行吧转你了"）；
     ⇒ 现在被拒的**原因 + 金额 + 单号**会落盘成 `EconomyWorld.LastTrade`，下一轮私信 V 段直接可见；
  3. **玩家看不到"结算成功/失败"的当场回执**（原先只写进日志与 `audit` 文件）。
     ⇒ 现在**当场弹一次结果框**（只留一个「好的」按钮）+ **V 段一行「已结算」回执** + **最近结算回读**。
- **动钱开关**：交付包与仓库配置里 `trade.settlementWired = false`（**甲方拍板的长期默认值**）；
  验收期间线上刻意置 `true`，验完已按同一拍板改回 `false`（见 §3 与第四步文档）。

---

## 1. 立刻要做的三件事

### ① 核对线上状态（30 秒）

```powershell
$dst = 'D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy'
(Get-Item "$dst\NationalSpireEconomy.dll").Length                        # 第三步后期望 439296
(Get-FileHash "$dst\NationalSpireEconomy.dll" -Algorithm SHA256).Hash     # 期望 77D6ADB0…
(Get-Content "$dst\config\economy_config.json" -Encoding utf8 | Select-String 'settlementWired')
```

- 第三步换版前：DLL `427008` / `46E7B94DC8C7BDE059FEC5F6D004581546842E6F31A0AF561AD70EF913BF2069`。
- 第三步换版后：DLL `439296` / `77D6ADB04FFBA9C4EE994D01B502FFAD7162A932966BF2217D93D1B9634C8040`
  （**已真机加载并验证**：2026-10-12 00:55 那次会话就是它，V 段从 76 涨到 262）。
- 之后又出了一版只含"账本口径哨兵"这条**诊断改进**的
  `440320` / `AB49A2BC38B698166655182521356AF8717A11398075308C10381CAF84D42DD3`（**待换版**，见第四步 §1）。
- 配置 `"settlementWired"` 的取值按"验收中 = true / 交付默认 = false"判断（见 §3 末条）。
- 若哈希不符：看 `docs/HANDOFF-PHASE-4-STEP2.md` §1①（五次换版的全哈希表与备份名），**换版前务必完全退出游戏**，
  并且**用"目标文件能否独占写"判断**而不是"有没有同名进程"（见 §10 第 12 条）。

### ② 跑两套基线（约 2 分钟，确认起点是绿的）

```powershell
$h = 'D:\蓝色大肥鱼\sandbox\dotnethome'
$env:APPDATA = "$h\Roaming"; $env:USERPROFILE = $h
$env:DOTNET_CLI_HOME = $h; $env:NUGET_PACKAGES = "$h\packages"; $env:DOTNET_NOLOGO = '1'
$root = 'D:\蓝色大肥鱼\NationalSpireEconomy'
& dotnet run --project "$root\tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj" -c Release
& dotnet run --project "$root\tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj" -c Release -p:GameRefsEnabled=true
```

期望：零依赖 **383/383**、含游戏引用 **509/509**（第三步前是 361/480）。
**含游戏引用那一次放最后**（`bin` 会停在完整版）。

### ③ 已拍板（**甲方 2026-10-12 确认，照建议执行**），所以这一步不再需要先问

见 §3。第三步的实现已经按这两条拍板落地，接手时**直接跑真机验收**即可。

---

## 2. 路径总表

| 用途 | 绝对路径 | 权限 |
| --- | --- | --- |
| 项目根 | `D:\蓝色大肥鱼\NationalSpireEconomy\` | 读写 |
| **本文** | 同目录 `docs/HANDOFF-PHASE-4-STEP3.md` | 读写（持续更新） |
| 第一步交接 | 同目录 `docs/HANDOFF-PHASE-4-TRADE.md` | 读写 |
| **第二步交接（含全部换版哈希与验收记录）** | 同目录 `docs/HANDOFF-PHASE-4-STEP2.md` | 读写 |
| 第二步方案全文 | 同目录 `docs/PLAN-PHASE-4-STEP2.md` | 读写 |
| 阶段 3 证据/坑位 | 同目录 `docs/HANDOFF-PHASE-3-PROBE.md` | 读写 |
| 阶段 0~2 | 同目录 `docs/HANDOFF-PHASE-0-2.md`、`docs/HANDOFF.md` | 读写 |
| 部署与真机判据 | `deploy/DEPLOY.md` | 读写 |
| 交付包产出 / 一键打包 | `deploy/NationalSpireEconomy/`、`build-deploy.ps1` | 读写 |
| **挑人 + 定金额的工具** | `tools\pick-sponsor.py`（**会给人名**，见 §10 第 10 条） | 执行 |
| **我们的部署目录** | `D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy\` | **需一次授权升级** |
| 动钱开关 | 同上 `config\economy_config.json` → `trade.settlementWired` | 需授权 |
| ⛔ 禁写 | `…\mods\NationalSpire\`、原版存档 `national_spire_career*.json` | 禁写（只读） |
| 文件保底日志（第一现场） | `%APPDATA%\SlayTheSpire2\national_spire_economy\mod-debug.log` | 读写 |
| 旁路取证日志 | `…\national_spire_economy\probe\path-probe.log`（很大，用 Select-String） | 读写 |
| 影子存档 | `…\national_spire_economy\worlds\<WorldId>.json`（+`.bak`） | 读写 |
| **审计（每笔结算一行）** | `…\national_spire_economy\audit\<WorldId>.jsonl` | 读写 |
| 原版生涯存档（**找 NPC 姓名用**） | `…\SlayTheSpire2\steam\<steamid>\modded\profile*\national_spire_career*.json` | **只读** |
| 游戏自己写的诊断（`private.response.reasoning` 是金矿） | `…\SlayTheSpire2\national_spire_diagnostics\national-spire.log` | 只读 |
| 当前世界 | `e35d0119`（1638 人、第 452 天 / 第 6 赛季） | — |

---

## 3. 铁律、已拍板的决策、**待拍板的两问**

**四条铁律（不变）**：① 绝不改原版（不改源码/DLL、**不写原版存档**，只读）；
② 影子存档按 `WorldId` 分片，绝不串档；③ `MoneyUnits` 是唯一换算出口（影子内一律**美元整数**）；
④ 余额变动一律走 `EconomyLedger` + 幂等键，重复调用不重复发钱。

**甲方为第二步拍板的四项（已全部落成代码）**：

| 决策 | 落地位置 |
| --- | --- |
| 玩家侧资金落点 = **影子钱包** | `src/Economy/ShadowPlayerWallet.cs` + `PlayerEconomy.cs`；首次从原版 `Credits + CreditFraction` 只读导入一次（`ShadowEconomyRuntime.ImportPlayerWalletOnce`） |
| 结算触发 = **弹窗「确认并执行」** | `TradeConfirmDialog.Title/BodySuffix` 随开关切换。**按钮文字无法改**（原版弹窗按钮吃它自己的本地化键，改字就得改原版词条 ⇒ 违反铁律 1），所以语义写在标题与正文里 |
| 审计形态 = **影子存档 + `audit\<WorldId>.jsonl`** | `TradeSettlement` → `EconomyAudit.Append`，`Kind=trade_settled`，含**双方余额前后**、幂等键、流水 id |
| 首笔范围 = **只开通 NPC→玩家** | `TradeSettlement` 前置**方向闸**：非 `npc_to_player` 返回 `DirectionNotWired`（回归测试 `PlayerToNpcDirectionIsNotWiredYet`） |

**甲方 2026-10-12 对第三步拍板（均为「照建议做」，已落成代码）**：

| 决策 | 落地位置 |
| --- | --- |
| 结算结果怎么送到玩家眼前 = **V 段回执 + 复用官方弹窗**（成功/失败各弹一次，正文写清金额、单号、双方余额） | `src/Game/TradeConfirmDialog.cs` 的 `TryShowResult`/`ResultTitle`（复用同一套骨架，**只留一个按钮**：`NVerticalPopup.HideNoButton()`）+ `src/Trade/TradeNoteText.cs` 的 `ReceiptLine`/`ReceiptDetail` |
| **"未成立"也要回灌**给玩家与模型 | `src/Economy/EconomyModels.cs` 的 `EconomyWorld.LastTrade`（`LastTradeNote` + `TradeNoteStatuses`）；写入点：`TradePreviewWiring.HandleModelIntent` 的三条拒绝分支、`ApplyPlayerAnswer`/`HandlePlayerCommands` 的拒绝分支、`TradeSettlement.Finish` 的全部出口 |
| 开关长期默认值 = **false**（要验钱时再打开） | 仓库 `config/economy_config.json` 与交付包内均为 `false`；线上验收期间置 `true`，**验完改回 `false`**（第四步文档 §2 有逐条命令） |

**为什么"两者都做"而不是二选一**：弹窗只能在玩家点下确认按钮的那一次出现（主线程、单模态、随时可能弹不出来），
而 V 段回执是**跨重启、跨界面、下一轮一定可见**的那一份——两条腿各管一段，缺一条就会回到"玩家不知情"。

---

## 4. 环境与命令（复制即用）

见 §1② 的环境变量与两条测试命令。补充：

```powershell
# 打包（**必须是最后一次构建**）
& "$root\build-deploy.ps1"

# 换版（游戏必须完全退出；mods\ 在沙箱之外，需要一次授权升级）
$stg = "$root\deploy\NationalSpireEconomy"
$dst = 'D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy'
Copy-Item "$stg\NationalSpireEconomy.dll" "$dst\NationalSpireEconomy.dll" -Force
Copy-Item "$stg\config\economy_config.json" "$dst\config\economy_config.json" -Force   # 会把手动开的开关退回 false！
Copy-Item "$stg\config\ai_prompt_economy.json" "$dst\config\ai_prompt_economy.json" -Force

# 挑人 + 定金额（**唯一正确的挑人方式**）
& 'C:\Users\Li\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe' `
    "$root\tools\pick-sponsor.py" --top 10
```

- **开关**：`config\economy_config.json` → `"settlementWired": true/false`（改完**重启游戏**生效；启动日志会明说
  `★结算已接线（…）` 还是 `结算未接线（默认）`）。环境变量 `NSE_SETTLEMENT=1` 只在**命令行启动**时有用
  ——**Steam 启动传不进环境变量**，直接跑 exe 又会弹「Steam初始化失败」（见 §10 第 11 条）。
- **游戏必须从 Steam 启动**。

---

## 5. 代码地图（本阶段要动的地方）

| 文件 | 作用 / 本阶段要碰的点 |
| --- | --- |
| **`src/Inject/PromptInjectInput.cs`** | 记录里最后一个参数 `ExtraVolatileNote` —— **V 段唯一的注记入口**。本阶段多半要把它从"一个字符串"扩成"一组按类型排好序的注记"（待确认预览 / 最近一次未成立 / 最近一次已结算） |
| **`src/Inject/VolatileSegment.cs`** | V 段的合成与行序（`LineKeys`：`volatileCurrent` / `volatileLastEntry` / `volatilePendingVoice`）。新注记要遵守"**只有 V 段能有数字**"与"逐字节稳定不适用于 V 段"这两条既有纪律 |
| **`src/Game/TradePreviewWiring.cs`** | 游戏侧接线：`HandleModelIntent`（落单/预览/NPC 票/弹窗/**落单即落盘**）、`ApplyPlayerAnswer`/`HandlePlayerCommands`（玩家票 + `TrySettle`）、`NoteFor(worldId)`（**当前 V 段注记的唯一来源**）、`LastPreview`/`LastConfirmation`/`LastSettlement`（诊断投影） |
| **`src/Trade/TradeSettlement.cs`** | 结算器：前置闸门（开关 → 方向 → 双人确认 → 状态/过期）→ `TradeEngine.Confirm` → 审计 → 强制落盘。**本阶段可能要在这里把"结果"交给上层用于回执** |
| `src/Trade/TradeConfirmation.cs` | 双人确认闸门（两票=两个收据键）+ **动钱开关 `SettlementWired`** + `Status` 只读投影 |
| `src/Trade/TradeEngine.cs` | `Submit`（Reserve）/`Confirm`（重跑校验链 + 动账 + 落状态）。**拒绝原因（`TradeCheck.Code/Message`）目前只进日志** |
| `src/Economy/EconomyModels.cs` | `EconomyWorld`（`Offers` / `Player` / `Receipts`）。**若要持久化"最近一次未成立+原因"，建议在这里加一个小节点**（而不是靠内存，重启即丢） |
| `src/Economy/ShadowPlayerWallet.cs` / `PlayerEconomy.cs` | 玩家影子钱包（`Apply`/`ImportOnce`/`ReadOnlySnapshot`）；玩家自己的流水也在 `Player.Ledger` |
| `src/Inject/EconomyAudit.cs` | `Append` / **`Read(worldId)`** / `PathFor` —— 想给玩家看"最近几笔结算"，直接读它最省事 |
| `src/Game/TradeConfirmDialog.cs` | 官方弹窗封装（主线程 + 单模态 + 失败降级聊天指令；`NSE_CONFIRM_DIALOG=0` 可关）。**结果弹窗可以复用它的骨架** |
| `src/Game/ShadowEconomyRuntime.cs` | 世界载入/播种/推进/首次导入；`BuildInputFor(...)` 在**第 387 行**把 `TradePreviewWiring.NoteFor(world.WorldId)` 塞进 `ExtraVolatileNote` |
| `tools/pick-sponsor.py` | 挑人 + 定金额 + **把 id 映射成人名**（读原版生涯存档，只读） |

---

## 6. 前两步定下的契约（本阶段必须保持）

1. **标记协议只由 S 段教模型**，且 S 段（`stableBlockSuffix`）**必须零数字**（`ConfigLoader` 用 `\d` 拦，Error 级 ⇒ 拒绝启用）。
   预览/回执含金额 ⇒ **只能进 V 段或日志**。
2. **确认（投票）绝不动钱**：投票路径用的钱包是只读快照（`InMemoryPlayerWallet`），
   动钱只能用 `ShadowPlayerWallet`（它自己还有"未导入就拒绝动钱"的纪律）。
3. **结算的前置四闸**：`SettlementWired` → 方向 = `npc_to_player` → 双方确认 → 单子仍 pending 且未过期；
   任一不满足**一个数字都不改**。
4. **结算后必须落盘**（`force:true`）**并写审计**；审计先写（它是旁路，落盘失败时仍是证据）。
5. **幂等**：`trade:{world}:{offerId}`；重复调用返回 `AlreadyApplied`/`NotReady` 且**不改任何数字**。
6. **失败的尝试不留痕**：玩家腿失败 ⇒ 回滚 NPC 腿 + **释放整笔幂等键**（`ReceiptBook.Unmark`），可安全重试。
7. **待确认单也落盘**：单子跨重启仍在（真机就是被"单子只在内存"绊过一次）。
8. **回声守卫**：模型回复里逐字引用玩家本轮说过的标记 ⇒ 不算它提议/同意。
9. **玩家指令要能穿过原版的壳**：原版把玩家的话包成「第 X 赛季第 Y 天，{谁}发给{谁}的消息\n正文」，
   解析按**行首**匹配（`src/Trade/PlayerTradeCommand.cs`），且**绝不全文包含**（否则我们自己的预览文案会被当成指令）。

---

## 7. 本阶段实施清单（**已全部实现**，逐条给落点与验收）

> **状态**：7.1/7.2/7.3/7.4/7.5 已实现并换版；**7.6 真机三步验收待做**（清单见 `docs/HANDOFF-PHASE-4-STEP4.md` §2）。

### 7.1 把「未成立」写进账本的可见处（**优先级最高**）—— ✅ 已实现

- **原状**：被拒时只有一行日志（`[Economy-Preview] 交易标记未落单（PERSONALITY_LOW_EMOTION）：…`），
  **玩家与模型都看不到**；模型下一轮仍可能说"我已经转过去了"。
- **实际落点**：
  1. `src/Economy/EconomyModels.cs` 新增 `LastTradeNote`（`Day/OfferId/Kind/Direction/AmountUsd/NpcId/Status/Reason/Receipt/Detail/LedgerEntryId`）
     与 `TradeNoteStatuses`（`settled/already_settled/incomplete/refused/failed/declined`），挂在 `EconomyWorld.LastTrade`；
  2. 写入点共 5 处：`TradePreviewWiring.HandleModelIntent` 的**补全失败 / 上下文失败 / Reserve 被拒**三条分支、
     `ApplyPlayerAnswer` 与 `HandlePlayerCommands` 的**玩家拒绝**分支（`TradeNoteStatuses.Declined`）、
     以及 `TradeSettlement.Finish`（结算每一条出口都写，含"开关没开 / 方向未接线 / 还没到齐"）；
  3. `TradePreviewWiring.NoteFor(EconomyWorld)` 把「待确认预览 / 最近一笔未成立 / 最近一笔已结算 / 最近结算回读 / 账本口径」
     按序拼进 V 段（原来是 `NoteFor(worldId)`，只给预览；调用点 `ShadowEconomyRuntime.BuildInputFor` 第 ~387 行）。
- **验收（单测）**：`TradeNoteTextTests` 15 条 + `PlanAInjectionTests.RejectedIntentBecomesVisibleNotSettledNote`
  （真机形状：$50,000,000 ⇒ `AMOUNT_TOO_LARGE` ⇒ V 段出现「未成立」+ 拒绝码 + 金额 + 「钱一分没动」+ 账本口径）。
- **验收（真机）**：见第四步文档；建议就用 `tools/pick-sponsor.py --npc <id>` 看窗口后故意报一个必被拒的金额。
- ⚠ **一条纪律**：`RecordNote` **绝不覆盖一条"已结算"的回执**（`TradeSettlement.Finish` 同样有这条闸）。
  理由：钱动过是终局事实，而结算之后同一张单再来一次必然命中幂等闸、也必然产生一条"未成立"；
  若允许覆盖，玩家会看到"弹窗说成了、私信说没成"。需要复位时用 `TradePreviewWiring.ResetNote(world)`（测试/诊断用）。

### 7.2 把「已结算」的回执送到玩家眼前 —— ✅ 已实现（两条腿都做了）

- **V 段回执**（跨重启、下一轮必可见）：
  `【已结算】赞助 $500（crowd-5-4 → 你）｜你的影子余额 $196,820 → $197,320｜crowd-5-4 $4,500 → $4,000｜单号 o0002｜流水 3e237337`
  —— 由 `TradeNoteText.ReceiptLine` 渲染，落 `LastTrade.Receipt`，随下一次落盘一起写下去。
- **当场结果框**：`TradeConfirmDialog.TryShowResult`（复用确认框同一套骨架：主线程 → `NModalContainer.Instance` →
  单模态不变量 → `NGenericPopup.Create()`），**只留一个「好的」按钮**——
  实测 `NVerticalPopup` 有公开的 `HideNoButton()`（元数据探查确认，见 §5），所以不必用取消键冒充"知道了"。
  标题由 `TradeConfirmDialog.ResultTitle` 给（`✅ 交易已结算（钱已到账）` / `⛔ 这个方向还没接线（钱没动）` / …）。
- **降级**：弹不出来只写一行 `[Economy-UI] <原因>` 并保留日志 + V 段；**结算结果永远不受弹窗影响**
  （结算在 `TradeSettlement` 里已经落盘+审计完毕，弹窗只是通知）。
- **验收（单测）**：`TradeNoteTextTests.ReceiptLineCarriesBothBalancesAndAmount` /
  `ReceiptDetailListsBalancesAndLedgerId` / `FailedDetailSaysNoMoneyMoved`；
  `PlanAInjectionTests.SettledReceiptReachesTheVolatileSegment`（含"开关关掉后再喂一次、回执不许被覆盖"）。
- **验收（真机）**：成交后**不翻日志**也能知道成了：先看到结果框，下一次私信 V 段还有同一份回执。

### 7.3 让模型"言行一致" —— ✅ 已实现

- **落点**：`src/Trade/TradeNoteText.cs` 的 `LedgerRuleLine`（常量，每轮都写进 V 段）：
  > 【账本口径】只有上面写着「已结算」的交易才算这笔钱真的动过；你在正文里说「我已经转过去了」不会让钱动，账本也不会因此变化。
- **为什么留在 V 段而不是 S 段**：它描述的是交易状态这件事，而 S 段背着"零数字 + 逐字节稳定"两条纪律，
  没必要为一句动态口径去动它（V 段本来就可以有数字）。
- **验收（单测）**：`LedgerRuleSaysOnlySettledCounts` + 两条端到端用例都断言它每轮在 V 段里。
- **验收（真机）**：连续做一次"被拒"与一次"成功"，模型下一轮措辞应与账本一致（不再宣称已转账而账上没动）。

### 7.4 审计回读（可选，但很划算） —— ✅ 已实现

- `EconomyAudit.Read(worldId)` 读回全部记录，`TradeNoteText.RecentSettlements(records, take: 3)` 渲染成
  `【最近结算】共 N 笔，最近 K 笔：…`，由 `TradePreviewWiring.NoteFor` 拼进 V 段（`RecentSettlementsOf` 是纯读且 **永不抛**）。
- 顺带补了一个字段：`AuditRecord.Direction`（结算行现在记方向），否则回读时说不清"谁给谁"；
  老文件里没有这一列，展示端按空串容忍。
- **验收（单测）**：`RecentSettlementsReadsAuditNewestFirst` / `RecentSettlementsIsNullWhenEmpty`；
  端到端那条用例断言 V 段里出现 `最近结算` 且能对上单号。

### 7.5 测试 —— ✅ 已完成

- 零依赖：新增 `TradeNoteTextTests`（15 条，含 `TradeSettlement` 五条出口的落盘结论）；
- 含游戏引用：`PlanAInjectionTests` 新增 3 条端到端（被拒 → V 段有原因；成交 → V 段有回执且不被覆盖；玩家拒绝 → 可见）；
- **基线**：零依赖 **383/383**、含游戏引用 **509/509**（第三步前 361/480）；
  换版时是 376/498；之后补了七组用例共 12 条："账本口径哨兵"、"V 段长度守门"、"老存档缺 LastTrade 键"、
  "**幂等命中的回执必须落盘**"、"**落盘 → 重新读盘 → 变成 V 段那一行**"、
  "**对已终结的单再表态也要把结论补进存档**"（两次真机踩坑的回归，见第四步 §1 的两节 ⚠）、
  "**每一个真相出口都必须经得起重新读盘**"（把'写了就必须落盘'变成从磁盘判的不变量）；
  三种构建（主工程零依赖 / 主工程含游戏引用 / 测试工程含游戏引用）**各 0 警告 0 错误**。
- ⚠ **跑的顺序是硬约束**：零依赖先跑、含游戏引用**最后**跑；跑完核对
  `bin\Release\net9.0\NationalSpireEconomy.dll` 哈希 == 交付包 == 线上（换版后的最新一版见第四步 §1）。
  顺序反了会红两条"环境敏感"用例（详见 §10 第 21、22 条与第四步文档 §7 第 6 条）。

### 7.6 真机 —— ⏳ 待做（换版已完成）

- 一次跑完"**被拒一次 + 成交一次 + 再确认一次（幂等）**"三件事最省时间。
- **完整清单与逐条期望见 `docs/HANDOFF-PHASE-4-STEP4.md` §2**；判据速查见本文 §8。

---

## 8. 真机判据速查（`mod-debug.log`）

| 搜索词 | 期望 | 含义 |
| --- | --- | --- |
| `★结算已接线` / `结算未接线（默认）` | 每次启动一行 | 动钱开关状态（配置 `trade.settlementWired` 或环境变量） |
| `[Economy-Wallet] 首次导入` | 每个世界一次 | 玩家影子钱包首次导入（只读原版 `Credits + CreditFraction`） |
| `识别出交易标记` | 第 2 条起才是模型真的提交易（**第 1 条永远是启动自检的"诊断 $5,000"**） | 协议生效；**模型只说不写时这一行不出现** |
| `[Economy-Preview] 交易单` | 带 `★只读预览，未动任何余额` | 补全 + 预览 + 落单成功（此时**已经落盘**） |
| `交易标记未落单（<码>）：<原因>` | 可出现 | 被校验链拒（`PERSONALITY_LOW_EMOTION` 太小 / `PERSONALITY_CAP` / `AMOUNT_TOO_LARGE` / `ALREADY_APPLIED`…）——**第三步起它同时落成 `LastTrade` 并进 V 段** |
| `[Economy-UI] 已弹出` | 出现 ⇒ 游戏里应看到弹窗 | 弹不出来会写原因并降级聊天指令 |
| `[Economy-UI] 已弹出第 N 次结果框` | 结算之后**应出现一次** | 第三步新增：当场结果框（`TryShowResult`）。**没出现不算失败**，但要能看到它后面那行原因 |
| `[Economy-Settle] …｜Applied` | 结算成功 | 钱真的动了：含双方余额前后 |
| `[Economy-Settle] …｜AlreadyApplied` | 幂等命中 | 之前结算过，**余额一分没动** |
| `[Economy-Settle] …｜DirectionNotWired` | 方向闸生效 | 反向交易被挡，**钱没动** |
| `[Economy-Trade] 模型正文里说了「…」，但本轮【没有】交易标记` | 出现即"嘴上说转了、账上没动" | 22:39 加的哨兵；**第三步做完后它应该变少**（口径行 + 未成立注记都摆在模型眼前了） |
| `audit\<WorldId>.jsonl` 的 `trade_settled` 行 | 每笔结算一行 | 对账的权威依据（含双方余额前后、幂等键、流水 id，**第三步起还含 `Direction`**） |
| `[Error]` | 0 | — |

**换版前世界实况（2026-10-11 23:5x）**：`e35d0119`、1638 人、第 452 天 / 第 6 赛季；
玩家影子 **$197,320**（`Imported: true`，流水 2 条）；`crowd-5-4` = **$4,500**；
`Offers` 里 `o0001` = `confirmed`（流水 `3e237337…`）；收据 512 条（含两条 `confirm:`）；
`audit\e35d0119.jsonl` **1 行**。

**第三步换版后世界实况**：见 `docs/HANDOFF-PHASE-4-STEP4.md` §1（每次真机验收都往里追加一行，别覆盖旧行）。

---

## 9. 待办（不阻塞，甲方同意延后）

1. **预览里玩家那行出现两次**（一次作"对手方"、一次作"你"）——甲方说先不急着改；
2. `trade.kindDirections` 的值写成 `npcToPlayer`，而程序常量是 `npc_to_player`（两者都被接受，运行无碍）；
3. `[Deal: accept]` 的**解析**（玩家发起交易 → NPC 同意）——协议文字已写，代码留待接线；
4. **玩家 → NPC 方向**（`playerToNpc`）：路径已写、被方向闸挡着；甲方要开通时删掉那条判定并补验收；
5. **弹窗按钮文字**无法改成「确认并执行」（原版本地化键）；如甲方坚持，需要另做自绘控件（有可行性调研，见 `TradeConfirmDialog` 的注释）。

---

## 10. 前两步踩过的坑（必读；每条都在真机上付过学费）

1. **模型的话 ≠ 账本**：它两次宣布"钱打过去了"而账上一分未动；它的正文还会被原版存进聊天历史
   （标记被剥掉），于是下一轮它把"我说过要给"当成"我已经给了"。**本阶段就是为这个做的。**
2. **静默失败最贵**：玩家打了指令而模组一声不吭，谁都查不出为什么。凡是"收到输入但没听懂"的分支，
   都要留一行可检索日志（现有哨兵：`玩家文本里出现确认/拒绝字样，但没能解析成指令`、
   `模型正文里说了…但本轮【没有】交易标记`）。
3. **玩家的话是被原版包了壳的**：`第 X 赛季第 Y 天，{谁}发给{谁}的消息\n<正文>`。
   解析必须**按行**、**只认行首**（全文包含会把我们自己的预览文案当成指令 ⇒ 自动确认交易）。
4. **弹窗答案不能依赖有时效的身份槽**（真机 5 分 13 秒后点确认，票被 TTL 挡掉）。
5. **挑人与金额必须算**：`情绪值 = 金额 ÷ max(500, 余额×10%) ≥ 1.0`，且 `金额 ≤ 余额 × tierCapRatio × give × (1−0.4×thrift)`。
   **有人窗口是空的**（实测 `p3`：上限 $428 < 情绪闸 $630），**有人要的钱远超直觉**（`crowd-4-3` $7,100 ⇒ 至少 $710）。
   **工具给的是人名**（游戏界面里看不到 id）——见**下一条**。
6. **工具给 id 等于没给**：`crowd-5-4` 在游戏里叫「酸橙汽水」。现在的 `pick-sponsor.py` 会读原版生涯存档映射姓名；
   注意**当前这局的生涯档文件名带哈希**，无后缀那份是另一局。
7. **金额是双侧夹逼**：太小 `PERSONALITY_LOW_EMOTION`、太大 `PERSONALITY_CAP`；
   每次被拒还会写 `LastRefusedCharityDay`（那个人 7 天内更冷）⇒ **换人 + 一次到位**。
8. **"0 警告"要分别验三种构建**（主工程零依赖 / 主工程含游戏引用 / 测试工程含游戏引用）。
   另外 `const bool` 开关会把结算分支变成**不可达代码**（CS0162）⇒ 开关做成静态属性 + 配置项。
9. **收据键必须在回滚时释放**：`Move` 登记了整笔键，而回滚不会撤销它 ⇒ 重放返回 `AlreadyApplied` 而账上一分未动
   （"收了又回退"被幂等闸吞掉）。修法：`ReceiptBook.Unmark` + 失败路径释放键。
10. **钱动了而状态没写盘 = 可以再动一次**：结算后必须 `force:true` 落盘；**待确认单也要落盘**
    （否则重启后单号失效，玩家拿旧号来确认只会得到"找不到交易单"）。
11. **Steam 启动拿不到环境变量**：`$env:NSE_SETTLEMENT=1` 传不进 Steam 启动的游戏；绕过 Steam 直接跑 exe 会弹
    「Steam初始化失败」（放 `steam_appid.txt` 也救不了）⇒ 开关做成**配置项**。
12. **沙箱看不到用户的进程**：沙箱内 `Get-Process` 查无游戏、授权视角下有两个 `SlayTheSpire2`，
    于是"游戏是否在运行"的守卫会抖。**换版的正确判据是"目标文件能否独占写"**；
    而且**沙箱杀不掉用户的进程**（`Access is denied`）——残留进程只能由人关。
13. **打包会吃 `config\` 下的一切 `*.json`**；备份请用 `.bak.json` 且放在 `config\` 外面。
14. **不要在打包之后又跑零依赖测试**：那会把 `bin` 覆盖成不含 `src/Game/` 的缩减版，破坏"bin/交付包/线上三处一致"。
15. **写 JSON 配置时当心 BOM**：PowerShell 5.1 的 `Set-Content -Encoding utf8` 会加 BOM；
    改完要用 `json.load`（或 `utf-8-sig`）验一遍，并**逐行 diff 确认只改了该改的那一行**。
16. **凡是"阶段范围/开关"的拍板，都要在代码里找到那个"不许过"的判定点**——否则它只是一句愿望
    （真机教训：甲方定"首笔只开通 NPC→玩家"，而我漏了方向闸，开关一开反向交易真的会扣玩家的钱）。

### 第三步新踩到的（2026-10-12）

17. **回执是终局事实，不许被后来的"未成立"覆盖**：结算之后同一张单再来一次必然命中幂等闸、
    也必然产生一条"未成立"。若允许它覆盖 `LastTrade`，玩家就会看到"弹窗说成了、私信说没成"
    ⇒ `RecordNote` 与 `TradeSettlement.Finish` 各有一道"已结算优先"的闸，需要复位时用 `ResetNote`。
18. **单测共用一个世界号 = 真机不会出现的串档**：`EnsureWorld` 会跨用例缓存同一个 `EconomyWorld`，
    于是"上一组用例留下的幂等收据/每日计数/回声文本"会让下一条用例在第一道闸就被挡掉——
    排查时看起来像"接线没生效"，其实是**测试自己的状态没清**。
    规矩：**每条端到端用例要么用独立世界号，要么显式 `Clear` + `RememberPlayerTexts(…, [])` + `ResetNote`**。
19. **金额决策必须用"这一局的余额"算，不能写死**：同一个 `crowd` 档位在不同世界里的余额差一倍，
    写死 $10,000 会在一个世界里过闸、在另一个世界里撞 `PERSONALITY_CAP` 或 `DAILY_LIMIT`。
    本阶段的用例改成按 `余额 × 30%` 夹逼，并**断言预览金额等于预期金额**（否则"前置失败"会伪装成"结算失败"）。
20. **官方弹窗只有一个按钮的正规做法**：`NVerticalPopup.HideNoButton()` 是公开方法
    （用 `System.Reflection.Metadata` 读 `sts2.dll` 元数据确认，不必瞎猜），
    于是结果框不需要拿"取消"冒充"知道了"——**别用猜测的 API，读元数据只要 5 分钟**。
21. **测试输出目录里有没有 `config/` 是随机的**：主工程的 `config\**\*.json CopyToOutputDirectory`
    会（有时候）复制到测试输出目录，于是"测试输出目录下没有 config/，应拒绝启用"这种断言
    会在两次相同的命令之间时绿时红（本机实测：一次 498/498 绿、下一次 496/498 红）。
    ⇒ 凡是"某个目录不存在"的用例，**必须显式把路径传进去**（`Initialize(log, dataRoot, configRoot)`），
    不要依赖"当前工作目录/程序集目录恰好没有那个东西"。
22. **全局静态状态会跨套件泄漏**：`TradeMarkerInterceptor.Feed` 在模组已初始化的前提下会继续调用
    `TradePreviewWiring.HandleModelIntent`，于是"一次 Feed 恰好一条日志"这条断言测的其实是
    **上一个套件留下的 Runtime**。`Program.cs` 里 `TradeMarkerInterceptor` 因此必须排在
    `ModEntry` / `PlanAInjection` 这些会建立 Runtime 的套件**之前**（已加注释钉住）。
    —— 排查时先问"这条用例真的在测它声称要测的东西吗"。

---

## 11. 文档地图

| 文档 | 内容 |
| --- | --- |
| **`docs/HANDOFF-PHASE-4-STEP4.md`** | **下一步接手入口**：第三步的真机验收清单、线上状态、待办 |
| `docs/HANDOFF-PHASE-4-STEP3.md`（本文） | 第三步（UI 与账本真相通道）的完整记录：三项拍板的落点、实施清单、验收与坑位 |
| `docs/HANDOFF-PHASE-4-STEP2.md` | 第二步（真正动钱 + 审计落盘）的完整交接：12 轮真机记录、五次换版全哈希、§7 验收清单、§10 坑位 |
| `docs/PLAN-PHASE-4-STEP2.md` | 第二步方案全文（§8 甲方四项决策、§9 实施顺序） |
| `docs/HANDOFF-PHASE-4-TRADE.md` | 第一步交接 + 三轮真机反馈（意图补全 / 回声守卫 / 协议未教 / TTL） |
| `docs/HANDOFF-PHASE-3-PROBE.md` | 阶段 3：落点根因、收据裁剪、按需载入三大缺陷的证据链 |
| `deploy/DEPLOY.md` | 构建、部署、回滚、真机判据表 |
| `docs/HANDOFF-PHASE-0-2.md` | 阶段 0~2 完整记录 |

*本文档结束。与本地代码不符时以本地代码为准，并顺手更新本文档与 `docs/PLAN-PHASE-4-STEP2.md`。*
