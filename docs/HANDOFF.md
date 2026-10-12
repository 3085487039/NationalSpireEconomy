> ⚠️ 本文档进度部分已过期。**接手请看 `docs/HANDOFF-PHASE-4-STEP5.md`（阶段 4 第五步：玩家 → NPC 方向与收尾 —— 下一步就做这个）**；
> 阶段 4 第四步（**第三步的真机验收：已通过，含两个落盘 bug 的根因与修复**）见 `docs/HANDOFF-PHASE-4-STEP4.md`；
> 阶段 4 第三步（UI 与账本真相通道：已实现并验收）见 `docs/HANDOFF-PHASE-4-STEP3.md`；
> 阶段 4 第二步（**真正动钱 + 审计落盘，已真机验收通过**）见 `docs/HANDOFF-PHASE-4-STEP2.md`；
> 阶段 4 第一步（只读预览 + 双人确认）见 `docs/HANDOFF-PHASE-4-TRADE.md`；阶段 3（落点/收据/按需载入三大缺陷）见 `docs/HANDOFF-PHASE-3-PROBE.md`；
> 阶段 0~2 见 `docs/HANDOFF-PHASE-0-2.md`；部署与真机判据见 `deploy/DEPLOY.md`。
> （本文保留作为第 1–6 步的设计与踩坑记录。）
>
> **2026-10-12 凌晨一句话现状**：第一步（只读预览 + 双人确认）与第二步（**真正动钱 + 审计落盘**）都已真机闭环——
> 23:47 成交过一笔 $500（NPC `$5,000 → $4,500`、玩家影子 `$196,820 → $197,320`、`audit\e35d0119.jsonl` 首行 `trade_settled`、
> 幂等复验通过）；**第三步（UI 与账本真相通道）已实现、已换版、并已于 2026-10-12 真机验收通过**：
> 线上 DLL `441344` / `C6F89483…`；真机三步（被拒 / 成交 $700 / 幂等）全绿、`[Error]` 0、落盘修复被证明有效；
> 动钱开关已按拍板回到 `false`（长期默认值）且**方向闸已封**（只有 NPC→玩家能动钱）。
> 已知遗留：玩家**看不到**即时回执（V 段不面向 UI + 结果框被私信模态挡），甲方明确"不需要"补 —— 见 `docs/HANDOFF-PHASE-4-STEP4.md` §6。
>
> 以下是历史进度记录（按时间倒序）：
>
> **2026-10-11 22:11 进度**：线上 DLL 已换成含 TTL 修复的 `1462D493…`（游戏当时已退出，核对通过）；
> **阶段 4 第二步 §7.1「影子玩家钱包」已实现并全绿**（`src/Economy/ShadowPlayerWallet.cs` + `PlayerEconomy.cs`，**尚未接线到结算**）。
> **22:4x 追加**：第二轮真机暴露**聊天指令通道静默失效**（原版把玩家的话包成"…发给…的消息\n正文"，
> 而解析要求"以『确认』开头"）——已修（`src/Trade/PlayerTradeCommand.cs`，按行匹配、只认行首）
> 并补了"没听懂就写 Warning"的哨兵；**第三轮真机（22:31）已验证修复生效**。
> **22:39 再上一版**（DLL `15C992E3…` + 配置 `7FA0F181…`）：S 段协议加真相声明
> 「只有写出这一行标记，钱才会真的转过去」，并新增"模型嘴上说转了、账上没动"的警告哨兵
> （`TransferClaimDetector`）。基线 **346 / 464**，三种构建 0 警告。
> **✅ 2026-10-11 22:58–23:00：阶段 4 第一步已完整闭环**（真机：$500 → 校验 7/7 → 落单 `o0001` → 弹窗 → 点「好的」→
> `✅ 双方已确认，待结算`；钱一分未动、`[Error]` 0）。
> **✅ 23:1x：§7.1 影子钱包 + §7.2 结算器 + §7.3 落地与审计都已实现**（零依赖 **358** / 含游戏引用 **477**，三种构建 0 警告；
> `src/Trade/TradeSettlement.cs` + `ShadowPlayerWallet` + `audit\<WorldId>.jsonl` + 待确认单/结算后强制落盘；
> §7.1 那条回滚隐患已修，有回归测试）。
> **★★ 23:47：阶段 4 第二步真机验收通过——钱第一次真的动了**（对 `crowd-5-4` 成交 $500：
> NPC `$5,000 → $4,500`、玩家影子 `$196,820 → $197,320`、交易单 `state=confirmed`（流水 `3e237337…`）、
> **`audit\e35d0119.jsonl` 首次生成**（`trade_settled`，含双方余额前后）、收据裁剪 `514→512` 且两条 `confirm:` 都在、`[Error]` 0）。
> **动钱开关现在是开的**（`config/economy_config.json` → `trade.settlementWired = true`），
> 之后任何一笔确认到齐的交易都会真的动钱；想暂停就改回 `false` 重启游戏（**不必换 DLL**）。
> **★ 23:50：幂等复验也通过** —— 再确认同一张单被状态闸挡下（`交易单 o0001 已是 confirmed 状态，确认被拒绝`），
> 余额/流水/审计**一字未变**；"嘴上说转了、账上没动"哨兵首次在真实场景触发且判断正确。
> **第二步真机验收完整闭环。** 剩下的只有 §7.4（UI 与账本真相通道）——它已被真机用三种方式证明必要。

# NationalSpireEconomy 项目交接文档

> **文档用途**：让一个完全没有上下文的新 AI 读完本文 + 查看本地代码后，能无缝接手**第 7 步（补丁层接线）**的编码。
> **最后更新**：第 6 步（Prompt 注入 + 意向演进 + 审计）完成时。当前状态：`src/` 37 个文件、`config/` 3 份配置、`tests/` 16 个测试套件、**265 条用例全部通过**、Release 构建 0 警告 0 错误。
> **第 5 步新增**：`src/Trade/`（协议解析、八条校验器、两段式执行器、TradeEngine 编排）、`src/Trade/Catalog/`（购买物品扩展点）。
> **第 6 步新增**：`src/Inject/`（PromptInjector、StableSegment、VolatileSegment、CacheGuard、IntentEvolve、EconomyAudit）。

---

## 0. 三十秒速览

我们在给《杀戮尖塔 2》的模组 **NationalSpire（国运尖塔，v0.26.7 源码）** 做一个**独立的经济系统适配模组**，让 1600+ 名 NPC 拥有真实的资金、工资、转会费和交易行为，并把这些经济状态注入到 AI 提示词里。

技术形态：**独立 Harmony 插件 + 影子存档**，绝不修改原版源码。

已完成第 1–6 步（Core 层、影子存档、档位与播种、记账与结算、交易引擎、注入与意向演进），**下一步是第 7 步：补丁层接线**。

---

## 1. 项目定位与四条铁律

### 铁律一：绝不修改原版 NationalSpire

- 我们**只写一个新的独立 DLL**，通过 Harmony 补丁挂钩，不改原版源码、不改原版 DLL、不覆盖原版存档。
- 模组清单 id 必须是 `NationalSpireEconomy`，**绝不能叫 `NationalSpire`**——否则会被原版按版本号挤出加载列表（这是上一轮踩过的坑，见 §6.1）。
- 原版源码只是**参考资料**，位于 `D:\蓝色大肥鱼\NationalSpirre\NationalSpire-main`（注意目录名是 `NationalSpirre`，双 r，是个拼写笔误）。
- 目标基线版本：**0.26.7**（源码里的 `NationalSpire.json` 写明）。工坊版是 0.26.3，略有差异。

### 铁律二：影子存档

- 经济数据存在**我们自己的文件**里，键是 `WorldId + PersonId`。
- **永不只是读**原版存档 `national_spire_career.json`，**永不写**它。
- 理由：原版 `CareerStore.Read` 有一整套严格校验（版本号、日期范围、必需集合非空），一旦我们塞字段改变结构，会触发 `InvalidDataException`，导致它把主档另存 `.broken-*` 并回落备份——风险极高。
- 副作用是好的：原版升级时它的 `CareerData.Version` 会变，我们的档案不受影响。

### 铁律三：单位制换算只允许有一个出口

- **原版内部存在两套金额单位**：`InternalUnits`（1 单位 = 10 美元）与 `Usd`（美元）。
- 我们模组**内部一律用美元整数**。
- 所有跨边界换算**只能走 `MoneyUnits`**，任何地方私自写 `* 10` 或 `/ 10` 都算缺陷。
- 依据（硬证据）：原版 `OwnedClubs.cs:62-63` 用 `o.Signing / 10m == contract.Signing` 比较两类合同的金额，证明换算因子恒为 10。
- 各字段归属见 §4.3。

### 铁律四：幂等防重

- 任何会改变余额的操作都**必须带幂等键**。同一键只能生效一次。
- 两道闸：① `ReceiptBook` 收据集合；② `NpcEconomy` 上的水位线字段（`LastTickedDay` / `LastSalaryPaidSeason`）。
- **被 Pending 交易引用的收据键永不被裁剪**——这条是防重复扣款的核心（见 §6.3）。

---

## 2. 本地路径、环境与编译命令

### 2.1 路径总表

| 用途 | 绝对路径 |
| --- | --- |
| **我们的项目根目录** | `D:\蓝色大肥鱼\NationalSpireEconomy\` |
| 主工程 | `D:\蓝色大肥鱼\NationalSpireEconomy\NationalSpireEconomy.csproj` |
| 模组清单 | `D:\蓝色大肥鱼\NationalSpireEconomy\NationalSpireEconomy.json` |
| 源码 | `D:\蓝色大肥鱼\NationalSpireEconomy\src\{Core,Economy}\` |
| 配置文件 | `D:\蓝色大肥鱼\NationalSpireEconomy\config\` |
| 测试工程 | `D:\蓝色大肥鱼\NationalSpireEconomy\tests\NationalSpireEconomy.Tests\` |
| **原版源码（参考资料，只读）** | `D:\蓝色大肥鱼\NationalSpirre\NationalSpire-main` |
| 游戏安装目录 | `D:\Steam\steamapps\common\Slay the Spire 2` |
| 游戏程序集 | `D:\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\` |
| 游戏存档（只读） | `%APPDATA%\SlayTheSpire2\steam\76561199077518611\modded\profile3\` |
| 原版诊断日志 | `%APPDATA%\SlayTheSpire2\national_spire_diagnostics\national-spire.log` |
| 我们的影子存档（运行时生成） | `<存档目录>\national_spire_economy\` |

### 2.2 编译环境（**这台机器有特殊限制，务必先读**）

- `dotnet` 9.0.318 已安装（`C:\Program Files\dotnet\dotnet.exe`）。
- **PowerShell 是 5.1（Windows PowerShell），不是 pwsh 7。** 部分语法（如 `-Directory` 参数）行为不同，写命令时注意。
- **命令行出网被沙箱拦截**：`dotnet tool install`、NuGet 还原来会因为 `SEC_E_NO_CREDENTIALS` 失败。
  - 因此**不能安装 xunit / nunit**，测试用的是零依赖自建运行器（见 §3.7）。
  - 因此**不能用 `Godot.NET.Sdk`**（原版主工程用的就是它，我们刻意避开）。
- `dotnet` 首次运行会尝试写 `C:\Users\Li\.dotnet`，会被拒绝。**必须重定向以下环境变量**（下面的命令已包含）。

### 2.3 精确的测试运行命令

本机的沙箱要求重定向 `APPDATA` / `USERPROFILE` / `DOTNET_CLI_HOME`。可直接复制：

```powershell
$h = 'D:\蓝色大肥鱼\sandbox\dotnethome'
$env:APPDATA = "$h\Roaming"
$env:USERPROFILE = $h
$env:DOTNET_CLI_HOME = $h
$env:NUGET_PACKAGES = "$h\packages"
$env:DOTNET_NOLOGO = '1'

