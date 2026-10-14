# 真机验收清单（B 项：玩家发起交易）

> **目的**：给上游作者的 Issue 提供**可信证据**。核心只需要证明一件事：
> **钱真的动了**（而不是"我画了个界面"）。
>
> **产出物**：① 一段 30–60 秒的 GIF/视频；② 三处文本证据。
> **不要做完整长录像** —— 没人看完，反而显得不专业。
>
> ⚠ **本清单的所有命令与判据都来自本仓库既有文档**（`deploy/DEPLOY.md`、
> `HANDOFF-PHASE-4-STEP4.md`），不是我编的。行号/路径已按当前仓库核对。

---

## 0. 先决条件（缺一不可）

| # | 条件 | 怎么确认 |
| --- | --- | --- |
| 1 | 原版 `NationalSpire` 已装好 | 我们是它的适配模组，没它什么都不跑 |
| 2 | **游戏完全退出** | ⚠ 沙箱**看不到也杀不掉**你的游戏进程；换版前必须你自己退出。判据是"目标 DLL 能否独占写"，不是"有没有同名进程" |
| 3 | 两套基线已全绿 | 395/395 与 533/533（已于 2026-10-14 实测） |

---

## 1. 步骤：打开动钱开关

开关是 `settlementWired`，**默认 false**（长期默认值，有测试专门拦它被改成 true）。
真机验收要**临时打开**。

```powershell
$live = 'D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy'
$path = "$live\config\economy_config.json"

# ① 先备份（放 config\ 外面更稳 —— 打包会吃 config\*.json）
Copy-Item $path "$path.bak-before-gif-$(Get-Date -Format yyyyMMdd-HHmmss)"

# ② 用 JSON 改写，不要字符串替换（当心 BOM）
$node = Get-Content $path -Raw -Encoding utf8 | ConvertFrom-Json
$node.trade.settlementWired = $true
$node | ConvertTo-Json -Depth 20 | Set-Content $path -Encoding utf8

# ③ 确认只改了那一行
Get-Content $path -Encoding utf8 | Select-String 'settlementWired'   # 期望 true
```

> ⚠ **`ConvertTo-Json` 会重排格式**。如果你在意，改完用 `git diff` 之外的方式比对
> 原版与本版的**语义**差异（STEP4 里用的是"压平成键值再逐键比对"），确认**只有**
> `settlementWired` 变了。这是既有纪律。

**改完必须重启游戏** —— 开关是启动时读的。启动日志会明说：

```
economy.init: ★结算已接线（配置 trade.settlementWired）：双方确认后会【真的动钱】并写 audit jsonl。
```

**看到 `★结算已接线` 才算开关生效。** 若显示 `结算未接线（默认）` ⇒ 没读到，回去查。

---

## 2. 步骤：录那 30 秒（**只录这四段**）

| # | 录什么 | 为什么这四段 |
| --- | --- | --- |
| ① | 私信里**玩家**发一句提议，NPC 回复 | 证明是**玩家发起**的（B 项的新能力） |
| ② | **预览弹窗**出现（双方余额怎么变） | 证明校验链在跑 |
| ③ | 玩家点「确认」 | 证明双人确认 |
| ④ | **余额真的变了** —— 前后对照 | ★★★ **全部价值所在** |

**第④步是唯一不可替代的。** ①②③ 都可以是"画出来的"，只有 ④ 证明钱真的动了。

> **B 项的完整语义是三段**：玩家提出 → NPC 同意（`[Deal: accept]`）→ 玩家确认 → 才结算。
> 录的时候如果能看到"NPC 同意之后单子仍是 pending、钱还没动"这一步，**很有说服力** ——
> 但那需要展示存档，未必录得进画面。录不进就用第 3 节的文本证据补。

---

## 3. 步骤：抓三处文本证据（**比录像更有力**）

作者 CONTRIBUTING §6 明确要求涉及钱的改动附这三处。**按他的格式来，等于告诉他你懂规矩。**

### 证据 1：`mod-debug.log` 关键行

```powershell
Get-Content "$env:APPDATA\SlayTheSpire2\national_spire_economy\mod-debug.log" |
  Select-String -Pattern 'Economy-Settle|Economy-PlayerTrade|Economy-Preview|Economy-Confirm|结算已接线'
```

**要抓的行**（按重要性）：

| 搜索词 | 含义 |
| --- | --- |
| `★结算已接线` | ✅ 开关真生效 |
| `[Economy-PlayerTrade] 玩家提出的交易单 oXXXX` | ✅ **玩家发起真的落单了**（B 项） |
| `[Economy-PlayerTrade] 模型同意玩家提出的交易单` | ✅ **NPC 真的同意了**（B 项核心） |
| `[Economy-Settle] oXXXX｜Applied` | ★★★ **真的动钱了**（终局证据，带双方余额前后） |

### 证据 2：影子存档的 `LastTrade` 键

> **落盘与否的唯一判据就是这一个键**（项目惯例）。重启后它还在 = 真的写进去了。

