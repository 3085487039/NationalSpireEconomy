> ⚠️ **2026-10-11 阶段 3 已验收通过、阶段 4 已开工**：
> 阶段 4 的交接文档在 **`docs/HANDOFF-PHASE-4-TRADE.md`**（第一步：只读成交预览 + 双人确认，不动钱）。
> 本文继续作为阶段 3 的证据与坑位记录（落点 / 收据裁剪 / 按需载入三个真机缺陷全在里面）。

# NationalSpireEconomy 阶段 3 增量交接文档（探针体检与方案 A 待执行版）

> **本文用途**：让一个完全没有上下文的新会话读完本文后，能立刻接手**阶段 3-D（探针排查）→ 阶段 3-A'（方案 A 末端注入）**，
> 且**不会**重做已经完成的阶段 3-A/3-B 逻辑、**不会**重蹈已踩过的坑。
>
> **撰写者**：上一轮接手 AI。**本文所有数字、签名、路径均为本机实测**，不是转述。
> **写于**：阶段 3-D（体检版）已打包、探针在真机尚未输出日志的卡点处。
>
> **先读这一页**：真正的卡点只有一句话 —— **我们的补丁没有在游戏真正执行的方法上生效**；
> 而当前最新状态是"模组已确认加载、但 `[Economy-Probe]` 一行都没有"，
> 所以下一个动作是**先让探针说话**（见 §4.1），再谈注入。
>
> ---
>
> ## ⚠️ 2026-10-11 16:0x 更新（接手会话追加，优先级高于本文其余部分）
>
> **归因改判**：探针静默**不是**探针抛异常（假设 A），而是**整个模组的输出通道在输出端就被丢弃**。
> 证据：最近三次会话（`godot2026-10-11T15.41.09.log` / `15.46.37` / `godot.log@15.48`）里，
> 模组**确实加载并正常返回**（有 `Loading assembly DLL` + `Calling initializer method` +
> `Finished mod initialization`，**没有** `未启用`、也没有 `init` 的 Error），
> 但 `[Economy-*]` = 0、`[NationalSpireEconomy]` = 0、`national-spire.log` 里 `economy.*` = 0。
> 代码层面的原因：`EconomyLogAdapter.Write` 的非警告行**只走 `_printHook`**，
> 而 `TryBindPrintHook` 开头 `if (!IsGodotNativeReady()) return;` 一票否决，
> 且 `_autoBind` 的懒重试**只重试 Record 通道**（第 107 行判断的是 `_recordHook`）。
> ⇒ **§2.3 的三个假设全部作废**，"`[Economy-Inject]` 有而 probe 无 ⇒ 不是假设 C"这条判据也不再成立。
>
> **本轮已落地的修改（已打包，见下方新哈希）**：
> 1. **新增文件保底通道** `src/Game/EconomyFileLogSink.cs`（纯 `System.IO`，**不经过 Godot**）：
>    落点依次为影子存档目录 → 模组目录 → 系统临时目录，某个落点写失败会**自动换下一个**，
>    第一个成功的落点沿用、不双写；2 MB 滚动一份 `.1`；**永不抛异常**。
> 2. `EconomyLogAdapter`：`AttachFileSink` 把文件通道接成**第一优先出口**（所有级别都落文件）；
>    `_printHook` 加**懒重试**（上限 200 次，机会性触发）；新增 `PrintStatus` 区分
>    `NotReadyYet`（该重试）与 `Unavailable`（重试无意义）；
>    `LastBindingReport` / `LastPrintReport` / `FileSinkReport` 全部落文件。
> 3. `EconomyModEntry.Init`：**第 0 步**先建文件通道并写 boot 行（版本 / 程序集 /
>    Godot 原生就绪与否 / 实际落点），收尾再落三条通道结论；`Initialize` 的降级原因全部进文件。
> 4. **Q4 单轨**：`InjectDiagnostics` **不再**调 `PrivateMessagePatch.ApplyTo`，
>    改为输出"S 段注入：已按设计停用｜待方案 A 接管"；`Initialize` 第 5 步后调
>    `PrivateMessagePatch.MarkRetired(...)` 重置静态状态，杜绝双注入。**探针保留**。
>
> **本轮实测数字**：零依赖 294/294 ✅｜含游戏引用 **378/378** ✅｜Release 两种 `GameRefsEnabled`
> 均 **0 警告 0 错误** ✅｜部署包 6 文件与 `SHA256SUMS.txt` 独立重算一致 ✅｜无夹带游戏程序集 ✅。
>
> **部署包（体检版 v2，2026-10-11 16:0x）**：
> `NationalSpireEconomy.dll` = **282624** 字节 / `922DFC585CD4C29784C787B5AE03558BB452961222D00576579F30914F5087A5`
> （其余 5 个文件哈希与 §2.8 相同，未变）
>
> **下一步**：按 §5.1 的验收清单跑一轮。**首个要看的文件不再是 `godot.log`，而是**
> `%APPDATA%\SlayTheSpire2\national_spire_economy\mod-debug.log`。
>
> ---
>
> ## ✅ 2026-10-11 16:3x 更新（方案 A 已落地，已打包）
>
> **决定性发现（元数据实测，推翻了本文 §4.2 的一个假设）**：
> **`NationalSpire.CareerEngine` 里没有任何"当前生涯"的静态入口** —— 用
> `System.Reflection.Metadata` 逐条扫过 `NationalSpire.dll` 确认：它<b>只有</b>
> `Person(CareerData, string)`（`Current` 属性/字段根本不存在）。
> 所以"`RequestPrivateAsync` 只有 `AiOptions`，靠 `CareerEngine.Current` 补世界/人物"这条路<b>走不通</b>。
>
> **身份来源改用它**：`AiService.ProcessPrivateAsync(Func<CareerData>, Action<CareerData>, String, Action<String,String>)`
> —— 私信链路上唯一同时握着**真实 `CareerData`** 与**人物 id** 的地方。它是 `async`，
> 所以补丁挂在编译器生成的 `ProcessPrivateAsync` 状态机（`<ProcessPrivateAsync>d__N`）的
> `MoveNext` 后缀上，并且**按字段类型**取数据（不写死生成字段名）：优先 `Func<CareerData>`，
> 人物 id 取"能在 `People` 里对上的那个字符串"。
>
> **方案 A 的三层结构（单轨，只有一处注入点）**：
>
> | 层 | 挂载点 | 职责 |
> | --- | --- | --- |
> | 身份来源（主） | `AiService.ProcessPrivateAsync` 状态机 `MoveNext` 后缀 | 把 (世界, 人物, 天, 赛季, **权威 CareerData**) 记进 `PrivateIdentitySlot` |
> | 身份来源（备） | `PrivateMessagePrompts.Context` 后缀 / `CareerEngine.MoveToNextDay` 后缀 | Context 给精确身份；日期推进登记"这个世界有谁"的兜底轨迹（按键配对，歧义就拒绝） |
> | **注入（唯一）** | `AiService.RequestPrivateAsync(AiOptions, List, …)` 前缀 | 只处理 `context` 以 `private/` 开头；S 段进 system、V 段追加到 `当前日期：` 那条 user 消息；**不新增 message**、幂等、绝不抛异常 |
>
> **身份拿不到就拒绝注入**（拿不到权威 `CareerData`、人或世界对不上、槽过期、轨迹歧义 —— 一律不注入并把原因写日志），
> 绝不用空数据硬插。旧 `Compose` 注入补丁继续停用（`PrivateMessagePatch.MarkRetired`），只在测试夹具里手工装配。
>
> **本轮新增/改动**：`PrivateIdentitySlot.cs`（新）、`PrivateIdentityTrackPatch.cs`（新）、
> `PrivateRequestInjectionPatch.cs`（新）、`ShadowEconomyRuntime.BuildInputFor(...)`、
> `PrivateMessagePatch.IfStableEnabled/IfVolatileEnabled` 改为返回结构化结果并公开、
> `HookProbe.ContextPostfix` 扩参并登记身份、`CareerDayPatch` 登记兜底轨迹、
> `InjectDiagnostics` 第 2 步改为"身份来源 + 方案 A 挂载点 + 签名/token 自证"。
>
> **本轮实测数字**：零依赖 **294/294** ✅｜含游戏引用 **388/388** ✅（新增 10 条方案 A 用例）｜
> Release 两种 `GameRefsEnabled` 均 **0 警告 0 错误** ✅｜包内 6 文件哈希独立重算一致 ✅｜无夹带 ✅。
>
> **部署包（方案 A 版，2026-10-11 16:3x）**：
> `NationalSpireEconomy.dll` = **320512** 字节 / `321A76B94E714F6D32266FBCEC8668AB35522BD6666670DC5B43520AEC0022B2`
> （其余 5 个文件哈希仍与 §2.8 相同）
>
> **验收关键词**：`[Economy-Inject-A]`（方案 A 全部结论）、`挂载点自证`、
> `身份来源首次实测`、`已登记身份`、`会话`/`轨迹兜底`、以及原有的 `[Economy-Probe] 命中`。
> 第一现场仍是 `%APPDATA%\SlayTheSpire2\national_spire_economy\mod-debug.log`。
>
> ---
>
> ## 🔎 2026-10-11 17:4x 更新（方案 A 真机未命中 → 旁路取证版）
>
> **甲方真机实测（已装 DLL = 方案 A 版 320512，哈希核对一致）**：
> 补丁全部"挂载成功"，token 与 §2.9 完全一致；`national-spire.log` 显示
> **私信请求确实发生**（17:06:16 / 17:08:12 / 17:11:08，都在挂载之后，18k 字符模型回复正常），
> 但同一时段 **`[Economy-Inject-A]` 零行、四个探针零命中、`已登记身份` 零行**。
> ⇒ **"挂上了" ≠ "被执行了"**，而当时所有自证都只能证明前者。
>
> **本轮已排除的可能（都用数据排除，不是推测）**：
> 1. **不是操作失误**：`private.request` 记录证明请求真的发出去了。
> 2. **不是"别的模组替换了方法体"**：Harmony 补丁表转储显示，私信链路上
>    `RequestPrivateAsync` / `Compose` / `Context` / `ProcessPrivateAsync` 等目标方法上
>    **转移器 = 0**，且 **owner 只有 `nationalspire.economy`**（没有任何其它模组的补丁）。
> 3. **不是"别的模组劫持了入口"**：逐个扫描全部本地/工坊模组 DLL
>    （含 `mods\ai接管\STS2_MCP.dll`、SCAgent、RitsuLib、STS2AIAgent…），
>    **没有一个引用** `RequestPrivateAsync` / `ProcessPrivateAsync` / `PrivateMessagePrompts` / `AiService`。
> 4. **不是"调用链不经过"**：IL 调用图扫描（`System.Reflection.Metadata`）确认
>    `<ProcessPrivateAsync>d__39.MoveNext` 里**确实** `call NationalSpire.AiService.RequestPrivateAsync`；
>    且该程序集里**只有一个** `RequestPrivateAsync` 重载。
> 5. **最后那个会话（截图那次）根本没发私信**：它只有一条 `kind:"weekly"` 的周报请求，
>    没有任何 `private.request`。真正发过私信的是 17:06 / 17:08 / 17:11 三次。
>
> **本轮新增：旁路取证探针 `PathProbe`（`path-probe.log`）**
> 它**不经过** `EconomyLogAdapter`、不经过 Godot，直接写独立文件，内容是：
> ① 每个候选方法的**装配清单**（方法名/token/程序集/成功或失败）；
> ② **Harmony 补丁表转储**（目标方法上所有补丁的 owner/priority/类型，
> 以及"该前缀是否返回 bool——返回 false 就能整体跳过原版方法体"）；
> ③ **运行痕迹**：每次进入/退出/抛异常一行，带线程 id 与关键实参
> （字符串内容、集合大小）。候选方法按<b>名字</b>匹配（不猜类型），覆盖
> `ProcessPrivateAsync` / `RequestPrivateAsync` / `SummaryRequest` / `Context` / `Compose` /
> `Feed` / `Finish` / `SendAsync` / `SendTransportAsync` / `EnterPrivateQueue` / `Conversation`。
> 用 `NSE_PATH_PROBE=0` 可关掉。
>
> **同时修掉一个真实缺陷**：`Harmony` 不会阻止同一补丁被重复叠加，而 `Initialize` 可能被反复调用
> （测试/热重载）——实测在一次测试里 `RequestPrivateAsync` 被叠了 **54 个前缀**。
> 现在所有装配点（方案 A 前缀、身份来源、旁路探针）都有"已挂过就跳过"的幂等守卫。
>
> **本轮实测数字**：零依赖 **294/294** ✅｜含游戏引用 **391/391** ✅｜
> Release 两种 `GameRefsEnabled` 均 **0 警告 0 错误** ✅｜包内 6 文件哈希独立重算一致 ✅｜无夹带 ✅。
>
> **部署包（旁路取证版 v2，2026-10-11 17:4x）**：
> `NationalSpireEconomy.dll` = **332800** 字节 / `BE832127A3966F5FDE4CC2E7584EE9B4F5EBE0A56E0E610127A96ECDF78DB4E5`
> （其余 5 个文件哈希仍与 §2.8 相同）
>
> **旁路探针的覆盖面是按调用图扫描结果定的（不是拍的）**，扫描结论如下，
> 它直接决定真机日志怎么读：
>
> | 方法 | 调用者 | 能否当"私信"证据 |
> | --- | --- | --- |
> | `ProcessPrivateAsync` | 只有 `CareerScreen.PrivateCommand` / `Coop.StartPrivate` | ✅ **命中即私信** |
> | `EnterPrivateQueue` | `ProcessPrivateAsync` / `ProcessGroupAsync` | ✅ 基本可作私信证据 |
> | `RequestPrivateAsync` | `ProcessPrivateAsync` **＋ `ProcessGroupAsync` ＋ `SummaryRequest`** | ⚠️ 命中可能是群聊/摘要 |
> | `Conversation` | **19 处**（含 `PreviewPrivatePrompt`、存档读写） | ❌ 开界面就会命中 |
> | `Compose` | 预览按钮/群聊/仲裁/直播解说都调 | ❌ 同上 |
> | `Feed` / `Finish` | 群聊流也调 | ❌ 同上 |
>
> 另外补了 **`PrivateTurn` 的 5 个字段赋值检查点**
> （`set_Context`/`set_Status`/`set_ReplyOrder`/`set_Reply`/`set_Reasoning`，
> 装配时实测 5/5 挂上，token 依次 `0x0600129C`/`0x0600129E`/`0x0600127C`/`0x06001292`/`0x06001294`）：
> 它们合起来是一条"私信流程走到哪一步"的时间线——看到 `set_Context` 就说明上下文已组装、
> 马上要发请求；请求之后应依次出现 `set_Status`/`set_Reply`。这是判断"卡在哪一步"的关键。
>
> **注意**：`set_Context` 与 `PrivateMessagePrompts.Context` 同名，所以它必须按
> **类型+方法**精确挂载；共用按名字的去重集合会让它永远挂不上（已修，测试钉住）。
>
> **本轮实测数字**：零依赖 **294/294** ✅｜含游戏引用 **393/393** ✅｜
> Release 两种 `GameRefsEnabled` 均 **0 警告 0 错误** ✅｜包内 6 文件哈希独立重算一致 ✅｜无夹带 ✅。
>
> **下一步要回答的问题只有一个**：真机上，私信链路里<b>到底哪些方法被执行了</b>。
> 看 `%APPDATA%\SlayTheSpire2\national_spire_economy\probe\path-probe.log` 的
> "进入 …" 行即可：若 `ProcessPrivateAsync`/`RequestPrivateAsync` 都不出现而
> `Conversation`/`Context` 出现，说明请求走的不是原版那条链；若它们出现却没有注入行，
> 那就是我们补丁内部的问题，日志里的 `★异常 …` 会直接指认。

