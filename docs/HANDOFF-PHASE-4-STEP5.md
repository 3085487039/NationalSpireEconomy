# NationalSpireEconomy 阶段 4 第五步交接文档（`playerToNpc` + D + E）——**实施已完成，待真机验收**

> **本文用途**：让一个完全没有上下文的新会话读完本文就能接手**阶段 4 第五步**。
> **撰写时间**：2026-10-12（第三步真机验收通过之后）。**本文所有数字、哈希、代码行号均为本机实测。**
>
> **★ 2026-10-12 更新（第五步已实施）**：§3 的三个问题甲方已拍板
> （**1(a) 接受分叉 / 单笔上限=影子余额、单日沿用现有配置 / 3(a) 硬失败**），
> 范围定为 **A + D + E**，代码与测试都已经写完并全绿：
>
> | 项 | 状态 | 证据 |
> | --- | --- | --- |
> | **A. `playerToNpc`** | ✅ 实施完成 | `TradeSettlement.AllowedDirections` 白名单；`npc_to_npc` 仍被挡（有测试钉住） |
> | **A 的真机验收①** | ✅ **已通过**（2026-10-12 13:20，复验 13:34） | `o0003`+`o0004`：`Applied｜雇佣 $3,000`；玩家 `$195,020 → $192,020`；审计 `Direction = player_to_npc`、`Day = 452` |
> | **② 打脸日志消失** | ✅ **真机验证**（13:34:46.759） | `双方已确认，立即结算（trade.settlementWired = true）`，不再跟"尚未接线" |
> | **① 弹窗余额与实际扣款一致** | ✅ **真机验证**（`o0004`） | 预览与结算**逐字相同**：玩家 `$195,020 → $192,020`、`tour-c51-5 $809,000 → $812,000`（上一版差 $1,200） |
> | **D. 审计行补 `Day`** | ✅ 实施完成 **且真机验证** | 审计 4 行全部 `Day = 452`，**没有 `Day: 0`** |
> | **E. `确认` 误报 Warning** | ✅ 实施完成 **且真机验证** | 本次会话 `[Economy-Confirm]` 全为 Debug，**零假 Warning** |
> | 第 2 轮修复 | ✅ 实施完成 **且真机验证** | 预览余额改用影子余额（真机挖出的 bug）；确认时那两处"尚未接线"日志按开关分叉 |
> | 第 3 轮修复 | ✅ 实施完成，待换版顺带验 | 落单时那条"玩家如何确认…尚未接线"也按开关分叉（同类假话的**第 3 处**，见 §9 第 11 条） |
> | 零依赖基线 | ✅ **389/389** | 原 383，本步 +6 条 |
> | 含游戏引用基线 | ✅ **516/516** | 原 509，本步 +7 条（含"预览与结算同源"回归） |
> | 三种构建警告 | ✅ **0** | 含 `TreatWarningsAsErrors=true` 的严格构建 |
> | 新交付包 DLL | ⏳ 待换版 | `442880` 字节 / `7B593EF0799B9444EAD00D1E6A3D4985A367ADA7FB384CFFEBA6C2484EBEEC28` |
> | 线上 DLL（待换第 3 轮） | `442880` / `4F89ED60…`（**真机验收①②③ 就是用它跑通的**） |
>
> **下一步只剩两件**：① 甲方**让游戏完全退出**（判据见 §1.5.3 第 2 条）；
> ② 换第 3 轮版（`7B593EF0`），然后**收尾**：把 `settlementWired` 改回 `false` 并重启，
> 启动行应回到 `结算未接线（默认）`。**换版与收尾步骤都在 §6.5。**
>
> **先读这一句**：阶段 4 第一步（只读预览 + 双人确认）、第二步（真正动钱 + 审计落盘）、
> 第三步（账本真相通道）**都已真机验收通过**；第五步（`playerToNpc` + D + E）
> **已在 2026-10-12 13:20~13:34 完成真机验收 ①②③（玩家真的掉钱了、两个回合都逐字对账）**，
> 期间又挖出并修掉两个问题（预览余额来源、过时日志），现在只差**第 3 轮换版**把最后一处日志瑕疵带上线。
>
> **为什么还有第 3 轮**：验收①②③ **全部通过**，但同一句假话在工程里有 **3 处**，
> 第 2 轮只修了 2 处（见 §9 第 11 条）。第 3 轮是纯日志措辞，无行为改动。
>
> **为什么会有第 2 轮换版**：验收①**通过了**，但它同时暴露了"确认弹窗显示的余额与实际扣款不符"
> 这个真 bug（见 §9 第 10 条）。甲方 2026-10-12 拍板"先修这两个问题，再重新验收"。
>
> **上游文档**：第三步的验收记录（两个落盘 bug、三条证据链、9 条坑位）在 `docs/HANDOFF-PHASE-4-STEP4.md`；
> 第三步的实施记录（三项拍板落点、7.1~7.6、22 条坑位）在 `docs/HANDOFF-PHASE-4-STEP3.md`。

---

## 0. 三十秒速览

- **现状**：交易链路已完整闭环，双向都能动钱（`npc_to_player` 与 `player_to_npc`），
  只剩 `npc_to_npc` 被方向闸挡着；**代码已改完、基线全绿，待真机验收**。
- **本步候选工作**（按价值/成本排，见 §4）：**A + D + E 已实施**，B / C / F 未做。

| 项 | 一句话 | 估时 | 状态 |
| --- | --- | --- | --- |
| **A. `playerToNpc`（玩家出钱）** | 放开方向闸，让玩家能给 NPC 花钱 | **1–2 天** | ✅ **已实施**（不是删闸，是白名单） |
| **B. `[Deal: accept]` 解析** | 玩家发起、NPC 同意 | 0.5–1 天 | ⬜ 未做（本轮范围外） |
| **C. 玩家侧可见回执** | 让玩家**在界面上**看到"已结算" | C1 2–3h / C2 半天 / C3 2–4 天 | ⬜ 甲方说不需要 |
| **D. 审计行补 `Day`** | 老世界水位线为 0 时没有天数 | ≤30 分钟 | ✅ **已实施**（只对新行生效） |
| **E. `确认` 误报 Warning 收紧** | 玩家随口写"确认"被判成指令形状 | ≤30 分钟 | ✅ **已实施** |
| ~~F. 预览里玩家那行出现两次~~ | 甲方说先不急着改 | — | — |

