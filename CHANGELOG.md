# 更新记录（CHANGELOG）

格式参照 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

> **"已真机验收"的含义**：不只是单测通过，而是在真实游戏进程里跑过一遍，
> 并用三处证据对账 —— `mod-debug.log`、`worlds/<WorldId>.json` 的 `LastTrade` 键、
> `audit/<WorldId>.jsonl`。本项目的多数 bug 都是单测全绿而真机失效。

---

## [未发布]

对 P0（交付正确性）与 P1/P2/P3（动钱正确性、真相通道、测试质量）几轮审查发现的问题做的修复，
外加**阶段 4 B 项：玩家发起交易（形态 b）**。
**基线：零依赖 395/395、含游戏引用 533/533、三种构建 0 警告。**

### 新增（B 项：玩家发起交易 —— 形态 b「NPC 有权拒绝」）

维护者 2026-10-14 拍板做 B 项的 **(b)** 形态：**玩家提出 → 等模型回 `[Deal: accept]` 才算 NPC 同意**。
（另一种 (a)「玩家发标记就自动落单、NPC 那一票自动投」被否掉 —— 那等于玩家一开口 NPC 就必须答应。）

- **玩家可以在私信里自己打 `[Trade: …]` 了**（此前被回声保护按下去，只当正文）。
  新增 `TradePreviewWiring.HandlePlayerIntent`：走**与模型那条完全同一套**的
  意图补全与八条校验链，Reserve 落 pending 单（**不动钱**）。
- **关键差异：落单时【不投 NPC 那一票】。** NPC 的票要等模型明确回 `[Deal: accept]`
  （新增 `TradePreviewWiring.HandleDealAccepted`）。
- **完整语义**：玩家提出 → NPC 同意 → **玩家自己再确认** → 才结算（四道闸一道不少）。
  「模型说了 accept」**不等于**钱走了 —— `DealAcceptCastsNpcVoteAndSettles` 把这三段
  逐段钉住（accept 之后单子仍是 pending、两边余额一字不动）。
- **`[Deal: accept]` 从"只剥不解析"改为"剥 + 解析"**：
  `TradeProtocol.DealAccepted` 暴露给接线层，`StripOnlyKeywords` 仍然负责"不当交易意图解析"
  （它没有 Kind/Amount，硬解只会刷假的 PARSE_ERROR）。
  每轮结束在 `RewriteFinish` 里复位，避免下一轮"白捡"一个同意。
- **只认最近一张"玩家发起且待确认"的单**；没有就忽略并记日志 —— 宁可拒绝也不猜
  （猜错就等于替 NPC 同意了另一笔）。
- **提示词加回 `[Deal: accept]`（D1 反转）**，但这次说清了**谁先提**：
  「如果玩家先在消息里提出了交易（你会看到一行 `[Trade: …]` 标记），那笔交易要由你来表态：
  同意就写一行 `[Deal: accept]`，不同意就不要写它、并照你的性格把理由说出来」。
  S 段仍**零数字**（已复核）。

### 测试 +3（530 → 533）

- `PlayerInitiatedTradeWaitsForModelConsent`：玩家标记落单、**NPC 票未投**、单子 pending、钱一分不动。
- `DealAcceptCastsNpcVoteAndSettles`：accept 只投 NPC 票（还差玩家那一票）→
  玩家确认后才真的结算，两个余额都变。
- `DealAcceptWithoutPlayerOfferIsIgnored`：没有可认领的单时 accept 必须被忽略
  （含"模型自己提议的单不该被 accept 再认领"的对照）。

### 修复（本轮顺带查出的两个测试陷阱，已写进代码注释）

- **`LastSettlement` / `LastPreview` 是进程级静态量**，`Clear(worldId)` **不清它们** ——
  测试里读到的是**上一个用例**留下的陈旧值（实测读到"已结算（私信指令）"而单子其实还是 pending）。
  测试请按世界查（`PendingFor` / `LastNoteOf` / `TradeConfirmationGate.Status`）。
