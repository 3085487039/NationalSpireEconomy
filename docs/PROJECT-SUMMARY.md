# NationalSpireEconomy 项目总结

> **撰写时间**：2026-10-12。本文所有数字、哈希、代码行号均为**当时实测**。
> 与本地代码不符时**以本地代码为准**，并顺手更新本文档。
>
> **本文性质**：一次完整总结，回答三个问题 ——
> ① mod 实现了什么；② 现在的链路是什么样；③ 后续注意事项。
> 面向接手者、贡献者与发布维护者。
>
> 上游与授权：[`NOTICE.md`](../NOTICE.md) ｜ 贡献流程：[`CONTRIBUTING.md`](../CONTRIBUTING.md)
> ｜ 文档索引：[`docs/README.md`](README.md)

---

## 一、Mod 实现了什么

**一句话**：给《Slay the Spire 2》的国运尖塔（NationalSpire）NPC 加上**真正的钱** ——
余额、工资、赛季收入，以及**玩家 ↔ NPC 之间会真正转账的交易系统**。

它是**二开适配模组**：不修改上游源码或 DLL，而是用 Harmony 把经济逻辑挂在
上游的私信与生涯流程上（`AiService.RequestPrivateAsync` 前缀、
`AiService.ProcessPrivateAsync`、`CareerEngine.MoveToNextDay` 后缀、
`PrivateStreamParser.Feed` 前缀）。

### 1.1 已验收的能力

| 能力 | 状态 |
| --- | --- |
| NPC 资金账本：余额、赛季工资、赛季/累计统计、转会费与奖金 | ✅ 已实现 |
| **只读成交预览 + 双人确认**（钱一分不动的那一步） | ✅ 真机验收通过 |
| **真正动钱 + 审计落盘** `audit/<WorldId>.jsonl` | ✅ 真机验收通过 |
| **NPC → 玩家**方向的结算（玩家进钱） | ✅ 真机验收通过 |
| **玩家 → NPC**方向的结算（**玩家真的掉钱**） | ✅ 真机验收通过（两笔 $3,000） |
| 账本真相通道（让模型无法谎称"钱已转"） | ✅ 真机验收通过 |
| 投票（确认）绝不动钱 | ✅ **结构上**保证（只读快照钱包） |
| 审计行含天数（老世界水位线为 0 时兜底） | ✅ 真机验证 |
| `确认` 误报 Warning 收紧 | ✅ 真机验证（零假 Warning） |

### 1.2 六个机制要点

1. **四道前置闸**：动钱开关 → 方向白名单 → 双方确认 → 单子仍 pending 且未过期。
   **任一不满足，一个数字都不改。**
2. **幂等**：键 `trade:{world}:{offerId}`；重复结算报"已结算过"，一分不动；
   失败路径**释放**整笔键，使"钱不够"之后凑够钱仍可重试。
3. **原子性**：一侧失败整笔回滚。顺序刻意是"NPC 先动、玩家后动"
   ——NPC 的钱在我们手里、失败可回滚；玩家的钱动过不好撤。
4. **确认路径的类型安全**：投票用 `InMemoryPlayerWallet`（只读快照），
   动钱只能用 `ShadowPlayerWallet` ——于是"确认绝不动钱"是**类型层面**的保证，
   不靠"我记得没调 TryAdd"。
5. **重启可证**：结算必须落盘，判据**只看** `worlds/<WorldId>.json` 的 `LastTrade` 键。
6. **反谎报**：角色卡会被告知"只有写着「已结算」的钱才算真的动过"；
   模型靠"嘴上说转了"改变不了账本。

### 1.3 玩家能感知到的东西

- NPC 会**赞助 / 打赏 / 发奖金**给玩家；玩家可以**雇佣 / 转账**给 NPC；
- 每笔交易先给一份**只读预览**（双方余额怎么变、八条校验各自怎么说、有效期到哪天）；
- 只有**双方都确认**才会真的动钱，且**会在私信里留下回执**；
- 玩家花掉的钱来自影子余额，**原版界面上的数字不会变**（见 §3.2）。

---

## 二、现在的链路全貌

### 2.1 一次交易的完整七步