- **最小可交付**：`D + E` —— 已含在本轮里。
- **最有价值**：`A` —— 已实施，等真机验收（§6.5）。

---

## 1. 线上现状（接手前先核对，30 秒）

```powershell
$dst = 'D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy'
(Get-Item "$dst\NationalSpireEconomy.dll").Length                        # 期望 441344
(Get-FileHash "$dst\NationalSpireEconomy.dll" -Algorithm SHA256).Hash     # 期望 C6F89483…
(Get-Content "$dst\config\economy_config.json" -Encoding utf8 | Select-String 'settlementWired')
```

| 项 | 值 |
| --- | --- |
| 线上 DLL（**第 2 轮，验收①②③ 已通过**） | `442880` 字节 / `4F89ED6004A754AE5D7BE3421BF5B4AEE5EB1E39E7A10475A7A21BF8455A6BA5` |
| **交付包 / `bin`（第 3 轮，待上线）** | **两处一致**：`442880` 字节 / `7B593EF0799B9444EAD00D1E6A3D4985A367ADA7FB384CFFEBA6C2484EBEEC28` |
| 动钱开关 | 线上 `trade.settlementWired = **true**`（**验收临时开着**，收尾必须改回 `false`）；交付包里是 `false` |
| 方向闸 | **已开通 `npc_to_player` 与 `player_to_npc`**（两者都能真的动钱，由 `TradeSettlement.AllowedDirections` 白名单定义）；`npc_to_npc` 仍被 `DirectionNotWired` 挡下，**一分不动** |
| 基线（**新基线**） | 零依赖 **389/389**、含游戏引用 **516/516**；三种构建 **0 警告** |
| 世界 | `e35d0119`（1638 人、第 452 天 / 第 6 赛季） |
| 余额（验收①之后） | 玩家影子 **$195,020**（花了 $3,000）、`pro2_5` **$824,000**、`fan1_1` **$6,300**、`crowd-5-4` **$4,500** |
| 审计 | `audit\e35d0119.jsonl` **3 行**（`o0001` $500、`o0002` $700、**`o0003` $3,000 `player_to_npc`**） |
| 交易单 | `Offers`：`o0001` / `o0002` / **`o0003`** 都是 `confirmed`（o0003 是玩家出钱那笔） |
| 回滚备份 | `NationalSpireEconomy.old-20261012-130638.dll`（`C6F89483`，第三步那版）+ config 备份 |
| `[Error]` | 全程 **0** |

> **注意**：本次基线跑完 `bin` 里已经是**第 3 轮 DLL**（`7B593EF0`，见 §9 第 4 条：
> 含游戏引用那轮会重建全量 DLL）。`deploy/NationalSpireEconomy/` 已由 `build-deploy.ps1`
> 重新打包成同一个哈希。

---

## 1.5 环境与雷区速查（**新会话必读**：这些不写在别处，是环境性的）

### 1.5.1 DSH 沙箱环境（本项目在本机跑测试的**唯一姿势**）

```powershell
$h = 'D:\蓝色大肥鱼\sandbox\dotnethome'          # 沙箱化的 dotnet 家目录（不在工作区内）
$env:APPDATA = "$h\Roaming"; $env:USERPROFILE = $h
$env:DOTNET_CLI_HOME = $h; $env:NUGET_PACKAGES = "$h\packages"; $env:DOTNET_NOLOGO = '1'
```

- **每一条要跑 `dotnet` 的命令都必须先设这四个变量**，否则会把 NuGet 缓存写进真实用户目录；
- 本机 **NuGet 出网被拦**，所以不能加新包（`MetadataLoadContext` 就是因为这个用不了，
  读 `sts2.dll` 元数据要改用 `System.Reflection.Metadata` + `PEReader`，见 `docs/HANDOFF-PHASE-4-STEP4.md` §7 第 4 条）。

### 1.5.2 本机工具的确切路径

| 工具 | 路径 |
| --- | --- |
| Python（**只用来读/写 JSON、查存档**） | `C:\Users\Li\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe` |
| `pick-sponsor.py`（挑人 + 算金额窗口 + id→人名） | `D:\蓝色大肥鱼\NationalSpireEconomy\tools\pick-sponsor.py` |
| 一键打包 | `D:\蓝色大肥鱼\NationalSpireEconomy\build-deploy.ps1` |

⚠ **`pick-sponsor.py` 必须读真实存档**，所以跑它时**不要**把 `APPDATA` 指到沙箱：

```powershell
$env:APPDATA = 'C:\Users\Li\AppData\Roaming'      # ← 与跑测试时相反！
& 'C:\Users\Li\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe' `
   'D:\蓝色大肥鱼\NationalSpireEconomy\tools\pick-sponsor.py' --top 10
