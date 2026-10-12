# NationalSpireEconomy

> **国运尖塔 · NPC 经济系统** —— 为国运尖塔（NationalSpire）的 NPC 引入资金、工资、转会费与交易系统。

**⚠️ 本模组是[二开作品](#%E4%BA%8C%E5%BC%80%E4%B8%8E%E4%B8%8A%E6%B8%B8%E6%9D%A5%E6%BA%90)**
—— 它是 [NationalSpire](https://steamcommunity.com/sharedfiles/filedetails/?id=3809501471)（作者 **Catfish872**）
之上的**独立适配模组**：不修改上游的源码或 DLL，而是在运行时把经济逻辑挂接在它的私信与生涯流程上。
**发布包文件名一律标明"二开"，避免误下载。**

| 项 | 值 |
| --- | --- |
| 版本 | `0.1.0` |
| 游戏 | 《Slay the Spire 2》，`min_game_version` **0.107.1** |
| 依赖 | **NationalSpire**（必需；本项目引用它的程序集并给它打补丁） |
| 许可证 | PolyForm Noncommercial License 1.0.0（**禁止商业使用**） |
| 署名与出处 | 见 [`NOTICE.md`](NOTICE.md) |
| 参与开发 | 见 [`CONTRIBUTING.md`](CONTRIBUTING.md) |

---

## 这是什么

给国运尖塔的 NPC 加上**真正的钱**。每个 NPC 有自己的余额、工资、赛季收入与花销；
他们会**赞助**你、**打赏**你、**雇佣**你，也会因为钱不够或性格不合而拒绝你。
所有资金变动都走一本可审计的账，并且**只有双方都确认**才会真的动钱。

### 已验收的功能

| 功能 | 状态 |
| --- | --- |
| NPC 资金账本、工资、赛季统计、转会费与奖金 | ✅ 已实现 |
| **只读成交预览 + 双人确认**（钱一分不动的那一步） | ✅ 已实现并真机验收 |
| **真正动钱 + 审计落盘**（`audit/<WorldId>.jsonl`） | ✅ 已实现并真机验收 |
| **账本真相通道**（回执 / "未成立" / 审计回读，让模型不能谎称"钱转了"） | ✅ 已实现并真机验收 |
| **NPC → 玩家**方向的结算 | ✅ 已实现并真机验收 |
| **玩家 → NPC**方向的结算（玩家会真的掉钱） | ✅ 已实现并真机验收 |
| 投票（确认）绝不动钱 | ✅ 结构上保证（投票走只读快照钱包） |

### 已知边界

- **`NPC ↔ NPC` 方向尚未接线**：转会费/工资/奖金那套业务没验收过，会被方向闸挡下，一分不动。
- **玩家发起交易尚未支持**：目前只能由 NPC（模型）提出、玩家确认。
  玩家自己在私信里打 `[Trade: …]` **不会落单** —— 那是刻意的回声保护
  （否则模型复读玩家的字就等于"替 NPC 同意"）。
- **玩家侧即时回执看不到**：注入进 Prompt（V 段）**不等于**显示在私信界面上；
  结果弹窗在私信模态打开时会被让位，此时回执只写进 V 段与日志。
- **影子余额与原版 `Credits` 会分叉**：玩家的钱由本模组管辖（从原版一次性只读导入），
  **原版数字不会再变化**，这是设计选择，不是 bug。

---

## 二开与上游来源

本项目的性质、上游信息与**再分发的两项硬性要求**写在 [`NOTICE.md`](NOTICE.md)。
一句话版本：

- 上游作者 **Catfish872**，上游许可证 **PolyForm Noncommercial 1.0.0**；
- 该许可证允许"修改与衍生作品"，但**要求把许可证本身一并传给每一个拿到副本的人**；
- 所以**任何**发布包/源码包都**必须**带上 [`LICENSE`](LICENSE)
  （或 [`licenses/LICENSE-NationalSpire.txt`](licenses/)）与上游出处；
- **禁止商业使用。**

> 本仓库在任何情况下**都不得包含 `NationalSpire.dll`**。

---

## 安装（玩家）

1. 确认已装 **NationalSpire**（本模组依赖它）；
2. 把本模组目录整个放进游戏的 mods 目录：

   ```
   <Steam 库>\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy\
   ```

   放好之后目录里应有：
   ```
   NationalSpireEconomy.json          ← 模组清单
   NationalSpireEconomy.dll
   config\economy_config.json         ← 主配置（可直接改）
   config\personality_economy.json    ← 性格参数
   config\ai_prompt_economy.json      ← 注入提示词模板
   Catalog\items.json
   ```

3. 启动游戏。**启动日志里第一眼要看的是一行"结算是否接线"**：

   ```
   结算未接线（默认）：双方确认后停在「双方已确认，待结算」，一分钱都不会动。
   ```

   **默认就是这一行，也是长期默认值。** 想让交易真的动钱，把
   `config/economy_config.json` 的 `trade.settlementWired` 改成 `true` 再重启游戏，
   启动行会变成 `★结算已接线（配置 trade.settlementWired）`。

   改配置**请用能读写 JSON 的工具**（例如 Python 的 `json`）；
   不要用 `Set-Content -Encoding utf8` 这类会写 BOM / 改数字格式的方式。

### 出问题先看哪里

| 文件 | 用途 |
| --- | --- |
| `%APPDATA%\SlayTheSpire2\national_spire_economy\mod-debug.log` | **第一现场**（纯文件通道，不依赖 Godot） |
| `…\worlds\<WorldId>.json` | 影子存档；交易结论看 **`LastTrade` 键** |
| `…\audit\<WorldId>.jsonl` | 每笔真实结算一行，含方向与双方余额前后 |

---

## 构建与测试（开发者）

### 前置

- .NET SDK **9.0**
- 《Slay the Spire 2》本体（含 `data_sts2_windows_x86_64`）
- 上游 **NationalSpire.dll**（创意工坊或本地 `mods\NationalSpire`）

工程文件 `NationalSpireEconomy.csproj` 会自动解析这两处路径；
需要时可用 `-p:NseNationalSpirePath=…` 显式指定。

### 两套基线（**顺序是硬约束**）

```powershell
# ① 零依赖基线（先跑）—— <当前 389 / 389>
dotnet run --project tests/NationalSpireEconomy.Tests/NationalSpireEconomy.Tests.csproj -c Release

# ② 含游戏引用基线（最后跑）—— <当前 516 / 516>
dotnet run --project tests/NationalSpireEconomy.Tests/NationalSpireEconomy.Tests.csproj -c Release -p:GameRefsEnabled=true
```

**三种构建配置都必须是 0 警告。**
顺序反了会红两条"环境敏感"用例，这是已知行为。

### 打包部署

```powershell
.\build-deploy.ps1      # 清理 → Release 构建（GameRefsEnabled=true）→ 组装 deploy\ → 写 SHA256SUMS
```

⚠ **打完包之后不要再跑零依赖基线** —— 那一轮会把 `bin\` 里的全量 DLL 覆盖成缩减版。

⚠ 该脚本里 `Remove-Item $stg` 会清掉 `deploy\NationalSpireEconomy\`，
但**刻意不删 `deploy\` 本身**（那里放着 `DEPLOY.md`）。

---

## 目录结构

```
NationalSpireEconomy/
├── src/                     源码（按依赖层次分四层）
│   ├── Core/                零依赖基础：日志、金额换算、配置加载、确定性哈希
│   ├── Economy/             账本、工资/赛季推进、影子玩家钱包、收据本、存档
│   ├── Trade/               交易引擎：校验链、执行器、双人确认、结算、预览、文案
│   └── Inject/              Prompt 注入（S 段 / V 段）、审计
│       └── Game/            ★ 游戏与上游适配层（只在 GameRefsEnabled=true 时编译）
├── tests/NationalSpireEconomy.Tests/   自研零依赖测试宿主（两套基线）
├── config/                  随包分发的 JSON 配置与提示词模板
├── tools/pick-sponsor.py    挑"能过闸"的交易对象 + 计算金额窗口 + id→人名
├── deploy/                  构建产物与部署说明（DEPLOY.md）
├── docs/                    ★ 全部交接与设计文档，入口见 docs/README.md
├── licenses/                上游许可证逐字副本
├── build-deploy.ps1         一键打包
├── LICENSE / NOTICE.md / CONTRIBUTING.md
└── NationalSpireEconomy.json / NationalSpireEconomy.csproj
```

⚠ `src/Game` 是**唯一**依赖游戏与上游程序集的目录；`GameRefsEnabled=false` 时它整体被排除，
于是 `Core/Economy/Trade/Inject` 四层可以**零依赖**编译与单测（这就是两套基线的由来）。

---

## 参与开发

请先读 [`CONTRIBUTING.md`](CONTRIBUTING.md)。三条最要紧的：

1. **动手前先在群里聊设计意图** —— 很多"顺手能加"的功能会破坏真机踩出来的约束；
2. **PR 之前必须自测完全**：两套基线（先零依赖、后含游戏引用）都绿、三种构建 0 警告、新功能有测试；
3. **不要破坏现有架构**：动钱只能走 `EconomyLedger` + 幂等键；投票绝不动钱；
   S 段零数字；绝不改原版/上游、绝不写原版存档。

**二开版本必须在文件名标明"二开"**，并随包带上许可证与上游出处。

---

## 文档

完整的交接与设计记录在 [`docs/`](docs/README.md)，包括每一阶段的方案、真机验收证据、
以及几十条**踩坑记录**（这些是这个项目最有价值的部分）。建议接手顺序：

| 文档 | 内容 |
| --- | --- |
| [`docs/HANDOFF-PHASE-4-STEP5.md`](docs/HANDOFF-PHASE-4-STEP5.md) | **当前入口**：双向结算、三项拍板、换版与验收步骤 |
| [`docs/HANDOFF-PHASE-4-STEP4.md`](docs/HANDOFF-PHASE-4-STEP4.md) | 第三步真机验收：两个落盘 bug、三条证据链、9 条坑位 |
| [`docs/HANDOFF-PHASE-4-STEP3.md`](docs/HANDOFF-PHASE-4-STEP3.md) | 第三步实施：UI 与账本真相通道 |
| [`docs/HANDOFF-PHASE-4-STEP2.md`](docs/HANDOFF-PHASE-4-STEP2.md) | 真正动钱 + 审计落盘的 12 轮真机记录 |
| [`deploy/DEPLOY.md`](deploy/DEPLOY.md) | 构建、部署、回滚、真机判据表 |

---

## 许可证

**PolyForm Noncommercial License 1.0.0** —— 允许非商业用途的分享与修改，**禁止商业使用**。

- 本项目：[`LICENSE`](LICENSE)
- 上游逐字副本：[`licenses/LICENSE-NationalSpire.txt`](licenses/LICENSE-NationalSpire.txt)
- 出处与再分发要求：[`NOTICE.md`](NOTICE.md)