> ---
>
> ## 🎯 2026-10-11 17:5x 更新（真机找到根因：**取证探针扫描早了一步**）
>
> **甲方回传的真机 `path-probe.log` 原文**（进程 31708，线程 T01 = 游戏主线程）：
> ```
> 2026-10-11 17:46:19.389 [T01] ===== 旁路取证开始｜…｜进程 31708 =====
> 2026-10-11 17:46:19.392 [T01] 精确检查点：命中类型 0 个／挂上 0 个（共 5 个目标）
> 2026-10-11 17:46:19.392 [T01] 装配完成：共挂 0 个候选方法（只覆盖 NationalSpire 程序集）
> 2026-10-11 17:46:19.393 [T01] ===== Harmony 补丁表（谁在这些方法上装了东西）=====
> 2026-10-11 17:46:19.394 [T01] ===== 补丁表转储结束 =====
> ```
>
> **根因**：探针装配被放在 `EconomyModEntry.Initialize` 的**第 5 步之后**，
> 而 `NationalSpire` 要到**第 6 步**才被显式加载（模组加载器把我们排在它前面）。
> 于是探针扫描时进程里**一份 NationalSpire 都没有** ⇒ 0 份程序集 / 0 个候选方法 / 空补丁表。
> **这份日志只说明"扫描太早"，不代表游戏后续的执行情况**
> —— 同一进程里 `[Economy-Inject-A] 方案 A 目标：发现 1 份` 能成功，正因为它跑在第 6 步之后。
>
> **本轮修了四处**：
> 1. **`AssemblyInventory`（新）**：跨 ALC 枚举程序集。
>    `AppDomain.CurrentDomain.GetAssemblies()` **只返回当前 ALC**，而游戏用
>    `AssemblyLoadContext.LoadFromAssemblyPath` 装载模组——所以"看得到自己挂载成功、
>    却扫不到游戏执行的那份"是真实存在的风险类别。现在全部查找路径都走本类。
> 2. **探针装配延后到第 6 步之后**（`EconomyModEntry` 新增第 6.5 步），
>    并在第 5 步与第 6.5 步各写一次 `程序集盘点`（含每个原版程序集的 **ALC 名 + 路径**）。
> 3. **所有反射查找/挂载点改用跨 ALC 枚举**：`HookProbe`、`PrivateRequestInjectionPatch`、
>    `PrivateIdentityTrackPatch`、`PrivateMessagePatch`、`PrivateStreamPatch`、`CareerDayPatch`、
>    `EconomyLogAdapter.FindLoadedType`、`InjectDiagnostics.FindType`、`NationalSpireLocator`、
>    `EconomyModEntry`。共 10 个文件、15 处替换。
> 4. **装配日志加数字**：`按名字装配：跨 ALC 扫到原版程序集 N 份`，
>    N=0 时直接标 `★一份都没有——这就是补丁永远不命中的原因`；每个候选程序集单独一行（带 ALC）。
>
> **部署包（ALC 感知版，2026-10-11 17:5x）**：
> `NationalSpireEconomy.dll` = **336384** 字节 / `FAC8F65985867A98513F29B2B9CE90FCCE2615B88BBB3583AF4E5E02BB342ABD`
> （其余 5 个文件哈希仍与 §2.8 相同）
>
> **本轮实测数字**：零依赖 **294/294** ✅｜含游戏引用 **395/395** ✅
> （新增 2 条：跨 ALC 盘点必须看得到原版程序集、装配时机必须晚于原版加载）｜
> Release 两种 `GameRefsEnabled` 均 **0 警告 0 错误** ✅｜包内哈希一致 ✅｜无夹带 ✅。
>
> **下一次真机日志应出现的三条关键行**：
> 1. `程序集盘点（第 5 步，NationalSpire 尚未加载）：… 名字含 NationalSpire 的 N 份`（N 可能为 0，正常）；
> 2. `程序集盘点（第 6 步之后，NationalSpire 应已加载）：… N 份` + 每份的 `ALC …｜路径`；
> 3. `按名字装配：跨 ALC 扫到原版程序集 N 份` ← **N 必须 ≥ 1**，否则就是同一个坑的另一种表现。

> ---
>
> ## 🧨 2026-10-11 18:0x 更新（ALC 已解决；但抓出**一个真 bug** + 一个未验证假设）
>
> **真机 `path-probe.log`（进程 9808 / 进程 31708 两次）给出的三条事实**：
>
> **① 程序集与 ALC 已查清（这个方向是对的，且已彻底解决）**：
> ```
> 程序集盘点（第 6 步之后）：进程内程序集 97 份｜ALC 2 个｜名字含 NationalSpire 的 2 份
>   · NationalSpireEconomy｜ALC IsolatedComponentLoadContext(sts2.dll)｜…\mods\NationalSpireEconomy\…
>   · NationalSpire        ｜ALC Default｜…\workshop\…\3809501471\NationalSpire.dll
> 按名字装配：跨 ALC 扫到原版程序集 2 份
> 精确检查点：命中类型 5 个／挂上 5 个（共 5 个目标）
> ```
> ⇒ 模组自己跑在 `IsolatedComponentLoadContext(sts2.dll)`，**原版 NationalSpire 在 Default ALC**；
> 跨 ALC 枚举让两者都可见。**"扫描时机"与"ALC 隔离"这两个坑都已关闭。**
>
> **② 抓出一个真 bug（我的幂等守卫写错了）**：初始化行写着
> `RequestPrivateAsync …｜(已挂过，跳过)`。原因是守卫实现成
> "该方法上有我们程序集的**任意**补丁就跳过"，而 `PathProbe` 在第 6.5 步先给同一批方法
> 挂了只读观测探针 ⇒ 第 7 步**真正的注入前缀被整体挡掉**（这一版里注入前缀实际不存在）。
> **已修**：守卫改成按 **「目标方法 + 具体补丁方法」** 精确判定
> （`PathProbe.HasPatch(method, patchMethod)`），并加回归测试
> `InjectionPrefixIsNotBlockedByObserverProbe`（注入与观测必须共存）。
>
> **③ 真正待查的问题更硬了**：第二个进程 17:57:55 初始化完成 → `path-probe.log` 在 17:57:54 之后
> **再无一行**；而 `private.request` 发生在 **17:58:50**、17:58:53 收到回复。
> 也就是说：**53 个只读探针（含 `ProcessPrivateAsync`/`RequestPrivateAsync`/`SendAsync`/`Feed`）
> 确实挂在 Default ALC 的原版程序集上，私信也真的发了——它们一条都没写。**
> （注意：17:56:21 那个进程与这次私信无关，别把两个进程的日志混着读。）
>
> **④ 由此暴露一个我此前从未验证的假设**：我一直默认"Harmony 挂上了 ⇒ 在游戏里一定被调用"。
> 四探针全零命中说明这个假设**可能不成立**。所以本轮新增**活性自检**：
> 启动时用反射真调一次原版 `PrivateMessages.Conversation`，再看探针有没有记下这次调用，
> 结论直接写进日志：
> `活性自检结果：✅ 探针被调用` / `❌ 探针没有被调用——…换挂载点也没用，必须换机制`。
> 另有**补丁表最终转储**（第 9 步，注入+体检都装完之后再转一次），用于确认方案 A 前缀是否真在。
>
> **部署包（活性自检版，2026-10-11 18:0x）**：
> `NationalSpireEconomy.dll` = **339456** 字节 / `D5866145C97538E8AAE450EC8A2F7392A3809A4FDB235C38882EB5442B1C2BCE`
> （其余 5 个文件哈希仍与 §2.8 相同）
>
> **本轮实测数字**：零依赖 **294/294** ✅｜含游戏引用 **397/397** ✅｜
> Release 两种 `GameRefsEnabled` 均 **0 警告 0 错误** ✅｜包内 6 文件哈希一致 ✅｜无夹带 ✅。
>
> **下一次真机日志的判读顺序（重要）**：
> 1. `活性自检结果` —— 若是 `❌`，**不要再换挂载点**，改查"为什么我们装的补丁不执行"
>    （重点怀疑 `IsolatedComponentLoadContext` 与我们/Harmony 的关系）；
> 2. `Harmony 补丁表` —— 确认 `RequestPrivateAsync` 上有本模组前缀、且没有别的 owner；
> 3. 只有前两条都正常，`★私信专属` / `★私信时间线` 的缺失才指向"私信走了别的路径"。

