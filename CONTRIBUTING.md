# 贡献指南（CONTRIBUTING）

感谢愿意参与开发。本模组是 **NationalSpire 的二开适配模组**，
代码与设计都有一整套已验收的纪律，所以下面这些不是"形式要求"，
而是**避免返工**的清单 —— 请先读完再动手。

---

## 一、动手之前：先聊设计意图

**在写代码之前，请在群里说明你的设计意图。**

原因很具体：这个项目的大部分架构决定都是**真机踩出来的**，代价写在 `docs/` 里
（例如"为什么确认路径必须用只读快照钱包"、"为什么 V 段不能用来给玩家看东西"）。
很多看起来"顺手就能加"的功能，恰好会破坏这些约束。

需要确认的典型问题：

- 它会不会动钱？动钱必须走 `EconomyLedger` + 幂等键，不能自己改余额；
- 它是不是"让玩家看到什么"的需求？如果是，**必须走 UI 通道**，
  注入进 Prompt（V 段）**不等于**显示在界面上（这是真机推翻过的假设）；
- 它会不会改原版或上游的行为？**绝不修改原版源码/DLL、绝不写原版存档**（只读）；
- 它和现有模块冲突吗？先搜一遍同名概念（例如"确认"有两套：`TradeConfirmationGate` 与 `PlayerTradeCommand`）。

## 二、提交 PR 之前：必须自测完全

### 1. 两套基线都必须绿，且**顺序是硬约束**

```powershell
$h = '<你的 dotnet 家目录>'          # 见 docs/HANDOFF-PHASE-4-STEP5.md §1.5.1
$env:APPDATA = "$h\Roaming"; $env:USERPROFILE = $h
$env:DOTNET_CLI_HOME = $h; $env:NUGET_PACKAGES = "$h\packages"; $env:DOTNET_NOLOGO = '1'
$root = '<仓库根目录>'

# ① 零依赖基线（先跑）
dotnet run --project "$root\tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj" -c Release

# ② 含游戏引用基线（最后跑）
dotnet run --project "$root\tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj" -c Release -p:GameRefsEnabled=true
```

| 基线 | 当前数量 |
| --- | --- |
| 零依赖 | **389 / 389** |
| 含游戏引用 | **516 / 516** |
| 编译警告 | **0**（三种构建配置都要 0） |

⚠ **顺序反了会红两条"环境敏感"用例** —— 这是已知行为，不是你的 bug，但 PR 里不许出现。

⚠ **含游戏引用那一轮需要本机装了游戏与上游 `NationalSpire.dll`**；没有环境的话请在 PR 说明里写清"只跑了零依赖基线"。

### 2. 新增/修改功能要有测试，且优先"从磁盘读"来判

- 测试放在 `tests/NationalSpireEconomy.Tests/`，并在对应套件的 `Cases` 数组里注册；
- **"写了真相就必须落盘"** 有专门的不变量用例（`EveryTruthWriteSurvivesAReload`）守着，
  新加真相出口时必须让它继续绿；
- 判断"钱有没有动"要**重新读盘**（换一个 `EconomyStore` 读回来），
  不要只看内存对象 —— 这个项目因此漏过两次真 bug。

### 3. 不要跑测试的时机

**打完包（`build-deploy.ps1`）之后不要再跑零依赖基线** ——
那一轮会把 `bin\` 里的全量 DLL 覆盖成缩减版（不含 `src/Game`），
随后真机加载的就是个残废包。顺序永远是：**先跑完两套基线 → 再打包 → 再部署**。

## 三、架构边界（改这些地方要特别小心）

| 边界 | 说明 |
| --- | --- |
| **`src/Game` 不进零依赖基线** | 它依赖游戏与上游程序集；能下沉到 `Core/Economy/Trade/Inject` 的逻辑就该下沉 |
| **动钱唯一出口** | `EconomyLedger` + 幂等键；玩家侧只能走 `ShadowPlayerWallet` |
| **确认（投票）绝不动钱** | 投票路径用只读快照 `InMemoryPlayerWallet`，这是**类型层面**的保证，别破坏它 |
| **结算前置四闸** | `SettlementWired` → 方向白名单 → 双方确认 → 单子仍 pending 且未过期；任一不满足**一个数字都不改** |
| **S 段零数字** | 稳定段（S 段）必须逐字节稳定且不含任何数字，`ConfigLoader` 会拦；带金额的话一律进 V 段或 UI |
| **审计只追加不修改** | `audit/<WorldId>.jsonl` 一行一条，历史行永不回填 |

完整纪律清单见 `docs/HANDOFF-PHASE-4-STEP5.md` §7（铁律）与 §8（不许顺手做的事）。

## 四、会被要求返工的 PR

以下情形我们会请你改完再交（不是拒绝，是要保证账对得上）：

- ❌ 功能与模组开发目的冲突，或不符合现有架构设计；
- ❌ 自动化测试未通过，或没有为新功能补测试；
- ❌ 动了原版/上游（改源码、改 DLL、写原版存档）；
- ❌ 引入新的第三方依赖（本机 NuGet 出网受限，且会增加玩家的安装负担）；
- ❌ 直接改 `trade.settlementWired` 的**默认值**（有一条测试专门拦它，因为默认值必须是 `false`）；
- ❌ 把"让玩家看到"做成注入 Prompt（V 段不面向 UI，这是已验证的死路）。

## 五、二开版本必须在文件名标明"二开"

如果你基于本模组再做一个分支/改版对外发布：

- **文件名必须标明"二开"**（例如 `NationalSpireEconomy-二开-你的名字-版本号`），
  避免玩家误下载、以及错误反馈渠道不清；
- 发布包根目录**必须**同时包含：
  1. `LICENSE`（PolyForm Noncommercial 1.0.0 全文）或至少其 URL；
  2. 上游出处声明（见 `NOTICE.md`）；
  3. 你自己的署名与仓库地址。
- **禁止商业使用**（上游与本项目都是 PolyForm Noncommercial）。

## 六、提交信息与 PR 描述

建议格式：

```
<模块>: <一句话做了什么>

为什么：<这个改动要解决的真实现象或需求>
怎么验的：<跑了哪两套基线、数量；是否有真机验证；关键日志/存档/审计证据>
风险：<会不会动钱、有没有碰原版、有没有可能影响已有交易>
```

对于涉及"钱"的改动，PR 描述里请附上三处证据（这是本项目验收的惯例）：

1. `%APPDATA%\SlayTheSpire2\national_spire_economy\mod-debug.log` 的关键行；
2. `worlds\<WorldId>.json` 里的 `LastTrade` 键（**落盘与否只看这个键**）；
3. `audit\<WorldId>.jsonl` 新增行（含 `Direction` 与双方余额前后）。