- **`MoneySnapshot` 含 `offers=N`**：落单/结算本来就会增加单数，用它比较永不相等。
  比较"钱有没有动"要用 `BalanceSnapshot`（余额 + 流水）。

### 变更（P3：出厂不再背排障工具的代价）

- **探针默认关闭**：`injection.probeEnabled` 的默认值从 `true` 改成 **`false`**
  （配置、`ConfigSchema` 一起改）。它会给 19 个候选方法各挂 3 个补丁并在**每次调用**时
  同步写文件 —— 真机实测一次私信的 `Feed` 就命中 4369 次，也就是每说一句话往盘上写几千行。
  这是阶段 3 排查"谁被执行了"时造的工具，那次已定案，正常游玩不该继续付这个代价。
  `NSE_PATH_PROBE=1` 仍可强制打开旁路取证（不改配置的临时手段）。
- **测试跳过不再静默**（`TestRunner.Skip`）：新增 `SKIP` 状态与"跳过明细"。
  原先几条最贵的回归用例（多副本挂载 / 落点警卫）在环境缺 `NationalSpire.dll` 时
  只能裸 `return`，输出上与 `PASS` **完全一样** ⇒ 它们在干净环境（CI）里会静默变成空操作
  而没人知道。现在既显式标记、又在结尾列出"这次没有验证任何东西"的用例，原因也带上根因。
- **测试脚手架去重**：`FindConfigDirectory` 那段"逐级向上找 config/"的代码原先在
  **19 个测试文件里各抄了一份**（返回类型已经开始漂移）。收敛到共享的 `TestConfig`，
  各文件只保留一行委托，签名不变。

### 修复（标记可见性：`[Deal: accept]` 泄漏 —— 与 `[Trade: …]` 同一个形状）

- **`[Deal: accept]` 会显示给玩家**：提示词明确要求模型"同意对方提出的交易时再写一行
  `[Deal: accept]`"，但剥离器的关键词表只有 `["Trade", "Buy"]`，于是**四个剥离出口
  （流解析 / 显示出口 / 并行可见流 / 存档固化文本）全都漏掉它** ——
  玩家会在私信里直接看到 `[Deal: accept]`。这正是当初为 `[Trade: …]` 来回 4 轮的那个问题，
  而它只修了一半。
  现在把 `Deal` 登记进 `TradeProtocol.MarkerKeywords`，并单独标为
  `StripOnlyKeywords`（**只剥、不当意图解析**）—— 它没有 `Kind`/`Amount`，
  硬解只会产出一条假的 `PARSE_ERROR` 把真故障淹掉。
  回归用例加在 `DisplayStripRemovesMarkerWithoutTouchingBody` 里，
  并要求"提示词让模型写的每一个标记都必须能被剥掉"。

### 变更（维护者 2026-10-13 拍板）

- **删除 `[Deal: accept]` 提示词**（D1）：既然没有代码消费它、NPC 那一票本来就来自
  `[Trade: …]`，那这段指示对模型是**空操作** —— 保留它只会让模型多写一行机器语法，
  增加泄漏面。已从 `stableBlockSuffix` 移除（S 段仍零数字，已复核）。
- **署名保留 `Dora`**（D2）；`NOTICE.md` 的仓库地址改成真实的
  `https://github.com/3085487039/NationalSpireEconomy`（那是给玩家的反馈入口，
  写错会让人报不到地方）。
- **NPC↔NPC（D3）明确搁置**：两道闸挡着、有测试钉住、没有需求方与入口 ——
  要做必须先出业务设计（谁发起、双方票怎么来、`ClubId` 联动与否）。


- **`IdentitiesIsUsedWhenRoleMisses` 原先只断言"结果非空"**（任何非空字符串都通过）。
  改成可证伪的写法后查出：**交付配置里没有任何档位使用 `identitiesContains`**，
  只有 `roles` —— 也就是说那条判定分支（`EconomyTier.cs:110`）在生产里是**死的**。
  现在用例用自造的档位表验"能力"、带对照组，并把"交付配置未使用它"这一事实也钉住。