> ---
>
> ## 🎯 2026-10-11 19:2x 更新（**真正根因找到：按名字去重把"游戏执行的那份程序集"丢掉了**）
>
> **触发式抓栈给出了铁证**（原版写 `mod.loaded` 时的调用栈）：
> ```
> 栈[2] IsolatedComponentLoadContext(sts2.dll)｜System.IO.File.AppendAllText_Patch1
> 栈[3] IsolatedComponentLoadContext(sts2.dll)｜NationalSpire.Diagnostics.Record   ← ★
> 栈[4] IsolatedComponentLoadContext(sts2.dll)｜NationalSpire.AiSettingsStore.ReadKey
> 栈[5] IsolatedComponentLoadContext(sts2.dll)｜NationalSpire.Entry.Init
> ```
> **游戏真正执行的 `NationalSpire` 在 `IsolatedComponentLoadContext` 里，
> 而我们所有补丁都挂在 `Default` 里那一份上。**
>
> **根因（一句话）**：`AssemblyInventory.AllAssemblies()` 原先按
> **`FullName + Location`** 去重。而**同一条 DLL 被两个 ALC 各加载一次**时，
> 两份 `Assembly` 对象的 `FullName` 与 `Location` **完全相同** ⇒ 其中一份被静默丢弃。
> 于是"跨 ALC 枚举"看似修好（日志显示 `ALC 2 个`、`扫到原版程序集 2 份`），
> 实际上**只扫到自己 + Default 那一份**，游戏执行的 Isolated 那份从未被挂上。
> 症状正是：**挂载成功、token 全对、活性自检通过，却永远不命中真实游戏流量。**
>
> **已修**：
> 1. `AllAssemblies()` 改为**按对象引用去重**（`ReferenceEqualityComparer`），
>    同名多副本一律保留；
> 2. 新增 **补丁落点体检**（`PathProbe.AuditPatchLocation`）：列出"名字含 NationalSpire 的每一份
>    程序集"，逐份统计"我们的补丁命中了几个目标方法"，并在多副本却只挂上 1 份时直接给结论；
> 3. 新增**回归测试** `InventoryKeepsSameDllLoadedInTwoAlcs`：
>    把同一条 DLL 装进两个自定义 ALC，断言盘点必须保留两份（否则老 bug 会重新长出来）。
>
> **部署包（落点修复版，2026-10-11 19:2x）**：
> `NationalSpireEconomy.dll` = **349184** 字节 / `7B26EE8DBA785186D34A0B3DF0FB7A86817991595133C776799B2C98714E57A4`
> （其余 5 个文件哈希仍与 §2.8 相同）
>
> **本轮实测数字**：零依赖 **294/294** ✅｜含游戏引用 **400/400** ✅｜
> Release 两种 `GameRefsEnabled` 均 **0 警告 0 错误** ✅｜包内哈希 6/6 一致 ✅｜无夹带 ✅。
>
> **下一次真机日志先看这一行**：
> `补丁落点体检｜原版程序集副本数：N（按对象身份计，不做名字去重）`
> 后面每份副本各有一行 `我们的补丁命中目标：M 个方法`；
> 若结论出现 **`★结论：只挂上了 1 份`**，说明还有第三份副本没被覆盖。

> **顺带更正**：运行时实际是 NationalSpire **0.26.11**（§2.5 写的 0.26.10 是旧记录）。