# 只诊断某个人（窗口算不出来时会直接告诉你原因）：
& ...\pick-sponsor.py --npc <PersonId>
```

**终端中文会显示成乱码**（GBK 控制台），这是显示问题不是数据问题。要读干净中文：

- `python` 前面加 `$env:PYTHONIOENCODING='utf-8'`，或把脚本写成 `.py` 文件再执行（不要用 `-c` 加内联中文）；
- PowerShell 里用 `[System.IO.File]::ReadAllText($p)` 读，别指望 `Get-Content` 的编码；

### 1.5.3 沙箱里做不到的三件事（别浪费时间试）

1. **改沙箱外的文件**（线上 `mods\…`、`%APPDATA%\SlayTheSpire2\…`）：
   会以 `[sandbox: file access denied under workspace-write mode]` 失败 ——
   需要**一次授权升级**（`danger-full-access`）并在 `justification` 里写清目的；
2. **看到/杀掉用户的游戏进程**：`Get-Process` 在沙箱里查不到游戏；
   **判断"游戏是否已退出"的唯一正确判据是"线上 DLL 能否独占写"**：

   ```powershell
   $dll = 'D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy\NationalSpireEconomy.dll'
   try { $fs=[System.IO.File]::Open($dll,'Open','ReadWrite','None'); $fs.Close(); '未运行' } catch { '在运行' }
   ```
3. **在写文件的同时让游戏启动**：换版必须在"游戏已退出"的窗口里做完；
   真机踩过 —— 人退出、工具还没换完，游戏又起来了，于是加载的是旧 DLL（时序问题，见 §9）。

### 1.5.4 明确的"弯路清单"（都付过学费，别重走）

| 别做 | 为什么 |
| --- | --- |
| ❌ 改原版源码 / DLL / **原版存档** | 铁律 1；影子余额与原版 `Credits` 分叉时也**不要**去同步（见 §3 问题 1） |
| ❌ 靠"V 段"让玩家看到东西 | **真机已证明 V 段不面向 UI**（它只进发给模型的请求）——任何"让玩家看到"的需求必须走 UI 通道 |
| ❌ 用 `Console.WriteLine`/临时打印做诊断 | 这个项目有正式日志：`IEconomyLog` + 文件保底 `mod-debug.log`，比到处 `println` 好排查 |
| ❌ 用 `Set-Content -Encoding utf8` 写 JSON 配置 | 老 PowerShell 会加 BOM，且会把 `0.40` 改成 `0.4`、把数组拆行（真机改配置时踩过）——用 Python `json` 读写，改完**压平成键值逐键比对** |
| ❌ 期待"两条基线一起绿"就没事 | 上一阶段有**两次单测全绿而真机失效**：判据要用"**重新读盘**"（`EveryTruthWriteSurvivesAReload` 就是这么来的） |
| ❌ 只按自己设想的出口写测试 | 真机走的出口可能是"提前 return"那条（`ReadyToSettle == false` ⇒ `TrySettle` 根本不被调用）——问"玩家怎么操作会走到哪一行" |
| ❌ 把"阶段范围的拍板"当口号 | 每条拍板都要在代码里找到那个"**不许过**"的判定点，否则它只是愿望 |

### 1.5.5 验收只看两组数字

- 零依赖 **383**、含游戏引用 **509**（**零依赖先跑、含游戏引用最后跑**）；
- 真机判据只看三处：`mod-debug.log`、`worlds\<WorldId>.json` 的 **`LastTrade` 键**、`audit\<WorldId>.jsonl`。

### 1.5.6 怎么把现场证据给 AI

- **直接把文件路径用 `@` 引用**即可（例如 `@C:\Users\Li\AppData\Roaming\SlayTheSpire2\national_spire_economy\mod-debug.log`），
  比在对话里粘一大段日志更可靠；
- **`probe\path-probe.log` 很大**（20 MB+，逐帧轮询）⇒ 永远不要整份读，用 `Select-String` 过滤关键词；
- 游戏内截图也直接贴，AI 能读图（第三步的"V 段不面向 UI"就是靠截图推翻的）。

---

## 2. 接手后要做的三件事（**代码已写完，所以只剩"验收"**）

### ① 跑两套基线（约 2 分钟，确认起点是绿的）

```powershell
$h = 'D:\蓝色大肥鱼\sandbox\dotnethome'
$env:APPDATA = "$h\Roaming"; $env:USERPROFILE = $h
$env:DOTNET_CLI_HOME = $h; $env:NUGET_PACKAGES = "$h\packages"; $env:DOTNET_NOLOGO = '1'
$root = 'D:\蓝色大肥鱼\NationalSpireEconomy'
& dotnet run --project "$root\tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj" -c Release
& dotnet run --project "$root\tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj" -c Release -p:GameRefsEnabled=true
```

期望 **389/389** 与 **516/516**（本步之前是 383 / 509）。**顺序是硬约束：零依赖先跑、含游戏引用最后跑**
（反了会红两条"环境敏感"用例，见 `docs/HANDOFF-PHASE-4-STEP4.md` §7 第 6 条）。

### ② 核对线上状态与**待上线的那个包**（见 §1 的两组哈希）

线上是第 2 轮的 `4F89ED60`、`bin`/`deploy` 是第 3 轮的 `7B593EF0` —— 这是**预期状态**（第 3 轮还没换版）。
若哈希不符：换版步骤与回滚哈希表在 `docs/HANDOFF-PHASE-4-STEP4.md` §4；**换版前必须让游戏完全退出**
（判据是"目标文件能否独占写"，不是"有没有同名进程"）。

### ③ **让甲方把游戏完全退出，然后换版 + 按 §6.5 做真机验收**

§3 的三个问题**已经拍板完毕**（见下），不用再问。

---

## 3. 甲方拍板结果（**2026-10-12 已定，不用再问**）

> 背景：放开 `playerToNpc` 之后**玩家的钱会真的减少**，而玩家的钱来自
> **阶段 4 第二步一次性从原版只读导入的 `Credits + CreditFraction`**（见 §5 代码地图）。
> 影子钱包从此与原版数字**各走各的**，所以下面三条必须有人拍板。

| # | 问题 | **拍板结果** | 落地位置 |
| --- | --- | --- | --- |
| **1** | 玩家花掉的钱与原版 `Credits` 的关系？ | **1(a) 接受分叉**：影子是唯一真相，原版数字仅供参考。**绝不写原版存档** | `ShadowPlayerWallet` 只管影子；代码里**没有**任何回写原版的路径 |
| **2** | 玩家一次能动多少？ | **单笔上限 = 影子余额**（不是新配置，就是余额闸的语义）；**单日上限沿用现有 `trade.dailyLimitPerNpc: 3`**（按 NPC 计数，玩家侧不另立计数器） | `EconomyLedger.Transfer` 第 249 行的余额闸；`DailyLimitValidator` |
| **3** | 玩家余额不足时？ | **3(a) 硬失败 + 明确告知**（`InsufficientFunds`），**不允许欠款/负余额** | `EconomyLedger` 第 88 行"不产生负余额"不变量；`TransferExecutor` 的软失败分支**只对 NPC 出钱生效** |

**范围**：本轮做 **A + D + E**（B / C / F 不做）。

---

## 4. 逐项实施方案与验收

### A. `playerToNpc`（玩家出钱）—— ✅ **已实施**

**关键发现（现在已被证实）**：**结算路径本来就写完了**，缺的只有方向闸那一条判定。

| 已经就绪的东西 | 位置 | 说明 |
| --- | --- | --- |
| 方向分支 | `src/Trade/Executors/TransferExecutor.cs` **第 43 行** `bool fromPlayer = offer.Direction == TradeDirections.PlayerToNpc;` | 两条腿的方向、对手方 id、流水条目都已按 `fromPlayer` 分开写 |
| 余额不足闸 | `src/Economy/EconomyLedger.cs` **第 249 行** `if (fromPlayer && wallet.BalanceUsd < amount)` | 玩家钱不够时返回 `InsufficientFunds`，**改动前不改任何数据** |
| 双侧结果 | 同文件第 225 行 `Transfer(...)` 返回 `TransferResult{NpcSide, PlayerSide}` | 一侧失败会走回滚（`BothApplied` 判定） |
| 出账能力 | `src/Economy/ShadowPlayerWallet.cs`（`Debit` / `Apply`，含"未导入就拒绝动钱"纪律 —— 第 145 行 `if (!node.Imported)`） | 负数支持已有 |
| 回滚与幂等 | 第二步已验收过（`ReceiptBook.Unmark` + 失败释放键） | 这套逻辑对两个方向通用 |

**实际改动清单（5 个源文件 + 3 个测试文件）**：

1. **方向闸改成白名单**：`src/Trade/TradeSettlement.cs`
   - 新增 `TradeSettlement.AllowedDirections = [npc_to_player, player_to_npc]` 与 `IsAllowedDirection(..)`；
   - 原第 156 行那条"只等于 `NpcToPlayer` 才放行"改成 `if (!IsAllowedDirection(offer.Direction))`。
   ⚠ **没有整条删掉**——删了就会放开第三个方向 `npc_to_npc`（有 `NpcToNpcDirectionIsStillNotWired` 钉住）。
2. **回执文案按方向渲染**：`src/Trade/TradeNoteText.cs`
   - `ReceiptLine`：`player_to_npc` 走「你 → NPC …（减少）/（增加）」那一支；
   - `NotSettledLine` / `LedgerSummary` / `RecentSettlements` 的"谁给谁"统一走新的私有 `PlayerPaid(direction)`。
   （原先 `ReceiptLine` 把「（{who} → 你）」写死了，放开反向之后那句话会**骗玩家**。）
3. **D 项：审计行 `Day` 三级兜底**：`TradeSettlement.DayForAudit(ctx, offerId)`
   —— `ctx.Day > 0` 就用它，否则用单子的 `CreatedDay`，再否则用水位线。**只对新行生效**。
4. **审计口径收紧**（顺带发现的必要修正）：审计行**只在 `Applied` / `AlreadyApplied` 两条出口写**
   —— `Failed` 也写过一行，但那样 `trade_settled` 这一行就不再等于"钱动过"，
   而 V 段的回读与"对账的人"都假设它等于。真机验收表 §6 第 ② 条的判据正是"audit 不新增"。
5. **E 项：`确认` 误报收紧**：`src/Trade/PlayerTradeCommand.cs`
   - `LooksLikeCommandAttempt` 从"全文包含"改成**与 `Parse` 同源的行首判定**（两处共用私有 `MatchLine`）；
   - 于是真机 `12:28:08` 那句"**没收到哥哥，你再确认一下**"不再刷 Warning。

**测试（+6 条，全部零依赖）**：

| 新增/改写 | 钉住什么 |
| --- | --- |
| `TradeSettlementTests.PlayerToNpcDirectionNowSettlesAndMovesMoney`（**取代** `PlayerToNpcDirectionIsNotWiredYet`） | 玩家减少 $10,000、NPC 增加 $10,000、两侧流水与幂等键都对 |
| `PlayerToNpcWithInsufficientFundsMovesNothing` | 硬失败、**两侧余额一字不动**、**audit 不新增**、幂等键被释放、单子置 invalid、结论落盘 |
| `PlayerToNpcSecondSettleIsIdempotent` | 重复结算 ⇒ `AlreadyApplied`，一分不动 |
| `NpcToNpcDirectionIsStillNotWired` | **本步的边界**：第三个方向仍然不许过（防"把闸删掉"） |
| `PlayerToNpcReceiptAndAuditSayTheRightDirection` | 回执说「你 → NPC」+「减少/增加」；审计 `Direction = player_to_npc`；回读行标「你支出」 |
| `AuditDayFallsBackWhenWaterLineIsZero` | 水位线 0 时审计写 `Day: 452` 而不是 `0` |
| `TradeNoteTextTests.PlayerToNpcReceiptSaysYouPaidNotNpcPaid` | 文案按方向；反方向行为不被改坏 |
| `TradeNoteTextTests.PlayerToNpcNoteCarriesDirectionAndNpc`（**取代** `DirectionNotWiredNoteCarriesDirectionAndNpc`） | 结论带方向与对手方，且现在是"真的结算" |
| `PlayerTradeCommandTests.LooksLikeCommandAttemptTellsSilenceFromNotUnderstood`（改写） | 行首判据；三句真机聊天文本都**不再**刷 Warning |

**仍然成立的风险（要让甲方知道）**：

- **这是本步唯一"玩家会掉钱"的改动** —— 开关默认 `false`，所以真机验收必须显式打开、验完关回去；
- **影子与原版分叉**（§3 问题 1(a)）：玩家花掉 $X 之后，**游戏界面上的原版数字不会变**。
  **这不是 bug，是甲方拍板的设计选择**；
- **软失败语义**：玩家出钱不足走的是**硬失败**（`TransferExecutor` 的软失败分支要求
  `NpcSide.Status == InsufficientFunds`，而玩家出钱不足时 NPC 侧是 `Rejected`）——有测试钉住。

### B. `[Deal: accept]` 解析 —— **0.5–1 天**（**先论证收益**）

**现状**：`src/Trade/PlayerTradeCommand.cs` 第 31 行的 `ConfirmWords` **已经包含**
`"[deal: confirm"` / `"[deal:accept"` / `"[deal: accept"`，也就是说**解析已经在里面了**。

**真正缺的是"玩家发起交易"这条流程**：

- 现在 `HandleModelIntent` 处理的是**模型**的标记（NPC 提出）；
- 玩家在私信里写 `[Trade: 雇佣, Amount: 3000]` 目前**不会被当成意图**（只被当作正文）；
- 协议文字（`config/ai_prompt_economy.json` 的 `stableBlockSuffix`）里已经教了模型
  "对方提出的交易你若同意，就在回复里再写一行 `[Deal: accept]`"。

**所以要先问清**：甲方要的是
**(a)** 玩家发标记 → 我们自己落单（不经模型）；还是
**(b)** 玩家发标记 → 只把它当"提议"，等模型写 `[Deal: accept]` 才算 NPC 同意？

**注意**：`npc_to_player` 方向上 **NPC 不需要 `[Deal: accept]`** —— 模型写 `[Trade: …]` 本身就等于它同意
（`HandleModelIntent` 里 `TradeConfirmationGate.Confirm(..., Npc, …)`），这条已经真机验证过。
所以 B 的价值只在"玩家发起"这条流程上。

### C. 玩家侧可见回执 —— **三选一**（甲方 2026-10-12 说"不需要"，除非改主意）

**背景（真机已确认的两条死路）**：

1. **V 段不面向 UI**：注入的是"发给模型的 messages 数组"，不是私信窗口渲染的那份记录 ——
   玩家在界面上**看不到** `【当前经济】`/`【已结算】`（详见 `docs/HANDOFF-PHASE-4-STEP4.md` §2）；
2. **结果框被私信模态挡住**：`12:31:16 [Economy-UI] 已有别的模态在开着，不抢（结果只写进 V 段与日志）`。

| 选项 | 做法 | 估时 | 风险 |
| --- | --- | --- | --- |
| **C1** | 结算后**延迟/重试**弹结果框（等模态关掉；`TradeConfirmDialog.TryShowResult` 已有，加个重试队列即可） | **2–3 小时** | 什么时候算"模态关掉了"要试；可能迟一点 |
| **C2** | 玩家**离开私信窗口**时弹一次"刚才结算了 X"——需要接一个新钩子（原版面板关闭/重新打开） | **0.5–1 天** | 要找挂载点（`PathProbe` 那套探针可以帮上忙） |
| **C3** | **自绘**常驻小面板（`CanvasLayer` + `Label`，读 `LastTrade`/`audit` 显示最近结算） | **2–4 天** | 本机**无自绘 UI 先例**；原版注释点名过"独立窗口会触发失焦静音"，所以布局/遮挡/焦点都要试 |

### D. 审计行补 `Day` —— **≤30 分钟**

`EconomyAudit.Append` 里 `Day` 取自调用方；老世界水位线为 0 时会写 `Day: 0`
（真机 `audit\e35d0119.jsonl` 第 2 行是正常的 `452`，第 1 行是旧版写的）。改动很小，
但**改历史数据要用迁就脚本还是只对新行生效**，要顺手定一下。

### E. `确认` 误报 Warning 收紧 —— **≤30 分钟**

真机实例（`12:28:08`）：玩家正文写"**你再确认一下**"，命中
`[Economy-Confirm] 玩家文本里出现确认/拒绝字样，但没能解析成指令（原版可能包了壳）` ——
**只多一行 Warning，没有任何副作用**（没落单、没动钱）。

修法：`PlayerTradeCommand.LooksLikeCommandAttempt` 可以收紧成"**行首**出现指令词"
（它本来就只认行首解析），或在"本行是疑问句/包含'一下/再'等"时降级为 Debug。改动极小。

### F. 预览里玩家那行出现两次 —— **甲方说先不改**

`TradePreviewBuilder.Compose` 会输出"对手方"与"你"两行，`player` 两边都出现。
纯文案问题，不阻塞。

---

## 5. 代码地图（本步要碰的地方）

| 文件 | 本步要动的点 |
| --- | --- |
| **`src/Trade/TradeSettlement.cs`** | **A 的核心**：`AllowedDirections` 白名单 + `IsAllowedDirection`；方向闸（原第 156 行）；**D 的 `DayForAudit`**；审计只在 `Applied`/`AlreadyApplied` 写；`Finish` 的落盘清单 |
| `src/Trade/Executors/TransferExecutor.cs` | 第 43 行 `fromPlayer` 分支（**已就绪，没改**）；第 57 行 `ledger.Transfer(...)`；软失败分支只对 NPC 出钱生效 |
| `src/Economy/EconomyLedger.cs` | 第 225 行 `Transfer`；**第 249 行的玩家余额不足闸**（= 甲方拍板的"单笔上限"）；第 88 行"不产生负余额"不变量 |
| `src/Economy/ShadowPlayerWallet.cs` | `Debit`/`Apply`/`Credit`；"**未导入就拒绝动钱**"纪律；`ReadOnlySnapshot` |
| `src/Game/ShadowEconomyRuntime.cs` | `ImportPlayerWalletOnce`（**玩家钱的唯一来源**，只读导入 `Credits + CreditFraction`） |
| `src/Trade/PlayerTradeCommand.cs` | 第 31/34 行指令词；**E 的收紧点**：`LooksLikeCommandAttempt` + 与 `Parse` 共用的私有 `MatchLine` |
| `src/Inject/EconomyAudit.cs` | `AuditRecord.Day`（D 写入端；本步**没改这个文件**） |
| `src/Game/TradeConfirmDialog.cs` | `TryShowResult` / `TryAsk`（C1/C2 要复用它） |
| `src/Trade/TradeNoteText.cs` | **A 的文案改动**：回执与"未成立"的措辞按 `PlayerPaid(direction)` 分叉 |
| `src/Game/TradePreviewWiring.cs` | `HandleModelIntent` / `HandlePlayerCommands` / `ApplyPlayerAnswer` / `NoteClosedTrade`（**真相写入的三条路**） |

---

## 6. 真机验收（A 项的判据）

**前置**：把线上 `config\economy_config.json` 的 `trade.settlementWired` 改成 `true` 并**重启游戏**
（启动行应出现 `★结算已接线`）；**验完必须改回 `false`**（甲方拍板），改完再重启一次收尾。

| # | 操作 | 判据 |
| --- | --- | --- |
| ① | 让玩家给某个 NPC **花一笔**（玩家发起或 NPC 提议都行，按 B 的拍板） | `[Economy-Settle] …｜Applied｜…｜玩家 $A → $B（减少）｜NPC $C → $D（增加）`；`audit` 新增一行且 `Direction = player_to_npc`；存档 `LastTrade.Status = settled` |
| ② | 玩家钱**不够**时再来一笔 | `Failed` + `InsufficientFunds`；**两侧余额一字不动**、`audit` 不新增；`[Error]` 0 |
| ③ | 同一单**再确认一次** | `AlreadyApplied`；余额与 audit 不动 |
| ④ | 收尾 | 开关改回 `false` → 重启 → 启动行 `结算未接线（默认）` |

**核心判据文件**（照第三步的规矩）：

- `%APPDATA%\SlayTheSpire2\national_spire_economy\mod-debug.log`（第一现场）；
- `…\worlds\<WorldId>.json` 里的 **`LastTrade` 键** —— **落盘与否只看这个键**；
- `…\audit\<WorldId>.jsonl` —— 每笔结算一行，含 `Direction` 与双方余额前后。

---

## 6.5 换版 + 验收的操作步骤（**本步唯一剩下的事**）

### 步骤 0：让游戏完全退出（**硬前置**）

唯一正确判据是"线上 DLL 能否独占写"（沙箱里看不到游戏进程）：

```powershell
$dll = 'D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy\NationalSpireEconomy.dll'
try { $fs=[System.IO.File]::Open($dll,'Open','ReadWrite','None'); $fs.Close(); '未运行' } catch { '在运行' }
```

**实测（2026-10-12）**：此刻状态是**在运行** —— 所以换版必须等甲方退出游戏。

### 步骤 1：备份线上 DLL 与 config（**回滚的命根子**）

线上目录里已有历次 `.old-*` 备份，照同一个命名习惯再留一份：

```powershell
$dst = 'D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
Copy-Item "$dst\NationalSpireEconomy.dll" "$dst\NationalSpireEconomy.old-$stamp.dll"
Copy-Item "$dst\config\economy_config.json" "$dst\config\economy_config.json.bak-before-step5-$stamp"
```

### 步骤 2：换 DLL（**用交付包那一份**）

```powershell
Copy-Item 'D:\蓝色大肥鱼\NationalSpireEconomy\deploy\NationalSpireEconomy\NationalSpireEconomy.dll' $dst -Force
(Get-Item "$dst\NationalSpireEconomy.dll").Length          # 期望 442880
(Get-FileHash "$dst\NationalSpireEconomy.dll" -Algorithm SHA256).Hash   # 期望 7B593EF0…
```

⚠ **不要覆盖线上 config**（§8 第 7 条）：`settlementWired` 是验收时临时拧的那个旋钮，
线上那份与交付包**逐键语义一致**但不必物理替换。

### 步骤 3：**用 Python 改线上 config 的 `settlementWired` 为 `true`**（不要用 `Set-Content`）

见 §1.5.4 第 4 条（`Set-Content -Encoding utf8` 会加 BOM、把 `0.40` 写成 `0.4`、把数组拆行）。

### 步骤 4：启动游戏，看启动行

- 期望 `★结算已接线（配置 trade.settlementWired）`；
- 若看到 `结算未接线（默认）` ⇒ config 没生效或改错了文件，**先别继续**。

### 步骤 5：按 §6 的 ①②③ 逐条验收（**判据表就是那张表**）

补充说明：

- ① 要让玩家给 NPC 花钱，走**现在唯一能自动落单的那条路**：让模型（NPC）发出
  `[Trade: 雇佣, Amount: N]` 或 `[Trade: 转账, Amount: N]`（这两个 Kind 在
  `trade.kindDirections` 里映射成 `playerToNpc`），然后玩家在私信里回 `确认 <单号>` 或点弹窗确认。
  **注意**：`o0001` / `o0002` 这两张老单都已经 `confirmed`，要**新的单号**；
  - ⚠ **★ 玩家发的那句话里绝对不能出现标记文本**（2026-10-12 13:14 真机踩到）：
    否则模型会原样复读，被 `LooksLikePlayerEcho` 判成"引用玩家的话"而**不落单**。
    正确姿势：**只给自然语言（金额 + 用途）**，例如
    「我雇你一小时，3000，你写一行交易标记给我确认。」——让模型自己把标记拼出来。
  - 建议用 **`雇佣`**：施舍门槛只审视 `打赏/赞助/转账` 三个 Kind，`雇佣`天然是"付费买服务"，少一道闸；
  - 被性格拒一次会记 `LastRefusedCharityDay`，**同一个人 7 天内更冷** —— 换人比反复磨有效；
- ② "钱不够"那一条：可以先花一笔把影子余额压低，再让模型报一个超过余额的 `playerToNpc` 金额
  （注意 `maxSingleAmount: 200000` 与性格上限会先拦下来，必要时把余额算准）；
- ③ 同一单再确认一次 ⇒ `[Economy-Settle] …｜AlreadyApplied`。

### 步骤 6：收尾（**必须做**）

把 `settlementWired` 改回 `false` → 重启 → 启动行必须是 `结算未接线（默认）`。

### 回滚

把 `NationalSpireEconomy.old-<stamp>.dll` 复制回 `NationalSpireEconomy.dll` 即可回到第三步那版
（`441344` / `C6F89483…`）；它的对应 config 备份是 `economy_config.json.bak-before-step5-<stamp>`。

### 验收时重点盯的三件事（都是本次改动的新面）

| 面 | 期望 |
| --- | --- |
| 回执文案 | `【已结算】雇佣 $N（你 → <NPC>）｜你的影子余额 A → B（减少）｜<NPC> C → D（增加）` |
| 审计行 | 新增一行，`Direction = "player_to_npc"`，`Day` **不是 0**，`PlayerBalanceAfterUsd` 比 Before **小** |
| 聊天噪音 | 玩家随口写"你再确认一下"时**不再**出现 `[Economy-Confirm] 玩家文本里出现确认/拒绝字样…` 那条 Warning |

---

## 7. 铁律与纪律（前几步的，**本步继续有效**）

1. **绝不改原版**（不改源码/DLL、**不写原版存档**，只读）；
2. 影子存档按 `WorldId` 分片，绝不串档；
3. `MoneyUnits` 是唯一换算出口（影子内一律**美元整数**）；
4. 余额变动一律走 `EconomyLedger` + 幂等键，重复调用不重复发钱；
5. **确认（投票）绝不动钱** —— 投票路径用只读快照 `InMemoryPlayerWallet`，动钱只能用 `ShadowPlayerWallet`；
6. **结算的前置四闸**：`SettlementWired` → 方向 → 双方确认 → 单子仍 pending 且未过期；任一不满足**一个数字都不改**；
7. **结算后必须落盘**（`force:true`）**并写审计**；审计先写；
8. **幂等**：`trade:{world}:{offerId}`；失败路径要**释放**整笔幂等键（`ReceiptBook.Unmark`）；
9. **凡是"阶段范围/开关"的拍板，都要在代码里找到那个"不许过"的判定点** —— 否则它只是一句愿望；
10. **写了真相就必须落盘**：现在由不变量用例 `EveryTruthWriteSurvivesAReload` 守着
    （**只从磁盘读**来判，覆盖三条写路径）—— 新加真相出口时**必须**让它继续绿。

---

## 8. 本步**不许**顺手做的事

1. **不要因为 A 而放开 `npc_to_npc`**：那条路径（转会费/工资/奖金）是另一套业务，没验收过；
2. **不要改 `settlementWired` 的默认值**（`SettlementSwitchIsOffByDefault` 会拦下）；
3. **不要把玩家影子余额"同步"回原版存档** —— 那是铁律 1 的红线（除非甲方明确豁免，见 §3 问题 1(b)）；
4. **不要为了 UI 去改原版本地化词条**；
5. **不要在验收中途重打包/换版**：一次真机会话只对应一个 DLL 哈希；
6. **不要在打包之后又跑零依赖测试**（会把 `bin` 覆盖成缩减版）；
7. **不要覆盖线上 `config\economy_config.json`**：它现在与交付包一致，但 `settlementWired` 是"验收时临时开"的那个旋钮。

---

## 9. 新踩过的坑位（第三步 + 第五步）

完整 9 条在 `docs/HANDOFF-PHASE-4-STEP4.md` §7，这里列与本步最相关的：

1. **"我测过的那条路"≠"真机走的那条路"** —— 第三步同一个 bug 因此漏了两次：
   补在了 `TradeSettlement.Finish`（真机走"还没到齐就提前 return"，根本不经过它）。
   本步动钱路径更多（四个闸 + 两条腿），**每条出口都要有测试，且优先用"重新读盘"来判**；
2. **注入进 Prompt ≠ 显示在界面上**（V 段的教训）—— 任何"让玩家看到"的需求都必须走 UI 通道；
3. **单测共用一个世界号会造成真机不会出现的串档**（幂等收据 / 每日计数 / 回声文本跨用例残留）——
   端到端用例要么用独立世界号，要么 `Clear` + `RememberPlayerTexts(…, [])` + `ResetNote`；
4. **两套基线的先后顺序是硬约束**：零依赖先、含游戏引用最后，跑完核对 `bin` 哈希 == 交付包。

### 第五步新踩到的（6 条）

5. **同一条出口的"身份"会因为筛选条件变化而整体错位** —— 本步写
   `AuditDayFallsBackWhenWaterLineIsZero` 时第一版用的是"同一张单结算两次"，
   结果第二次走的是 `TradeOfferStates.Confirmed` 那条**幂等出口**（第 ③ 步就 return 了），
   **根本不经过写审计的那一段** —— 测了个空。
   教训：**断言之前先问"这个场景会走到我想要的那一行吗"**（§1.5.4 第 6 条的同一条病）；
   改法是用两个独立世界分别造两次"真的结算"。
6. **`Audit` 那一行原先连 `Failed` 也写**，于是 `trade_settled` 不再等于"钱动过"，
   而 V 段的回读与对账的人都假设它等于。放开 `playerToNpc` 时顺手收紧了它
   —— **判据是"读者怎么理解这一行"，不是"写的时候方不方便"**；
7. **诊断日志的判据必须与解析器的判据同源**：`LooksLikeCommandAttempt` 原来是"全文包含"，
   而 `Parse` 是"行首"——两者不一致就会在真机上刷出假的 Warning。
   改法是让它们共用同一个私有 `MatchLine`，于是"Warning 会不会出现"这个问题可以直接读出来。
8. **★ 真机验收现场（2026-10-12 13:14）踩到的最大一条：模板里绝不能出现标记文本。**
   第一版验收模板让玩家这样发消息：「…请用 `[Trade: 雇佣, Amount: 3000, Direction: playerToNpc]`」，
   于是模型把那行**原样复读**出来 —— 日志里标记**被正确识别**
   （`识别出交易标记（第 1 条）：雇佣 $3,000`），却立刻被回声保护拦下：
   `[Economy-Preview] 这个交易标记与玩家本轮说的话逐字相同（模型只是在引用），已按「NPC 并未提议/同意」处理，不落单。`
   **这不是 bug，是第二步刻意做的保护**（真机原话"这行字你从哪抄来的"）。
   正确姿势：**玩家只给自然语言（金额 + 用途），让模型自己把标记拼出来**。
   连带结论：**"玩家自己打 `[Trade: …]` 让模组落单"这件事目前做不到，那正是未做的 B 项**。
9. **原版会把 `[Trade: …]` 当普通正文显示并存进聊天历史**（`Trade` 不在原版的标记白名单里，
   见 `TradeProtocol.cs` 开头）。所以玩家一旦打过标记，那串机器语法就会留在界面上和后续上下文里，
   模型下一轮可能又去复读它。**验收时换人比反复磨有效**。
10. **★ 一个"数字来源写错"的 bug：预览用原版余额、结算用影子余额。**
    真机验收①（2026-10-12 13:19）弹窗写 `player: $196,820 → $193,820`，
    而结算日志写 `玩家 $198,020 → $195,020` —— **差 $1,200**。
    根因：`TradePreviewWiring.BuildContext` 用 `PlayerWallet`（原版 `Credits`），
    而 `BuildContextForSettlement` 用 `ShadowPlayerWallet`；两者按甲方 1(a) 合法分叉，
    且原版那两个字段我们**只读不写** ⇒ 弹窗那个数字**永远停在导入那一刻**。
    **它不只是显示问题**：同一个数字是 `BalanceValidator` 判"钱够不够"的依据。
    修法：新增 `TradePreviewWiring.WalletFor(…)`，已导入时一律用
    `ShadowPlayerWallet.ReadOnlySnapshot()`（只读快照 ⇒ "绝不动钱"回到类型层面）。
    回归测试：`PlanAInjectionTests.PreviewAndSettlementUseTheSamePlayerBalance`
    （刻意让原版是 $0、影子是 $100,000，两个数字无法蒙混）。
11. **一条"开关打开后就是假话"的日志**：`ReadyToSettle` 为真时原先**无条件**拼上
    `SettlementIsNextPhase`（"…尚未接线…一分钱不动"），而开关打开时紧接着结算就真的发生了
    —— 真机日志同一瞬间出现互相打脸的两行
    （`13:20:00.052 …尚未接线` / `13:20:00.060 [Economy-Settle] …Applied`）。
    修法：`TradePreviewWiring.SettlingLine(offerId)` 按 `SettlementWired` 分叉。
    **教训：日志也是产品的一部分，对着"当时的状态"写，不要对着"写代码那一刻的状态"写。**

---

## 10.5 新会话开场怎么发（**直接复制这段**）

> 接手 NationalSpireEconomy 阶段 4 第五步。先读 `docs/HANDOFF-PHASE-4-STEP5.md`（同目录），
> 它是本次的接手入口；需要背景时再按它的 §10 往上读。
>
> 当前切点：**第五步（`playerToNpc` + D + E）代码已实施完**、
> 基线零依赖 389 / 含游戏引用 516、三种构建 0 警告；新交付包 DLL 是 `7B593EF0…`（442880 字节），
> 而**线上还是第三步那版 `C6F89483…`**（开关 `settlementWired: false`，
> 且"开关已关"的那次重启已经做过了）。
>
> **本轮唯一剩下的事是真机验收**，所以请先做三件事，**不要先改代码**：
> ① 按 §1 核对线上/交付包两组哈希、按 §2① 跑两套基线（期望 389 / 516）；
> ② 确认**游戏是否已完全退出**（§6.5 步骤 0 的独占写判据）；
> ③ 我确认退出后，按 **§6.5** 换版 → 临时打开 `settlementWired` → 按 §6 的 ①②③ 验收 → 收尾关回去。
>
> 另外本机有两个环境约定必须遵守（§1.5.1 的沙箱环境变量、§1.5.3 的沙箱限制），
> 而换版/改线上 config 需要**一次授权升级**（`danger-full-access`）。

**为什么不再问那三个问题**：§3 已经拍板完毕（1(a) 接受分叉 / 单笔上限=影子余额、单日沿用现有配置 / 3(a) 硬失败），
拍板结果与落地位置都记在 §3 的表里。

---

## 10. 文档地图

| 文档 | 内容 |
| --- | --- |
| `docs/HANDOFF-PHASE-4-STEP5.md`（本文） | **接手入口**：第五步实施记录（A+D+E）、甲方拍板结果、A 的改动清单与 6 条新测试、**§6.5 换版与验收步骤** |
| `docs/HANDOFF-PHASE-4-STEP4.md` | 第三步的真机验收记录：两个落盘 bug、三段证据链、`LastTrade` 落盘判据、9 条坑位 |
| `docs/HANDOFF-PHASE-4-STEP3.md` | 第三步实施记录：三项拍板各自落点、7.1~7.6、判据表、22 条坑位 |
| `docs/HANDOFF-PHASE-4-STEP2.md` | 第二步（真正动钱 + 审计落盘）：12 轮真机记录、五次换版全哈希 |
| `docs/PLAN-PHASE-4-STEP2.md` | 第二步方案全文（§8 甲方四项决策） |
| `docs/HANDOFF-PHASE-4-TRADE.md` | 第一步交接 + 三轮真机反馈 |
| `docs/HANDOFF-PHASE-3-PROBE.md` | 阶段 3：落点根因、收据裁剪、按需载入三大缺陷 |
| `deploy/DEPLOY.md` | 构建、部署、回滚、真机判据表 |
| `docs/HANDOFF-PHASE-0-2.md` | 阶段 0~2 完整记录 |

*本文档结束。与本地代码不符时以本地代码为准，并顺手更新本文档。*