```
① 玩家在私信发话（自然语言，绝不能含 [Trade: …] 标记）
      ↓
② 注入：S 段（零数字·逐字节稳定，保住前缀缓存）+ V 段（余额/最近结算/账本口径）
      ↓
③ 模型输出 [Trade: 雇佣, Amount: 3000, Direction: playerToNpc]
      ↓  TradeProtocol：流式状态机抽取标记，并从显示正文里剔除
④ 回声闸：标记与玩家本轮原话逐字相同 → 判为"引用玩家的话"，不落单
      ↓  TradeIntentCompleter：补全方向与双方（显式 > kindDirections 映射，都没有则拒绝）
⑤ 八条校验器：形状/金额边界/余额/每日上限/好感/目录/性格适配/幂等
      ↓  Reserve：落 pending 单【不动钱】；NPC 那一票自动投上
⑥ 只读预览 + 确认弹窗；玩家点「好的」或回「确认 o0004」
      ↓  只用只读快照钱包登记玩家那一票
⑦ 双方到齐 → 结算器四闸 → 动账 → 先写审计 → 强制落盘 → 写 LastTrade
```

### 2.2 真机证据（2026-10-12 13:32–13:34，交易单 `o0004`）

```
13:32:26  识别出交易标记（第 1 条）：雇佣 $3,000
13:32:26  生成待确认交易单 o0004：雇佣 $3,000（player_to_npc）
13:32:26  player：$195,020 → $192,020（−$3,000）（发起方）
13:32:26  tour-c51-5：$809,000 → $812,000（+$3,000）（对手方）
13:34:46  双方已确认，立即结算（trade.settlementWired = true）
13:34:46  [Economy-Settle] o0004｜Applied｜雇佣 $3,000｜tour-c51-5 $809,000 → $812,000｜玩家 $195,020 → $192,020
```

- **预览与结算逐字一致**（这正是第五步修掉的那个 bug：预览曾用原版数字，差 $1,200）；
- 审计行 `Direction = player_to_npc`、`Day = 452`（不是 0）；
- 审计账实自洽：`197,320 + 500 + 700 − 3,000 − 3,000 = 192,020`。

### 2.3 三条真相通道

| 通道 | 位置 | 作用 |
| --- | --- | --- |
| 影子存档 | `worlds/<WorldId>.json` 的 **`LastTrade`** | **落盘与否的唯一判据**；重启后回执仍在 |
| 审计 | `audit/<WorldId>.jsonl` | 每笔真实结算一行，含方向与双方余额前后；**只追加、不修改** |
| 注入 | V 段 | 模型与玩家在私信里看到的账本口径 |

### 2.4 分层与依赖方向

```
src/Core     零依赖：日志、金额换算、配置加载、确定性哈希
src/Economy  账本、工资/赛季推进、影子玩家钱包、收据本、存档
src/Trade    交易引擎：校验链、执行器、双人确认、结算、预览、文案
src/Inject   Prompt 注入（S 段 / V 段）、审计
src/Game     ★ 唯一依赖游戏与上游程序集的适配层
```

⚠ `src/Game` 在 `GameRefsEnabled=false` 时**整体被排除**，
于是 `Core/Economy/Trade/Inject` 四层可**零依赖**编译与单测（这就是两套基线的由来）。

### 2.5 关键文件与行号（实测）

| 文件 | 要点 |
| --- | --- |
| [`src/Trade/TradeSettlement.cs`](../src/Trade/TradeSettlement.cs) | `AllowedDirections`(:119)、方向闸(:182)、审计口径(:244)、`DayForAudit`(:446) |
| [`src/Game/TradePreviewWiring.cs`](../src/Game/TradePreviewWiring.cs) | `WalletFor`(:655，预览/投票同源)、`SettlingLine`(:622)、`ConfirmationEffectLine`(:603) |
| [`src/Economy/EconomyLedger.cs`](../src/Economy/EconomyLedger.cs) | `Transfer`(:225)、玩家余额闸(:249) |
| [`src/Economy/ShadowPlayerWallet.cs`](../src/Economy/ShadowPlayerWallet.cs) | `ReadOnlySnapshot`(:88)、未导入拒动钱(:145) |
| [`src/Trade/Executors/TransferExecutor.cs`](../src/Trade/Executors/TransferExecutor.cs) | `fromPlayer` 分支(:43) |
| [`src/Trade/TradeNoteText.cs`](../src/Trade/TradeNoteText.cs) | `PlayerPaid`(:213)、`ReceiptLine`(:53) |
| [`src/Trade/PlayerTradeCommand.cs`](../src/Trade/PlayerTradeCommand.cs) | `LooksLikeCommandAttempt`(:86)、`MatchLine`(:115) |