dotnet run --project 'D:\蓝色大肥鱼\NationalSpireEconomy\tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj'
```

退出码：`0` 表示全部通过，`1` 表示有失败。失败明细会打在末尾。

> ⚠️ **踩坑提醒**：修改了 `src/` 下的代码后，**只跑 `dotnet run` 有时不会重建主工程**。稳妥做法是先 `dotnet build` 主工程再 `dotnet run`，或者直接删掉 `bin`/`obj` 全量重建。曾因此浪费大量时间排查一个"其实已经修好"的 bug。

### 2.4 Release 构建

```powershell
# 同样先设置 §2.3 的环境变量
dotnet build 'D:\蓝色大肥鱼\NationalSpireEconomy\NationalSpireEconomy.csproj' -c Release -v q --nologo
```

### 2.5 工程文件的关键设计（读 `NationalSpireEconomy.csproj` 时注意）

- **刻意不使用 `Godot.NET.Sdk`**。第 1–6 步完全不依赖游戏，可独立编译与单测。
- 游戏程序集引用被 `<GameRefsEnabled>` 开关控制，**默认关闭**：

```xml
<GameRefsEnabled Condition="'$(GameRefsEnabled)' == ''">false</GameRefsEnabled>
<ItemGroup Condition="'$(GameRefsEnabled)' == 'true'">
  <Reference Include="sts2">...<HintPath>$(Sts2DataDir)\sts2.dll</HintPath>...
  <Reference Include="0Harmony">...
  <Reference Include="GodotSharp">...
  <Reference Include="Steamworks.NET">...
</ItemGroup>
```

  第 7 步（补丁层）开始需要打开：`dotnet build -p:GameRefsEnabled=true`。
- 源码只在 `src/` 下。**csproj 显式排除了 `tests\**\*.cs`**，否则 SDK 默认 glob 会把测试代码吸进主程序集并报 `CS8805`（见 §6.6）。
- 三份配置以 `None` + `CopyToOutputDirectory="PreserveNewest"` 随包分发（玩家可直接改，不是嵌入资源）。
- **已验证**：构建出的 `NationalSpireEconomy.dll` 引用表里只有 `System.*`，没有任何游戏程序集——这是"Core/Economy 层与游戏解耦"的证明。

---

## 3. 已完成进度（第 1–6 步）

### 3.1 分层结构

```
src/
├─ Core/           ← 与游戏完全解耦，纯逻辑
│  ├─ MoneyUnits.cs            单位换算唯一出口
│  ├─ DeterministicHash.cs     确定性随机（对齐原版算法）
│  ├─ ConfigSchema.cs          三份 JSON 的强类型镜像
│  ├─ ConfigLoader.cs          加载 + 校验 + 热重载
│  └─ EconomyLog.cs            日志抽象（Core 不依赖 Godot）
├─ Economy/
│  ├─ EconomyModels.cs         数据模型
│  ├─ EconomyStore.cs          影子存档读写
│  ├─ ReceiptBook.cs           幂等收据本
│  ├─ EconomyTier.cs           财富档位判定
│  ├─ PersonalityEconomy.cs    性格 → 经济画像
│  ├─ EconomySeed.cs           初始资金播种
│  ├─ EconomyLedger.cs         记账器
│  └─ EconomyTick.cs           时间推进与赛季结算
└─ Trade/          ← 第 5 步：交易引擎（同样与游戏完全解耦）
   ├─ TradeIntent.cs           协议解析结果
   ├─ TradeProtocol.cs         [Trade:]/[Buy:] 增量解析 + 剔除标记
   ├─ TradeCheck.cs            校验结果（含 Soft / InjectLine）+ 失败 Code 常量
   ├─ TradeContext.cs          求值上下文（校验器只读它）
   ├─ ITradeValidator.cs       校验器接口（必须无副作用）
   ├─ ITradeExecutor.cs        两段式执行器接口 + Reserve/Commit 结果
   ├─ ITradeEffectHandler.cs   副作用处理器 + TradeReceipt + TradeEffectRegistry
   ├─ TradeEngine.cs           编排：Submit / Confirm / Decline / ExpireStale
   ├─ Validators/              八条校验器（见 §5.3）
   ├─ Executors/
   │  ├─ TransferExecutor.cs   转账/赞助/打赏/雇佣/奖金/借款
   │  └─ PurchaseExecutor.cs   购买物品（骨架，默认 Enabled=false）
   └─ Catalog/                 扩展点，本期只留接口（见 §5.5）
      ├─ TradeItem.cs / ITradeCatalog.cs / ITradeInventory.cs
      ├─ NullTradeCatalog.cs / NullTradeInventory.cs
      └─ items.json            示例数据（未接线）
```

第 6 步新增：

```
src/Inject/       ← Prompt 注入 + 意向演进 + 审计（依旧与游戏完全解耦）
├─ PromptInjectInput.cs   一次注入所需的全部输入（稳定/易变字段刻意放在一起以便断言隔离）
├─ StableSegment.cs       S 段合成（铁律：零数字）
├─ VolatileSegment.cs     V 段合成（所有数字只允许在这里）
├─ CacheGuard.cs          稳定段指纹比对：决定"前缀缓存是否需要重写"
├─ PromptInjector.cs      编排：合成 S/V + 决定是否提交副作用
├─ IntentEvolve.cs        drift 累积 + 触发规则 + 意向文案选择
└─ EconomyAudit.cs        追加写 audit/<WorldId>.jsonl
```

### 3.2 第 1 步：`Core/` —— 单位换算与配置

**`MoneyUnits`（`src/Core/MoneyUnits.cs`，143 行）**

| 成员 | 说明 |
| --- | --- |
| `const int UsdPerInternalUnit = 10` | 换算因子，硬证据见 §1 铁律三 |
| `InternalToUsd(long/decimal)` | 内部单位 → 美元 |
| `UsdToInternal(long, RoundingPolicy)` | 美元 → 内部单位。**默认 `RequireExact`**：不是 10 的整数倍就抛 `MoneyLossException`，逼调用方显式选 `Nearest`/`Floor`/`Ceiling` |
| `UsdToInternalExact(long)` | 保留分数 |
| `Format(long usd)` | 渲染美元文本。**入参已是美元，绝不再乘 10**（原版 `PrivateMessageScreen.cs:507` 就是这个 bug） |
| `FormatInternal(long)` | 渲染内部单位（先换算） |
| `BalanceUsd(long credits, decimal fraction)` | 原版余额 = `Credits` + `CreditFraction` |
| `AddUsd(credits, fraction, usd)` | 返回 `(Credits, Fraction)`，溢出不静默截断 |
| `TryParseUsd(string, out long)` | 接受 `50000` / `$50,000` / `5万` / `1.2万` / `3亿` / `5万5千` |
| `WealthBand(long)` | 大额数字 → 人话档位（用于 Prompt 稳定段，零数字） |

**关键约定**：金额恒为**非负**。`UsdToInternal` 对负数直接抛 `ArgumentOutOfRangeException`（因为 `Ceiling`/`Floor` 对负数语义反直觉，业务上也不需要）。

**`DeterministicHash`（51 行）**

- `StableHash(string)`：FNV-1a 32 位、掩到 31 位，**与原版 `CareerEngine.StableHash` 逐位等价**。
- 已用独立探针复核两个锚点值，测试里钉死了：`StableHash("") == 18652613`、`StableHash("a") == 1678518572`。
- `Seed(params string[])`：用 `:` 连接后哈希。
- `LogUniform(Random, min, max)`：按数量级均匀铺开，带取整。

**`ConfigLoader`（338 行）**

- 加载三份 JSON，校验后返回 `ConfigLoadResult { Economy, Personality, Prompt, Issues, RootDirectory }`。
- `HasErrors` 为 true 时调用方应拒绝启用功能，而不是带着坏配置继续跑。
- `ReloadIfChanged()`：按文件时间戳热重载。
- **校验内容**（第 5 步新增配置项时也要往这里加校验）：
  - 档位金额区间、权重、是否有兜底档位
  - `ConfigLoader.CanonicalPersonalityKinds`（**24 种性格的规范列表**）的覆盖性：缺项/多项都报警告
  - 6 个经济轴的取值范围（`give` 0.2~1.5、`charitySensitivity` 0~0.95、`bargain` 0~0.95、`thrift` 0.05~1.0、`initiative` 0~1.0）
  - 好感档位的区间覆盖性（用 -100/-1/0/19/20/49/50/79/80/100 十个探针验）
  - `charity.sharp` / `charity.gentle` / `cap` 必须有 `default` 兜底台词
  - **稳定段模板必须零数字**（正则 `\d` 检查）——防止破坏前缀缓存
- 特殊解析逻辑：`ai_prompt_economy.json` 里 `_weightBands` 是"容器对象"（下划线开头但**不是注释**），其内部键构成一张独立表；`_placeholders` 这类无字符串值的对象会被当作空表忽略。**改这块时务必读 `ReadPrompt` 的 `NestedTableContainers` 分支**。

### 3.3 第 2 步：影子存档

**`EconomyModels.cs`（217 行）—— 第 5 步必须扩展的核心**

```
EconomyWorld                    ← 单生涯（WorldId）的完整经济数据
  SchemaVersion = 1
  WorldId, CreatedAt, LastWrittenAt
  LastTickedDay, LastSeason
  Npcs:      Dictionary<string, NpcEconomy>     ← PersonId → 经济状态
  Offers:    Dictionary<string, TradeOffer>     ← OfferId → 交易单（第 5 步主战场）
  Receipts:  List<string>                       ← 幂等收据，最多 512
  SeededTotalUsd, SeededCount
  StableSegmentFingerprint
  const MaxReceipts = 512, MaxOffers = 256

NpcEconomy
  PersonId, Balance(美元), TierId, ClubId
  PersonalityKind, PersonalityVersion          ← 性格快照，用于侦测被 AI 改写
  Season, SeasonStats, Lifetime
  LastTickedDay, LastSalaryPaidSeason, LastSponsorPaidSeason   ← 幂等水位线
  Ledger: List<LedgerEntry>        ← 环形，最多 64，最新在前
  IntentTriggers: List<IntentTrigger>  ← 环形，最多 8
  PendingVoice: string             ← 软失败留下的"未竟之言"
  Retired: bool, LastTouchedDay: int
  const MaxLedgerEntries = 64, MaxIntentTriggers = 8
  方法：AppendLedger(entry) / AppendTrigger(trigger)

LedgerEntry
  Id, Day, Season, Kind, Title, Amount(正入负出), BalanceAfter
  CounterpartyId("player" 表示玩家), OfferId, Reason
  ApplicationKind  ← [JsonIgnore] 冲正流水用它记住"撤销的是哪个科目"（见 §6.4）

SeasonStats     ← SeasonIncome/Spend, SalaryIncome, WinBonusIncome, SponsorIncome,
                   TransferIncome/Spend, PrizeIncome, GiftIncome/Spend, Net
LifetimeStats   ← Income, Spend, TradeCount, RefusedCount, DriftScore, LastRefusedCharityDay
IntentTrigger   ← Day, Kind, Detail, Delta

TradeOffer      ← 第 5 步的核心数据，现有字段：
  Id, Kind, Direction, InitiatorId, CounterpartyId
  Amount(美元), Items: List<TradeItemLine>   ← Items 本期恒空，为"购买物品"预留
  Reason, State, CreatedDay, Season, ExpiresDay, Report, LedgerEntryId, ValidationStamp
  IsExpired(day)

TradeItemLine   ← { ItemId, Quantity }   ← 已定义但未使用

TradeKinds            = 赞助/打赏/雇佣/奖金/转账/借款
TradeOfferStates      = pending/confirmed/declined/invalid/expired + IsTerminal()/IsPending()
TradeDirections       = player_to_npc/npc_to_player/npc_to_npc
```

**⚠️ 第 5 步设计约束**：`TradeOffer.Items` 与 `Amount` 是**并列字段，不是二选一**。这样将来"用 3 万加一件道具换某人加盟"这类复合交易不需要重构数据结构。

**`EconomyStore.cs`（361 行）**

| 成员 | 说明 |
| --- | --- |
| `EconomyStore(rootDirectory, IEconomyClock?, IEconomyLog?)` | 路径由调用方给出（接线层传 `user://` 或模组目录） |
| `LoadWorld(worldId, createIfMissing)` | 返回 `WorldLoadReport { Status, World, Detail, DataWasLost }` |
| `SaveWorld(world, force)` | 写入前跑 `Validate`；受 `MinimumWriteInterval`（默认 2 秒）节流 |
| `LoadIndex()` / `SaveIndex()` / `SetActiveWorld()` / `MarkArchived()` | WorldId 索引隔离 |
| `Static Validate(EconomyWorld)` | 返回问题列表 |
| `Static SafeWorldId(string)` | 防目录穿越，`"../../evil"` → `"______evil"` |
| `WorldLoadStatus` | `Loaded` / `RecoveredFromBackup` / `RebuiltFromScratch` / `CreatedNew` |

**目录布局**：

```
<root>/national_spire_economy/
├─ index.json                     已知 WorldId + 当前选中 + 归档标记
├─ worlds/<WorldId>.json          单生涯经济数据
├─ worlds/<WorldId>.json.bak      上一份完好副本
└─ worlds/<WorldId>.json.tmp      原子写中间态（正常不留存）
```