- **`PersonalityRefusalAtConfirmAlsoRecordsGrudge` 有两条互斥路径**：实测走的是
  "Reserve 就被拒"那一支，`engine.Confirm` **从未被调用**，而用例名承诺的正是"确认阶段"。
  现在显式构造"Reserve 通过、确认时才失败"（抽走 NPC 余额 ⇒ 确认时重跑校验链必然失败），
  保证要验的那条路径一定被执行。
- **`GodotIsNotLoadedInTestProcess` 里那条 `True(true)`** 是恒真断言，什么都不验。
  改成验真正的契约：没有原生运行时时 `ResolveDataRoot` 仍须给出**绝对路径**的回退。

### 修复（P3：名不副实的断言 —— 改强之后立刻查出两件真相）

### 修复

- **存档清理器会把失败当成功**（`MailboxSanitizer.Sanitize`）：原先在取
  `Life`/`Mailbox`/`Conversations` **之前**就 `Done.Add(data, data)` 登记"已处理"，
  于是"结构取不到"的提前返回会把那份数据**永久**标记成处理过（`ConditionalWeakTable`
  按对象身份记），而 `WorldFor` 的注释还承诺"下次启动会再清一遍"——对同一份对象并不成立。
  真机症状会是"标记一直在，日志说清过了"。现在登记挪到三层结构都确认拿到之后；
  取不到就不登记，下次调用仍会重试，日志也明说"本次不登记"。
- **玩家余额导入的静默截断**（`ShadowPlayerWallet.ImportOnce`）：
  `(long)MoneyUnits.BalanceUsd(...)` 会把非整数美元（`CreditFraction` 含亚单位分数时
  折算出的角分）**悄悄丢掉最多 $9**，是全模块唯一违反自己"不静默丢精度"教条的地方。
  现在非整数美元**拒绝导入**且不置位水位线（与"负余额拒绝导入"同一种保守取舍：
  宁可响亮地不动钱，也不要一个悄悄少几块钱的余额）。
- **余额溢出的错误码**（`EconomyLedger.Move`）：裸的 `npc.Balance + deltaUsd`
  在接近 `long.MaxValue` 时会静默回绕成负数，紧接着被误报成 `InsufficientFunds`——
  错误码与真实原因不符。现在用 `checked`，溢出报 `Rejected` 并写明原因，
  与玩家侧 `ShadowPlayerWallet` 的强度一致。
- **★ 仅凭 `Confirmed` 状态就宣称「已结算」**（`TradePreviewWiring.NoteClosedTrade`）：
  `TradeEngine.Confirm` 在**幂等命中**时也会把单子置成 `Confirmed`，而它的判据只是收据本；
  收据会被裁剪，NPC 流水本身又是**每 NPC 64 条的环形**。于是一张很久以前结算过的单，
  可能只剩一个状态位与一个**悬空的流水 id**，而旧代码会照它的余额重建一条"已结算"回执——
  **那是仅凭状态位替账本撒谎**，而这条通道的全部意义就是"钱到底动没动"。
  现在加了证据闸：流水 id 必须真的还在某一方的流水里；对不上就写
  **「无法核实」**（新状态 `TradeNoteStatuses.Unverified`，**刻意不算 `Settled`**），
  既不宣称已结算也不宣称没动过，并指引玩家去核对 `audit/<WorldId>.jsonl`。
- **结算抛异常时不留任何痕迹**（`TradePreviewWiring.TrySettle`）：`TradeSettlement.Settle`
  内部自有 try/catch 与落盘，但异常若抛在 **`Settle` 之前**（组装上下文、构造结算器…），
  就一个字都不会写 ⇒ 玩家点了确认却**毫无痕迹**，模型下一轮还会照着"确认过了"往下编。
  现在补一条【未成立】把原因写清楚，并**明说这一笔是否动过钱无法确定**（因为确实不知道
  异常发生在动账之前还是之后），同时弹一次结果框。