---

## 三、后续注意事项

### 3.1 ✅ 已定案：署名保留 `Dora`

**维护者 2026-10-13 拍板：继续用 `Dora` 作显示名。**

| 位置 | 定案后的值 |
| --- | --- |
| `LICENSE:2` | `Copyright (c) 2026 Dora`（**不改**） |
| `NOTICE.md:13` | 作者 `Dora`（**不改**） |
| `NOTICE.md:15` | 仓库地址 —— 见下 |
| `git remote origin` | `https://github.com/3085487039/NationalSpireEconomy.git` |

**唯一遗留的小事**：`NOTICE.md:15` 的仓库地址若仍写着 `…/Dora/…`，
它与真实的 `git remote`（`3085487039`）不一致 —— 那是**给玩家看的反馈入口**，
写错了会让问题报不到地方。定案是"署名用 Dora、仓库地址用真实账号"。

> 采纳哪个都不影响许可证合规（PolyForm 只要求传递许可证本身与出处声明，
> 不要求署名必须是账号名）。改完地址跑一次 `build-deploy.ps1` 即可自动同步所有哈希。

**重打发布包不再需要手工同步哈希**（2026-10-13 起）：`build-deploy.ps1` 会重建
`release\` 与 zip 并自动写入 `SHA256SUMS-release.txt`。

> ⚠ 别忘了一件事：`init-repo.ps1` 会往 `release\` 里回填署名/地址，而**回填会改包内文件、
> 却不重算包内 `SHA256SUMS.txt`**。改这些请走 `build-deploy.ps1`（它先重建再算哈希），
> 不要单独跑 `init-repo.ps1`。

### 3.2 设计约束（改代码前必须知道）

| 约束 | 说明 |
| --- | --- |
| **`npc_to_npc` 未接线** | 转会费/工资/奖金那套业务没验收过。方向闸是**白名单**，**别整条删掉**——删了会放开第三个方向 |
| **玩家发起交易未支持** | 玩家自己打 `[Trade: …]` **不会落单**（回声保护，防"模型复读玩家的字 = 替 NPC 同意"） |
| **影子余额与原版 `Credits` 分叉** | 甲方拍板的设计选择（1(a)）。玩家花钱只从影子扣，**原版数字冻结是正常的**；**绝不写原版存档** |
| **动钱开关默认 `false`** | 有专门测试拦"改默认值"。真机验收要临时打开、验完关回并重启 |
| **S 段零数字** | 稳定段必须逐字节稳定以保住前缀缓存；带金额的一律进 V 段或 UI 或日志 |
| **单笔上限 = 影子余额**、单日上限沿用 `dailyLimitPerNpc` | 不是新配置，是余额闸与既有校验器的语义 |

### 3.3 已知遗留

| 遗留 | 影响 | 备注 |
| --- | --- | --- |
| **结果弹窗被私信模态挡住** | 此时结果只写 V 段与日志（未做的 C 项） | 回执在私信经济段与日志里照常可见 |
| **模型爱自创交易类型** | 例如把"雇佣"写成"陪练费" → `BAD_KIND` | 属**协议措辞**问题；长期解法是在 `config/ai_prompt_economy.json` 里把六个允许的 Kind 明确列出 |
| **V 段的"最近结算"会误导模型** | 真机见过模型因看到上一笔"已结算"而以为"已付过"、于是不发新标记 | 属协议/消歧问题，可在 V 段加"以上是历史记录"的措辞 |
| **`deploy/DEPLOY.md` 首行仍写"阶段 0/1/2/3"** | 纯文档陈旧 | 措辞待刷新 |
| **预览里玩家那行出现两次** | 纯文案问题 | 甲方已明确"先不改" |

### 3.4 工程纪律（血泪换来的，务必遵守）

1. **两套基线顺序是硬约束**：零依赖先（**395/395**）、含游戏引用最后（**530/530**）。
   反了会红两条"环境敏感"用例（已知行为，不是 bug）。
2. **打完包不要再跑零依赖基线** —— 会把 `bin/` 里的全量 DLL 覆盖成缩减版，
   随后真机加载的就是残废包。顺序永远是：跑基线 → 打包 → 部署。
3. **每条出口都要有测试，且优先"只从磁盘读"来判** —— 这个项目**两次**单测全绿而真机失效。
4. **凡是"阶段范围/开关"的拍板，都要在代码里找到那个"不许过"的判定点**，否则它只是愿望。
5. **诊断判据必须与解析器同源** —— 曾因"全文包含 vs 行首"的判据不一致而刷出假 Warning。
6. **日志也是产品的一部分** —— 有一句"尚未接线"在开关打开时成为假话，
   而且**同一句在 3 处**；修第一遍时只找到 2 处。改措辞要 `grep` 同一个常量。
7. **`.ps1` 必须带 BOM 或纯 ASCII** —— 本机是 Windows PowerShell 5.1，
   无 BOM 的 UTF-8 脚本会被读成 ANSI、中文乱码导致脚本直接失效。
   （`build-deploy.ps1` 靠"全文件只用 ASCII"躲过去；`init-repo.ps1` 已存成带 BOM。）
8. **配置只能用 JSON 感知的方式改** —— `Set-Content -Encoding utf8` 会加 BOM、
   把 `0.40` 改成 `0.4`、把数组拆行。用 Python 的 `json` 读写。
9. **发布包必须带 `LICENSE` + 上游许可证逐字副本 + `NOTICE.md`** ——
   上游 PolyForm 的 Notices 条是硬要求。`build-deploy.ps1` 已修成会自动拷贝
   （原先 `Remove-Item $stg` 会在重新打包时删掉它们，使发布包不合规）。
10. **绝不把上游 `NationalSpire.dll` 放进仓库或发布包**；
    `deploy/`、`release/` 等含二进制的产物**不进 Git**，走 GitHub Releases。

### 3.5 可复现的验证命令

```powershell
# 仓库状态
cd <仓库根目录>
git log --oneline                       # 应有 1 条首次提交
git ls-files | Measure-Object -Line     # 140
git status                              # 应为 clean