**四条硬性保证**：

1. **不损坏**：序列化 → 写 `.tmp` → **回读校验** → 轮换 `.bak` → 原子替换。顺序不可颠倒。
2. **拒绝写入好过写坏账本**：自检失败直接抛 `InvalidDataException` 且**不落任何文件**。
3. **可恢复且坏文件永不删除**：主档坏 → 回落 `.bak`；两者都坏 → 重建空账本并标记 `DataWasLost`；坏文件改名成 `.broken-<时间戳>` 保留。
4. **不串档**：读回时校验文件名与内容里的 `WorldId` 一致，不一致就拒绝。

**`ReceiptBook.cs`（98 行）**

- `Mark(key, now)`：登记一次已完成操作，重复返回 `false`。
- `Prune(protectedKeys, now, maxAge)`：**两条规则**——数量上限 512（丢最旧）+ 年龄上限 30 天。
- **`protectedKeys` 永不裁剪**，即使受保护的键本身超过上限也一个都不丢。理由：防重优先于内存整齐。
- `ToList()` / `Contains()` / `Dirty` / `TakeDirty()`。
- `IEconomyClock` / `SystemEconomyClock` / `FixedEconomyClock`（测试用，可 `Advance`）也在本文件。

### 3.4 第 3 步：档位、性格、播种

**`EconomyTier.cs`（125 行）**

- `INpcFacts` 接口：`Id / Role / Identities / Rating / MaxAscension / ClubId / PersonalityKind / PersonalityVersion`。
  **刻意与游戏类型解耦**——接线层负责把 `NationalSpire.CareerPerson` 投影成它。
- `NpcFacts` record：接线层与测试都用它（带默认值，可 `with` 派生）。
- `EconomyTier.KnownRoles`：**0.26.7 实际出现的 9 种 Role**：
  `普通玩家 / 青训选手 / 职业选手 / 世界顶尖 / 退役选手 / 教练 / 主播 / 解说员 / 赛事记者`
- `Resolve(npc, seeding)` / `ResolveId(...)`：判定顺序 **`Roles` 精确命中 → `IdentitiesContains` → `RatingAtLeast` 分层 → 无条件兜底**。
- **打分是 `(条件数, 门槛值)` 的字典序**。多条件同时满足会累加，条件数相同时**门槛更高者优先**——否则 `普通玩家(≥0)` 会先于 `普通玩家(≥800)` 命中，高阶分层永久失效（这是修过的真 bug）。
- `GroupByTier(people, seeding)`：分组统计。
- `PlayerId = "player"`：玩家本人**不进我们的账本**（他的钱在原版 `CareerData.Credits` 里）。

**`PersonalityEconomy.cs`（157 行）—— 第 5 步的关键依赖**

```
ProfileFor(config, kind)                 → EconomyProfile（缺失回退 defaults）
IsKnownKind(config, kind)
IsSnapshotCurrent(npc, facts)            → 侦测原版性格被 [Profile:] 改写
ApplySnapshot(npc, facts)
FavorFor(config, favour)                 → FavorAdjustment { TierId, Multiplier, LineSet }
Evaluate(config, kind, favour)           → PersonalityEconomyValues（已叠加好感修正的六轴）
SpendCap(config, tierId, balance, profile)      → 支出上限（美元）
SpendFloor(config, tierId, cap, charitySensitivity) → 支出下限（美元）
Emotion(amount, balance)                 → 情绪值 = amount / max(500, balance × 0.10)
IsExtremeGap(config, amount, balance)    → 高好感仍拒绝的判据
CharityLine(config, lineSet, kind)       → 施舍拒绝台词
CapLine(config, kind, suggestedUsd)      → 议价台词（替换 {suggested}）
SoftLine(config, kind)                   → 软失败素材
TagText(profile, prompt, kind)           → Prompt 的「金钱观」人话素材
```

**公式（务必原样沿用）**：

```
基准上限 = balance × tierCapRatio(tierId)
实际上限 = 基准上限 × give × (1 − 0.4 × thrift)     再被 trade.maxSingleAmount 夹住
实际下限 = 实际上限 × minRatio(tierId) × (0.5 + charitySensitivity × favorMultiplier)
情绪值   = amount / max(500, balance × 0.10)         小于 1 表示"分量不够"
极端差距 = amount < balance × season.extremeGapRatio
```

**`EconomySeed.cs`（239 行）**

- `RollInitialBalance(worldId, npc, seeding, tier)`：**纯函数**，同输入必同输出。
- 阶梯水位线：`level = 进阶/10 × 0.6 + (评分−500)/1600 × 0.4`（clamp 0~1），
  抽样用幂分布 `x = min + (max−min) × u^(1/(0.5+level))`。`level=0.5` 时指数恰为 1，即标准 log-uniform。
- `RoundToMagnitude(value, max)`：按量级取整（<1000→10，<10000→100，<100000→500，否则 1000）。
- `Seed(world, people, config, log)` → `SeedResult { SeededCount, SkippedPlayer, SkippedSeeded, TotalUsd, CountByTier, TotalByTier }`。
  **重复调用不改变既有金额**；玩家本人与已播种者分开计数。
- `AssignTiers(people, worldId, seeding)`：**精确命中 Roles 的人不参与洗牌**，其余用 `WorldId` 做种的稳定洗牌按权重切分。
- `Budget(seeding, people)` → `SeedingBudget`：用**精确公式** `E[X] = (b−a)/(ln b − ln a)` 配取整偏移，不做蒙特卡洛。

### 3.5 第 4 步：记账与结算

**`EconomyLedger.cs`（367 行）—— 第 5 步的主要依赖**

```
enum MoneyMoveStatus { Applied, AlreadyApplied, InsufficientFunds, Rejected }

record MoneyMoveResult(Status, AmountUsd, BalanceAfterUsd, ReceiptKey, LedgerEntryId, Detail)
    .Applied 属性
record TransferResult(NpcSide, PlayerSide, ReceiptKey) { BothApplied }
interface IPlayerWallet { long BalanceUsd; bool TryAdd(long deltaUsd, out string? error); }
class InMemoryPlayerWallet : IPlayerWallet        ← 测试用；接线层要接真实 CareerData

class EconomyLedger
  const string ReversalKind = "冲正"
  ctor(EconomyWorld world)

  Key(category, params string[] parts)   → "category:worldId:part1:part2"
  HasReceipt(key) / MarkReceipt(key) / FlushReceipts()
  ProtectedKeys()                        → 所有 Pending 交易关联的收据键（永不裁剪）
  PruneReceipts(clock)

  Credit(personId, amountUsd, entry, receiptKey?)  → MoneyMoveResult
  Debit (personId, amountUsd, entry, receiptKey?)  → MoneyMoveResult
  Move   (personId, deltaUsd, entry, receiptKey?)  → MoneyMoveResult
  Transfer(amountUsd, npcId, fromPlayer, wallet, entry, receiptKey?) → TransferResult
  Reverse(personId, ledgerEntryId, reason, day, season) → MoneyMoveResult

  static RollSeason(npc, newSeason)      ← 赛季统计清零，Lifetime 保留
  static IdFor(receiptKey)               ← 流水 id = SHA256(键) 前 16 字节
```

**五条契约（第 5 步必须遵守）**：

1. **余额不足在源头拒绝**，绝不先把余额扣成负数再补救。
2. **账实一致**：余额、流水、赛季统计、累计统计、收据在**同一次调用里一起改完**。
3. **幂等**：命中收据 → 返回 `AlreadyApplied` 且**一分不动**。
4. **玩家转账顺序是"NPC 先动、玩家后动"**：NPC 的钱在我们手里，失败能回滚；玩家的钱在游戏本体里，动过不好撤。NPC 侧失败 → 整笔作废，玩家侧根本不执行。玩家侧失败 → 回滚 NPC 侧。
5. **只有 `EconomyLedger` 能改 `NpcEconomy.Balance`**，其他地方一律不准直接赋值。

**`EconomyTick.cs`（269 行）**

```
record TickResult(Day, Season, SeasonChanged, SalariesPaid, SalaryTotalUsd)
static class EconomyTick
  SeasonOf(day, seasonDays) / SeasonStartDay(season, seasonDays) / SeasonEndDay(...)
  AdvanceTo(world, config, targetDay, log)      ← 幂等水位线，逐日补齐，返回 Skipped 表示无需推进
  RollSeasonForAll(world, season)
  SalaryFor(config, tierId)                     ← 未配置时按档位名给保守默认
  PayPrize(world, personIds, amountUsd, eventId, title, day, season, log) → int (实付人数)
  PayTransferFee(world, buyerId, sellerId, feeUsd, transferId, day, season) → (bool Ok, string? Error)
  TransferFee(config, rating)                   ← Rating × 转会费系数 × 10，钳制 clamp
  SigningFee(config, tierId)                    ← SalaryFor × signingFeeRatio
```

- 发薪时机由 `season.salaryPayoutMode` 决定（`seasonStart` 默认 / `seasonEnd`）。
- 每个人的 `LastSalaryPaidSeason` 是**第二道防线**：即使 `LastTickedDay` 被人为重置也不会重复发薪。
- `PayTransferFee` 是**双向原子**的：买方余额不足 → 整笔作废；卖方入账失败 → 冲正买方。

### 3.6 配置文件现状

**`config/economy_config.json`（124 行）**

```jsonc
{
  "configVersion": 1,
  "seeding": {
    "maxWealth": 500000,
    "curve": "log-uniform",
    "tiers": [
      { "id": "crowd",        "roles": ["普通玩家"],                     "min": 500,    "max": 5000,   "weight": 62 },
      { "id": "youth",        "roles": ["青训选手"],                     "min": 8000,   "max": 30000,  "weight": 12 },
      { "id": "media",        "roles": ["主播","解说员","赛事记者","教练"], "min": 5000, "max": 40000, "weight": 10 },
      { "id": "veteran",      "roles": ["退役选手"],                     "min": 20000,  "max": 120000, "weight": 2  },
      { "id": "professional", "roles": ["职业选手"],                     "min": 30000,  "max": 160000, "weight": 12 },
      { "id": "elite",        "roles": ["世界顶尖"],                     "min": 200000, "max": 500000, "weight": 2  },
      { "id": "default",                                                 "min": 1000,   "max": 20000,  "weight": 0.0001 }
    ]
  },
  "season": {
    "daysPerSeason": 84,
    "salaryPayoutMode": "seasonStart",
    "salaryByTier": { "crowd":600, "media":6000, "youth":9000, "veteran":15000, "professional":24000, "elite":60000, "default":3000 },
    "tierCapRatio": { "crowd":0.25, "media":0.40, "youth":0.35, "veteran":0.45, "professional":0.50, "elite":0.70, "default":0.30 },
    "minRatio":     { "crowd":0.02, "media":0.04, "youth":0.03, "veteran":0.05, "professional":0.08, "elite":0.15, "default":0.03 },
    "transferFeeRatingTimes": 5,
    "transferFeeClamp": [40000, 100000],
    "signingFeeRatio": 0.2,
    "prizeUsd": { "local":1000, "city":2000, "academy":1500, "open":2600, "league":2600,
                  "continental":4000, "worldcup":8000, "worldfinal":50000, "masters":50000 },
    "extremeGapRatio": 0.0005
  },
  "trade": {
    "minAmount": 100,
    "maxSingleAmount": 200000,
    "dailyLimitPerNpc": 3,
    "offerExpiryDays": 14,
    "npcPayoutRequiresBalance": true,
    "requireFavourForNpcPayout": 0,
    "allowedKinds": ["赞助","打赏","雇佣","奖金","转账","借款"],
    "auditEnabled": true
  },
  "injection": { "stableEnabled": true }
}
```

**`config/personality_economy.json`（225 行）**：24 种性格 × 6 轴 + `tags`、好感四档、三套台词（`charity.sharp` / `charity.gentle` / `cap` / `soft`）。详见 §4.1、§4.2、§4.4。

**`config/ai_prompt_economy.json`（87 行）**：S 段/V 段模板 + `moneyAttitude`(24) / `intentPhrase`(5) / `incomeSource`(10) / `_weightBands`(9) 四张表。详见 §4.5。

**第 5 步补进 `trade` 节点的两个字段**（`_commentEmotion` / `_commentSoft` 是说明）：

```jsonc
"minEmotionToAccept": 1.0,   // 情绪值低于此值时，即使金额达标也会拒绝（"这点钱是在羞辱我吗"）
"softFailOnBalance": true     // NPC 出钱余额不足时走软失败而不是硬失败
```

