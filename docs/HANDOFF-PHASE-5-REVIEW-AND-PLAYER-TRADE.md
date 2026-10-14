# 阶段 5 交接文档（审查修复 + 玩家发起交易 + 发布流程）

> **本文用途**：让一个**完全没有上下文的新会话**读完本文就能接手。
> 记录 **2026-10-13 ~ 10-14** 这一段做过什么、踩了什么、接下来往哪走。
>
> **撰写时间**：2026-10-14。**本文所有数字、哈希、行号均为本机实测。**
>
> **先读这一句**：这一段**没有做新玩法**，做的是**质量与正确性**——
> 按五项审查（P0/P1/P2/P3）修掉了 15 个问题，其中 **4 个是真 bug**；
> 末尾补做了**阶段 4 的 B 项（玩家发起交易，形态 b）**。
>
> **接手顺序**：本文 → `PROJECT-SUMMARY.md`（全貌）→ `HANDOFF-PHASE-4-STEP5.md`（上一段入口）。

---

## 0. 三十秒速览

| 项 | 值 |
| --- | --- |
| 零依赖基线 | **395 / 395**（本段开始前是 391） |
| 含游戏引用基线 | **533 / 533**（本段开始前是 523） |
| 三种构建 | **各 0 警告 0 错误** |
| bin / deploy / release DLL | **三处一致**（当前 `AAA8CAF3…`，重打包后会变） |
| 发布包 | `release/NationalSpireEconomy-二开-v0.1.0.zip`，**214357 字节**，`D87B5CFF…` |
| 动钱开关 | `trade.settlementWired = **false**`（长期默认值，没变） |
| 探针开关 | `injection.probeEnabled = **false**`（**本段改的**，原来是 true） |
| git | HEAD = `18c5b36`；**未提交 0**；**未推送 3 个 commit**（沙箱做不到，见 §4.1） |
| 上游 0.26.16 落点 | ✅ **已核对：10/10 签名一致**（见 §3.1），无静默失效风险 |

**一句话**：模组的**账变准了、谎话变少了、测试不再骗人**；并新增了"玩家可以自己发起交易"。

---

## 1. 这一段做了什么

### 1.1 五轮审查（P0 → P3）

按"影响真实玩家 / 影响钱 / 影响可维护性"排序，逐轮清理。**每一项都回源码核实过，不是照抄审查结论。**

#### P0 — 交付正确性（会让玩家装到错东西）