```powershell
$w = "$env:APPDATA\SlayTheSpire2\national_spire_economy\worlds"
Get-ChildItem $w -Filter *.json | ForEach-Object {
  Write-Host "=== $($_.Name) ==="
  (Get-Content $_.FullName -Raw -Encoding utf8 | ConvertFrom-Json).LastTrade | ConvertTo-Json -Depth 10
}
```

### 证据 3：审计行

```powershell
$a = "$env:APPDATA\SlayTheSpire2\national_spire_economy\audit"
Get-ChildItem $a -Filter *.jsonl | ForEach-Object {
  Write-Host "=== $($_.Name) 最后 5 行 ==="
  Get-Content $_.FullName -Encoding utf8 | Select-Object -Last 5
}
```

**要看到**：一行含 `Applied`、含 `Direction = player_to_npc`、含**双方余额前后**、含 `Day`（不是 0）。

---

## 4. 判据表：什么算成功

| 看到 | 结论 |
| --- | --- |
| 启动行 `★结算已接线` | ✅ 开关生效 |
| `玩家提出的交易单 oXXXX` | ✅ B 项落单 |
| 落单后单子 **仍是 pending**、余额未动 | ✅ **形态 b 的要害成立**（NPC 还没同意） |
| `模型同意玩家提出的交易单` | ✅ NPC 同意 |
| `[Economy-Settle] …Applied` | ★★★ **钱真的动了** |
| `LastTrade` 键存在且重启后仍在 | ✅ 落盘成立 |
| 审计行含方向 + 双方余额前后 + `Day` | ✅ 账实自洽 |
| ⚠ `[Error]` 出现 | 先别删任何东西，把前后 5 行留下 |

**独立核对（不依赖日志）**：`worlds\<WorldId>.json` 里 NPC 的 `balance` 在结算前后**真的变了**，
且变化量 = 审计行里的金额。**这一步能证明日志没撒谎。**

---

## 5. 步骤：验完关回去（**不要忘**）

```powershell
$live = 'D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy'
$path = "$live\config\economy_config.json"
Copy-Item $path "$path.bak-before-off-$(Get-Date -Format yyyyMMdd-HHmmss)"
$node = Get-Content $path -Raw -Encoding utf8 | ConvertFrom-Json
$node.trade.settlementWired = $false
$node | ConvertTo-Json -Depth 20 | Set-Content $path -Encoding utf8
Get-Content $path -Encoding utf8 | Select-String 'settlementWired'   # 期望 false
```

**重启游戏**，启动行应变成 `结算未接线（默认）`。

> ⚠ **交付包里必须是 false。** 有测试（`SettlementSwitchIsOffByDefault`）专门拦默认值被改，
> 但你**手工改的是线上那份**，不在 git 里 —— 所以这一步只能靠你自己记得。

---

## 6. 收工后的三件事

```powershell
$root = 'D:\蓝色大肥鱼\NationalSpireEconomy'
$live = 'D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy'

# ① 核对线上 DLL == 交付包 DLL（三处一致：bin / 交付包 / 线上）
(Get-FileHash "$live\NationalSpireEconomy.dll" -Algorithm SHA256).Hash
(Get-FileHash "$root\deploy\NationalSpireEconomy\NationalSpireEconomy.dll" -Algorithm SHA256).Hash
```

2. **把结果追加到 `HANDOFF-PHASE-4-STEP4.md` §1 的换版表里**（那是项目惯例：
   每次真机验收追加一行，别覆盖旧行）。
3. ⚠ **验收完不要重打包**。`bin` 现在停在"含游戏引用"的完整版；
   再跑一次零依赖测试会把 `bin` 覆盖成缩减版，破坏"三处一致"。

---

## 7. 已知会挡路的问题（先看一眼，省得白跑）

| 问题 | 症状 | 怎么办 |
| --- | --- | --- |
| **结果弹窗被私信模态挡住** | 结算了但看不到结果框 | **不是失败**：结果照常写 V 段与日志，用第 3 节的文本证据 |
| 模型爱自创交易类型 | 例如把"雇佣"写成"陪练费" → `BAD_KIND` | 属协议措辞问题；换几个说法再试 |
| V 段的"最近结算"会误导模型 | 模型看到上一笔"已结算"以为已付过、不发新标记 | 换个 NPC 或重开一局再试 |
| 玩家发起后模型不回 `[Deal: accept]` | 单子停在 pending | **这就是形态 b 的设计**（NPC 有权拒绝）；换个说法或换个 NPC 再试 |

> ⚠ **不要引导 NPC 输出标记**（既有纪律）。实测 NPC 会识破并回绝
> （"你这是拿我当程序试呢"）。**自然对话**，让它自己决定。

---

## 8. 诚实边界（写进 Issue 用）

**这两条必须主动说**，比被作者发现强得多：

1. **多人模式完全未测** —— 作者有 `CoopJson.Copy` + 回滚那套机制，说明他真在意多人。
2. **玩家发起交易是新一轮新加的能力**，真机只验了这一次。

> 主动说边界 = 可信；让他自己发现 = 不可信。这一条比 GIF 重要。