`ConfigSchema.TradeConfig` 加了 `MinEmotionToAccept` / `SoftFailOnBalance` 两个属性，
`ConfigLoader.ValidateEconomy` 加了对应校验：情绪门槛不能为负（0 = 关闭该校验）、
超过 10 会警告（几乎会拒绝一切出钱）、`dailyLimitPerNpc` 必须为正、
`requireFavourForNpcPayout` 应落在 -100~100、白名单缺「转账」会警告。

### 3.7 第 5 步：`Trade/` —— 交易引擎

**`TradeProtocol.cs`（约 420 行）—— 增量流式解析兼正文剔除器**

原版 `PrivateMessages.Known`（`PrivateMessages.cs:311`）是一张写死的正则白名单，
`Trade` 不在其中，所以 `[Trade: ...]` 会被当正文直接显示给玩家。我们不能改原版，
于是改用前缀补丁 `PrivateStreamParser.Feed`：先进我们的扫描器抽出标记，
把剔除后的干净文本再交给原方法。

```csharp
IReadOnlyList<TradeIntent> Feed(string? chunk, StringBuilder cleanBody);  // 增量喂入
IReadOnlyList<TradeIntent> Feed(string? chunk);                          // 只取意图
IReadOnlyList<TradeIntent> Flush(StringBuilder cleanBody);               // 结束流，必须调用
static IReadOnlyList<TradeIntent> ParseAll(string? text);                // 一次性解析
static string StripMarkers(string? text);                                // 剔除标记
```

四条设计要点（都是实测修出来的，**不要"顺手优化"回去**）：

1. **按深度配对括号**，不是找第一个 `]`。否则 `"[未知: [Trade: 打赏]] ..."` 里内层的 `]`
   会被当成外层的结束，外层被切成两半。
2. **绝不进入不认识的组**。不认识就整组逐字放行——剥掉内层会让原版解析器看到残缺标记。
3. **Reason 是最后一个字段**，它内部的逗号属于理由正文。`$50,000` 的千分位逗号也豁免切分。
4. **无错误时 `ParseError` 必须是 `null`**（不是空串）：`ParseFailed` 与调用方都按 null 判定。

**`TradeEngine.cs`（约 430 行）—— 编排**

```csharp
TradeEngine(EconomyConfig, PersonalityConfig, PromptConfig, IEconomyLog?, ITradeCatalog?)
IReadOnlyList<ITradeValidator> Validators         // 提交链：八条全上
IReadOnlyList<ITradeValidator> ConfirmValidators  // 确认链：去掉第 8 条（见下）
ReserveResult Submit(TradeContext, TradeIntent)   // 不动余额；软失败在这里被识别
CommitResult  Confirm(TradeContext, string offerId)
void          Decline(TradeContext, string offerId, string reason)
int           ExpireStale(EconomyWorld, int day)
TradeCheck    RunValidators(ctx, intent, chain?)  // 第一条失败即返回
```

**⚠️ 一个必须知道的坑**：`Submit` 成功时会把**意图级幂等键**登记进收据本
（`intent:{world}:{发起方}:{对手方}:{Kind}:{金额}:{天}`）。所以 `Confirm` **不能**再跑
第 8 条校验器，否则玩家一点确认就会被判成"重复提交"——这是实测出来的自锁。
确认阶段的防重靠另外两道：① 交易单状态必须仍是 `pending`；② 动账幂等键 `trade:{world}:{offerId}`。

**`Commit` 的步骤顺序不可颠倒**（改顺序会破坏幂等或产生负余额）：
取单 → 查状态 → 查过期 → **重新跑确认链** → 幂等 → 动账 → 落状态。
重新校验失败时**不静默改金额**（对齐原版 `PrivateContracts.Confirm` 的"重新报错让玩家重谈"）。

**软失败的落地**（`TradeEngine.ApplySoftDecline`）：
写 `NpcEconomy.PendingVoice` → 记 `IntentTrigger{Kind="trade_soft_declined", Delta=-0.05}`
→ `Lifetime.RefusedCount++`。**不落 `TradeOffer`、不生成失败卡片、不动余额、不产生流水。**

**`EconomyModels.cs` 第 5 步的改动**：只加了 `TradeOffer.HasItems`（`[JsonIgnore]`）。
`Amount` 与 `Items` 是**并列**字段，将来"用 3 万加一件道具换某人加盟"不需要重构数据结构。

**`TradeEffectRegistry`** 是纯内存静态表，**不参与影子存档序列化**。

### 3.9 第 6 步：`Inject/` —— 注入、意向演进与审计

**`StableSegment.cs` / `VolatileSegment.cs` —— 三段切分的落地**

| 段 | 内容 | 放在哪 | 变化频率 |
| --- | --- | --- | --- |
| **S（稳定）** | 量级档位、收入来源、金钱观（tags + `moneyAttitude`）、意向措辞 | system 尾部 | 每 7 天或跨赛季才动 |
| **V（易变）** | 精确余额、最近一笔流水、本季累计收支、`PendingVoice` | user 的 `ordered.Current` 段内 | 每笔交易 |
| **I（意向）** | 意向**类型**（driftBand → `intentPhrase`）进 S | — | 低频 |

**稳定段的硬性纪律**：`StableSegment` 只读 `TierId` / `Facts` / `Personality` /
`Lifetime.DriftScore` / `Lifetime.LastRefusedCharityDay` / `Npc.PersonalityKind`，
**绝不读** `Balance` / `SeasonStats` / `Ledger` / `PendingVoice`。
`StableSegment.ForbiddenInputs` 把这条纪律写成常量，测试据此断言"输入隔离"。

**一个容易踩的小坑**：`_weightBands` 与 `incomeSource` 两张表的键是
**Role 名**（"职业选手"）而<b>不是</b>档位 id（"professional"）。拿档位 id 去查会静默退回
`default`，量级措辞就全错了。两张表都要先按 `Facts.Role` 查，查不到才退 `default`。

**`CacheGuard.cs` —— 前缀缓存守卫**

```csharp
static string Fingerprint(PromptInjectInput input, string? bandId = null);
static string FingerprintOf(string stableText);          // 只依赖稳定段正文
static bool   Refresh(EconomyWorld world, PromptInjectInput input, string? bandId = null);
static bool   RefreshFingerprint(EconomyWorld world, string fingerprint);
static bool   HasChanged(EconomyWorld world, PromptInjectInput input, string? bandId = null);
```

**语义约定**：`Refresh` 只在"指纹**发生了变化**"时返回 true。
首次写入（原指纹为空）返回 **false**，因为那时还没有缓存可供失效。
指纹只依赖稳定段正文本身——正文一样，指纹就必须一样。

**`IntentEvolve.cs` —— 意向演进**

```csharp
static string Record(NpcEconomy npc, string kind, string detail, int day, double? deltaOverride = null);
static void   ApplyDelta(NpcEconomy npc, double delta);
static string BandIdFor(NpcEconomy npc, int day, int resentfulWindowDays = 7);
static string BandIdForScore(double driftScore);
static string PhraseFor(PromptConfig prompt, string bandId);
```

档位分界（从慷慨到谨慎）：

| 档位 | 分数区间 | 触发表 |
| --- | --- | --- |
| `generous` | ≥ 0.30 | — |
| `steady` | ≥ 0.02 | 起点（0）落在这里 |
| `cautious` | ≥ −0.15 | — |
| `stingy` | < −0.15 | — |
| `resentful` | **叠加** | `LastRefusedCharityDay` 落在 7 天窗口内即生效 |

增量常量集中在 `DriftTriggers`（**唯一定义点**）：
`trade_received` +0.05、`trade_paid` −0.02、`trade_soft_declined` −0.05、
`trade_charity_refused` −0.03。分数被夹在 ±1，不能无限漂。

**`resentful` 是叠加档而不是分数档**：一个刚被连续赞助到 `generous` 的人，
只要前几天被人拿小钱试探过，措辞就必须立刻转向"最近有点介意被人小看"。
所以 `BandIdFor` 先查记恨窗口，再按分数分档。

**`EconomyAudit.cs` —— 追加写 `audit/<WorldId>.jsonl`**

- **为什么是 JSONL**：一行一条、追加是 O(1)、断电最多丢最后一行，
  且可以直接 `Select-String` / `grep`，不需要先解析整个文件。
  换成 JSON 数组就必须整体重写，一旦损坏全丢。
- **失败策略：永不抛出**。审计是旁路，坏掉不该让游戏崩；异常记成 Warning 后吞掉，
  但通过 `LastError` 暴露出来供自检。
- **一行一条 + 立即 `Flush(flushToDisk: true)`**：宁可慢一点，
  也不要"崩了才发现最后几十条还在缓冲区"。
- **坏行跳过**：半行（断电）或手工编辑坏了，只跳那一行，其余照读。
- **金额一律美元**，且 `AmountUsd`（动了多少）与 `BalanceAfterUsd`（动完剩多少）
  **是两个字段**，混用会让审计线无法复原。
- WorldId 走 `EconomyStore.SafeWorldId` 消毒，防目录穿越。

### 3.10 测试基建（**新 AI 必须先理解这点**）

- **本机装不了 xunit**（NuGet 出网被拦）。测试用**零依赖自建运行器** `TestRunner`（93 行）。
- 每个测试套件是一个 `static class XxxTests`，暴露 `public static Action[] Cases => [TestMethod1, ...]`。
- 测试方法签名统一 `private static void Xxx()`，内部用 `TestRunner.Equal/True/False/Throws/InRange/Contains/NotContains`。
- **新增套件必须在 `Program.cs` 的 `TestRunner.Run(...)` 里注册**，否则不会执行。
- 配置类测试**直接跑仓库里的真实 `config/` 目录**（向上遍历找到 `config/economy_config.json`），所以它们同时是"配置自洽性"的回归测试。

| 套件 | 用例数 | 覆盖 |
| --- | --- | --- |
| `MoneyUnitsTests` | 14 | 换算、取整策略、渲染、解析中文金额 |
| `DeterministicHashTests` | 10 | FNV-1a 锚点、log-uniform 分布 |
| `PromptRenderTests` | 5 | 占位符替换 |
| `ConfigLoaderTests` | 12 | 真实配置校验、容器表不被拆分 |
| `ReceiptBookTests` | 9 | 防重、双规则裁剪、保护优先 |
| `EconomyStoreTests` | 18 | 原子写、备份恢复、串档防护、自检、节流 |
| `EconomyTierTests` | 8 | 9 种 Role 归属、门槛优先、兜底 |
| `PersonalityEconomyTests` | 19 | 24 种映射、好感修正、公式核对、台词 |
| `EconomySeedTests` | 13 | 确定性、区间、权重、预算报告 |
| `EconomyLedgerTests` | 25 | 记账、冲正、转账、发薪、奖金、转会费 |
| `TradeProtocolTests` | 18 | 增量分片、全角、千分位、畸形输入、保守放行 |
| `TradeValidatorTests` | 48 | 八条校验器逐条正反例、优先级顺序、性格适配、软失败 |
| `TradeEngineTests` | 15 | Reserve/Commit、确认时重校验、幂等、过期、端到端 |
| `PromptInjectorTests` | 23 | S 段零数字、V 段含数字、缓存不抖动、PendingVoice 注入与消费 |
| `IntentEvolveTests` | 15 | drift 累积、档位边界、记恨窗口、环形、措辞零数字 |
| `EconomyAuditTests` | 15 | 追加写、分文件、BOM、中文不转义、坏行跳过、失败不抛 |
| **合计** | **265** | 全绿 |

---

## 4. 策划案总结

### 4.1 为什么是 24 种性格而不是 5 种

原版 `PersonalityLibrary` 的性格是**硬编码的 24 种"处置"**（`ExpandedPersonality.cs:5-31` 的 `Dispositions`），每种带 `Name/Min/Max/Social` 四字段。`PersonalityLibrary.Kinds`（public）暴露名字，每人的 `Kind` 存在存档的 `PersonalityProfile.Kind` 里——**可以直接按名字成表，不需要反射或猜测**。

24 种：暴躁喷子、尖刻挑刺者、战绩踩头者、打法优越者、嘴硬争胜者、嫉妒比较者、怨气抱怨者、反感说教者、狂热拥护者、护短熟人、夸张吹捧者、热心同好、兴奋追随者、较真维护者、自我炫耀者、故意挑事者、反串拱火者、看戏起哄者、反骨抬杠者、自认客观者、刻薄玩笑者、不服输挑战者、直率求助者、自嘲找同伴者。

**两条重要的设计修正（曾经差点做错）**：