# 发布包完整性（哈希以仓库根 SHA256SUMS-release.txt 为准，由 build-deploy.ps1 生成）
Get-FileHash release\NationalSpireEconomy-二开-v0.1.0.zip -Algorithm SHA256
Get-Content SHA256SUMS-release.txt
# 包内逐项核对：
Get-Content release\NationalSpireEconomy-二开-v0.1.0\SHA256SUMS.txt

# 两套基线（顺序不可反）
dotnet run --project tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj -c Release
dotnet run --project tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj -c Release -p:GameRefsEnabled=true
```

### 3.6 当前交付状态（实测）

| 项 | 值 |
| --- | --- |
| 仓库 | **140 个文件**，2 次提交（`15a6f71` 首次、`2c722bb` 标记可见性） |
| 发布包 | `release/NationalSpireEconomy-二开-v0.1.0.zip`，**210,673 字节**，`19612A8D…` |
| 交付包 DLL / 线上 DLL | `458752` / `892FE1D1…`（bin / deploy / release **三处一致**） |
| 线上 `settlementWired` | `false`（交付默认值） |
| 基线 | 零依赖 **395/395**、含游戏引用 **530/530**、三种构建 **0 警告** |
| 打包 | `build-deploy.ps1` 一条命令产出 deploy + release + zip + `SHA256SUMS-release.txt`；zip **可复现**（固定时间戳） |
| 文档 | `docs/` 共 **11 份**（含索引 `docs/README.md`），覆盖阶段 0~4 全程交接与数十条坑位 |
| 尚未完成 | `git push`（凭据不在本仓库）；GitHub Release 上传 ZIP |

> **DLL 哈希会随代码改动而变**：上表的 `892FE1D1…` 是**当前工作区**（含未提交修复）实测值，
> 并非线上 DLL。线上 DLL 的哈希请以 `docs/HANDOFF-PHASE-4-STEP5.md` §1 为准。

### 3.7 反馈问题时请附上的东西

1. `%APPDATA%\SlayTheSpire2\national_spire_economy\mod-debug.log` 的**相关片段**（整份很大，不必）；
2. `worlds\<WorldId>.json` 里 **`LastTrade` 键**的内容；
3. `audit\<WorldId>.jsonl` 的最新几行。

这三处是判定"这笔交易到底成没成、钱动没动"的**唯一**依据。

---

*本文档结束。与本地代码不符时以本地代码为准，并顺手更新本文档。*