> ---
>
> ## 🧭 2026-10-11 19:5x 更新（**真根因定案：是我们自己用 `Assembly.LoadFrom` 造出了一份死副本**）
>
> **一句话根因**：`NationalSpireLocator` 用 `Assembly.LoadFrom` 预加载原版，而
> **`Assembly.LoadFrom` 总是装进 `Default` ALC**（本机实测：即使调用方在自定义 ALC 里也一样）。
> 游戏自己的模组加载器则在 **`IsolatedComponentLoadContext(…sts2.dll)`**（= **我们自己模组所在的那个 ALC**）
> 里装载并执行 NationalSpire。于是进程里有两份 `NationalSpire`：
> 一份是我们造的 `Default` 假副本（16 个补丁目标全挂它上面）、一份是游戏真正执行的（0 个目标）。
>
> **真机日志里的直接证据**（`path-probe.log`，19:28 那轮）：
> ```
> 第 5 步（我们还没加载原版）：进程内程序集 103 份｜ALC 2 个｜名字含 NationalSpire 的 1 份
>   · NationalSpireEconomy｜ALC IsolatedComponentLoadContext(…sts2.dll)｜…\mods\NationalSpireEconomy\…
> 第 6 步之后（我们调了 NationalSpireLocator）：… 名字含 NationalSpire 的 2 份
>   · NationalSpire｜ALC Default｜D:\Steam\steamapps\workshop\content\2868840\3809501471\NationalSpire.dll
> ```
> ⇒ 第 6 步多出来的那一份**是我们自己加载的**；而游戏执行的那一份（Isolated）当时**还没出现在进程里**
> ——因为 `ModManager` 是**逐个模组加载并立刻初始化**，NationalSpire 排在我们后面
> （19:28:41.28 的抓栈：`ModManager.TryLoadMod → CallModInitializer` 在 Isolated 里执行 `NationalSpire.Entry.Init`，
>   而我们 19:28:39.5–41.25 就已经挂完补丁了）。
>
> **★ 同一次会话里还有一条更硬的证据（19:5x 补查，之前没人注意）**：
> 那一轮**用户真的发出了一条私信**，游戏自己也把它记下来了——
> `%APPDATA%\SlayTheSpire2\national_spire_diagnostics\national-spire.log`：
> ```
> 11:29:06.895Z  private.request  {"trace":"347a…","context":"private/e35d0119/tour-c00-5/…",
>                                  "stream":true,"Model":"deepseek-flash","messages":45,"characters":20875}
> 11:29:13.826Z  private.usage    {…"total_tokens":14969…}
> 11:29:13.827Z  private.response {…"content":"你好你好，第九遍了…"}
> ```
> 而 **`path-probe.log` 的最后一次写入停在 `19:28:46`**（本地时间 = 11:28:46Z，比私信早 20 秒）。
> ⇒ **私信真实发生、我们 53 个探针零命中**。这不是猜测，是同一次会话里的两个事实对齐。
> 也就是说：这份日志同时给出了"根因"（补丁挂在 Default 假副本上）与"后果"（真实私信零命中），
> 修好落点之后**同一动作应当立刻在 `path-probe.log` 里留下 `★私信专属`**。
>
> **ALC 语义实测**（本机 net9.0，脚本 `D:\蓝色大肥鱼\_alc-probe\`，输出 `_alc-probe\RESULT.txt`）：
> | 问题 | 实测结果 |
> | --- | --- |
> | 同一 ALC、同一路径 `LoadFromAssemblyPath` 两次 | **同一个对象**（Q1） |
> | 同一 ALC、`LoadFromAssemblyName` | **同一个对象**（Q2） |
> | 同一 ALC、**同一身份但不同路径** | **返回已加载的那个对象**（Q3） |
> | 两个 ALC 各装同一 DLL | 两个对象，`FullName` 与 `Location` **完全相同**（Q4/Q4b，原去重 bug 的诱因） |
> | `AppDomain.GetAssemblies()` | **包含**自定义 ALC 里的程序集（Q5，旧注释"只返回当前 ALC"是错的） |
> | `AppDomain.AssemblyLoad` | **对自定义 ALC 的加载也会触发**，且能拿到正确的 ALC（Q6） |
> | 自定义 ALC 里的代码调 `Assembly.LoadFrom` | **落进 `Default`**（Q7b，就是本轮的元凶） |
>
> **修复（改 6 处，全部有注释说明原因）**：
> 1. `NationalSpireLocator`：改为 `OurContext.LoadFromAssemblyPath(...)`——装进**我们自己所在的 ALC**。
>    ALC 按身份去重（Q1/Q2/Q3）⇒ 游戏稍后来装同一个模组 DLL 时，拿到的是**我们已经打好补丁的那一份**。
>    查找顺序也改为"先自己 ALC 里的副本"。`Assembly.LoadFrom` 只作最后兜底（并会留痕）。
>    新增 `LoadedContext` / `OurContext`。
> 2. `AssemblyInventory`：`GameAssemblies()` 改为**精确程序集名**（不再把 `NationalSpireEconomy`
>    算成"原版副本"）；新增 `MarkerAssemblies()`（模糊）与 `IsGameAssembly()`；`Report()` 两个口径都报。
> 3. `PathProbe`：**去掉"每进程只装一次"的闩锁**（正是它让后出现的执行副本永远挂不上）；
>    装配改为**可重入**：按「目标方法 + 具体补丁方法」幂等，去重键**带副本身份**
>    （原来只按签名去重，同名方法在第二份副本上会被跳过）；`InstallDiagnosticsListener` 逐份副本挂。
> 4. **新增 `★执行副本判据`**（`PathProbe.ReportExecutingCopy`）：从**游戏正在执行的那个方法**反查
>    它属于哪个程序集／ALC，并**就地**检查那一份副本上有没有我们的补丁，直接打印
>    `⇒ 结论：游戏执行的这一份【就是】挂了补丁的那一份（落点正确）`。
>    这是"谁在跑就查谁"，不再从"我们能枚举到什么"出发（这是本轮方法论上的修正）。
> 5. **新增落点警卫**（`EconomyModEntry.ReinforceIfNewCopies` / `ReinforcePatches`）：
>    触发源有两个，都已证明会随游戏流量触发——
>    ① `AppDomain.AssemblyLoad` 里出现新的 NationalSpire 副本；
>    ② 原版写 `national-spire.log` 的那一刻（`PathProbe.FileSinkPrefix`）。
>    补挂**幂等、不跑任何自证、不写账本**（`[Economy-Reinforce]` 开头的那几行）。
> 6. `EconomyLogAdapter` 不再用 `Type.GetType("…, NationalSpire")`（它可能再拉出一份 Default 副本），
>    绑定"我们自己 ALC 里的那一份"，并把 ALC 写进绑定结论。
>
> **顺带抓出并修掉一个 Harmony 陷阱**：**"补丁记录存在" ≠ "detour 还活着"**。
> 反复 Unpatch／反复 Initialize 之后实测出现过"记录在、方法体已还原"的僵尸状态
> （`TradeMarkerInterceptor` 自证因此失败：标记不再被剥离）；而且 **Harmony 不挡重复挂载**
> （同一方法上叠出过 4 份同名前缀）。⇒ `PrivateStreamPatch.ApplyTo` 改成**先卸后挂**
> （`TryUnpatchMethod` 再 `Patch`），既保证恰好一份、又能把僵尸状态修回来。
>
> **新增 4 条回归测试**（`tests/…/PathProbeTests.cs`，含游戏引用那套 400 → **404**）：
> `GameAssembliesUsesExactName`（精确名/自己不算原版）、
> `LocatorLoadsGameAssemblyIntoOurOwnContext`（必须与我们同 ALC）、
> `ProbeInstallCoversEveryCopy`（**后出现的第二份副本也必须挂上** + 反复装配不叠层 + 体检结论"都已挂上"）、
> `ReinforcePatchesCoversLateCopy`（落点警卫必须把方案 A 注入前缀补到新副本上）。
>
> **本轮实测数字**：零依赖 **294/294** ✅｜含游戏引用 **404/404** ✅｜
> Release 两种 `GameRefsEnabled` 均 **0 警告 0 错误** ✅｜`SHA256SUMS.txt` 6/6 独立重算一致 ✅｜无夹带游戏程序集 ✅。
>
> **部署包（落点修复版 v2，2026-10-11 19:5x）**：
> `NationalSpireEconomy.dll` = **361984** 字节 / `17EE2E0CDD3F3C4619212B11E2142904E0315B4DCD77C2ABB36C485A851E68E8`
> （其余 5 个文件哈希与 §2.8 相同）
> **辨识这一版**：DLL 里能搜到 ASCII 串 `Economy-Reinforce` 与 `Economy-Locator`（旧包没有）。
>
> **下一次真机日志的判读顺序（覆盖本文 §4.4 的旧版本）**：
> 1. `mod-debug.log` 搜 **`[Economy-Locator]`**：期望
>    `原版副本 1 份（…ALC IsolatedComponentLoadContext(D:/…/sts2.dll)…）｜我们的补丁覆盖 1／1 份`。
>    若出现 `ALC Default` 或 `覆盖 0／1` ⇒ 落点又错，后面都不用看；
> 2. `path-probe.log` 搜 **`★执行副本判据`**：期望 `⇒ 结论：游戏执行的这一份【就是】挂了补丁的那一份（落点正确）`；
> 3. `path-probe.log` 搜 **`补丁落点体检`**：期望 `我们的补丁覆盖：1／1 份` + `结论：1 份副本都已挂上`；
> 4. 若上一步看到 `[Economy-Reinforce] … 补挂完成`，说明游戏确实在我们之后又加载了一份，**也已补齐**；
> 5. 然后才是 `活性自检结果` → `★私信专属` → `[Economy-Inject-A] 注入成功／注入未发生｜原因 …`。
>
> **仍未取得的证据（= 甲方这一轮要做的唯一一件事）**：
> 在真机上拿到 ①`[Economy-Locator]` 显示 1 份且覆盖 1／1、②`★执行副本判据` 显示"落点正确"、
> ③发一条真实私信后出现 `★私信专属`（`进入 …ProcessPrivateAsync`）。
> 已经不需要再改机制：**这三行不出现就是落点问题，出现之后剩下的全是"最后一百米"（身份/锚点/条数）**。

> ---
>
> ## 🏁 2026-10-11 20:1x 更新（**★真机验收：落点闭环、真实私信第一次注入成功；同轮暴露并已修一个阻断缺陷**）
>
> 甲方在 20:03–20:05 用**部署校验通过的那一版**（361984 / `17EE2E0C…`）跑了一轮真机。
> 结果：**落点问题闭环**，而且**第一次在真实私信上注入成功**。
>
> ### ① 落点闭环（判据全中 + 直证）
>
> ```
> mod-debug.log:53  [Economy-Locator] 原版副本 1 份（…ALC IsolatedComponentLoadContext(D:/…/sts2.dll)｜
>                   …workshop\…\NationalSpire.dll）｜我们的补丁覆盖 1／1 份｜带探针的方法 53 个
> mod-debug.log:54  [Economy-Reinforce] 落点警卫已装：当前原版副本 1 份
> path-probe.log:386 我们的补丁覆盖：1／1 份副本
> path-probe.log:366 ⇒ 结论：游戏执行的这一份【就是】挂了补丁的那一份（落点正确）
> path-probe.log:379 活性自检结果：✅ 探针被调用（Conversation 命中 0 → 1，总命中 0 → 2）
> ```
>
> `★执行副本判据` 在 6 个方法上各触发一次（`Conversation` / `CanChat` / `Configure` /
> `set_ReplyOrder` / `set_Reply` / `set_Reasoning`），**全部"落点正确"**。
> 私信链路命中计数：`★私信专属` 27 行、`ProcessPrivateAsync` 8、`EnterPrivateQueue` 6、`RequestPrivateAsync` 7。
> **对比上一轮**（同一动作）：`path-probe.log` 在私信发生前 20 秒就停止写入、零命中。
>
> ### ② 真实私信第一次注入成功
>
> ```
> mod-debug.log:165  [Economy-Inject-A] 首次实测：已注入
> mod-debug.log:166  [Economy-Inject-A] 注入成功｜context=<private/e35d0119/tour-c00-5/5eea97b6…>｜
>                    身份 世界 <e35d0119>｜人物 <tour-c00-5>｜第 398 天 / 第 5 赛季｜来源 ContextPostfix｜
>                    message 47 → 47 条（条数不变）｜S 段 注入成功｜位置 anchor｜长度 129｜指纹 19d00f57｜零数字 是｜
>                    V 段 注入成功｜位置 appended｜message 下标 45｜长度 93｜指纹 37432cd7｜含数字 是｜读出未竟之言 否
> ```
>
> 逐条对照当初定的验收要点：**单一挂载点** ✅（方案 A 前缀）｜**只处理 `private/`** ✅｜
> **身份来自真实世界/人物** ✅（`e35d0119` / `tour-c00-5`）｜**不新增 message** ✅（47 → 47）｜
> **S 段命中锚点** ✅｜**V 段落在 `ordered.Current`（下标 45）** ✅｜**S 段零数字** ✅。
> 唯一"未达成"是 `读出未竟之言 否`——那是**数据条件**（那一刻这条私信没有未竟之言），不是缺陷。
>
> ### ③ 同轮暴露并已修的阻断缺陷：收据本从不裁剪 ⇒ 真实世界**永远写不进磁盘**
>
> ```
> [Error] economy.store: 世界 e35d0119 自检失败：收据条数 8160 异常膨胀，可能未裁剪
> [Error] economy.store: 世界 e35d0119 落盘失败（推进到第 369 天）：InvalidDataException: …拒绝写入
> ```
>
> **机制**：发薪是"每人每季一条收据"，而真实世界有 **1632 人** ⇒ 一季 1632 条、5 季 8000+ 条；
> 而 `EconomyStore.Validate` 在收据数 > `MaxReceipts(512) × 2` 时**拒绝写入**。
> `ReceiptBook.Prune` / `EconomyLedger.PruneReceipts` 早就实现好了，**但全项目没有任何调用者**。
> **后果（实测坐实）**：磁盘上 `worlds\e35d0119.json` = **1632 人 / 0 条收据 / lastTickedDay = 0**，
> 而内存里已经跑到第 398 天 —— 日期推进、发薪、账本**一个字都没落盘**。
> 这个缺陷此前一直看不见，正因为**日期推进补丁此前挂在死副本上、从未真正执行**（落点一修就暴露了）。
>
> **已修（2 处代码 + 2 条回归测试）**：
> 1. `EconomyTick.PaySalariesIfDue` / `EconomyTick.AdvanceTo` 收尾各裁剪一次：
>    发薪防重本就靠 `npc.LastSalaryPaidSeason` 水位线（收据只是第二道闸），Pending 交易引用的键由 `Prune` 保护；
> 2. `EconomyStore.SaveWorld` 在自检**之前**兜底裁剪（`TrimReceiptsInPlace`）：
>    **任何来源的收据膨胀都不该让整个世界写不出去**；
> 3. 回归测试 `ReceiptBookStaysBoundedAcrossSeasons`（300 人推进到第 252 天：发薪 900 份、收据裁到 512、
>    自检通过、重复推进不再发钱）与 `SavePrunesBloatedReceiptsInsteadOfRefusing`（5000 条收据也能落盘）。
>
> **本轮实测数字**：零依赖 **296/296** ✅｜含游戏引用 **406/406** ✅｜
> Release 两种 `GameRefsEnabled` **0 警告 0 错误** ✅｜包内哈希 6/6 独立重算一致 ✅｜无夹带 ✅
>
> **部署包（收据裁剪修复版，2026-10-11 20:1x）**：
> `NationalSpireEconomy.dll` = **361984** 字节 / `027E7CC3C1AEB324D581718F521E87E793D92FEB9DAF6A9A21B09A97472B999D`
> ⚠️ **字节数与上一版完全相同（都是 361984），只核对长度会误判！**必须核对哈希，
> 或搜 ASCII 串 **`TrimReceiptsInPlace`**（上一版没有）。
>
> **下一步（甲方再跑一次，与上一轮同样的动作）**：
> 1. `mod-debug.log` 里**不再**出现 `落盘失败` / `收据条数 … 异常膨胀`；
> 2. 出现 `世界 … 已保存（1632 人，…）`，且 `worlds\e35d0119.json` 的 `lastTickedDay` 会跟着涨；
> 3. （加分项）若出现 `世界 … 收据兜底裁剪：N → 512` 说明兜底路径生效；
> 4. 再发一条私信，`[Economy-Inject-A] 注入成功` 应仍然出现。
>
> **附注**：磁盘上那份 `e35d0119.json`（第 0 天 + 1632 人）是**合法状态，不需要删**；
> 下次进生涯时影子推进会逐日补齐到当前游戏日（按发薪水位线在正确日期结算）再落盘。
> 想从头体验就删掉 `worlds\e35d0119.json`（**原版存档不受影响**）。

> ---
>
> ## 🥈 2026-10-11 20:2x 更新（**第二轮真机验收：落盘修复确认生效；又抓出并修掉第三个缺陷"按需载入"**）
>
> 甲方用**收据裁剪修复版**（362496 前身那一版 = 361984 / `027E7CC3…`）跑了一轮（20:11 与 20:15 两次启动）。
>
> ### ① 上一轮的落盘修复：真机确认生效
>
> ```
> [Error] 数量：0 ✅（上一轮同一动作是天天报错）
> 20:15:40.479  economy.world: 载入影子存档 e35d0119：Loaded｜1632 人｜水位线第 0 天。
> 20:15:40.573  economy.store: 世界 e35d0119 已保存（1632 人，0 笔交易，512 条收据）。
> 20:15:40.574  economy.store: 世界 e35d0119 已落盘（推进到第 399 天）。
> … 第 400 / 401 / 402 / 403 天 … 全部落盘成功
> ```
> - `落盘失败` = **0**、`收据条数 … 异常膨胀` = **0**（上一轮这两个是每推进一天报一次）；
> - 收据数稳定在上限 **512**（不再无限膨胀）；
> - `worlds\e35d0119.json` 从 998 KB → **2.87 MB** 并带 `.bak`，说明世界真的在持久化；
> - 20:15:53 的私信**注入成功**（第 427 天 / 第 6 赛季，`message 51 → 51 条`，S+V 都注入）⇒ 落点逻辑未受影响。
>
> ### ② 同一天抓出的第三个缺陷：重启后的**第一条私信**永远不注入
>
> ```
> 20:11:54.299 [Warning] [Economy-Inject-A] 注入未发生｜context=<private/e35d0119/tour-c00-5/…>｜
>               身份 世界 <e35d0119>｜人物 <tour-c00-5>｜来源 ContextPostfix｜message 49 → 49 条｜
>               S 段 (未执行)｜V 段 (未执行)｜原因 世界 <e35d0119> 不在影子存档里（未播种或载入失败）
> ```
>
> **机制**：影子存档原先**只在"游戏日期推进"时**才载入 + 增量播种（`CareerDayPatch` 的后缀）。
> 于是"刚进游戏、还没跳过任何一天就先发一条私信"必然命中缓存里没有这个世界 ⇒ 安全地拒绝注入。
> 那一刻**身份其实是完整的**（`ContextPostfix` 拿到了权威 `CareerData`），只是账本还没被载入。
>
> **已修**：`PrivateRequestInjectionPatch.Inject` 在缓存未命中、且身份槽带着**同一世界号**的权威
> `CareerData` 时，走 `runtime.WorldFor(data)` **按需载入**——它与"跳天"是同一条
> `EnsureWorld` 路径（`EconomySeed` 幂等，不会重复发钱），只是把时机提前到第一条私信；
> 世界号不一致时仍然拒绝（日志里说明是"不一致"而不是"未播种"）。
> 新增回归测试 `PlanAInjectionTests.LoadsWorldOnDemandForFirstPrivateMessage`。
>
> ### ③ 第三轮真机验收（20:23，按需载入修复版）：判据全中
>
> 甲方**进生涯后先直接发私信**（没有先跳天）——这正是上一轮失败的那个场景：
> ```
> 20:23:09.565 [Economy-Inject-A] 世界 <e35d0119> 此前未载入，已在私信路径上按需载入（1632 人）。   ← ★本轮修复的直接证据
> 20:23:09.569 [Economy-Inject-A] 注入成功｜context=<private/e35d0119/tour-c00-5/…>｜身份 世界 <e35d0119>｜
>              人物 <tour-c00-5>｜第 427 天 / 第 6 赛季｜来源 ContextPostfix｜message 53 → 53 条（条数不变）｜
>              S 段 注入成功｜位置 anchor｜长度 129｜指纹 19d00f57｜零数字 是｜
>              V 段 注入成功｜位置 appended｜message 下标 51｜长度 93｜指纹 063a744a｜含数字 是
> 20:23:09.564 economy.world: 载入影子存档 e35d0119：Loaded｜1632 人｜水位线第 427 天。
> 20:23:35.359 economy.store: 世界 e35d0119 已保存（1632 人，0 笔交易，512 条收据）。
> ```
> - `不在影子存档里` = **0** ✅（上一轮同一动作是 1）；
> - `[Error]` = **0**，唯一一条 `[Warning]` 是设计内的 `user:// 不可用，回退到 ApplicationData`；
> - **水位线从第 0 天变成第 427 天** ⇒ 上一轮的落盘修复不只是"能写"，而是**跨会话读回来了**；
> - 落点依旧正确：`[Economy-Locator] … 覆盖 1／1 份`；`★执行副本判据` 6/6 全部"落点正确"；
>   `★私信专属` 30 行、`ProcessPrivateAsync` 8、`RequestPrivateAsync` 7。
>
> **⇒ 阶段 3 的三个真机缺陷（落点 / 收据膨胀落盘 / 按需载入）至此全部闭环，且都有真机原文。**
>
> ### ④ 本轮实测数字与部署包
>
> 零依赖 **296/296** ✅｜含游戏引用 **407/407** ✅｜Release 两种配置 **0 警告 0 错误** ✅｜包内哈希 6/6 一致 ✅
>
> **部署包（按需载入修复版，2026-10-11 20:2x）**：
> `NationalSpireEconomy.dll` = **362496** 字节 / `F0264831897DE06F34AA9D4358B54D0E9BD2B85608C3EECF5A1493B484812737`
> （这一版**长度与上一版不同**：361984 → 362496，辨识更容易；但**照样必须核对哈希**。）
>
> **下一步（甲方再跑一次，动作同上一轮）**：
> 1. 进生涯后**先直接发一条私信**（不点「跳过四周」）——应当**注入成功**，
>    并在 `mod-debug.log` 看到 `世界 <…> 此前未载入，已在私信路径上按需载入（N 人）`；
> 2. `[Error]` 仍应为 0；`世界 … 已保存` 应继续出现；
> 3. `[Economy-Inject-A] 注入成功` 仍在。