1. **"拮据型"不是性格，是财富档位。** 原始需求把"慷慨/骄傲/贪婪/拮据/豪爽"当成一套人格，但"拮据"（余额少）指的是**有没有钱**，"慷慨"（愿不愿花）指的是**愿不愿意花**。所以架构必须把两者**正交化**：
   ```
   tier（财富档位，来自 Role/MaxAscension/Rating）  →  决定绝对量级
   Kind（24 种性格之一，来自存档）                  →  决定相对系数
   两者相乘
   ```
   这才真正实现了需求里那句"同样余额 10 万，骄傲的愿出 8 万、拮据的最多出 1 万"。

2. **原版没有"商人"这类角色。** 需求里的"贪婪型"没有直接对应，我把它落到 `故意挑事者`（`bargain: 0.90`）与 `看戏起哄者`（`bargain: 0.80`）这两个槽位上，不凭空造原版不存在的性格。

### 4.2 六个经济轴 + 好感四档

| 轴 | 范围 | 含义 |
| --- | --- | --- |
| `give` | 0.2 – 1.5 | 给予倾向，直接乘进支出上限 |
| `charitySensitivity` | 0.05 – 0.90 | 施舍敏感度，越高越抗拒小额 |
| `bargain` | 0.15 – 0.90 | 议价强度，越高越会加价还价 |
| `thrift` | 0.15 – 0.90 | 消费谨慎度，反向压低上限 |
| `borrow` | true/false | 是否会向玩家借钱。**只有 2 种性格开**（`怨气抱怨者`、`自嘲找同伴者`） |
| `initiative` | 0.15 – 0.80 | 主动发起交易的倾向 |

另带 `tags: string[]`：既是 Prompt 的"人话素材"，也是 UI 的速读索引。例如 `自我炫耀者 → ["骄傲","要面子"]`。

**好感四档**（`favorTiers`，`Favour` 来自 `PrivateMessages.Favour`，-100~100）：

| Favour | 档位 id | charity 系数 | 台词套 |
| --- | --- | --- | --- |
| < 20 | `stranger` | × 1.00 | `sharp`（尖锐） |
| 20–49 | `familiar` | × 0.70 | `sharp` |
| 50–79 | `close` | × 0.40 | `gentle`（柔和） |
| ≥ 80 | `intimate` | × 0.10 | `gentle` |

**感情只削弱 `charitySensitivity` 这一个轴，其他五个轴完全不受影响**（有测试钉住）。
效果：`战绩踩头者`（base 0.82）对陌生人是 0.82，对交情是 0.082——几乎是两个人。

**高好感仍然可以拒绝**，判据是 `IsExtremeGap`（金额相对余额的比例低于 `extremeGapRatio`）。高好感 ≠ 来者不拒，交易系统要保留张力。

### 4.3 单位制对照表（**最容易出错的地方**）

| 字段 / 结构 | 单位 | 证据 |
| --- | --- | --- |
| `CareerData.Credits` | **InternalUnits** | `1 单位 = $10` |
| `CareerLifeState.CreditFraction` | 不足一单位的分数 | 与 `Credits` 合成余额 |
| `ClubOffer.SigningBonus/SeasonPay/WinBonus/GoalReward` | **InternalUnits** | `CareerCommerce` 直接把它加进 `d.Credits`；`ClubTerms` 用 `CareerMoney.Format()` 渲染 |
| `SponsorContract.Retainer/WinBonus` | **InternalUnits** | 同上 |
| `OwnedPlayerContract.Signing/Wage/WinBonus` | **InternalUnits** | `OwnedClubs.cs:62-63` 的 `/10m` 比较 |
| `CareerClub.Budget` | **InternalUnits** | `ClubOperations` 直接加减 |
| **`PrivateOffer.Signing/Wage/WinBonus`** | **USD** | 模型直接输出、玩家直接核对的那一套 |
| **我们的影子存档全部金额** | **USD** | 本模组内部统一 |

**已知的原版 bug（我们用注释标了，但不去改）**：
`PrivateAppointments.cs:155` 手动 `× 10` 后喂给模型（显示 $30,000），而 `PrivateMessageScreen.cs:507` 直接展示原值（显示 $3,000）。同一份合同，模型看到的是玩家看到的 10 倍。

### 4.4 软失败机制（**第 5 步要落地的重点之一**）

**硬失败**：`MoneyMoveStatus.InsufficientFunds` / `Rejected` → 生成 `TradeOffer{State="invalid", Report=...}`，界面显示失败卡片，失败原因会回灌到后续 Prompt（对齐原版 `PrivateOffer.Detail` 的用法）。

**软失败**（NPC 想给玩家钱但余额不足）：

- **不落 `TradeOffer`、不弹硬报错、UI 不加任何提示。**
- 只把一句素材写进 `NpcEconomy.PendingVoice`，下一轮注入 Prompt 的 V 段，**让 NPC 用自己的性格口吻在对话里自然说出来**。
- **`default` 给的是台词原文**（"囊中羞涩，只能口头支持。"），**性格变体给的是"语气方向"**（如 `自嘲找同伴者 → "拿自己手头紧这件事开玩笑"`），由模型自行措辞，避免机械复述。
- **原版提示词明确禁止心理描写**，所以绝不能写"面露难色"这类旁白。

### 4.5 档位分级、权重切分与 Prompt 注入

**档位与权重**：见 §3.6 的 `seeding.tiers`。9 种 Role 映射到 6 个档位（`教练/主播/解说员/赛事记者` 共用 `media`）。人数比例按 1800 人标定：crowd 62%、professional 12%、youth 12%、media 10%、veteran 2%、elite 2%。

**播种预算（实测值，`EconomySeed.Budget` 精确公式算出）**：

```
人数 1800，预计总量 $28,195,465，人均 $15,664
professional   216 人  $30,000~$160,000   均值 $77,659   小计 $16,774,430
media          216 人  $5,000~$40,000     均值 $16,831   小计 $3,635,592
youth          216 人  $8,000~$30,000     均值 $16,644   小计 $3,595,218
crowd         1116 人  $500~$5,000        均值 $1,954    小计 $2,181,027
veteran         36 人  $20,000~$120,000   均值 $55,811   小计 $2,009,198
```

对标需求"人均 1.6 万 / 总量 2900 万"。测试里有 `15,500 ~ 16,500` 的回归带。

**Prompt 注入的三段切分（保护前缀缓存）**：

| 段 | 内容 | 放在哪 | 变化频率 |
| --- | --- | --- | --- |
| **S（稳定）** | 量级档位、薪资水平、金钱观（`TagText`）、消费倾向 | system 尾部，紧邻 `Protocol` 那串之后 | 每 7 天或跨赛季才动 |
| **V（易变）** | 精确余额、最近 3 笔流水、本季累计收支、`PendingVoice` | user 的 `ordered.Current` 段内（与日期/日程/状态同组） | 每笔交易 |
| **I（意向）** | `intentEvolve` 的 driftBand → `intentPhrase` 措辞 | 意向**类型**进 S（低频），意向**细节**进 V | 中频 |

**为什么必须这么切**：原版 `PrivateMessagePrompts.OrderContext`（181-202 行）把 `profile/results/public` 归为 Stable、`current/topic/schedule/learning/mood` 归为 Current，**分成两条独立 user message 发送**。前缀缓存的命中面就是"从头到 Stable 结束"这一段。私信单次请求的 payload 在 **1.1 万–5.3 万字符**之间（日志实测），把余额写进 Stable 段会让整段前缀每笔交易都失效。

**纪律**：S 段模板**必须零数字**（`ConfigLoader` 有正则校验），所有数字只允许出现在 V 段。

### 4.6 意向演进（第 6 步做，第 5 步只需写数据）

`NpcEconomy.Lifetime.DriftScore` + `IntentTriggers`（环形 8 条）。规则示例（配置化）：

| 触发 | drift 变化 | 注入文案变化 |
| --- | --- | --- |
| 收到过小打赏被拒 | ↓ | "最近有点介意被人小看" |
| 花钱雇到玩家且成绩变好 | ↑ | "愿意为成绩投入" |
| 连续被玩家借钱未还 | ↓↓ | "最近手头紧，不太想再借" |
| 赛季收入大涨 | ↑ | "这赛季刚拿工资，正好赞助你一点" |

---

## 5. 第 5 步：TradeEngine（交易引擎）—— ✅ 已完成

> **交付状态**：§5.1 配置补齐、§5.2 协议解析、§5.3 八条校验器、§5.4 两段式执行器、
> §5.5 扩展点接口、§5.6 新增文件清单全部完成，§5.7 验收清单全部有测试覆盖。
> **基线从 133 条涨到 214 条**，新增 81 条（协议 18 + 校验器 48 + 引擎 15）。
> 本节的实现细节与踩坑记录见 §3.7、§6.8、§6.9。

### 5.1 准备工作：先补 `economy_config.json` 的缺口

**当前配置缺一个必需字段**：`trade.minEmotionToAccept`（见 §5.4 校验器 6）。第 5 步开工前请先在 `config/economy_config.json` 的 `trade` 节补上，建议初值 `1.0`：

```jsonc
"trade": {
  ...现有字段...,
  "minEmotionToAccept": 1.0,      // 情绪值低于此值时，即使金额达标也会拒绝（"这点钱是在羞辱我吗"）
  "softFailOnBalance": true        // NPC 出钱余额不足时走软失败而不是硬失败
}
```

同时要在 `ConfigSchema.cs` 的 `TradeConfig` 里加对应属性，并在 `ConfigLoader.ValidateEconomy` 里加校验。

### 5.2 协议解析：`[Trade: ...]` / `[Buy: ...]`

**关键前置事实（务必先理解）**：原版的指令识别是**写死的正则白名单**——

```csharp
// NationalSpire/PrivateMessages.cs:311
private static bool Known(string text) => Regex.IsMatch(text,
    @"^\[\s*(Favour|Attitude|Relationship|Match|Contract|Skill|Mood|Post|Profile|Activity|Training|Lineup)\s*[:：]",
    RegexOptions.IgnoreCase);
```

**`Trade` 不在其中**，所以 `[Trade: ...]` 现在会被当作正文直接显示给玩家。我们**不能改它**（那是改原版）。

**推荐路线（唯一推荐）**：**Prefix 补丁 `PrivateStreamParser.Feed(string text)`**——
先扫描进入的文本，命中 `[Trade: ...]` / `[Buy: ...]` 就抽出、记录到我们自己的收集器，并把这段从 `text` 里剔除，再交给原方法。

- 优点：零正文污染、不依赖上游正则、上游改正则我们也不受影响。
- **注意**：原版的 `PrivateStreamParser` 是**流式**的（可能跨越任意网络分片），所以我们的扫描器也必须是**增量状态机**，不能假设一次 `Feed` 拿到完整括号。参考资料：`PrivateMessages.cs:301-319` 的 `_pending` / `_depth` 实现。

**协议形态（扁平，与 `[Contract: ...]` 同族风格）**：

```
[Trade: 赞助, Amount: 50000, Reason: 这赛季刚拿工资，正好赞助你一点]
[Trade: 雇佣, Amount: 80000, Reason: 出五万找人帮我冲 A10]
[Trade: 打赏, Amount: 5000, Reason: 你这局打得漂亮]
[Trade: 奖金, Amount: 30000, Reason: 联赛名次奖金按约定分你一半]
[Trade: 转账, Amount: 20000, Reason: 先借你周转]
[Trade: 借款, Amount: 10000, Reason: 手头紧，能不能先借我点]
[Buy: 礼物, ItemId: potion_energy, Amount: 3000, Reason: 送你个能量药水]
```

- `Kind` **必须是 `trade.allowedKinds` 白名单内的值**，否则拒绝。
- 物品走**独立的 `[Buy: ...]`**（带 `ItemId`），与纯转账语义不同，分开更清晰、能各自演进。
- `Amount` **只作为提议值**，任何余额变动都不由模型决定。

**解析器接口（建议）**：

```csharp
public sealed record TradeIntent(
    string Kind,                 // 白名单内的 Kind，或 "购买"（Buy）
    string Direction,            // TradeDirections.*
    string InitiatorId,
    string CounterpartyId,
    long AmountUsd,              // 已用 MoneyUnits.TryParseUsd 解析
    IReadOnlyList<TradeItemLine> Items,   // 恒非 null；纯转账时为空数组
    string Reason,
    string RawText,              // 原始标记文本，供审计
    string? ParseError);         // 非 null 表示解析失败

public sealed class TradeProtocol
{
    // 增量喂入；返回本次新识别出的意图（可能 0 个或多个）
    public IReadOnlyList<TradeIntent> Feed(string chunk);

    // 供测试与一次性解析
    public static IReadOnlyList<TradeIntent> ParseAll(string text);

    // 把文本里所有 Trade/Buy 标记剔除，返回干净正文
    public string StripMarkers(string text);
}
```