| 编号 | 问题 | 修法 |
| --- | --- | --- |
| H1 | **`release\` 分发包已停更** —— 里面 DLL 是旧版（`442880`，落后一版），`ai_prompt_economy.json` 也停在旧版。玩家从 zip 装到的**不是仓库里的版本** | `build-deploy.ps1` 增加 `[5/5]` 步：一条命令产出 `deploy\` + `release\` + zip + 哈希清单 |
| H2 | **`SHA256SUMS-release.txt` 里的 `$zipHash` 从未展开** —— 发布页承诺的校验不可用 | 脚本真实写入；**并发现 zip 本身不可复现**（`Compress-Archive` 会存 mtime，两次跑出 210330/210673 字节）⇒ 改用 `ZipArchive` + **固定时间戳**，现在同一提交重复打包哈希完全一致 |
| H3 | **`MailboxSanitizer` 把失败当成功** —— `Done.Add` 在取 `Life/Mailbox/Conversations` **之前**执行，任一层取不到就 `return false`，但那份数据已被**永久**标记成"处理过"。而 `WorldFor` 的注释还承诺"下次会再清一遍" | 登记挪到三层结构都确认拿到之后；取不到就不登记，日志明说"本次不登记，下次会重试" |

#### P1 — 动钱正确性

| 编号 | 问题 | 修法 |
| --- | --- | --- |
| H7 | **回声保护的因果链完全没测** —— 只有判定函数本身被测过 | 新增端到端用例，钉"不落单/不生成预览/不动钱/不写未成立"，**带对照组**（玩家没说过 ⇒ 正常落单） |
| H4 | **动钱开关是进程级静态量**，测试忘记还原则污染后续所有用例 | 新增 `TradeConfirmationGate.WithSettlement`，`using` 包住、**抛异常也还原** |
| H5 | **玩家余额导入静默截断** —— `(long)MoneyUnits.BalanceUsd(...)` 最多悄悄丢 $9（`CreditFraction` 含亚单位分数时） | 非整数美元**拒绝导入**且不置位水位线（与"负余额拒绝导入"同一种保守取舍） |
| H6 | **余额溢出被误报成余额不足** —— `EconomyLedger.cs:181` 的裸加法回绕成负数，`after < 0` 报 `InsufficientFunds`；玩家侧一直是 `checked`，两侧强度不一致 | 改 `checked`，溢出报 `Rejected` 并写明原因 |

#### P2 — 真相通道

| 编号 | 问题 | 修法 |
| --- | --- | --- |
| H8 | **仅凭 `Confirmed` 状态就宣称「已结算」** —— 该状态在**幂等命中**时也会被置上，而流水是每 NPC 64 条的**环形**、收据会被裁剪，所以老单可能只剩一个**悬空的流水 id**。旧代码照它的余额重建"已结算"回执 = **替账本撒谎** | 加**证据闸**：流水 id 必须真的还在某一方的流水里；对不上就写新状态 `Unverified`（**刻意不算 `Settled`**），既不宣称已结算也不宣称没动过，并指引去核对 `audit/<WorldId>.jsonl` |
| H13 | **结算抛异常时不留任何痕迹** —— `TradeSettlement.Settle` 内部自会落盘，但异常抛在 **`Settle` 之前**时一个字都不写，玩家点了确认却毫无痕迹 | 补一条【未成立】写明原因，并**明说这笔是否动过钱无法确定** |
| B8 | **一句永久的假话** —— 方案 A 接管后旧 Compose 补丁退场，`Invocations` 恒为 0，于是"挂载点没被调用"**每推进一天刷一次**，把人往错误方向引 | 新增 `PrivateMessagePatch.IsRetired` 区分"按设计退场"与"压根没装上"；退场就闭嘴，只有"装过却从未命中"才提醒 |

#### P3 — 可维护性

| 编号 | 问题 | 修法 |
| --- | --- | --- |
| C1/C2 | **出厂仍挂着排障探针** —— `PathProbe` 给 19 个方法各挂 3 个补丁、**每次调用同步写文件**（真机实测一次私信的 `Feed` 命中 **4369** 次） | `probeEnabled` 默认改 **false**；`PathProbe` 也归同一个开关管（原先只认环境变量） |
| H11 | **测试静默跳过** —— 4 条最贵的回归用例（多副本挂载/落点警卫）在缺 `NationalSpire.dll` 时裸 `return`，**输出与 PASS 一模一样** ⇒ 在 CI 里静默变成空操作 | `TestRunner.Skip` + `SKIP` 状态 + 跳过明细；跳过原因带根因 |
| H17 | **19 个测试文件各抄一份 `FindConfigDirectory`**（返回类型已开始漂移） | 收敛到共享 `TestConfig`，各文件保留一行委托 |
| H18 | **三处名不副实的断言** | 见下面 §2 的两条"改强之后立刻查出真相" |

### 1.2 标记泄漏修复：`[Deal: accept]`（**维护者发现的**）

维护者指出"NPC 和玩家之间的交易只修了一半"。查证属实：

- 提示词明确要求模型写 `[Deal: accept]`，但剥离器的关键词表只有 `["Trade", "Buy"]`
- ⇒ **四个剥离出口（流解析 / 显示出口 / 并行可见流 / 存档固化文本）全都漏掉它**，玩家会在私信里直接看到

**修法**：`Deal` 登记进 `TradeProtocol.MarkerKeywords`，并单独标为 `StripOnlyKeywords`（**只剥、不当交易意图解析** —— 它没有 Kind/Amount，硬解只会刷假的 `PARSE_ERROR`）。

**并加了审计**：把提示词里所有括号标记与注册表对了一遍，确认只有 `[Trade: …]` 与 `[Deal: accept]` 两种。测试里加了一条要求：**提示词让模型写的每一个标记都必须能被剥掉**。

### 1.3 阶段 4 B 项：玩家发起交易（形态 b）

**维护者拍板的形态 (b)**：玩家提出 → **等模型回 `[Deal: accept]` 才算 NPC 同意** → **玩家自己再确认** → 才结算。

> 另一种 (a)"玩家发标记就自动落单、NPC 那票自动投"被否掉 —— 那等于玩家一开口 NPC 就必须答应。
> **(b) 的价值就是 NPC 有独立意志、能拒绝。**

| 改动 | 落点 |
| --- | --- |
| 玩家打 `[Trade: …]` 会落单 | 新增 `TradePreviewWiring.HandlePlayerIntent`，走**与模型那条完全同一套**的补全与八条校验链 |
| **落单时不投 NPC 那一票** | 这是形态 b 的全部要害 |
| 模型回 accept 才投 NPC 票 | 新增 `HandleDealAccepted`，**只认最近一张"玩家发起且待确认"的单**（多张时猜是错的） |
| `[Deal: accept]` 从"只剥"改为"剥+解析" | `TradeProtocol.DealAccepted`；每轮在 `RewriteFinish` 里复位 |
| 提示词加回 `[Deal: accept]`（**D1 反转**） | 这次说清**谁先提**：「如果玩家先提出交易（你会看到一行 `[Trade: …]`）…」S 段仍**零数字** |

**完整语义三段**：玩家提出 → NPC 同意 → 玩家确认。测试 `DealAcceptCastsNpcVoteAndSettles` 把三段逐段钉住（accept 之后单子仍是 pending、两边余额一字不动）。

### 1.4 文档口径澄清（W1）

**"NPC↔NPC 未接线"只描述交易引擎。** 实际上 `EconomyTick` 里的**工资 / 奖金 / 转会费真的会改变 NPC 余额**，且**有意绕过**八条校验器与确认闸、**也不写审计**。

实测确认：三者都走 `EconomyLedger`（所以账实一致与幂等仍成立），而 `EconomyTick.cs` 里 **audit 引用数为 0**。

已在 README / CONTRIBUTING / `PROJECT-SUMMARY.md` §3.2.1 写清三列对照表，并注明这是**有意取舍**（程序化赛季结算走"性格适配"没有意义）。

### 1.5 维护者拍板的三件事

| 编号 | 决定 | 落实情况 |
| --- | --- | --- |
| D1 | 删 `[Deal: accept]` 提示词 | 先删 → **后因做 B 项反转加回**（措辞改准确了） |
| D2 | 署名**保留 `Dora`** | LICENSE / NOTICE / 清单三处一致；仓库地址统一成真实账号 `3085487039` |
| D3 | NPC↔NPC **搁置** | 要在 CHANGELOG 记为"要做先出业务设计" |

---

## 2. 教训（这一段最值钱的部分）

### 2.1 ★ 把断言改强，会立刻暴露它原先在掩盖什么

这是本段**最有价值的发现方式**。三处"名不副实"的断言，改强之后**当场查出两件真相**：

| 用例 | 原来 | 改强之后查出的真相 |
| --- | --- | --- |
| `IdentitiesIsUsedWhenRoleMisses` | 只断言"**结果非空**"（任何非空字符串都通过） | **交付配置里没有任何档位使用 `identitiesContains`**，只有 `roles` ⇒ `EconomyTier.cs:110` 那条判定分支在生产里是**死的**（代码支持、配置没用） |
| `PersonalityRefusalAtConfirmAlsoRecordsGrudge` | `if/return` 两条互斥路径 | 实测走的是"Reserve 就被拒"那一支，`engine.Confirm` **从未被调用** —— 而用例名承诺的正是"确认阶段" |
| `GodotIsNotLoadedInTestProcess` | `True(true)`，恒真 | 什么都验不到 |

**方法论**：拿到一条"只断言非空/非假/没崩"的用例时，**先把它改成可证伪的**，再看它是不是红的。**绿的弱断言比红的强断言危险得多。**

### 2.2 ★ 进程级静态量会跨用例泄漏，而且会骗你

本段**踩了两次**：

- `TradePreviewWiring.LastPreview` / `LastSettlement` 是**进程级静态量**，而 `Clear(worldId)` **只清 `PendingByWorld`、不清它们**。
- 实测：测试读到 `LastSettlement` = "已结算（私信指令）"，而单子其实还是 `pending` —— 读到的是**上一个用例**留下的陈旧值。
- 后果：我以为代码有 bug，实际是**测试读错了地方**。诊断打出票数（NPC票=True / 玩家票=False）才看清真相。

**纪律**：测试判据一律**按世界查**（`PendingFor(worldId)` / `LastNoteOf(world)` / `TradeConfirmationGate.Status`），**不要用进程级静态量**。

### 2.3 ★ `MoneySnapshot` 含 `offers=N`，比较"钱有没有动"会永远失败

落单/结算**本来就会增加单数**。本段踩了两次，两次都是同一个形状。

**纪律**：比较"钱有没有动"用 `BalanceSnapshot`（余额 + 流水），**不要用 `MoneySnapshot`**。

### 2.4 ★ 批量文本替换会咬人（本段我自己犯的）

我用脚本批量重写测试的 `Cases` 数组时**删掉了 3 个用例注册**；为撤销一次失败的批量编辑做了 `git checkout`，又**连带丢掉了那 3 个方法体**。

表现是：基线从 395/530 掉到 **392/527**（数字不对但我差点没注意）。

**纪律**：改测试文件**不要用脚本批量替换**；发现数字变化先怀疑自己改坏了。

### 2.5 沙箱里做实验要先想清楚会不会把环境弄坏

我为了验证"跳过"机制，试着把 `NSE_GAME_DIR` 指向一个空目录 —— 沙箱不允许创建那个目录，结果**整轮 65 条用例全红**（全是无关的 `FileNotFoundException`）。那轮输出**什么都证明不了**。

**纪律**：破坏性实验要么先在副本上做，要么用显式的自造输入（如 `TestRunner.Skip` 那样能在单测里直接构造的分支）。

### 2.6 ★ 加新目录前先想一遍：主工程会不会把它吸进去（收尾会话踩的）

`NationalSpireEconomy.csproj` 用**默认 glob**，会把仓库里**所有** `.cs` 编进主程序集。
`tests/`、`config/` 早就被 `Compile Remove` 挡上了，但 **`tools/` 是新目录，没人补那一行** ——
我加 `tools/sig-audit/` 之后零依赖基线**直接 `生成失败`**（8 条 CS0579 特性重复）。

**纪律**：根目录新增任何含 `.cs` 的目录 ⇒ **同步在 csproj 里 `Compile Remove`**。
两种症状对照：`tests/` 混进来报 **CS8805**（多个入口点），`tools/` 混进来报 **CS0579**（特性重复）。

> **值得单独记一句**：这个错误**不是测试红，而是根本编不过**。
> 也就是说 —— **"跑一遍基线"这个动作天然会拦住它**；
> 而我当时已经准备收工了。**如果只 `git status` 看到干净就结束，
> 交给你的就是一个编不过的仓库。** 见 §3.2.1。

### 2.7 沿用的既有教训（仍然有效）

- **"我测过的那条路" ≠ "真机走的那条路"**：本项目三次栽在这里。测试要按"玩家怎么操作会走到哪一行"来写。
- **写了真相就必须落盘**：`EveryTruthWriteSurvivesAReload` 用"**只从磁盘读**"来判，覆盖三条写路径。
- **日志也是产品的一部分**：同一句假话曾在 3 处出现（本段又修了一处：B8）。
- **两套基线顺序是硬约束**：零依赖先（395）、含游戏引用最后（533）。反了会红两条。
- **打完包不要再跑零依赖基线**：会把 `bin\` 覆盖成缩减版。
- **改完代码一定要真的把基线跑一遍再收工** —— 见 §2.6，这是本段收尾的实证。

---

## 3. 接下来的方向

### 3.1 ✅ 已核对完毕（2026-10-14）：**上游 0.26.16 的签名与我们的落点完全一致，无静默失效风险**

> **原担忧**：上游源码显示 `0.26.16`，而我们的落点是按 `0.26.11` 定的；
> 若两版之间签名有变，注入会静默失效（本项目历史上正是栽在"挂载成功却永不命中"上）。
>
> **核对结论**：**10 个挂载点全部存在、签名逐字一致。不需要改任何代码。**

**核对方法与证据**（两条独立证据链，互为印证）：

1. **对实际部署的上游二进制做纯反射**（`tools/sig-audit/`，只读、不执行上游代码）。
   目标 DLL 就是游戏真正加载的那一份：
   `D:\Steam\steamapps\workshop\content\2868840\3809501471\NationalSpire.dll`
   4,509,184 字节｜改动 10-14 13:17:40｜`SHA256 = D973D392B0014F9EBDF8A06B94FC3074D975D8408B186E6904B7342236BB9E82`
2. **上游源码逐字复核**（`D:\蓝色大肥鱼\NationalSpire-main`），与二进制结论一致。

| 我们的挂载点 | 0.26.16 实测签名 | token | 源码位置 |
| --- | --- | --- | --- |
| `AiService.RequestPrivateAsync`（方案 A 主挂载点） | `(AiOptions, List`1, Boolean, Action`1, CancellationToken, Action`1, String) → Task`1` | `0x060004B7` | `AiPrivateMessages.cs:135` |
| `AiService.ProcessPrivateAsync`（身份跟踪） | `(Func`1, Action`1, String, Action`2) → Task` | `0x060004B5` | `AiPrivateMessages.cs:23` |
| `CareerEngine.MoveToNextDay`（日期推进·首选） | `(CareerData) → Void` | `0x06000947` | `CareerEngine.cs:90` |
| `CareerEngine.AdvanceToDay`（日期推进·后备） | `(CareerData, Int32) → Int32` | `0x06000949` | `CareerEngine.cs:120` |
| `PrivateStreamParser.Feed` | `(String) → Void` | `0x06001562` | `PrivateMessages.cs:326+` |
| `PrivateStreamParser.Finish` | `() → Void` | `0x06001563` | 同上 |
| `PrivateMessagePrompts.Compose`（旧补丁·已退场） | `(CareerData, PrivateConversation, PrivateTurn) → List`1` | `0x0600153D` | — |
| `PrivateMessagePrompts.Context`（诊断） | `(CareerData, PrivateConversation, PrivateTurn, List`1) → String` | `0x06001540` | — |
| `LeadingReasoningStream.Feed`（并行可见流） | `(String) → Void` | `0x06000F95` | `LeadingReasoningStream.cs:12` |
| `LeadingReasoningStream.Finish` | `() → Void` | `0x06000F96` | `LeadingReasoningStream.cs:40` |

**挂载点相关的类型全部存在**：`AiService`｜`CareerEngine`｜`PrivateStreamParser`｜
`PrivateMessagePrompts`｜`LeadingReasoningStream`｜`PrivateTurn`｜`CareerData`｜`Diagnostics`（8/8）。

#### ⚠ 关键澄清：token 变了，但**这恰恰是"没变"的证据**

与 `HANDOFF-PHASE-3-PROBE.md` §2.9 记录值对比，**四个 token 全部前移**：

| 方法 | §2.9 记录 | 0.26.16 实测 | 位移 |
| --- | --- | --- | --- |
| `PrivateMessagePrompts.Compose` | `0x0600131B` | `0x0600153D` | +546 |
| `PrivateMessagePrompts.Context` | `0x0600131E` | `0x06001540` | +546 |
| `AiService.RequestPrivateAsync` | `0x060004AF` | `0x060004B7` | +8 |
| `PrivateStreamParser.Feed` | `0x0600133F` | `0x06001562` | +547 |

**判读**：同一类型内**全体成员整齐前移**、且**方法名与参数类型逐字未变** ——
这是"上游**新增了**成员"的形态（元数据行号被顶开），**不是**"签名被改"。
若真有签名变动，表现会是**找不到方法**或**参数类型对不上**，而不是整齐平移。

**并且**：Harmony 是按**签名**（名 + 参数类型）匹配的，**token 只用于诊断打印**，
所以 token 变化本身**不影响挂载**。本项目历史上栽的"挂载成功却永不命中"，
根因是**挂在了错的方法/错的类**上（见 `HANDOFF-PHASE-3-PROBE.md` §3.2），不是 token 漂移。

#### 附带查清的两件事

- **"0.26.11"这个数字的来源**：上游 `ARCHITECTURE.md:3` 仍写着 "v0.26.11 的具体函数…"，
  而 `README.md:29` 与 `NationalSpire.json:6` 都已是 **`0.26.16`**。
  ⇒ 是**上游自己的文档陈旧**，不是我们的落点真的按旧版定的。
  `NationalSpire-main` 源码与部署 DLL **同为 0.26.16**。
- **`<ProcessPrivateAsync>d__39` 这个 `39` 是安全的**：`PrivateIdentityTrackPatch.cs:35,161`
  按**前缀** `<ProcessPrivateAsync>d__` 匹配状态机，**没有硬编码序号** ⇒
  上游哪天改了那个方法、编译器重新编号，**照样能挂上**。这是正确的写法，不必改。
  （部署 DLL 实测：状态机为 `<ProcessPrivateAsync>d__39`。）

> **留给下一次的护栏**：上游下次更新后**重跑一次** `tools/sig-audit/` 即可（只读，几秒）。
> 判据：**任何一行变成 `[缺失类型]` 或 `[缺失方法]`** ⇒ 那个落点已经失效，必须改代码。

### 3.2 与上游的关系（**PR 这条路目前走不通**）

**已查清的事实**：

| 项 | 结论 |
| --- | --- |
| 我们的仓库 vs 上游 | **两套完全不同的项目结构**（上游 466 个 `.cs` 平铺在根；我们是 `src/`+`tests/`+`docs/` 的独立模组） |
| 共同历史 | **零**（我们只有 6 个提交，全是自己的） |
| 把我们的分支 PR 到上游 | Git 会试图**用我们的项目替换掉作者的整个项目** ⇒ 那不是提功能，是删人家代码 |
| 你那次失败的尝试 | 痕迹还在：`fork` remote（`3085487039/NationalSpire.git`）+ 分支 `feat/add-economy-mod`，内容是我们自己的整个仓库、与上游零共同祖先。**这就是"提交不了"的原因**（不是网络或凭据） |
| 作者的 CONTRIBUTING | **很严**：改赛制/奖励数值/存档处理/交互生效方式 **必须先开 Issue 经维护者确认**；功能 PR 必须同 PR 更新接口文档；一个 PR 一个目的 |

**三条可行路径**：

| 方案 | 说明 |
| --- | --- |
| **A. 不做 PR**（推荐） | 模组本来就是**独立二开模组**，玩家同时装两个。NOTICE 已写清"不修改上游源码/DLL" |
| **B. 先开 Issue 沟通** | 按作者 CONTRIBUTING 的正确姿势：说明方案、玩家怎么操作、何时生效、影响谁、旧存档怎么办、单人/多人分别怎么处理 |
| **C. 真做代码 PR** | 需把经济系统**重写进上游的平铺结构与模块规范**里，工程量巨大，且必须先过 Issue |

**建议 A + B**：模组保持独立，同时开 Issue 表达意愿。

### 3.2.1 ★ 收尾会话新增的坑：**往仓库加新目录会让主工程编不过**

**这是本段收尾时我自己踩的，值得记下来**（连同修法一起）。

**症状**：加完 `tools/sig-audit/`（一个带自己 `.csproj` 的独立控制台工具）后，
零依赖基线**直接 `生成失败`**，报 8 条：

```
error CS0579: "TargetFrameworkAttribute" 特性重复
error CS0579: "AssemblyCompanyAttribute" 特性重复
...（共 8 条 AssemblyInfo 特性重复）
```

**根因**：`NationalSpireEconomy.csproj` 用的是**默认 glob**，会把仓库里**所有** `.cs`
吸进主程序集 —— 于是 `tools/sig-audit/Audit.cs` 被当成主工程源码，
和它自己的 `AssemblyInfo` 撞车。

**这个坑的形状值得记住**：`tests/` 和 `config/` **早就被挡上了**，
csproj 里还留着注释"避免这类问题反复出现" —— 但 `tools/` 是**新目录**，没人补那一行。
**纪律：往仓库根加任何含 `.cs` 的新目录，必须同步在 csproj 里 `Compile Remove`。**

**已修**（commit `18c5b36`）：按同一模式补

```xml
<Compile Remove="tools\**\*.cs" />
<None Remove="tools\**" />
```

并把注释改成同时写清两种症状：`tests/` 混进来报 **CS8805**，`tools/` 混进来报 **CS0579**。

> **附带的方法论**：这个错误**不是测试红，是根本编不过** —— 所以"跑基线"这一步
> 天然会拦住它。**如果我只跑 `git status` 看干净就收工，就会把一个编不过的仓库交出去。**
> 又一次印证 §2 那条：**验证要真的跑，不能只看"看起来对"。**

### 3.3 阶段 4 剩下的项

| 项 | 状态 |
| --- | --- |
| **B. 玩家发起交易** | ✅ **本段完成**（形态 b） |
| **C. 玩家侧可见回执** | ⬜ 未做，甲方说"不需要"（三条备选 C1/C2/C3 见 `HANDOFF-PHASE-4-STEP5.md` §4） |
| **2b. 往聊天写账本事实** | ⏸ 搁置，**需先与上游作者沟通**"往聊天写消息"的入口权限 |
| **原版金额显示口径** | ⬜ 跨模组协调的事，可与 2b 一起谈 |

### 3.4 未做的新功能（按价值/成本）

| # | 项 | 状态 | 阻塞点 |
| --- | --- | --- | --- |
| 1 | **物品交易**（引入新物品） | 🟡 **只有骨架**：接口全在（`ITradeCatalog` / `ITradeInventory` / `ITradeEffectHandler` / `TradeEffectRegistry`）、`CatalogValidator` 已进校验链、`TradeOffer.Items` 与 `Amount` **并列**预留 | `PurchaseExecutor.Enabled` 默认 false；`items.json` **全项目无 reader**；`TradeEffectRegistry` **零 handler**。**缺的是业务内容（有哪些物品、买了给什么效果），需要先出策划案** |
| 2 | **雇佣机制** | ⬜ **完全没有特殊逻辑** | 原定"触发玩家带打/当教练的合同或日程"（原版 `CoachLineups` / `ClubCoaching`）。实测 `src/` 里搜 `CoachLineup`/`ClubCoaching` **零命中** —— 它现在就是一笔普通转账，只是标题叫"雇佣" |
| 3 | **赞助机制** | ⬜ **没有关联** | 原定"可关联 `SponsorContract`，但不要直接改原版字段"。实测 `SponsorContract` 在 `src/` 里零命中。⚠ `NpcEconomy.LastSponsorPaidSeason` 字段存在但**无任何逻辑在用它**（当初留的水位线） |
| 4 | **贷款 / 借款** | ⬜ 未做 | 有 `TradeKinds.Loan` 常量与 `PERSONALITY_REFUSES_LOAN` 拒绝码，但**待还账数据结构从未定义** |
| 5 | **NPC↔NPC（交易引擎那条路）** | ⏸ **明确搁置**（D3） | 两道闸挡着 + 有测试钉住。**私信场景拿不到第二个 NPC 的可靠身份**，配置里也没有 Kind 指向 `npcToNpc` ⇒ 就算删闸也没有自然入口。要做必须先做**业务设计** |

> **第 2/3 项依赖原版 API**，而铁律是"不改原版、只读"。接线要用反射**读** `CoachLineups`/`ClubCoaching` 的状态，**不能写** —— 风险等级与之前的 Harmony 落点问题同级，需要真机验收。

### 3.5 已知但暂不打算动（低优先）

| 项 | 说明 |
| --- | --- |
| `ReinforcePatches` 漏挂窗口 | 极端时序下可能漏补一份副本，有 `FileSinkPrefix` 兜底 |
| `PathProbe.PatchMethod` 每次 `new Harmony(同 id)` | 目前无 Unpatch，无碍 |
| `pick-sponsor.py` 的公式是 C# 的独立重实现 | 无一致性校验，配置改了两边静默漂移；`emotion_scale`/`emotion_floor` 还硬编码 |
| "0 警告"是人工核对项 | `csproj` 里 `TreatWarningsAsErrors=false`，没有测试守护 |
| 每日上限只有次数、无单日金额上限 | 3 × 200,000 = 每天最多动 $600,000 |
| `NOTICE.md` 的 `Required Notice` 结论 | 上游**没有**提供该行（唯一实例是许可证模板自带的示例），所以只需传许可证 + 出处 |

---

## 4. 提交状态（✅ 本段收尾时已办完）

```
HEAD = 18c5b36  fix(build): 主工程排除 tools/，避免独立工具被吸进主程序集（CS0579）
       af1f95d  chore(tools): 核对上游 0.26.16 签名（10/10 一致）+ 新增 sig-audit 护栏
       307c489  feat(trade): 玩家发起交易（形态 b）—— 玩家提出、模型同意、玩家确认
未提交 = 0（工作区干净）
未推送 = 3 个 commit（沙箱做不到 push，见 §4.1）
```

**收尾会话做的事**：

1. ✅ 把 B 项那批**全部 13 个文件**提交成一个 commit（`307c489`），
   提交信息按项目惯例写**为什么 / 怎么改 / 怎么验的 / 风险**四段。
   > 注：交接文档原文写"11 个文件"，实测是 **13** —— 多出的两个是
   > `docs/README.md`（文档索引，需同步登记本文档）与本文档自身
   > （写文档时还没被 `git status` 看到）。不是异常。
2. ✅ 跑了两套基线再提交：**零依赖 395/395、含游戏引用 533/533，跳过 0**。
3. ✅ 核对了发布包哈希属实：zip 实测 `SHA256 = D87B5CFF…`（214357 字节），
   与 `SHA256SUMS-release.txt` 逐字一致。
4. ✅ **完成了 §3.1 的上游签名核对**（新增 `tools/sig-audit/` 作为长期护栏，commit `af1f95d`）。
5. ✅ **修掉自己引入的编译回归**（commit `18c5b36`）—— 见 §3.2.1，加新目录导致 CS0579。
6. ⬜ **仍未做**：`git push`（需维护者在本机执行）与 GitHub Release 上传。

### 4.1 环境与凭据

| 事项 | 结论 |
| --- | --- |
| 跑测试的环境变量 | `$h='D:\蓝色大肥鱼\sandbox\dotnethome'`；`APPDATA`/`USERPROFILE`/`DOTNET_CLI_HOME`/`NUGET_PACKAGES` 四个都要重定向（见任何一份交接文档 §环境） |
| **`git push`** | ❌ **沙箱做不到**：实测 `schannel: AcquireCredentialsHandle failed: SEC_E_NO_CREDENTIALS`。**需要维护者在本机执行** |
| GitHub Release 上传 | 同样需要维护者操作 |

---

## 5. 验证命令（复制即用）

```powershell
$h = 'D:\蓝色大肥鱼\sandbox\dotnethome'
$env:APPDATA = "$h\Roaming"; $env:USERPROFILE = $h
$env:DOTNET_CLI_HOME = $h; $env:NUGET_PACKAGES = "$h\packages"; $env:DOTNET_NOLOGO = '1'
$root = 'D:\蓝色大肥鱼\NationalSpireEconomy'

# ① 零依赖基线（先跑）—— 期望 395/395
dotnet run --project "$root\tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj" -c Release

# ② 含游戏引用基线（最后跑）—— 期望 533/533
dotnet run --project "$root\tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj" -c Release -p:GameRefsEnabled=true

# ③ 打包（必须是最后一次构建）—— 产出 deploy/ + release/ + zip + 哈希清单
& "$root\build-deploy.ps1"

# ④ 上游签名核对（**上游更新后必跑**，只读、几秒）—— 期望 10/10 全 [找到]、无 [缺失]
dotnet run --project "$root\tools\sig-audit\nsesig.csproj" -c Release -- `
  'D:\Steam\steamapps\workshop\content\2868840\3809501471\NationalSpire.dll'
```

**三种构建都要 0 警告**（主工程零依赖 / 主工程含游戏引用 / 测试工程含游戏引用）。

---

## 6. 文档地图

| 文档 | 内容 |
| --- | --- |
| **本文** | **本段（阶段 5）入口**：审查修复、B 项、教训、方向 |
| `docs/PROJECT-SUMMARY.md` | 一次读完全貌（注意：本段的修改已并入） |
| `docs/HANDOFF-PHASE-4-STEP5.md` | 上一段入口：双向结算、三项拍板、**§6.6 交易标记的完整出口清单**（改标记必读）、§9 第 6/7 条两条最贵的坑 |
| `docs/HANDOFF-PHASE-4-STEP4.md` | 第三步真机验收：两个落盘 bug、三条证据链 |
| `docs/HANDOFF-PHASE-4-STEP3.md` | 第三步实施：UI 与账本真相通道 |
| `docs/HANDOFF-PHASE-4-STEP2.md` | 真正动钱 + 审计落盘的 12 轮真机记录 |
| `docs/HANDOFF-PHASE-3-PROBE.md` | 阶段 3：落点根因（**含真实方法签名表 §2.9**）、收据裁剪、按需载入 |
| `docs/HANDOFF-PHASE-0-2.md` | 阶段 0~2 + 工程纪律与日志通道 |
| `deploy/DEPLOY.md` | 构建、部署、回滚、真机判据表 |
| `CHANGELOG.md` | **本段的改动逐条列在 `[未发布]` 段**（推荐先扫这一节） |

---

## 7. 如果只能记住三件事

1. **上游 0.26.16 的签名已核对：10/10 一致，落点安全** —— 但**上游下次更新后要重跑
   `tools/sig-audit/`**；只要有一行变成 `[缺失方法]`，那个落点就已经失效。
2. **改测试时：别用脚本批量替换；判据按世界查、别用进程级静态量；比较"钱动没动"用 `BalanceSnapshot`。**
3. **把弱断言改强，比读代码更容易发现真相** —— 本段两处"真相"都是这么查出来的。

> **给下一个会话的第一句话**：工作区是干净的（`307c489`），两套基线 395/533 全绿，
> §3.1 那个"最高优先风险"**已经查清并且不存在**。
> 所以现在是**干净的起跑线** —— 可以直接进 §3.4 挑功能（但那几项**都需要先出策划案或先与上游沟通**，
> 见各项的"阻塞点"列），或者去做 §3.3 里甲方说"不需要"的 C 项。

---

*本文档结束。与本地代码不符时以本地代码为准，并顺手更新本文档。*