---

## 1. 项目核心信息与四条铁律

### 1.1 路径总表

| 用途 | 绝对路径 | 权限 |
| --- | --- | --- |
| **项目根目录** | `D:\蓝色大肥鱼\NationalSpireEconomy\` | 读写 |
| 交接文档（旧，进度已过期） | `D:\蓝色大肥鱼\NationalSpireEconomy\HANDOFF.md` | 只读参考（顶部有警告，内容勿删改） |
| 交接文档（阶段 0~2 + 3-A/3-B/3-D 全量） | `D:\蓝色大肥鱼\NationalSpireEconomy\HANDOFF-PHASE-0-2.md` | 读写（**§7.6 是本轮事故的完整记录**） |
| **本文** | `D:\蓝色大肥鱼\NationalSpireEconomy\HANDOFF-PHASE-3-PROBE.md` | 读写 |
| 原版源码（只读参考） | `D:\蓝色大肥鱼\NationalSpirre\NationalSpire-main`（注意双 r 拼写） | **只读** |
| 游戏安装目录 | `D:\Steam\steamapps\common\Slay the Spire 2` | 只读 |
| 游戏程序集目录 | `D:\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64` | 只读 |
| **我们的部署目录** | `D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy\` | **需管理员** |
| ⛔ 严禁修改 | `D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpire\` | 禁写（已备份） |
| **运行时真正加载的原版程序集** | `D:\Steam\steamapps\workshop\content\2868840\3809501471\NationalSpire.dll`（4304384 字节） | 只读 |
| 部署包产出 | `D:\蓝色大肥鱼\NationalSpireEconomy\deploy\NationalSpireEconomy\` | 读写 |
| 部署教程（阶段 3 完整版，含体检判定表） | `D:\蓝色大肥鱼\NationalSpireEconomy\deploy\DEPLOY.md` | 读写 |
| 一键打包脚本 | `D:\蓝色大肥鱼\NationalSpireEconomy\build-deploy.ps1` | 读写 |
| **游戏控制台日志（唯一可靠通道）** | `%APPDATA%\SlayTheSpire2\logs\godot.log` | 只读 |
| **历史会话日志（按会话轮转）** | `%APPDATA%\SlayTheSpire2\logs\godot<时间戳>.log` | 只读 |
| 原版诊断日志 | `%APPDATA%\SlayTheSpire2\national_spire_diagnostics\national-spire.log` | 只读 |
| 诊断日志历史 | 同目录 `national-spire.log.previous` | 只读 |
| 影子存档（我们自己的） | `%APPDATA%\SlayTheSpire2\national_spire_economy\` | 读写 |

### 1.2 四条铁律（**任何方案都不得违反**）

| # | 铁律 | 具体含义 |
| --- | --- | --- |
| 1 | **绝不改原版** | 只写独立 DLL + 运行时 Harmony 补丁；**不改原版源码**、**不改原版 DLL**、**不写原版存档**（`national_spire_career.json` 只读） |
| 2 | **影子存档** | 所有经济数据写在我们自己的 `national_spire_economy\worlds\<WorldId>.json`；按 `WorldId` 分片，绝不串档 |
| 3 | **MoneyUnits 是唯一换算出口** | 1 内部单位 = 10 美元；任何 `* 10` / `/ 10` 都视为缺陷。影子存档内部一律用**美元整数** |
| 4 | **幂等键** | 余额变动一律走 `EconomyLedger`；`ReceiptBook` 幂等键 + `LastTickedDay` / `LastSalaryPaidSeason` 水位线保证"重复调用不重复发钱" |

### 1.3 分层纪律（不要破坏）

`src/Core`、`src/Economy`、`src/Trade`、`src/Inject` 四层**不认识任何游戏类型**，可零依赖编译与单测；
只有 `src/Game/` 碰原版。这是 294 条零依赖测试能跑起来的原因。

---

## 2. 当前真实状态与最新进展（**最重要的一节**）

### 2.1 阶段完成度

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| 第 1–6 步 | 纯逻辑：单位换算/配置、影子存档、档位+性格+播种、记账结算、交易引擎（23 个文件）、意向演进+审计 | ✅ 完成 |
| 阶段 0 | 模组骨架：入口 + 配置门禁 + 日志转接 + Harmony 登记 | ✅ 已验收 |
| 阶段 1 | 适配器：`CareerPerson → NpcFacts`、`Credits+CreditFraction → IPlayerWallet` | ✅ 已验收 |
| 阶段 2 | `PrivateStreamParser.Feed` 前缀补丁（剔除 `[Trade:]` 标记） | ✅ 已验收（但见 §3.1：自证的盲区同样适用于它） |
| 阶段 3-A | S 段注入（system 尾部）+ 影子存档日期推进（tick/发薪/落盘） | ✅ **真机端到端通过**（20:23：`注入成功｜S 段 注入成功｜位置 anchor｜零数字 是`；落盘/发薪/跨赛季均实测） |
| 阶段 3-B | V 段注入（`ordered.Current` 那条 user 消息） | ✅ **真机端到端通过**（`V 段 注入成功｜位置 appended｜message 下标 51`） |
| 阶段 3-D | 挂载点体检（探针，只观测不改数据） | ✅ **有效**：连续三轮据此定位根因（扫描早一步 / 落点错副本 / 收据膨胀 / 按需载入） |
| **方案 A** | 注入改挂 `AiService.RequestPrivateAsync` 前缀（单轨） | ✅ **真机端到端通过**（`[Economy-Inject-A] 注入成功` × 多次，`message N → N 条（条数不变）`） |

### 2.2 最新卡点（甲方最新实测）

1. 甲方**重命名 `mods` 文件夹强制重扫**后，`godot.log` 里已确认：
   ```
   Loading assembly DLL ...NationalSpireEconomy.dll
   Calling initializer method of type NationalSpireEconomy.Game.EconomyModEntry for NationalSpireEconomy, Version=1.0.0.0
   ```
   ⇒ **模组确实被加载、初始化入口确实被调用**。这一条排除了"模组根本没装/没被扫到"。
2. **但 `[Economy-Probe]` 一行都没有输出**（探针模块完全没有说话）。

### 2.3 由此可以立刻推出的结论（**下一个会话从这里开始**）

> ⛔ **本节已作废（2026-10-11 19:5x）**：下面三条假设是在"还以为是探针抛异常/旧包"的阶段写的，
> 后来都已排除。真根因是**程序集落点**（我们用 `Assembly.LoadFrom` 造了一份 `Default` 假副本，
> 而游戏执行的是 `IsolatedComponentLoadContext` 里自己那份）。**以本文最上面 19:5x 小节为准。**
> 保留原文只为记录"当时的排查路径"。

`[Economy-Inject]` 系列行（阶段 3-A/3-B 的）是在 `EconomyModEntry.Initialize` 里 **`HookProbe.Run` 之前**发出的；
`[Economy-Probe]` 是紧跟在后的 `HookProbe.Run` 发出的。**同一次初始化里前一批出现、后一批完全不出现**，
只剩三种可能（按可能性排序）：

| # | 假设 | 一行验证 | 修法 |
| --- | --- | --- | --- |
| **A** | **`HookProbe.Run` 抛异常**，被 `EconomyModEntry.Init()` 的 `catch` 吞成"未启用（阶段 unexpected）"。**最可能的抛出点是 `method.MetadataToken`**：RitsuLib 这类补丁框架可能加载/发射**动态程序集**，动态方法的 `MetadataToken` 会抛 `InvalidOperationException` | 搜 `godot.log` 里的 **`未启用（阶段 unexpected）`** 与 `economy.init` 的 `[Error]` 行 | ① `HookProbe.Run` 内**每一步**都 try/catch；② 跳过 `assembly.IsDynamic`；③ 所有 `MetadataToken` 访问包 try/catch；④ 在 `Initialize` 里**把 `HookProbe.Run` 也包进 try/catch**（探针失败绝不能让整个模组降级） |
| **B** | 甲方测试用的**不是探针版 DLL**（旧包覆盖或复制不完整） | 核对 `mods\NationalSpireEconomy\NationalSpireEconomy.dll` 的**大小 276992** 与哈希 `E838B1B6…` | 重新按 §5.3 覆盖部署 |
| **C** | 控制台通道绑定失败（`Skip` / 未就绪），于是**所有非警告控制台行都没了** | 若 `[Economy-Inject]` 也一并没有，就是这条；若 `[Economy-Inject]` 有而 probe 无，则不是 | 看 `[Economy-Probe] 诊断通道：…`——但它本身也不输出，所以改用 `HoldProbe` 之外的手段：在 `EconomyModEntry` 里用 `log.Warn`（Warning 级）打一条兜底 |

> **判据一句话**：**搜 `未启用`**。若出现 `未启用（阶段 unexpected）` ⇒ 假设 A 成立（且异常类型就写在同一行里）；
> 若模组显示"已启用"但仍无 probe 行 ⇒ 假设 B 或 C。

### 2.4 已经推翻的假设（**不要再往这个方向查**）

**❌ "玩家可以替换整套 system prompt，所以覆盖了我们的注入"** —— 代码级不成立：

1. 外部提示词导入只替换 **13 个"段落正文"**（`PromptLibrary.LegacySections` / `PromptOverrides` /
   `PromptTransfer` 的 bundle / `CareerData.PrivatePromptSnapshot`），它们被拼进**同一条 system 字符串**
   （`PrivateMessagePrompts.cs:144-145`），而我们的补丁是**在原方法体跑完之后**追加 ⇒ 覆盖不到。
2. `RequestPrivateAsync`（`AiPrivateMessages.cs:135`，类名注意见 §3.2）第 142 行
   `new { model, messages, stream }` 把消息列表**原样**序列化发出，**中间没有任何重建/替换 system 的环节**。
3. 二进制实测：部署版 DLL 里锚点 `后续人物资料与记忆是参考内容`、`当前日期：` **都存在**；
   没有 `systemPrompt` / `系统提示词` / `覆盖提示词` 之类的"整套 system 覆盖"痕迹。

### 2.5 真实原因候选（待探针确认）

1. **`PrivateMessagePrompts.Compose` 挂载点没有被执行**（运行中的 NationalSpire 是 **v0.26.10**，
   我们的参考源码是 **0.26.7**；版本差足以让私信组装路径换方法）。
2. **进程里有多份 `NationalSpire.PrivateMessagePrompts`**（不同程序集实例 / ALC）。
   甲方环境装了 **RitsuLib**（一个"补丁框架 + 程序集加载器"，日志显示它应用了 431 个补丁），
   而 `PrivateMessagePatch.FindComposeMethod()` 只挂了**第一个匹配**。

### 2.6 事故证据（甲方机器实测，会话轮转日志）

| 会话 | 部署构建 | `[Economy-Inject]` | `[Economy-Inject-V]` | `首次实测注入` | 同期真实私信请求 |
| --- | --- | --- | --- | --- | --- |
| 14:43 | 早于 3-A | 0 | 0 | 0 | — |
| 14:46 / 14:50 | 3-A | 8 | 0 | **0** | 有（12:48、12:50） |
| 15:08 | 3-A | 8 | 0 | **0** | — |
| 15:11 | 3-B | 8 | **2 ✅** | **0** | 有（15:11:12，messages=9, characters=17542） |

- 私信确实发生过的**硬证据**：`national_spire_diagnostics\national-spire.log` 里的 `private.request` 记录
  （字段含 `context: "private/<key>/<person>/<turnId>"`、`messages` 条数、`characters` 总字数）。
- **启动自证 ≠ 端到端**：自证全过、请求真的发生、注入日志却是 0 行 ⇒ 补丁不在执行路径上。

### 2.7 测试基线（**必须保持全绿**）

```
零依赖测试（默认）：                    总计 296：通过 296，失败 0
含适配层+补丁层（GameRefsEnabled=true）：总计 407：通过 407，失败 0
Release 构建（GameRefsEnabled 两种取值都实测）：0 个警告 0 个错误
```

### 2.8 当前部署包（按需载入修复版）

| 文件 | 字节 | SHA256 |
| --- | --- | --- |
| `NationalSpireEconomy.dll` | **362496** | `F0264831897DE06F34AA9D4358B54D0E9BD2B85608C3EECF5A1493B484812737` |
| `NationalSpireEconomy.json` | 414 | `405C77886D727A5B39394903A9A04B1B8A566725D53D236BE423F643D7809D8C` |
| `config/economy_config.json` | 5293 | `DBDB56108ED328A4790C92AE5DE09906F51EAF44CBD666A1E4F808AB38CEF64E` |
| `config/personality_economy.json` | 11572 | `1E4DA255FC6B7F7D9F18841BBAC0E55237812F4179FBEEAE44AD923232FAD7A2` |
| `config/ai_prompt_economy.json` | 6451 | `DEF2825DADC62C1AD1A406F5D85F31642527B6CB7BC56E2875B8A01A74C0145D` |
| `Catalog/items.json` | 1567 | `F85E0D3F6C380D4F76F5C993219BAF42E53AE69148FC90A8E2C00DFE2C6FA22D` |

### 2.9 挂载点的**真实签名**（部署版二进制实测，方案 A 直接照抄）

```
NationalSpire.PrivateMessagePrompts.Compose(CareerData, PrivateConversation, PrivateTurn) → List`1
  ｜token=0x0600131B｜程序集 NationalSpire
NationalSpire.PrivateMessagePrompts.Context(CareerData, PrivateConversation, PrivateTurn, List`1) → String
  ｜token=0x0600131E｜程序集 NationalSpire