### 5.3 八条校验器

**统一接口与结果类型**：

```csharp
public sealed record TradeCheck(
    bool Ok,
    string Code,            // 机器可读，用于日志与幂等键
    string Message,         // 给人看的说明，会回灌到 Prompt
    bool Soft = false,      // ★ 软失败标记
    string InjectLine = "");  // Soft == true 时，注入下一轮 V 段的素材

public interface ITradeValidator
{
    TradeCheck Validate(TradeContext ctx, TradeIntent intent);
}

public sealed class TradeContext
{
    public EconomyWorld World { get; init; }
    public EconomyConfig Config { get; init; }
    public PersonalityConfig Personality { get; init; }
    public PromptConfig Prompt { get; init; }
    public INpcFacts? Counterparty { get; init; }   // NPC 侧事实（拿 Role/Rating/Kind）
    public int Favour { get; init; }                // 玩家与这个 NPC 的好感
    public int Day { get; init; }
    public int Season { get; init; }
    public IPlayerWallet Wallet { get; init; }
    public string LocalHumanId { get; init; }       // 多人模式下"玩家"的 id
}
```

**校验链按顺序短路执行，第一条失败即返回**。顺序是刻意排的：先廉价的结构校验，再昂贵的性格计算。

| # | 校验器 | 规则 | 失败 Code |
| --- | --- | --- | --- |
| 1 | `IntentShapeValidator` | `Kind` 在白名单内；`InitiatorId`/`CounterpartyId` 非空且不相等；`Direction` 合法；`Items` 里每个 `ItemId` 非空、`Quantity > 0` | `BAD_KIND` / `BAD_SHAPE` |
| 2 | `AmountBoundsValidator` | `minAmount ≤ Amount ≤ maxSingleAmount`；纯转账时 `Amount > 0` | `AMOUNT_TOO_SMALL` / `AMOUNT_TOO_LARGE` |
| 3 | `CatalogValidator` | 有 `Items` 时：目录里能解析出每个 `ItemId`、库存足够、单价与 `Amount` 一致（**本期空实现，只留接口**，见 §5.5） | `ITEM_UNKNOWN` / `ITEM_OUT_OF_STOCK` |
| 4 | `BalanceValidator` | 出钱方余额充足。`Direction == npc_to_player` 时看 NPC 余额；`player_to_npc` 时看 `Wallet.BalanceUsd` | `NPC_INSUFFICIENT`（→ 软失败）/ `PLAYER_INSUFFICIENT` |
| 5 | `DailyLimitValidator` | 同一 NPC 每日交易次数 ≤ `dailyLimitPerNpc`（数当日 `LedgerEntry`） | `DAILY_LIMIT` |
| 6 | **`PersonalityFitValidator`** ★ | 见下方详解 | `PERSONALITY_REFUSES_CHARITY` / `PERSONALITY_CAP` / `PERSONALITY_LOW_EMOTION` |
| 7 | `FavourValidator` | `Direction == npc_to_player` 且 `npcPayoutRequiresBalance` 时，要求 `Favour ≥ requireFavourForNpcPayout` | `FAVOUR_TOO_LOW` |
| 8 | `IdempotencyValidator` | 该意图的幂等键尚未登记；防重复提交同一笔 | `ALREADY_APPLIED` |

**第 6 条详解（性格适配 + 施舍门槛，本步的核心玩法）**：

```
values = PersonalityEconomy.Evaluate(personality, kind, favour)
cap    = PersonalityEconomy.SpendCap(config, tierId, balance, profile)
floor  = PersonalityEconomy.SpendFloor(config, tierId, cap, values.CharitySensitivity)
emotion = PersonalityEconomy.Emotion(amount, balance)

// (a) 上限：性格决定的"最多愿意出多少"
if (Direction == npc_to_player && amount > cap)
    → 失败，CapLine(personality, kind, suggested: cap)
      Code = "PERSONALITY_CAP"

// (b) 施舍门槛：骄傲的人被小额冒犯
if (Direction == player_to_npc
    && Kind is 打赏 or 赞助 or 转账
    && amount < floor)
    → 失败，CharityLine(personality, values.LineSet, kind)
      Code = "PERSONALITY_REFUSES_CHARITY"

// (c) 情绪值：金额分量够不够
if (Direction == npc_to_player && emotion < config.Trade.MinEmotionToAccept)
    → 失败，Code = "PERSONALITY_LOW_EMOTION"

// (d) 高好感仍然拒绝（极端差距）
if (Direction == player_to_npc && PersonalityEconomy.IsExtremeGap(config, amount, balance)
    && values.CharityMultiplier <= 0.10)      // 只在交情档生效
    → 失败，CharityLine(..., "gentle", kind)
```

**软失败的产生点在 #4**：`Direction == npc_to_player` 且 NPC 余额不足、
且 `config.Trade.SoftFailOnBalance == true` 时，**不返回硬失败**，而是返回：

```csharp
new TradeCheck(Ok: false, Code: "NPC_SOFT_DECLINE", Message: "", Soft: true,
    InjectLine: PersonalityEconomy.SoftLine(personality, kind))
```

调用方（`TradeEngine`）见到 `Soft == true` 时的行为：
1. **不落 `TradeOffer`**、不生成失败卡片；
2. 把 `InjectLine` 写进 `NpcEconomy.PendingVoice`；
3. 记一条 `IntentTrigger { Kind = "trade_soft_declined", Delta = -0.05 }`；
4. `Lifetime.RefusedCount++`。

### 5.4 两段式执行器：`Reserve` / `Commit`

**为什么必须两段**：模型提出报价 → 玩家可能隔很久才确认。这中间余额会变、性格可能被改写、NPC 可能被交易走。所以：

- **`Reserve`**：生成待确认的 `TradeOffer`，做一次**预校验**，把当时的校验码存进 `ValidationStamp`。**不动任何余额。**
- **`Commit`**：玩家确认时**重新跑完整校验链**，通过才真正动账。这是原版既有做法（`PrivateContracts.Confirm` 第 170 行在确认时重新调用 `Error`）。

```csharp
public interface ITradeExecutor
{
    /// 生成待确认交易单。不改变任何余额。
    ReserveResult Reserve(TradeContext ctx, TradeIntent intent);

    /// 玩家确认后真正执行。内部必须重新校验。
    CommitResult Commit(TradeContext ctx, string offerId);
}

public sealed record ReserveResult(
    bool Ok, string? OfferId, TradeCheck Check,
    bool SoftDeclined = false, string InjectLine = "");

public sealed record CommitResult(
    bool Ok, TradeCheck Check, string? LedgerEntryId,
    long NpcBalanceAfter, long PlayerBalanceAfter);
```

**`TradeEngine` 编排（建议签名）**：

```csharp
public sealed class TradeEngine
{
    public TradeEngine(EconomyConfig config, PersonalityConfig personality,
                       PromptConfig prompt, IEconomyLog? log = null);

    public IReadOnlyList<ITradeValidator> Validators { get; }   // 可注入，便于单测替换

    /// 模型输出 → 解析 → 校验 → 生成待确认交易单。软失败在这里被识别。
    public ReserveResult Submit(TradeContext ctx, TradeIntent intent);

    /// 玩家确认 → 重新校验 → 执行 → 记账。幂等键 = "trade:{worldId}:{offerId}"
    public CommitResult Confirm(TradeContext ctx, string offerId);

    /// 玩家拒绝。
    public void Decline(TradeContext ctx, string offerId, string reason);

    /// 把超期未确认的交易单标记为 expired（由 EconomyTick 调用）。
    public int ExpireStale(EconomyWorld world, int day);
}
```

**`Commit` 的执行步骤（顺序不可颠倒）**：

1. 取 `offer`；`State` 必须是 `pending`；`IsExpired(day)` 则置 `expired` 并失败。
2. **重新跑校验链**。失败 → `State = invalid`，`Report = check.Message`，返回。
3. **重新校验失败时不要静默改金额**——原版是重新报错让玩家重谈（`PrivateContracts.Confirm` 的做法）。
4. 幂等键 `ledger.Key("trade", offerId)`；若已登记 → 返回 `AlreadyApplied`。
5. 按 `Direction` 调用 `EconomyLedger.Transfer(...)`（玩家侧）或 `EconomyTick.PayTransferFee(...)` 风格的 NPC↔NPC 双向转账。
6. 成功 → `offer.State = "confirmed"`、`offer.LedgerEntryId = 新流水 id`、记账、`FlushReceipts()`。
7. 记 `IntentTrigger`（`Delta` 按 §4.6 的规则表），必要时更新 `DriftScore`。
8. `Lifetime.TradeCount++` 已由 `EconomyLedger` 内部处理，不要重复加。

**执行器需要按 Kind 分支的部分（NPC 侧的额外语义）**：

| Kind | 额外语义（第 5 步先留 TODO，第 7 步接线时实现） |
| --- | --- |
| `雇佣` | 触发"玩家带打/当教练"的合同或日程（原版 `CoachLineups` / `ClubCoaching`） |
| `奖金` | 通常由程序按赛事名次触发，而不是 AI 主动决定金额 |
| `借款` | `borrow == false` 的性格一律拒绝；记入待还账（**待还账数据结构本期未定义，需要新增**） |
| `赞助` | 可关联原版的 `SponsorContract` 概念，但**不要直接改原版字段** |

### 5.5 扩展点接口：为"购买物品"预留（**本期不实现**）

需求明确要求"后续增加购买物品等活动"，所以结构必须留好。

```csharp
public sealed class TradeItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public long UnitPriceUsd { get; init; }
    public string Currency { get; init; } = "USD";
    public int Stock { get; init; } = -1;          // -1 表示无限
    public List<string> Tags { get; init; } = [];  // 例如 ["消耗品","礼物"]
    public List<string> EffectKeys { get; init; } = [];
}

public interface ITradeCatalog
{
    bool TryResolveItem(string itemId, out TradeItem item);
    IReadOnlyList<TradeItem> All { get; }
}

public interface ITradeInventory
{
    int CountOf(string ownerId, string itemId);
    void Grant(string ownerId, string itemId, int quantity);
    void Consume(string ownerId, string itemId, int quantity);
}

/// 购买成功后的副作用处理器。注册表按 EffectKey 分发。
public interface ITradeEffectHandler
{
    string EffectKey { get; }                                  // "inventory.grant" / "mood.boost" / "contract.hire"
    TradeCheck CanApply(TradeContext ctx, TradeIntent intent);
    void Apply(TradeContext ctx, TradeIntent intent, TradeReceipt receipt);
}

public static class TradeEffectRegistry
{
    public static void Register(ITradeEffectHandler handler);
    public static IReadOnlyList<ITradeEffectHandler> Handlers { get; }
}
```

**本期交付要求**：
- 定义上述接口；
- 提供**空实现** `NullTradeCatalog`（`TryResolveItem` 恒返回 false）与 `NullTradeInventory`；
- 在 `CatalogValidator` 里接上 catalog，但当 `Items` 为空时直接通过；
- 在 `src/Catalog/` 下放一个 `items.json`（可暂空或只放 2–3 个示例）；
- **不要**实现真实物品逻辑、不要接原版道具系统。

**关键设计约束（复述一次）**：`TradeOffer.Amount` 与 `TradeOffer.Items` 是**并列字段**。将来"用 3 万加一件道具换某人加盟"这类复合交易不需要重构数据结构，只是 `Items` 非空。

### 5.6 新增文件清单（第 5 步）

```
src/Trade/
├─ TradeIntent.cs              协议解析结果
├─ TradeProtocol.cs            [Trade:]/[Buy:] 增量解析 + 剔除标记
├─ TradeContext.cs             求值上下文
├─ TradeCheck.cs               校验结果（含 Soft / InjectLine）
├─ ITradeValidator.cs
├─ ITradeExecutor.cs
├─ TradeEngine.cs              编排：解析 → 校验链 → Reserve → Commit
├─ Validators/
│  ├─ IntentShapeValidator.cs
│  ├─ AmountBoundsValidator.cs
│  ├─ CatalogValidator.cs
│  ├─ BalanceValidator.cs
│  ├─ DailyLimitValidator.cs
│  ├─ PersonalityFitValidator.cs   ★ 性格适配 + 施舍门槛 + 情绪值
│  ├─ FavourValidator.cs
│  └─ IdempotencyValidator.cs
└─ Executors/
   ├─ TransferExecutor.cs         转账/赞助/打赏/奖金/借款
   └─ PurchaseExecutor.cs         购买物品（骨架，接口就绪）

src/Catalog/
├─ ITradeCatalog.cs
├─ ITradeInventory.cs
├─ TradeItem.cs
├─ NullTradeCatalog.cs
├─ NullTradeInventory.cs
└─ items.json                     暂空或 2-3 个示例

tests/NationalSpireEconomy.Tests/
├─ TradeProtocolTests.cs          解析、增量分片、剔除标记、畸形输入
├─ TradeValidatorTests.cs         八条校验器逐条 + 优先级顺序 + 软失败
├─ TradeEngineTests.cs            Reserve/Commit、确认时重校验、幂等、过期
└─ 别忘了在 Program.cs 注册新套件
```