- **一句永久的假话**（`ShadowEconomyRuntime.ComposeHintFor`）：方案 A 接管后旧的 Compose
  注入补丁被 `MarkRetired` 停用，而 `Invocations` 只由它递增 ⇒ 计数恒为 0，
  于是"私信组装补丁 0 次（已发过私信的话，说明挂载点没被调用）"这句**每推进一天刷一次**，
  把接手者往"挂载点坏了"的方向引。现在按补丁的**实际状态**分叉：已退场就闭嘴，
  只有"装过却从未命中"才提醒（新增 `PrivateMessagePatch.IsRetired` 区分
  "按设计退场"与"压根没装上"这两种 `Applied == false`）。

### 变更

- **打包一次产出全套**（`build-deploy.ps1`，新增 `[5/5]` 步）：原先 `release\` 是**手工**组装、
  且已经落后一版（DLL 与 `ai_prompt_economy.json` 都停在旧版），玩家从 zip 装到的不是仓库里的版本。
  现在一条命令产出 `deploy\` + `release\...-二开-v0.1.0\` + zip + `SHA256SUMS-release.txt`。
  zip 用固定时间戳写入，**同一个提交重复打包哈希完全一致**，可直接复现核对。
- **`SHA256SUMS-release.txt` 不再被 `.gitignore` 忽略**：它由脚本生成，
  且发布 README 承诺"zip 哈希记在仓库根"——以前那条忽略规则让这个承诺落空。
  此前文件里还留着一个**从未展开**的字面量 `$zipHash`。
- **`release\` 的源 README 提为仓库根的 `release-readme.md`**（脚本按 Unicode 转义输出中文名，
  保持 `.ps1` 纯 ASCII）；删掉里面两行**已经过期**的硬编码哈希，
  改为"以包内 `SHA256SUMS.txt` 为唯一准据"。
- `NOTICE.md` §6：原先随**每个发布包**发出去说"`.gitignore` 不排除 `deploy/`"，与事实相反。已改为
  准确描述 `deploy/*` + `!deploy/DEPLOY.md` + `release/` 的规则。

### 新增

- **`TradeConfirmationGate.WithSettlement`**：临时开关结算并在离开作用域时**无条件还原**
  （含抛异常那条路径）。动钱开关是进程级静态量，泄漏会让同进程后续所有用例都在"钱可以动"下运行——
  现在把这条纪律交给编译器，而不是交给记性。
- **测试 +7**（391/523 → 395/530）：
  - `PlanAInjectionTests.EchoOfPlayerTextNeverBecomesAnOffer`：**回声保护的端到端回归**。
    此前只有判定函数本身被测过，"判定为回声 ⇒ 不落单"这条**因果链完全没有覆盖**——
    漏掉调用或忘了用返回值时，全部用例照样全绿。现在钉住"不落单 / 不生成预览 / 不动钱 /
    不写未成立"，并用对照组（玩家没说过 ⇒ 正常落单）证明测的不是"落单整个坏了"。
  - `PlanAInjectionTests.ConfirmedButNoLedgerEvidenceIsNotClaimedSettled`：
    悬空流水 ⇒ 必须写「无法核实」而不是「已结算」；并带"流水真的在 ⇒ 仍然说已结算"的对照组，
    防止这道闸把真话也挡掉。
  - `ShadowPlayerWalletTests.ImportRefusesFractionalDollarsInsteadOfTruncating`
  - `EconomyLedgerTests.BalanceOverflowIsReportedAsOverflowNotInsufficientFunds`
  - `TradeConfirmationTests.WithSettlementRestoresTheSwitchEvenWhenTheBodyThrows`
  - `TradeNoteTextTests.UnverifiedNoteNeverClaimsSettledNorUnsullied`
  - `InjectWiringTests.RetiredComposeHintNoLongerCriesWolf`

---

## [0.1.0] — 2026-10-12

首个版本。玩家 ↔ NPC 双向结算闭环，全部经真机验收。

### 新增

- **NPC 经济账本**：余额、工资（赛季结算）、赛季/累计统计、转会费与奖金、收据本（幂等）、
  审计文件 `audit/<WorldId>.jsonl`、影子存档分片 `worlds/<WorldId>.json`。
- **交易引擎**：八条校验器（形状 / 金额边界 / 余额 / 每日上限 / 好感 / 目录 / 性格适配 / 幂等）、
  `Reserve`（不动钱落单）与 `Commit`（确认时重新校验后动账）两段式执行器。
- **只读成交预览 + 双人确认**：预览逐字节不动任何余额或流水；
  双方各投一票（NPC 由私信标记产生、玩家由弹窗或 `确认 <单号>` 指令产生）。
- **结算器**（`TradeSettlement`）：四项前置闸（开关 / 方向白名单 / 双方确认 / 仍 pending 且未过期），
  通过后强制落盘并写审计；幂等键 `trade:{world}:{offerId}`；失败路径释放整笔幂等键。
- **玩家影子钱包**：从原版 `Credits + CreditFraction` **一次性只读导入**；
  未导入时拒绝任何动钱；不产生负余额；确认（投票）路径只拿只读快照，**结构上不可能动钱**。
- **账本真相通道**：回执 / "未成立"原因 / 审计回读写进 V 段，
  使模型不能谎称"钱已经转过去了"；`LastTrade` 落盘保证重启后仍能看到回执。
- **Prompt 注入**：S 段（零数字、逐字节稳定、保住前缀缓存）+ V 段（所有数字）。
- **工具**：`tools/pick-sponsor.py`（挑能过闸的交易对象、算金额窗口、id→人名）。

### 变更

- **放开 `player_to_npc` 方向**（原"首笔范围"只开通 NPC → 玩家）：
  现在是方向**白名单**，`npc_to_player` 与 `player_to_npc` 都能真的动钱；
  `npc_to_npc` 仍被挡下（未验收）。玩家的影子余额会**真的减少**。
- **审计行只记"钱真的动过"的出口**：`Applied` / `AlreadyApplied`。
  原先连 `Failed` 也写一行，导致 `trade_settled` 不再等于"钱动过"。
- **预览/投票/结算统一余额来源**：三者都读影子余额。
  原先预览读原版 `CareerData.Credits`（导入后就冻结了），
  真机实测弹窗显示 `$196,820` 而实际扣款起点是 `$198,020`（差 $1,200）。
- **日志措辞按开关分叉**：开关打开时不再打印"结算尚未接线…一分钱不动"这类假话
  （同一句话原先在 3 处出现）。
- **`确认` 误报 Warning 收紧**：诊断判据从"全文包含指令词"改成**行首**
  （与解析器同源），玩家随口说"你再确认一下"不再刷 Warning。

### 修复

- **落盘 bug（真机两次踩到）**：
  1. 结算结论先落盘后写注记 ⇒ 存档里永远没有 `LastTrade` 键，重启后回执消失；
  2. 幂等命中时确认闸直接返回、走不到结算器 ⇒ 补记路径（`NoteClosedTrade`）补上落盘。
- **回滚时整笔幂等键未释放** ⇒ 失败后重试会被误判成"已经结算过"（已 `Unmark`）。
- **收据本膨胀**：超过上限时只裁剪幂等收据，**余额与水位线不动**。
- **玩家侧失败未回滚 NPC 侧** ⇒ 现在两侧原子：一侧失败整笔作废。
- **审计 `Day` 为 0**：老世界水位线为 0 时写 `Day: 0`；现在三级兜底
  （调用方天数 → 单子 `CreatedDay` → 水位线），**只对新行生效，历史行不回填**。
- **按需载入**：刚进游戏、还没跳天就发私信时，世界不在影子存档里而被拒注入。

### 已知问题

- **结果弹窗会被私信模态挡住**：此时结果只写进 V 段与日志（V 段不面向 UI，
  所以玩家看不到即时回执，但重启/下一轮私信里能看到）。
- **玩家发起交易尚未支持**：玩家自己打 `[Trade: …]` 不会落单（回声保护）。
- **`npc_to_npc` 未接线**（转会费/工资/奖金）。
- **影子余额与原版 `Credits` 分叉**是设计选择，不是缺陷。

### 安全

- 引入上游 **NationalSpire**（PolyForm Noncommercial 1.0.0）的出处声明与许可证副本；
  见 [`NOTICE.md`](NOTICE.md)。**禁止商业使用。**