NationalSpire.AiService.RequestPrivateAsync(AiOptions, List`1, Boolean, Action`1, CancellationToken, Action`1, String) → Task`1
  ｜token=0x060004AF｜程序集 NationalSpire        ← 方案 A 的主挂载点（"最后一站"）
NationalSpire.PrivateStreamParser.Feed(String) → Void
  ｜token=0x0600133F｜程序集 NationalSpire
```

### 2.10 配置开关（`config/`）

| 文件 | 键 | 当前值 | 含义 |
| --- | --- | --- | --- |
| `economy_config.json` | `injection.stableEnabled` | true | S 段总开关 |
| `economy_config.json` | `injection.volatileEnabled` | true | V 段总开关 |
| `economy_config.json` | `injection.probeEnabled` | true | **体检开关**（确认完可改 false） |
| `ai_prompt_economy.json` | `stableEnabled` / `volatileEnabled` | `"true"` | 同一段第二道闸（字符串），两处都开才注入 |
| `ai_prompt_economy.json` | `stableAnchor` | `后续人物资料与记忆是参考内容` | S 段插在这句之前 |
| `ai_prompt_economy.json` | `volatileAnchor` | `当前日期：` | V 段追加到以这句开头的 user 消息末尾 |

三份 JSON 的 `configVersion` 均为 `1`（阶段 3 未升版本）。

---

## 3. 避坑的血泪教训（绝不允许重蹈覆辙）

### 3.1 「自证」不能代替端到端验证（**本轮最大教训**）

启动自证只能证明三件事：**纯逻辑正确**、**Harmony 接受补丁**、**模板/配置健康**。
它**不能**证明"挂上的那份就是游戏在跑的那份"。本轮 3-A/3-B 自证全过、私信请求真实发生，
注入却 0 行 —— 就是栽在这个盲区上。

**纪律**：
- 任何新挂载点，自证里必须包含**运行期 Liveness 证据**（探针命中计数 / 目标方法签名与 Token），
  而不只是"我挂上了"。
- 阶段 2 的 `PrivateStreamParser.Feed` 补丁**同样没有端到端证据**（当时的验收靠自证），
  所以本轮探针把它一起体检了。

### 3.2 `RequestPrivateAsync` 在 `AiService`，**不在** `AiPrivateMessages`

- 文件名是 `AiPrivateMessages.cs`，但类声明是 **`public static partial class AiService`**（`AiPrivateMessages.cs:7`）。
- **按类名找必然找不到**。必须**按方法名 + 首参类型 `AiOptions`** 在整个程序集里找。
- 已踩过一次：二进制字符串搜 `AiPrivateMessages` 返回 `False`，差点误判"这个版本没有请求方法"。

### 3.3 游戏环境有加载器（RitsuLib），**同一类型可能有多份程序集副本**

- 不能只挂"第一个找到的"。所有查找必须**遍历全部已加载程序集**，凡定义目标的副本**全挂**（甲方已批准）。
- 推论：动态程序集/动态方法上调用 `MetadataToken` 会抛异常 —— 探针的静默失败极可能就是这么来的，
  **所有反射访问都要包 try/catch，并跳过 `assembly.IsDynamic`**。

### 3.4 绝不要引导甲方去游戏里逼 NPC 输出 `[Trade:]`

- 实测：NPC 会识破并回绝（"你这是拿我当程序试呢"）。
- 纪律：**验证一律走"启动自证 + 日志 + 原版界面本身的预览入口"**，不做任何诱导。
- 顺带的好消息：原版私信界面顶部有 **「提示词预览」** 按钮（`PrivateMessageScreen.cs:44`），
  它直接调用 `PrivateMessagePrompts.Compose` 并显示完整提示词、还能一键复制 —— **完全不经过 AI**，
  是 S/V 段最理想的、甲方可自行使用的验收入口。

### 3.5 `national-spire.log` 的写入通道**一直没接通**

- 实测：当前文件与 2.1MB 的历史文件里 **`economy.*` 全是 0**（`economy.inject/world/store/seed/tick/config` 全 0）。
- 也就是说：我们**只有 `godot.log` 一条可见通道**。
- 已修（待验证）：不再只用 `Type.GetType`（对程序集名很挑）→ 遍历已加载程序集按完整名找类型；
  `Record` 先按精确签名、再放宽到"任意两参 Record"；并加**懒重试**（最多 5 次，仅运行时构造生效）。
  绑定结论写进 `EconomyLogAdapter.LastBindingReport`，由体检打印。
- 注意：`Diagnostics.Record(string, object)` 的签名在部署版里**确实存在**，所以问题不在签名，而在绑定时机/方式。

### 3.6 我们自己的日志曾全是 `ERROR:` + 调用栈（已修，待验证）

非警告行原本走 `Godot.GD.PushError`，导致 `godot.log` 里每条诊断都带 `ERROR:` 前缀和一段堆栈，
既像报错又刷屏。现已改为优先 `GD.Print`。
**并且绑定前必须先问 `EconomyModEntry.IsGodotNativeReady()`**：GodotSharp 程序集在进程里 ≠ 原生函数可用
（Sentry 的 Godot 扩展会把程序集拖进来），那种进程里调 `GD.Print`/`GD.PushError` 会直接杀进程（`0xC0000005`）。

### 3.7 搜索日志时的假阴性（我踩过，差点误判"模组完全没输出"）

**不要把 `Select-String -SimpleMatch` 与 `[regex]::Escape()` 混用**：转义后的模式在 SimpleMatch 下
是"字面含反斜杠的字符串"，永远匹配不到。直接用 `-SimpleMatch '原文'` 或纯正则。

### 3.8 `dotnet run` 会污染 `bin`（交付相关）

用默认配置（`GameRefsEnabled=false`）跑一次测试，会把主工程重建成**不含 `src/Game/` 的缩减版**并覆盖
`bin\Release\net9.0\NationalSpireEconomy.dll`。
⇒ **交付/部署一律走 `build-deploy.ps1`**（它先清 `bin`/`obj` 再用 `GameRefsEnabled=true` 重建）；
**不要手工从 `bin` 拷 DLL**。判断某份 DLL 是否完整版：搜二进制里有没有 `ShadowEconomyRuntime` / `HookProbe`。

### 3.9 其它环境坑（简表）

| 坑 | 结论 |
| --- | --- |
| 本机是 PowerShell 5.1 | `.ps1` **必须纯 ASCII**；路径从 `$PSScriptRoot` 派生；`Get-Content` 读 UTF-8 无 BOM 会乱码（用 `[System.IO.File]::ReadAllText(..., UTF8)`） |
| `.NET` 用户字符串存 **UTF-16**（#US 堆） | 二进制搜中文串要**双对齐**（从 0 与从 1 各解一次），否则会漏检（实测锚点就在奇偏移） |
| 类型/方法名存 UTF-8 | 搜类型名用 UTF-8 解码 |
| 构建确定性 | 同输入重复构建，DLL SHA256 **完全一致**（实测） |
| 日志轮转 | `godot.log` = 最近一次会话；历史会话是 `godot<时间戳>.log`；**不要只查 `godot.log`** |

### 3.10 ★ 用 `Assembly.LoadFrom` 加载游戏程序集 = 自己造一份"永不执行"的假副本

**这是阶段 3 最贵的一个坑**（症状：挂载成功、token 全对、活性自检通过，真实私信零命中）。

| 要点 | 内容 |
| --- | --- |
| **机制** | `Assembly.LoadFrom` **总是装进 `Default` ALC**，即使调用方在自定义 ALC 里（本机实测：`_alc-probe\RESULT.txt` 的 Q7b）。而游戏的 `ModManager` 把模组装进 `IsolatedComponentLoadContext(…sts2.dll)`，**逐个加载 + 立刻初始化**，NationalSpire 排在我们后面 ⇒ 我们初始化时"游戏会执行的那一份"**还不存在** |
| **后果** | 进程里两份 `NationalSpire`：`Default`（我们造的、16 个补丁目标全在它上面、永不执行）+ `Isolated`（游戏执行的、0 个目标）。两份 `FullName`/`Location` **完全相同** |
| **正确做法** | `AssemblyLoadContext.GetLoadContext(自己的程序集).LoadFromAssemblyPath(path)`。同一 ALC 按身份去重（Q1/Q2/Q3）⇒ 游戏后来装同一个模组 DLL 时，拿到的是**我们已经打好补丁的那一份** |
| **判据** | 日志里看 ALC：`ALC IsolatedComponentLoadContext(D:/…/sts2.dll)` = 对；`ALC Default` = 又错了 |
| **同类陷阱** | `Type.GetType("SomeType, NationalSpire")` 也按程序集名解析，同样可能拉出一份 `Default` 副本——本项目已在绑定路径上禁用它（见 `EconomyLogAdapter`） |

### 3.11 ★ Harmony：**"补丁记录存在" ≠ "detour 还活着"，而且它不挡重复挂载**

- **僵尸状态（实测）**：反复 `Unpatch` / 反复 `Initialize` 之后，`Harmony.GetPatchInfo(m)` 里**明明白白列着我们的前缀**，
  但方法体其实已经还原 —— 症状正是"补丁在表里、就是不生效"（`TradeMarkerInterceptor` 的标记不再被剥离，
  被 `TradeMarkerInterceptorTests.PatchAppliesToRealParserType` 抓出来）。
- **不挡重复（实测）**：同一方法连挂 4 次，表里就有 4 份同名前缀。
- **⇒ 现行写法**：幂等不要写成"查记录 → 有就跳过"，而要用 **先 `Unpatch` 再 `Patch`**
  （见 `PrivateStreamPatch.TryUnpatchMethod`）：既保证恰好一份，又能把僵尸状态修回来。
- `PathProbe.HasPatch(method, patchMethod)` 仍然用于**避免叠加同一前缀**（历史上"观测探针挡住注入前缀"那个坑），
  但**不能拿它当"补丁一定活着"的证明**——活着的证明只有**命中/执行痕迹**。

---

## 4. 下一步任务：探针排查 → 方案 A 执行

> ⚠️ **本章 4.1 / 4.2 已于 2026-10-11 19:5x 执行完毕**（根因 = 程序集落点；方案 A 代码已落地并全绿）。
> **现在只剩一件事：一次真机复验**，判据在本文最上面的 **19:5x 小节**（先看 `[Economy-Locator]` → 再看 `★执行副本判据`）。
> 下面 4.1 里那个"期望 Length = 276992"已经过期，**现行部署包的期望值是 361984 / `17EE2E0C…`**。
> 原文保留，作为"当时为什么那样查"的记录。

### 4.1 第一步（**已完成**）：让探针说话

**目标**：拿到 `[Economy-Probe]` 的程序集/类型副本清单与四个探针的命中情况。

按 §2.3 的三条假设依次验证，**先做这三件小事**（都很小，但必须做）：

1. **`Initialize` 里把 `HookProbe.Run` 包进 try/catch**（现在没有！探针一抛异常就会连带把整个模组降级为"未启用"）。
2. **`HookProbe` 内所有反射访问 fail-safe**：
   - 跳过 `assembly.IsDynamic`（动态程序集）；
   - `MetadataToken`、`Module`、`Assembly.GetName()`、`assembly.GetTypes()` 全部包 try/catch；
   - `Run` 的每一步（清单、类型副本、通道报告、四个装配）都单独 try/catch，**任何一步失败都要 Emit 一行说明**。
3. **加一条 Warning 级兜底日志**：`log.Warn("probe", "体检开始")` —— Warning 级走 `PushWarning`，
   即使普通控制台通道判定异常也仍能看见，用来区分"探针没跑"与"控制台通道没输出"。

**顺带核对**（一次命令就能排除假设 B）：
```powershell
Get-Item 'D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy\NationalSpireEconomy.dll' |
  Select-Object Length, LastWriteTime
Get-FileHash 'D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy\NationalSpireEconomy.dll' -Algorithm SHA256
# 期望：Length = 361984，哈希 = 17EE2E0CDD3F3C4619212B11E2142904E0315B4DCD77C2ABB36C485A851E68E8
# （19:2x 那一版是 349184/7B26EE8D…，已作废；再早的 276992/E838B1B6… 更早已作废）
```

**给甲方的验证指令（每次交付都按这个格式给）**：
在 `%APPDATA%\SlayTheSpire2\logs\godot.log` 里依次搜：
1. `未启用` —— 若出现 `未启用（阶段 unexpected）`，把整行发我（这就是探针抛异常的直接证据）；
2. `[Economy-Probe]` —— 期望看到"程序集清单 + 类型副本 + 诊断通道 + 探针已挂 + 体检装配完成"；
3. 进生涯点一次「跳过四周」→ 搜 `[Economy-Tick]`，看末尾 `｜探针 compose=N context=N request=N feed=N`；
4. 给任意 NPC 发一条私信 → 搜 `[Economy-Probe] 命中`。

**判定 → 动作对照**：

| 体检结果 | 结论 | 动作 |
| --- | --- | --- |
| `命中 RequestPrivateAsync` + **无** `命中 Compose` | 坐实 Compose 挂载点猜错 | 直接执行 §4.2 方案 A |
| 清单显示**多份** `PrivateMessagePrompts` | 坐实多副本 | 方案 A 必须**全副本挂载** |
| 两个探针都命中 | 方向变了：回去查 `Runtime`/配置为何没注入 | 先看 `[Economy-Inject] 首次实测注入：未注入｜原因 …` |
| 仍然一行都没有 | 说明还有更早的失败 | 看 §4.1 的 Warning 兜底行与 `未启用` 行 |

### 4.2 第二步：方案 A（末端注入，**单轨**，甲方已拍板）

**不再依赖 `PrivateMessagePrompts.Compose` 挂载点**，把干预点后移到"私信上线前的最后一站"。

| 要点 | 做法 |
| --- | --- |
| **主挂载点** | `NationalSpire.AiService.RequestPrivateAsync(AiOptions, List<Dictionary<string,string>>, Boolean, Action<string>, CancellationToken, Action<string>, String)` 的 **prefix**（签名/token 见 §2.9）。它的"会执行"由 `private.request` 诊断记录**实测证明**，且它是序列化前的最后一站，**下游无法覆盖** |
| **参数注入** | 用 `object[] __args`（不用具名/具类型），这样原版改参数个数或类型也照样能观测与读取；内部再按类型安全取值 |
| **全副本挂载** | 遍历**全部已加载程序集**，凡定义该方法的副本都挂；启动自证打印"发现 N 份、已挂 M 份" |
| **非私信过滤** | 只处理 `context` 以 **`private/`** 开头（`context` 是最后一个 `string` 参数）。群聊走 `group/…`、发帖/周报/仲裁各有自己的 context ⇒ 一律不注入 |
| **身份来源** | 主挂载点拿不到 `CareerData`（只有 `AiOptions`）。用 `PrivateMessagePrompts.Context(CareerData, PrivateConversation, PrivateTurn, List<ContextSpan>)` 的 **postfix** 记录"当前正在组装的 `(WorldId, PersonId)`"（带时间戳，窗口内有效；只接受账本里存在的人） |
| **S 段落点** | 第一条 `role=system` 的消息内容**追加**（复用 `SystemPromptInsert`，锚点 `stableAnchor`；找不到锚点退化到 system 尾部并记日志） |
| **V 段落点** | 以 `volatileAnchor`（`当前日期：`）开头的**那条 user 消息末尾**追加（复用 `CurrentSegmentInsert`）；找不到锚点**拒绝注入**并记日志，绝不改别的消息 |
| **不新增 message** | 只改字符串；注入前后断言 message 条数不变，并写进日志 |
| **幂等** | 内容已含 `【经济状况】` / `【当前经济】` 则跳过（防重复装配/双次调用） |
| **PendingVoice** | 先 `BuildVolatileOnly(commit:false)` 探测，**插入成功之后**才 `ClearPendingVoice` |
| **旧的 Compose 补丁** | 保留但**降级/停用**，避免两处同时注入（单轨原则：一处生效即可，另一处若也生效必须靠标题去重兜住） |

**方案 A 的自证要求（新增，针对本轮教训）**：

1. 打印**解析到的目标方法签名 + MetadataToken + 所在程序集名 + 副本份数**；
2. 打印 `RequestPrivateAsync` 探针的**命中计数**（Liveness）；
3. 保留 S/V 段合成自证（零数字 / 含数字 / 条数不变 / 锚点）；
4. 保留影子推进自证（前 83 天不发薪 / 第 84 天发薪 / 落盘 / 账实一致）；
5. 新增**反向控制**：模板里塞数字必须让自证**失败**（防止"通过"是恒真断言）。

**建议的新增测试**（游戏引用层）：
- 身份槽：命中 / 过期 / 人不存于账本 → 不注入；
- `context` 过滤：`private/` 注入，`group/` 不注入；
- S 落 system、V 落 Current、玩家那条消息一字不动；
- message 条数不变 + 重复调用幂等；
- **多副本**：构造两份同名类型（测试内可用两个测试程序集/动态类型）验证"全挂"；
- 签名不匹配时必须**明确报错**而不是静默跳过。

### 4.3 不能忘的两条纪律（方案 A 也一样）

- **S 段零数字**（`ConfigLoader.ValidatePrompt` 用 `\d` 拦着；违规要报 Error）；
- **V 段追加在 `ordered.Current`**（以 `当前日期：` 开头的那条 user 消息）**之后**，不新增 message。

---

## 5. 交付与部署规范

### 5.1 每次交付必须给甲方四样

1. **文件清单**（部署包内每个文件 + 字节数）
2. **SHA256**（包内 `SHA256SUMS.txt`，并**独立重算复核一遍**贴出来）
3. **构建命令**（可直接复制粘贴）
4. **日志搜索关键词**（去哪搜、搜什么、看到什么算成功、看到什么算失败）

### 5.2 构建与测试命令（本机沙箱必须重定向 4 个环境变量）

```powershell
$h = 'D:\蓝色大肥鱼\sandbox\dotnethome'
$env:APPDATA = "$h\Roaming"
$env:USERPROFILE = $h
$env:DOTNET_CLI_HOME = $h
$env:NUGET_PACKAGES = "$h\packages"
$env:DOTNET_NOLOGO = '1'

$root = 'D:\蓝色大肥鱼\NationalSpireEconomy'

# 零依赖测试（294）
dotnet run --project "$root\tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj" -c Release

# 含适配层+补丁层（404）
dotnet run --project "$root\tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj" -c Release -p:GameRefsEnabled=true

# 一键打包（清 bin/obj → Release 构建 → 组包 → SHA256 → 校验不夹带游戏 DLL）
& "$root\build-deploy.ps1"
```

> 打包**必须**是最后一次构建（否则 `bin` 会被零依赖构建覆盖成缩减版，见 §3.8）。

### 5.3 部署（**需管理员 PowerShell**，由甲方执行）

```powershell
# ① 备份原版（本项目不是 git 仓库）
$mods = 'D:\Steam\steamapps\common\Slay the Spire 2\mods'
Copy-Item "$mods\NationalSpire" "$mods\NationalSpire.backup" -Recurse -Force

# ② 覆盖部署（覆盖即可，不必删除目录）
$src = 'D:\蓝色大肥鱼\NationalSpireEconomy\deploy\NationalSpireEconomy'
$dst = "$mods\NationalSpireEconomy"
New-Item -ItemType Directory -Path $dst -Force | Out-Null
Copy-Item "$src\*" $dst -Recurse -Force
Get-ChildItem $dst -Recurse -File | Select-Object FullName, Length, LastWriteTime
```

**关键布局**：`config\` 必须与 DLL 同级（模组用"DLL 所在目录 + `config`"定位配置）。

> **本轮新经验**：甲方**重命名 `mods` 文件夹强制重扫**后模组才被正确加载。
> 若日志里看不到 `Loading assembly DLL ...NationalSpireEconomy.dll`，先让甲方确认模组清单是否被扫到
> （重命名/触碰 `mods` 目录可以强制重扫）。

### 5.4 回滚

```powershell
$mods = 'D:\Steam\steamapps\common\Slay the Spire 2\mods'
Rename-Item "$mods\NationalSpireEconomy" 'NationalSpireEconomy.disabled'     # 临时停用（推荐）
# 彻底清除（原版存档无损）
Remove-Item "$mods\NationalSpireEconomy" -Recurse -Force
Remove-Item "$env:APPDATA\SlayTheSpire2\national_spire_economy" -Recurse -Force
```

---

## 6. 甲方协作纪律

1. **汇报用中文**，技术细节可用 English 术语。
2. 每轮结束必须说清三件事：**我做了什么 / 我验证了什么 / 我需要你做什么**。
3. **不要替甲方宣布验收通过**：给证据 + 判定标准，让甲方拍板。
4. **不动账**：任何真实资金变动（阶段 4：交易执行/UI 确认）**必须等前面阶段全部验收通过**才开始。
5. **验证方式固定**：启动自证 + 日志 + 原版自带界面入口（「提示词预览」）；
   **绝不**引导甲方在游戏里逼 NPC 输出标记。
6. 甲方部署需管理员权限，`mods\` 目录不要尝试由 AI 直接写。
7. 每次交付都要给出**可 grep 的关键词**与**判定表**，让甲方能独立判断成败。

---

## 附录 A：本轮新增/修改的关键文件（供快速定位）

| 文件 | 作用 |
| --- | --- |
| `src/Game/NationalSpireLocator.cs` | **★ 本轮核心修复**：把原版装进**我们自己所在的 ALC**（`OurContext.LoadFromAssemblyPath`），暴露 `OurContext`/`LoadedContext`；`Assembly.LoadFrom` 只作最后兜底（并留痕） |
| `src/Game/AssemblyInventory.cs` | `GameAssemblies()` = **精确程序集名**（不再把 `NationalSpireEconomy` 算成原版副本）；新增 `MarkerAssemblies()`（模糊口径，仅供报告）、`IsGameAssembly()`、`SafeName()` |
| `src/Game/PathProbe.cs` | 旁路取证：**去掉"每进程只装一次"的闩锁**（可重入、按「方法+补丁方法」幂等、去重键带副本身份）；新增 **`★执行副本判据`**（`ReportExecutingCopy`）；`InstallDiagnosticsListener` 逐副本挂；`LastCopyCount` / `LastPatchedCopyCount` |
| `src/Game/EconomyModEntry.cs` | 第 **-1** 步装 `AppDomain.AssemblyLoad` 监听；第 **11.5** 步写 `[Economy-Locator]`；第 **12** 步装**落点警卫**（`ReinforceIfNewCopies` / `ReinforcePatches` / `ReinforceRuns`） |
| `src/Game/EconomyLogAdapter.cs` | 不再用 `Type.GetType("…, NationalSpire")`；绑定"我们自己 ALC 里的那一份"，结论里带 ALC |
| `src/Game/PrivateStreamPatch.cs` | 幂等改为**先卸后挂**（`TryUnpatchMethod`），修 Harmony 僵尸状态 + 防叠层 |
| `src/Game/HookProbe.cs` | 阶段 3-D 体检模块：程序集/类型副本清单、诊断通道结论、四个探针 |
| `src/Game/InjectDiagnostics.cs` | 五项启动自证（S/V 合成、S/V 插入、影子推进），前缀 `[Economy-Inject]` / `[Economy-Inject-V]` |
| `src/Game/CareerDayPatch.cs` | `CareerEngine.MoveToNextDay` postfix → 影子推进；幂等守卫改成按「目标方法+补丁方法」 |
| `src/Game/PrivateMessagePatch.cs` | 旧 Compose 挂载点（**已按单轨原则退场**，仅测试夹具手工装配） |
| `src/Game/ShadowEconomyRuntime.cs` | 影子存档生命周期 + `[Economy-Tick]` 行 + 探针摘要后缀 |
| `src/Inject/SystemPromptInsert.cs` / `CurrentSegmentInsert.cs` | S/V 段插入的纯逻辑（**方案 A 直接复用**） |
| `src/Inject/PromptInjector.cs` | `BuildStableOnly` / `BuildVolatileOnly`（后者负责 `PendingVoice` 语义） |
| `src/Economy/EconomyTick.cs` | `SalaryDueDay`：第 1 赛季按赛季末（第 84 天）结算 |
| `tests/.../PathProbeTests.cs` | 取证自证 + **本轮 4 条落点回归**（精确名 / 同 ALC / 多副本全覆盖 / 落点警卫补挂） |
| `tests/.../TradeMarkerInterceptorTests.cs` | `[Trade:]` 流式抽取；其中 `PatchAppliesToRealParserType` 是 Harmony 僵尸状态的哨兵 |
| `tests/.../InjectWiringTests.cs` | 游戏引用层接线测试，含挂载点签名解析测试 |
| `_alc-probe/`（工作区根目录） | **ALC 语义实测脚本**：`Program.cs` + `RESULT.txt`（去重行为、跨 ALC 可见性、`AssemblyLoad` 覆盖范围、`LoadFrom` 落 Default） |

## 附录 B：日志关键词速查

| 关键词 | 位置 | 含义 |
| --- | --- | --- |
| **`[Economy-Locator]`** | `mod-debug.log` | **★本轮第一判据**：原版副本数 + 每份的 ALC/路径 + 我们的补丁覆盖几份（期望 `1 份` + `覆盖 1／1` + `ALC IsolatedComponentLoadContext(…sts2.dll)`） |
| **`★执行副本判据`** | `path-probe.log` | **★直证**：从"游戏正在执行的方法"反查它属于哪个 ALC，并**就地**检查那一份上有没有我们的补丁；期望 `⇒ 结论：…落点正确` |
| **`[Economy-Reinforce]`** | `mod-debug.log` | 落点警卫：`落点警卫已装` / `发现原版副本 N 份，逐份补齐` / `补挂完成：…覆盖 M 份` |
| `补丁落点体检` / `我们的补丁覆盖` | `path-probe.log` | 逐副本体检（**精确程序集名**口径）；结论应为 `都已挂上` |
| `★私信时间线覆盖` | `path-probe.log` | 每次装配都写：精确检查点目标清单 + 本副本命中/新挂数（替代旧日志里靠措辞推断的做法） |
| `[Economy-Patch]` | `mod-debug.log` | 阶段 2：`PrivateStreamParser` 补丁装配与自证 |
| `[Economy-Inject]` | `mod-debug.log` | 阶段 3-A：S 段自证 + 影子推进自证 + 日期推进挂载 |
| `[Economy-Inject-V]` | `mod-debug.log` | 阶段 3-B：V 段自证与插入自证 |
| `[Economy-Tick]` | `mod-debug.log` | 影子存档推进记录（含发薪、跨赛季、探针摘要）；带 `｜自证` 的是自检，不带才是游戏日期 |
| `[Economy-Probe]` | `mod-debug.log` | 阶段 3-D 体检：程序集/副本清单、探针装配 |
| `★私信专属` / `★私信时间线` | `path-probe.log` | 私信链路命中 / 流程走到哪一步 |
| `★异常` | `path-probe.log` | 抛异常的那一环 |
| `private.request` | `national-spire.log`（游戏自己写） | 游戏确实发出了私信请求（**唯一硬证据**，与本地时间差 8 小时要对时区） |
| `未启用` | `mod-debug.log` | 初始化失败的降级说明（排查探针静默的第一入口） |

---

*本文档结束。若与本地代码不符，**以本地代码为准**，并顺手更新本文档与 `docs/HANDOFF-PHASE-0-2.md`。*