### 5.7 第 5 步必须有测试覆盖的场景（验收清单）

**协议解析**：
- 完整标记正确解析；跨分片的标记（分 3 次 `Feed` 喂入）仍能识别；畸形标记（缺 `]`、`Amount` 非数字、`Kind` 不在白名单）不崩且报错；
- `Amount` 用 `MoneyUnits.TryParseUsd` 接受 `5万` / `$50,000`；
- 剔除标记后正文不含 `[Trade:` 残留。

**校验器**：
- 八条逐条正例 + 反例；
- **优先级顺序**：同时违反多条时返回的是**第一条**（例如金额越界 + 性格拒绝 → 返回金额错误）；
- **性格适配**：`战绩踩头者`（charity 0.82）对陌生玩家的小额打赏被拒；对交情玩家（favour 90，×0.10）同样金额通过；
- **上限**：慷慨性格 vs 刻薄性格在同一余额下的 cap 差异；
- **软失败**：NPC 余额不足 → `Soft == true`、`InjectLine` 非空、**没有生成 `TradeOffer`**、`PendingVoice` 被写入。

**两段式执行**：
- `Reserve` 不动任何余额；
- **确认时余额已变**（Reserve 后手动扣掉 NPC 的钱）→ `Commit` 必须重新校验并失败，而不是照着旧校验码执行；
- 同一 `offerId` 重复 `Commit` → 第二次返回 `AlreadyApplied` 且余额不再变；
- 过期的 offer（`ExpiresDay < day`）→ `Commit` 失败并置 `expired`。

**回归**：现有 133 条必须继续全绿。

---

## 6. 已知的坑与经验教训

### 6.1 模组 id 冲突：同 id 的模组按版本号二选一，低版本被静默禁用

**现象**：自己改的分支（0.25.39）在装了创意工坊版（0.26.3）后"功能全没了"，日志里只有一行警告：

```
[WARN] Mod with ID NationalSpire is loaded both via Steam and local mods directory.
       Steam version (v0.26.3) is greater than local version (v0.25.39),
       so we are disabling the local version.
```

**教训**：我们的模组 id 必须是全新的。同时要意识到——**"弹幕模组没生效"这类问题的根因可能根本不是 API 不兼容，而是加载顺序/版本仲裁**。排查任何"模组失效"问题时，第一件事是看日志里有没有这条警告。

### 6.2 原版的 10 倍单位 bug

见 §4.3。同一份合同金额，模型看到 $30,000、玩家看到 $3,000。
**我们的应对**：`MoneyUnits` 是唯一换算出口；`Format(long usd)` 的入参**已经是美元**，绝不再乘 10；`UsdToInternal` 默认 `RequireExact`，非整单位直接抛异常逼调用方表态。

### 6.3 幂等收据的裁剪必须"保护优先于上限"

**坑**：一笔还在等玩家确认的交易，可能在队列里蹲很久。如果它的防重键被数量上限裁掉，玩家隔天点确认就可能**被扣两次款**。
**教训**：`ReceiptBook.Prune` 里受保护的键**永不裁剪**，即使受保护的键本身超过上限也一个都不丢。防重的重要性高于内存整齐。对应测试：`ProtectedKeysSurviveCapOverflow`、`ProtectedKeysCanExceedCapWithoutLoss`。

### 6.4 冲正的四层嵌套错误（**本次最大的教训，重点读**）
冲正（`Reverse`）的赛季统计曾经有**四层嵌套错误**，光读代码完全看不出来，是靠"**最小复现 + IL 反编译 + 运行期追踪**"一层层剥出来的：

| 层 | 症状 | 根因 |
| --- | --- | --- |
| 1 | 冲正一笔收入 → 记成一笔**支出** | 按金额符号归桶，`-5000` 落进了支出分支 |
| 2 | 修了归桶，科目仍错 | `Reverse` 构造冲正流水时没设 `ApplicationKind`，`Move` 里的回退把它填成了 `"冲正"` **自身** |
| 3 | 修了科目，收入仍不退 | `Move` 对**所有**流水都调 `Accumulate`（+方向），冲正流水应该走 `Revert`（−方向） |
| 4 | 修了路由，仍不退 | 冲正流水的金额**本身就是负数**，`Revert` 里 `amount > 0` 的判定又滑回支出桶 |

**最终方案**（写进了 XML 注释，防止被"顺手优化"回去）：

```csharp
// 冲正流水的金额符号与原流水相反，所以不能按符号归桶。
// 统一按"原流水的方向 + 是新增还是冲正"决定净额。
private static void ApplyStats(NpcEconomy npc, string kind, long amount, int direction)
//  direction = +1 表示新增，-1 表示冲正
//  原流水是收入：新增加收入桶，冲正从收入桶里减
//  原流水是支出：新增加支出桶，冲正从支出桶里减

// Revert 必须传【绝对值】：冲正流水的金额本身带反号（账务记录需要），
// 但"撤销的方向"已由 direction:-1 表达。把负号一起传进去会再次走错桶。
private static void Revert(NpcEconomy npc, LedgerEntry entry)
    => ApplyStats(npc, kind, Math.Abs(entry.Amount), direction: -1);
```

**方法论教训（比 bug 本身更重要）**：

1. **当"源码逻辑"与"运行结果"矛盾时，不要继续读源码，要立刻上证据。** 我在这上面绕了很久——反复读 `ApplyStats` 觉得没问题，但运行时 `seasonIncome` 就是不退。
2. **IL 反编译能排除"产物与源码不一致"**。`sandbox/ildump`（已删除，可重建）用 `MethodInfo.GetMethodBody().GetILAsByteArray()` 解出 `call`/`callvirt` 目标，确认了 `Move` 确实调用了 `Accumulate`、`Reverse` 确实调用了 `Move`。
3. **最小复现（独立 console 工程只做 Credit + Reverse）比在测试框架里猜快得多。** 那次一把就复现了，还顺带暴露了"冲正支出"方向也错。
4. **stderr 是缓冲的，插桩输出顺序会错乱。** 我一度被"看起来属于 A 测试的 trace 其实来自 B 测试"误导，浪费了整整一轮。**改为把状态拼进断言消息**（`TestRunner.Equal(expected, actual, "… | " + state)`）之后立刻定位到了问题。
5. **不变量比断言更值得写**：`LedgerEntriesAfterRoundTripStayConsistent` 验证"余额 = 初始值 + 流水求和"，这条不变量在四层 bug 里始终保持成立——它帮我排除了"余额算错"这个方向，把注意力集中到统计上。

### 6.5 档位判定的"同分先定义者赢"会让分层永久失效

**坑**：`普通玩家(Rating≥0)` 与 `普通玩家(Rating≥800)` 用"条件数"打分时同分，先定义的赢 → 高阶分层**永远不生效**。
**教训**：打分必须是 `(条件数, 门槛值)` 的**字典序**，条件数相同时门槛高者优先。

### 6.6 SDK 默认 glob 会把 `tests/` 吸进主程序集

**坑**：`Microsoft.NET.Sdk` 默认 `<Compile Include="**/*.cs" />`，`tests/` 下的顶层语句文件被编进类库，报 `CS8805: 使用顶级语句的程序必须是可执行文件`。
**教训**：主 csproj 里显式排除：

```xml
<ItemGroup>
  <Compile Remove="tests\**\*.cs" />
  <Compile Remove="config\**\*.cs" />
  <None Remove="tests\**" />
</ItemGroup>
```

### 6.7 曾把"容器对象"拆成多张单条目表

**坑**：`ai_prompt_economy.json` 里的 `_weightBands` 是**一个容器对象**，其内部键（国家名）是表的**条目**，不是表名。我一开始写成了"每个内部键建一张独立表"，结果 `weightBands` 表为空、反而多出 10 张单条目表。
**教训**：配置里"带下划线但其实是数据容器"的键要在**跳过下划线键之前**处理。已加回归测试 `ContainerTableIsNotSplitIntoSingleEntryTables`。

### 6.8 第 5 步实测出来的五个坑（协议层最容易踩）

第 5 步的协议解析一开始有 6 条用例失败，**全部是真实缺陷，不是测试写错**。
共同教训：**这类"看起来该对却不对"的问题，读代码没有用，必须打中间状态。**
这五个坑各自都加了回归测试钉住。

| # | 症状 | 根因 |
| --- | --- | --- |
| 1 | `Kind` 解析成 `"赞助, Amount: 50000"` | `Parse` 收到的 `content` **还带着 `Trade:` 前缀**，于是关键词本身被当成第一个字段。必须先把冒号之前的关键词剥掉再分段。 |
| 2 | `Amount: $50,000` 解析成 **50** | 按逗号分段时把千分位逗号也切了。<b>一笔五万美元的交易被记成五十美元</b>——金额类缺陷里最难发现的一种。修法：该段里出现过 `$` 且逗号后恰好 3 位数字到边界，就不切。 |
| 3 | `Reason: 这赛季刚拿工资，正好赞助你一点` 只剩前半句 | Reason 是最后一个字段，它之后的一切（含被逗号切开的**无冒号**尾巴）都必须拼回理由，并**补回那个逗号**。少了补逗号这一步，理由被静默改写且不报错。 |
| 4 | `[未知: [Trade: 赞助, Amount: 50000]]` 被改成 `x [] y` | 两处叠加：① 找括号组时用了"第一个 `]`"而不是**按深度配对**；② 不认识的分组没有原样放行。结果把一个属于原版的标记撕开了。 |
| 5 | 明明解析成功却判成失败 | `ParseError` 返回的是**空串**而不是 `null`。`ParseFailed` 按 `!string.IsNullOrEmpty` 判定，于是"没错误"被当成"有错误"。修法是双管齐下：解析器在无错误时返回 `null`，`ParseFailed` 也改成同时覆盖 null 与空白。 |

**方法论**：这轮我加了两个临时探针（`ProtocolProbe` 与 `RefProbe`），
用 `dotnet run -- probe` / `-- refs` 把中间状态与程序集引用表打出来，
定位完**立刻删除**（§7 自检项之一就是"没有残留插桩"）。
失败信息里带上实际值（`$"实际 <{x}>"`）比反复读代码快一个数量级——
这与 §6.4 第 4 条"把状态拼进断言消息"是同一条经验。

### 6.9 幂等校验器不能同时用于提交与确认（自锁）
**坑**：`Submit` 成功时会登记**意图级幂等键**。如果 `Confirm` 也跑第 8 条校验器，
那么玩家一点确认就会被判成 `ALREADY_APPLIED`——**交易永远无法完成**。
6 条引擎用例同时失败才暴露出来。

**教训**：同一个校验器在"提交"和"确认"两个阶段的语义**不同**，不能共用一条链。

- **提交链**（`Validators`，8 条）：意图键还没登记，第 8 条是有效的防重。
- **确认链**（`ConfirmValidators`，7 条）：**必须去掉第 8 条**。确认阶段的防重靠
  ① 交易单状态必须仍是 `pending`；② 动账幂等键 `trade:{world}:{offerId}`。

推论：**"第一条失败即返回"的链式校验，必须明确每条规则服务于哪个阶段。**

### 6.10 第 6 步实测出来的三个坑（注入层）

| # | 症状 | 根因 |
| --- | --- | --- |
| 1 | 模板缺失时稳定段里出现**空行** | `PromptConfig.Get` 对缺失键返回空串，直接塞进行列表就变成空行。空行也是"内容"：它会让指纹白白变化，还在 Prompt 里留一段莫名其妙的空白。修法：加一个只收非空行的 `Add` 帮助函数。 |
| 2 | `_weightBands` / `incomeSource` 查不到，静默退回 `default` | 这两张表的键是 **Role 名**（"职业选手"），不是档位 id（"professional"）。拿档位 id 去查会一律 miss，量级措辞全错但**不报任何错**。修法：先按 `Facts.Role` 查，查不到才退 `default`。 |
| 3 | 意向档位边界算错 | `trade_paid` 是 −0.02/次，10 次就是 −0.20，已经跨过 `cautious` 的下界（−0.15）进了 `stingy`。写档位测试时**别用"差不多"的次数**：要么挑一个明确落在区间内的值，要么把边界单独钉住。 |

**一条值得记住的设计判据**：`resentful` 是**叠加档**而不是分数档。
一个刚被连续赞助到 `generous` 的人，只要前几天被人拿小钱试探过，
措辞就必须立刻转向"最近有点介意被人小看"——所以 `BandIdFor` 先查记恨窗口，再按分数分档。

### 6.11 测试断言必须用独立探针核实，不能凭记忆

**坑**：我曾两次把 FNV-1a 的期望值算错（误以为 `""` 哈希为 0），还误判了 `Math.Round` 在 `.5` 上的行为，把**正确的实现**当成 bug 改。
**教训**：涉及位运算、取整、哈希的断言，**先用独立探针跑出真实值再写断言**。构建产物验证时也用同样的原则（见 §6.4 第 2 条）。

### 6.13 第 7 步实测出来的坑（适配层）

#### (1) `Godot.ProjectSettings.GlobalizePath` 在 Godot 之外调用会**直接杀死进程**

**症状**：单测跑到某个用例，整个进程消失，退出码 `-1073741819`（即 `0xC0000005`），
后半段用例一条都没跑。

```
Fatal error. 0xC0000005
   at Godot.NativeInterop.NativeFuncs.godotsharp_string_new_with_utf16_chars(...)
   at Godot.StringName..ctor(String)          ← ProjectSettings 的静态构造函数
   at Godot.ProjectSettings..cctor()
   at Godot.ProjectSettings.GlobalizePath(String)
```

**根因**：`Godot.ProjectSettings` 的静态构造函数会调 Godot 原生函数。
在 Godot 进程之外，那些原生函数指针是空的 → `AccessViolationException`。
而那是**损坏状态异常**，`catch (Exception)` **拦不住**，进程当场死。

**修法**（两道防护，缺一不可）：

```csharp
// ① 只在 GodotSharp 已经被加载时才尝试去碰它。
//    读 AppDomain 里已加载的程序集清单是安全的，不碰任何原生函数。
private static bool IsGodotAssemblyLoaded() =>
    AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is "GodotSharp");

// ② 只用一次。失败就记进 _globalizeFailed，之后永不重试。
private static MethodInfo? _globalizeMethod;
private static bool _globalizeFailed;
```

**连带发现**：测试的 `NseProbe` 一开始把 `GodotSharp` 也放进了程序集解析表，
于是"GodotSharp 已加载"成立 → 防护 ① 失效 → 照样崩。
**`NseProbe` 现在默认不索引 GodotSharp**，正好让测试走 `%APPDATA%` 回退分支
（那也正是要验的分支）。要验 Godot 分支只能在游戏内跑。

**回归测试**：`GodotIsNotLoadedInTestProcess` 钉住"测试进程不加载 GodotSharp"这个前提，
谁哪天把它加回解析表，这里立刻变红，而不是等到随机崩溃。

#### (2) `MoneyUnits.AddUsd` 拒绝负数——一笔从第 1 步潜伏至今的缺陷

**症状**：从玩家余额里扣钱时抛 `ArgumentOutOfRangeException: 金额不能为负 (Parameter 'usd')`。

**根因**：`AddUsd` 是"加一笔美元增量"的入口，而<b>支出/冲正/退款都是负增量</b>。
它原先直接把入参丢给 `UsdToInternalExact`，后者<b>刻意拒绝负数</b>
（负数在 Ceiling/Floor 下语义反直觉）。两者语义冲突，于是任何"扣钱"路径都走不通。

```csharp
// 修法：把负号剥掉再补回来。金额恒为 10 的整数倍时，除法与符号可交换。
decimal delta = usd < 0 ? -UsdToInternalExact(-usd) : UsdToInternalExact(usd);
```

第 1–6 步没暴露它，是因为那几层只处理 NPC 余额（走 `EconomyLedger`），
直到第 7 步把玩家钱包接上才第一次真正"扣玩家的钱"。

**教训**：**"只处理正数"的假设会在最晚的时候爆炸。** 一个叫 `Add*` 的增量 API
必须明确它是否支持负增量，并把这条写进 XML 注释——现在写了。

#### (3) 单位换算写测试断言时最容易犯的错

`1 内部单位 = $10`，所以 `$25,000` 是 **2500** 内部单位，不是 25000。
我连着写错三处断言（`AddUsd(1000, 0, -25000)` 期望 750、`DataWith(1000)` 当成 $100,000），
全都是把美元和内部单位混着算。

**应对**：写金额断言前先把两边都折算成同一单位写在注释里，
例如 `// 5000 内部单位 = $50,000`。**不要心算。**

#### (4) `Environment.GetFolderPath` 读的是注册表，不是 `%APPDATA%`

本机沙箱把 `APPDATA` 重定向到了 `D:\蓝色大肥鱼\sandbox\dotnethome\Roaming`，
但 `GetFolderPath(ApplicationData)` 仍返回 `C:\Users\Li\AppData\Roaming`（注册表的 shell 文件夹设置）。
**断言具体盘符会变成环境依赖。** 该断言的是契约：绝对路径 + 末级目录名 + 与原版同结构。

#### (5) 部署需要管理员权限

`mods\` 目录在 `D:\Steam` 下、受保护，从沙箱写会被拒。
构建与打包我来做，**复制进 `mods\` 必须你手动操作**（详见 `deploy/DEPLOY.md`）。

### 6.15 第 7 步阶段 2 实测出来的坑（前缀补丁）

#### (1) `Feed` 会把"标记之前的正文"一起押后——必须用 `StreamingFeed`

**症状**：分片是 `"开头的话 [Trade: 赞助, Amo"`（标记未闭合）时，玩家只看到空白，
`"开头的话 "` 要等标记补全才出现。

**根因**：`TradeProtocol.Drain` 只在"缓冲里连 `[` 都没有"时才放行文本。
一旦出现未闭合的标记，整个缓冲被留下，**包括标记之前那一段已经确定的正文**。

**修法**：给 `Drain` 加一个 `flushPrefixBeforeIncomplete` 开关，并新增
`TradeProtocol.StreamingFeed`：

```csharp
// Feed：押后处理，整段解析完再一起给出（语义简单，适合一次性解析）
// StreamingFeed：把标记【之前】的正文立刻放行，未闭合的标记继续等下一个分片
//                ——逐分片推进 UI 的场景必须用它
intents = state.Protocol.StreamingFeed(text, state.Clean);
```

**教训**：一个"处理流式输入"的解析器，"还没法解析的部分"和"还没送达的部分"
是两件事。前者可以等，后者必须立刻给出去。

#### (2) 补丁会改写交给**原版**的文本——"不弄坏原版"比"实现功能"更重要

前缀补丁是在别人家的解析器上动刀。`PrivateStreamParser` 的 `<c>_pending</c>` 括号栈
会因为我们的标记被抽走而**孤立出一个 `[`**，如果 `Finish()` 没被拦住，
它会作为垃圾字符进入玩家的正文。

因此：**`Finish` 也必须拦**（它负责清理），并且原版的 `<c>_pending</c>` 必须照常工作。

对应的验收测试刻意分成两组：
- **功能组**：标记被抽走、正文一字不差。
- **不弄坏原版组**：`[Profile: ...]` / `[Favour: ...]` 仍能解析、普通方括号 `[重要]` 仍显示、
  不认识的括号组（含嵌套）整组放行。

**教训**：给别人的方法打补丁时，**"原功能没坏"的测试必须和"新功能生效"的测试一样多。**

#### (3) 构建脚本 `Remove-Item deploy` 把 `deploy/DEPLOY.md` 一起删了

**症状**：写完部署文档，跑一次打包脚本，文档就没了。

**根因**：打包脚本第一步清理 `deploy\` **整个目录**，而文档就躺在 `deploy\` 下。

**修法**：只清包目录本身（`deploy\NationalSpireEconomy`），不要清 `deploy\`。

**教训**：`Remove-Item <目录>` 之前先问一句"这个目录下还有没有别人的东西"。
删非计算出来的固定路径时尤其要看一眼。

### 6.16 其他环境坑
- **PowerShell 5.1**：`Get-ChildItem -Directory` 在某些上下文会报"找不到参数"，改用 `Where-Object { $_.PSIsContainer }`。
- **`dotnet` 首次运行**：必须重定向 `APPDATA`/`USERPROFILE`/`DOTNET_CLI_HOME`，否则写 `C:\Users\Li\.dotnet` 被拒。
- **`dotnet tool install` 不可用**：出网被拦，装不了 ilspycmd。需要反编译时用自建的反射 + IL 读取工具（见 §6.4）。
- **中文金额解析**：`MoneyUnits.TryParseUsd` 已处理全角/半角、千分位、`万亿千百`；PowerShell 读 JSON 时中文会有编码问题，**用 .NET 的 `System.Text.Json` 而不是 `ConvertFrom-Json`** 来验证配置。

---

## 7. 给接手者的建议工作流

1. **先读**：本文 §1（铁律）、§3.7 / §3.9（第 5、6 步交付）、§6.8 ~ §6.10（踩过的坑）。
2. **再跑**：按 §2.3 的命令跑一遍测试，确认 **265/265 全绿**。这是你的基线。
3. **然后读代码**：至少读 `EconomyModels.cs`、`EconomyLedger.cs`、`PersonalityEconomy.cs`、
   `TradeProtocol.cs`、`TradeEngine.cs`、`PersonalityFitValidator.cs`、
   `StableSegment.cs`、`PromptInjector.cs` 八个文件。第 7 步建立在这些之上。
4. **第 7 步建议顺序**：打开 `-p:GameRefsEnabled=true` → 写 `INpcFacts` 的投影适配器
   （`CareerPerson` → `NpcFacts`）→ 接 `IPlayerWallet` 到 `CareerData.Credits`/`CreditFraction`
   → Harmony 补丁 `PrivateStreamParser.Feed`（喂 `TradeProtocol`，把剔除后的正文交回原方法）
   → 在 `PrivateMessagePrompts` 的 Stable/Current 两处分别插入 S 段与 V 段 → 把 `EconomyTick.AdvanceTo`
   挂到日期推进上（并调用 `TradeEngine.ExpireStale`）。
   接线时记得用 `MoneyUnits` 做唯一的单位换算，且**永不写原版存档**。
5. **提交前自检**：
   - 265 + 新增用例全绿；
   - `Release` 构建 0 警告；
   - **没有引入任何游戏程序集依赖**（第 1–6 步实测引用表只有 12 个 `System.*`，见 §6.8 末尾的探针做法）；
   - 没有残留插桩代码（搜 `NSE_TRACE` / `[trace` / `Console.Error.WriteLine`，只应命中 `EconomyLog.cs`）；
   - 所有新的金额换算都走了 `MoneyUnits`（搜 `\*\s*10\b|/\s*10\b`，只应命中 `MoneyUnits.cs` 与 `EconomySeed` 的评分归一化）。

---

## 8. 术语表

| 术语 | 含义 |
| --- | --- |
| **WorldId** | 原版 `CareerData.WorldId`，8 位 hex，一份生涯的唯一标识。我们所有数据按它分片 |
| **档位 / tier** | 财富分级（`crowd`/`youth`/`media`/`veteran`/`professional`/`elite`/`default`），决定绝对金额量级 |
| **Kind** | 原版 24 种性格之一，决定相对经济系数 |
| **六轴** | `give`/`charitySensitivity`/`bargain`/`thrift`/`borrow`/`initiative` |
| **好感档位** | `stranger`/`familiar`/`close`/`intimate`，只修正 `charitySensitivity` |
| **软失败** | NPC 想给钱但余额不足——不报错、不弹窗，只把素材注入下一轮 Prompt 让它自己说 |
| **幂等键 / receipt** | `"category:worldId:parts"` 形式的字符串，同键只生效一次 |
| **冲正 / reverse** | 撤销一笔已记账的流水。用**新增一条反向流水**而非删除原条（审计需要） |
| **S 段 / V 段** | Prompt 注入的稳定段（system 尾部，零数字）与易变段（user 的 Current 部分，含数字） |
| **前缀缓存** | 服务端对 prompt 前缀的缓存。S 段必须稳定才能命中，所以数字只能进 V 段 |
| **两段式执行** | `Reserve`（生成待确认单，不动余额）+ `Commit`（确认时**重新校验**后动账） |

---

*文档结束。如果有任何与本地代码不符之处，以本地代码为准，并请顺手更新本文档。*
