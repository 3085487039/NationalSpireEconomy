# NationalSpireEconomy 部署与验证指南（阶段 0 / 1 / 2 / 3）

> **当前阶段：方案 A —— 末端注入（`RequestPrivateAsync` 前缀）已落地**。
> 阶段 0 / 1 / 2 已验收；阶段 3-A（S 段 + 影子推进）、3-B（V 段）的启动自证已在真机通过。
>
> **两条关键结论（都基于实测）**：
> 1. **输出通道修复（16:0x）**：探针静默不是"探针抛异常"，而是**整个模组的输出通道在输出端被丢弃**
>    —— 初始化时 Godot 原生运行时未就绪 ⇒ 控制台通道一票否决且从不重试。已加**文件保底通道**
>    （纯 `System.IO`，不经过 Godot），**第一现场改为 `mod-debug.log`**。
> 2. **注入点后移（16:3x）**：旧的 `PrivateMessagePrompts.Compose` 不在真实私信路径上（已停用），
>    注入改挂在 **`AiService.RequestPrivateAsync` 前缀**——消息序列化发出去之前的最后一站。
>    身份来自 `AiService.ProcessPrivateAsync`（那里同时有真实 `CareerData` 与人物 id；
>    元数据扫描已确认 `CareerEngine` **没有**"当前生涯"的静态入口）。
>
> **本轮请做两件事**：按 §4.4 跑一轮 → 搜 **`[Economy-Inject-A]`** 把结论发我。
>
> **工程纪律**（不变）：不改原版源码；S 段零数字；V 段追加在 `ordered.Current` 之后；
> 只改字符串、不新增 message；启动即可见、不依赖 AI 配合的自证日志。

---

## 0. 两条一直有效的经验（先看，能省一整轮排查）

### 0.1 日志去哪找

```
%APPDATA%\SlayTheSpire2\national_spire_economy\mod-debug.log          ← ★ 第一现场（纯文件，不依赖 Godot）
%APPDATA%\SlayTheSpire2\logs\godot.log                                ← 控制台通道（可能整条不可用）
%APPDATA%\SlayTheSpire2\national_spire_diagnostics\national-spire.log  ← economy.* 明细，钩子接上之后才有
%TEMP%\mod-debug.log                                                   ← 上面第一现场写不进去时的兜底落点
```

**为什么第一现场换了**：实测确认，模组初始化时 Godot 原生运行时可能尚未就绪，
那时 `godot.log` 里连一条我们的日志都不会有（整条通道在输出端被丢弃）。
`mod-debug.log` 是纯文件写入、不经过 Godot，**无论原生运行时是否就绪都必定有内容**。
它的第一行是"启动时间 + 进程号"，第二行起是带时间戳与级别的明细。

### 0.2 我们比原版 NationalSpire 早初始化，编译期与运行期必须是同一份

| # | 坑 | 现象 | 现状 |
| --- | --- | --- | --- |
| 1 | NationalSpire 比我们晚加载 | `AccessTools.TypeByName` 返回 null | ✅ 已修：`NationalSpireLocator` 显式加载 |
| 2 | 编译时引用的是被禁用的旧版 | 编译期/运行期版本不一致 | ✅ 已修：csproj 优先解析创意工坊版 |
| 3 | 失败原因只写进了看不到的通道 | 看起来"静默失败" | ✅ 已修：强制诊断 + 文件保底通道 |
| 4 | **`Assembly.LoadFrom` 把原版装进了 `Default`** | 补丁 16 个目标全挂在一份**游戏永不执行**的假副本上：`挂载成功、token 全对、活性自检通过、真实私信零命中` | ✅ 已修（阶段 3 定案）：改成装进**我们自己所在的 ALC**（= 游戏执行模组的那个），并新增"落点警卫"在出现新副本时自动补挂 |

**第 4 条是本项目最贵的一个坑，务必看懂**：
- 我方模组跑在 `IsolatedComponentLoadContext(…sts2.dll)`；游戏 `ModManager` 把每个模组 DLL 装进**同一个** ALC，并且**逐个**加载 + 立刻初始化（我们排在 NationalSpire 前面）。
- 旧代码用 `Assembly.LoadFrom` 预加载原版 → 本机实测它**总是装进 `Default`**（即使调用方在自定义 ALC 里）。
- 于是进程里有两份 `NationalSpire`：`Default`（我们造的、打了补丁、永不执行）和 `Isolated`（游戏执行的、零补丁）。两份的 `FullName` 与 `Location` **完全相同**。
- 修法：用 `AssemblyLoadContext.GetLoadContext(自己的程序集).LoadFromAssemblyPath(...)` 装进**自己的 ALC**——ALC 按程序集身份去重，游戏稍后来装同一个模组 DLL 时会拿到**我们已经打好补丁的那一份**。

原版的加载仲裁规则是「同 id 取版本号大者」，所以 `mods\NationalSpire\` 里那份通常是**被禁用的死代码**。

---

## 1. 阶段 3 新增了什么

| 能力 | 玩家能看到的变化 |
| --- | --- |
| **S 段注入**（阶段 3-A） | NPC 开始"知道自己大概有多少钱"：量级档位、收入来源、金钱观、最近的用钱倾向 |
| **影子存档日期推进**（阶段 3-A） | 每推进一天，影子账本跟着走；到结算日 NPC 会收到工资 |
| **V 段注入**（阶段 3-B） | NPC 开始"知道自己的准确余额与本季收支"，也会带回上一轮没说完的那句话 |
| **挂载点体检**（阶段 3-D，本轮） | 玩家看不到变化；它往日志里写"到底哪个方法真的被调用了" |

> ⚠️ **S 段与 V 段的"动态注入"目前还没有被证实生效**（启动自证通过、但真实私信里没命中）。
> 详见 §4.4。这一条不解决，前面三项都只是"代码写好了"。

阶段 3 的硬性纪律（都已落实，改动时别破坏）：

1. **不新增 message**：S 段只改 system 那条的字符串，V 段只改 `ordered.Current` 那条的字符串，
   message 条数与顺序与原版逐字节一致。
2. **数字只允许出现在 V 段**：S 段零数字，否则私信的前缀缓存每笔交易都会失效。
3. **清 `PendingVoice` 必须发生在"V 段真的注入之后"**，否则软失败留下的那句话会被悄悄弄丢。
4. **第 1 赛季发薪在赛季末（第 84 天）结算**，不是第 1 天：新世界前 83 天 NPC 只靠播种资金生活。
5. **V 段找不到锚点就不注入**（绝不退化成"随便挑一条 user 消息"）。

阶段 3 **不做**的事（别期待）：

- 交易的执行仍然不会发生（`[Trade:]` 标记被抽走并记日志，但没有确认框、不动钱）。
- 不做 UI。

---

## 2. 构建与产物

### 2.1 一键构建

本机沙箱要求重定向四个环境变量：

```powershell
$h = 'D:\蓝色大肥鱼\sandbox\dotnethome'
$env:APPDATA = "$h\Roaming"
$env:USERPROFILE = $h
$env:DOTNET_CLI_HOME = $h
$env:NUGET_PACKAGES = "$h\packages"
$env:DOTNET_NOLOGO = '1'

& 'D:\蓝色大肥鱼\NationalSpireEconomy\build-deploy.ps1'
```

脚本流程：清理旧产物 → `Release` 构建（`GameRefsEnabled=true`）→ 组装部署包 →
生成 `SHA256SUMS.txt` → **校验包里没夹带游戏程序集**（夹带就报错）。

> ⚠️ **不要手工从 `bin\Release\net9.0\` 拷 DLL**：跑过"零依赖"那条测试之后，
> 那里可能是不含 `src/Game/` 的缩减版。判断办法：完整版 DLL 里能搜到类型名 `ShadowEconomyRuntime`。

### 2.2 测试（实测基线）

```powershell
# 零依赖（Core/Economy/Trade/Inject 纯逻辑）—— 311 条
dotnet run --project 'D:\蓝色大肥鱼\NationalSpireEconomy\tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj' -c Release

# 含适配层与补丁层 —— 424 条
dotnet run --project 'D:\蓝色大肥鱼\NationalSpireEconomy\tests\NationalSpireEconomy.Tests\NationalSpireEconomy.Tests.csproj' `
  -c Release -p:GameRefsEnabled=true
```

两类都必须**全绿**（`失败 0`），Release 构建必须 **0 警告 0 错误**。

> 2026-10-11 阶段 4 第一步 v2 实测：**311/311** ✅ 与 **424/424** ✅。
> 新增的都是"不动钱"与"别猜钱"的钉子：只读预览（含"预览 delta 必须等于真跑一遍"）、双人确认幂等、
> 结算未接线、模型标记→只读预览、玩家确认指令不动钱、意图补全（方向/双方/回声判定）。

### 2.3 构建参数

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `GameRefsEnabled` | `false` | `true` 时才引用游戏程序集并编译 `src/Game/` |
| `Sts2Dir` | `D:\Steam\steamapps\common\Slay the Spire 2` | 游戏根目录 |
| `Sts2DataDir` | `$(Sts2Dir)\data_sts2_windows_x86_64` | 程序集所在目录 |
| `ModsDir` | `$(Sts2Dir)\mods` | 模组目录（引用 NationalSpire 本体用） |

### 2.4 部署包文件清单

产出目录：`D:\蓝色大肥鱼\NationalSpireEconomy\deploy\NationalSpireEconomy\`

| 文件 | 字节 | SHA256 |
| --- | --- | --- |
| `NationalSpireEconomy.dll` | 403968 | `8165D00CE043B2B35549A88B6507862496B94D52CA654921ECFF48AE74626BD8` |
| `NationalSpireEconomy.json` | 414 | `405C77886D727A5B39394903A9A04B1B8A566725D53D236BE423F643D7809D8C` |
| `config/economy_config.json` | 5892 | `8C35A50CB144D1F11FD192FC0BD6B52052AD22E3B2DF0BAD3EBE5F8176E78259` |
| `config/personality_economy.json` | 11572 | `1E4DA255FC6B7F7D9F18841BBAC0E55237812F4179FBEEAE44AD923232FAD7A2` |
| `config/ai_prompt_economy.json` | 7201 | `DDA4BF9A1507BF0CE2A0DEC992403A78FF2249188CC0E9CA1DF45BBF1084A24B` |
| `Catalog/items.json` | 1567 | `F85E0D3F6C380D4F76F5C993219BAF42E53AE69148FC90A8E2C00DFE2C6FA22D` |
| `SHA256SUMS.txt` | 564 | （清单本身） |

> 版本标识：**阶段 4 第一步 v3（教模型交易协议，纯配置）**。
> ⚠️ **DLL 哈希与 v2 完全相同**（403968 / `8165D00C…`）——这一版**只改了
> `config/ai_prompt_economy.json`**（在稳定段里补上 `[Trade: …]` 协议说明）。
> 只想吃到这次修复的话，**替换这一个 JSON 即可**（替换后重启游戏）。
> 为什么必须补这段：真机实测证明模型**从来不知道要发这个标记**——上一轮出现的标记
> 只是它在复读玩家自己打的字。没有这段，阶段 4 在自然对话里没有入口。
> 历史值：阶段 4 第一步 v2/v1 `403968…`/`400384 / 4F8D084B…`；按需载入修复版 `362496 / F0264831…`；
> 收据裁剪修复版 `361984 / 027E7CC3…`；落点修复版 v2 `361984 / 17EE2E0C…`。

校验完整性（重新计算并与清单逐行比对）：

```powershell
$stg = 'D:\蓝色大肥鱼\NationalSpireEconomy\deploy\NationalSpireEconomy'
Get-Content "$stg\SHA256SUMS.txt" | ForEach-Object {
  $parts = $_ -split '\s+', 2
  $path = Join-Path $stg ($parts[1].Trim() -replace '/', '\')
  "{0}  {1}" -f (Get-FileHash $path -Algorithm SHA256).Hash, $parts[1]
}
```

### 2.5 新增/变化的配置项

| 文件 | 键 | 默认 | 含义 |
| --- | --- | --- | --- |
| `economy_config.json` | `injection.stableEnabled` | `true` | S 段总开关 |
| `economy_config.json` | `injection.volatileEnabled` | `true` | V 段总开关 |
| `ai_prompt_economy.json` | `stableEnabled` / `volatileEnabled` | `"true"` | 同一段的第二道闸（字符串），**两边都开才注入** |
| `ai_prompt_economy.json` | `stableAnchor` | `后续人物资料与记忆是参考内容` | S 段插在这句之前 |
| `ai_prompt_economy.json` | `volatileAnchor` | `当前日期：` | V 段追加到以这句开头的那条 user 消息末尾 |
| `economy_config.json` | `injection.probeEnabled` | `true` | **挂载点体检开关**（排障用，确认完可改 false） |

`configVersion` 仍为 `1`（阶段 3 没有改存档结构）。

### 2.6 程序集引用表（实测）

```
0Harmony      ← HarmonyLib（Harmony 实例 + 补丁）
NationalSpire ← PrivateMessagePrompts / CareerEngine / CareerPerson / CareerData / PrivateStreamParser
sts2          ← ModInitializer（模组入口标记）
+ 12 个 System.*
```

**没有 `GodotSharp`。** 游戏程序集都是 `<Private>false</Private>`，**不会**进部署包。

---

## 3. 部署

> ⚠️ **`mods\` 目录需要管理员权限**（Steam 装在 `D:\Steam`）。这一步必须由你操作。

### 3.1 先备份

```powershell
$mods = 'D:\Steam\steamapps\common\Slay the Spire 2\mods'
Copy-Item "$mods\NationalSpire" "$mods\NationalSpire.backup" -Recurse -Force
```

### 3.2 复制

```powershell
$src = 'D:\蓝色大肥鱼\NationalSpireEconomy\deploy\NationalSpireEconomy'
$dst = 'D:\Steam\steamapps\common\Slay the Spire 2\mods\NationalSpireEconomy'
New-Item -ItemType Directory -Path $dst -Force | Out-Null
Copy-Item "$src\*" $dst -Recurse -Force
Get-ChildItem $dst -Recurse -File | Select-Object FullName
```

### 3.3 清掉旧的影子存档（**建议做**）

```powershell
Remove-Item "$env:APPDATA\SlayTheSpire2\national_spire_economy" -Recurse -Force -ErrorAction SilentlyContinue
```

原版存档 `national_spire_career.json` 不受影响（我们只读不写它）。

最终结构：

```
mods\NationalSpireEconomy\
├─ NationalSpireEconomy.dll
├─ NationalSpireEconomy.json
├─ SHA256SUMS.txt          ← 可不复制
├─ Catalog\items.json
└─ config\
   ├─ economy_config.json
   ├─ personality_economy.json
   └─ ai_prompt_economy.json
```

**关键：`config\` 必须与 DLL 同级。**

---

## 4. 验证

### 4.0 阶段 2 的补丁自证（仍然有效，先看这条）

搜 `[Economy-Patch]`。成功时：

```
[Economy-Patch] 正在尝试挂载补丁…
[Economy-Patch] 结果：成功｜Feed + Finish｜自证 通过
```

`自证 通过` 表示模组自己造了一个真实的 `PrivateStreamParser`、喂进
`PROBE[Trade: 诊断, Amount: 5000]END`、读回 `Text` 发现标记被吃掉了——**完全不依赖 AI**。

### 4.1 加载与补丁装配

搜 `NationalSpireEconomy`。成功时应看到（**重点是 `补丁 …` 那一段**）：

```
[NationalSpireEconomy] [Info] economy.init: NationalSpireEconomy v0.1.0 → 已启用 | 数据 <路径> | 配置 <路径> | 错误 0 警告 0 | 补丁 Feed + Finish + Compose (postfix) + MoveToNextDay (postfix)
```

### 4.2 启动期自证（**核心验收项**）

搜 `[Economy-Inject]`。**成功时应当看到这 10 行**（长度/指纹是实测量级，应与下面一致）：

```
[Economy-Inject] 正在挂载 S 段注入与影子推进…
[Economy-Inject] S 段挂载：成功｜NationalSpire.PrivateMessagePrompts.Compose 后缀
[Economy-Inject] 日期推进挂载：成功｜CareerEngine.MoveToNextDay(CareerData) 后缀
[Economy-Inject] S 段自证：长度 131｜指纹 06fe15d0｜零数字 通过｜含标题 通过｜余额未泄漏 通过
[Economy-Inject] 插入自证：锚点 命中｜位置 anchor｜message 条数 不变｜重复插入 幂等 通过
[Economy-Tick] 第 0 天 → 第 83 天（第 1 赛季）：推进 83 天｜自证
[Economy-Tick] 第 83 天 → 第 84 天（第 1 赛季）：推进 1 天，发薪 2 人共 $24,600｜自证
[Economy-Inject] 发薪规则：第 1 赛季按赛季末结算（第 84 天）；第 2 赛季起按 salaryPayoutMode = seasonStart
[Economy-Inject] 影子推进自证：播种 2 人｜前 83 天不发薪 通过｜第 84 天发薪 2 人｜落盘 通过｜账实一致 通过
[Economy-Inject] 结果：成功｜S 段 …Compose 后缀｜日期推进 …MoveToNextDay(CareerData) 后缀｜自证 通过
```

再搜 **`[Economy-Inject-V]`**（阶段 3-B 新增），应看到这 2 行：

```
[Economy-Inject-V] V 段自证：长度 115｜指纹 5d606f1f｜含数字 通过｜含标题 通过｜含余额 通过｜含未竟之言 通过
[Economy-Inject-V] 插入自证：锚点 命中｜位置 appended｜message 下标 3｜message 条数 不变｜重复插入 幂等 通过｜未污染玩家消息 通过
```

**五项自证各自锁住什么**：

| 自证 | 锁住的契约 |
| --- | --- |
| `S 段自证` | 稳定段非空、**零数字**、不含余额文本（前缀缓存不会被余额抖动打穿） |
| `插入自证`（S） | 插在锚点句之前、**message 条数不变**、重复插入幂等 |
| `影子推进自证` | **前 83 天不发薪**、第 84 天准时发薪、账本落盘、余额增量与工资一致 |
| `V 段自证` | 易变段非空、**含数字**、含标题、含余额、含未竟之言 |
| `插入自证`（V） | 落点在 `ordered.Current` 那条消息、条数不变、幂等、**玩家那条消息一字未动** |

### 4.3 最可靠的一条：用原版「提示词预览」（**完全不经过 AI**）

原版私信界面**顶部有一个「提示词预览」按钮**。点它会直接显示这次私信真正会送出的完整提示词，
还能一键"复制完整提示词"。它调用的是同一个 `PrivateMessagePrompts.Compose`。

> ⚠️ **这条不能用来验收方案 A**：方案 A 的注入点在 `RequestPrivateAsync`（**真正发送**时），
> 而「提示词预览」走的是 `Compose`（组装阶段），**不经过发送**，所以预览里看不到我们的两段。
> 预览目前只用来核对"原版提示词本身长什么样"。方案 A 的验收一律看 `mod-debug.log` 里的
> `[Economy-Inject-A] 注入成功`。

| 你看到的 | 结论 |
| --- | --- |
| system 段尾部出现 `【经济状况】` 那几行 | ✅ **S 段注入成功**（如果将来把注入点同时挂回 Compose） |
| system 段里**没有任何阿拉伯数字** | ✅ 零数字契约成立（前缀缓存安全） |
| 上下文中出现 `【当前经济】` 与 `你可用余额 $…` | ✅ **V 段注入成功**（同上） |
| 上下文里那句话在 `当前日期：…` 那条 user 消息的**末尾** | ✅ 落点正确 |
| 玩家自己写的那句正文**一个字都没变** | ✅ 没污染原版数据 |

复制出来发我，就是最直接的验收证据。

### 4.4 挂载点体检（**本轮请优先做这个**）

> ## ⚠️ 本轮（旁路取证版）只要一个文件：`path-probe.log`
>
> 方案 A 的补丁在上一轮真机里**全部挂载成功**，而私信请求也确实发生过
> （`national-spire.log` 的 `private.request` 为证），但**注入与四个探针零命中**。
> 已用 Harmony 补丁表排除"别的模组替换方法体/装了拦截前缀"，
> 也已逐个扫描全部模组 DLL 排除"别的模组劫持入口"。剩下唯一要回答的问题是：
> **真机上私信链路里到底哪些方法被执行了**。
>
> `PathProbe` 就是为回答它而加：它不经过我们的日志适配器，直接写
> **`%APPDATA%\SlayTheSpire2\national_spire_economy\probe\path-probe.log`**，
> 三部分内容——
> 1. **装配清单**：每个候选方法（`ProcessPrivateAsync` / `RequestPrivateAsync` / `Context` /
>    `Compose` / `Feed` / `Finish` / `SendAsync` / `SendTransportAsync` / …）是否挂上；
> 2. **Harmony 补丁表**：这些方法上现存的全部补丁（owner / 优先级 / 类型），
>    以及"该前缀是否返回 bool（返回 false 会跳过原版方法体）"；
> 3. **运行痕迹**：每次 `进入 …` / `退出 …` / `★异常 …`，带线程 id 与关键实参。
>
> **怎么跑**：清日志 → 部署 → 进生涯 → **点一次「跳过四周」** → **给任意 NPC 发一条私信**
> → 回传 **`path-probe.log`**（连同 `mod-debug.log`、`logs\`、`national_spire_diagnostics\`）。
>
> ### 搜什么（照这个表看就行）
>
> 文件：`%APPDATA%\SlayTheSpire2\national_spire_economy\probe\path-probe.log`
> （记事本打开即可。**先删掉旧的 `probe` 目录**再启动，避免新旧混在一起。）
>
> | 搜索这个词 | 看到什么 | 含义 |
> | --- | --- | --- |
> | **`[Economy-Locator]`**（在 `mod-debug.log`） | `原版副本 1 份（…ALC IsolatedComponentLoadContext(…sts2.dll)…）｜我们的补丁覆盖 1／1 份` | ⭐**本轮第一判据**：原版与我们同在游戏那个 ALC、且副本被我们覆盖。若显示 `ALC Default` 或 `覆盖 0／1` ⇒ 落点又错了，后面都不用看 |
> | **`★执行副本判据`**（在 `path-probe.log`） | `⇒ 结论：游戏执行的这一份【就是】挂了补丁的那一份（落点正确）` | ⭐**直证**：这一行是"从游戏正在执行的方法反查副本"得到的，不依赖任何清单。出现 `★结论：…没有我们的补丁` ⇒ 立刻回传全文 |
> | **`[Economy-Reinforce]`**（在 `mod-debug.log`） | `落点警卫已装：当前原版副本 N 份` | 保险丝就位；之后若又出现新副本，会看到 `[Economy-Reinforce] …：发现原版副本 M 份，逐份补齐` + `补挂完成：…补丁覆盖 M 份` |
> | **`活性自检结果`** | `✅ 探针被调用` | 证明"我们装的补丁在这个进程里真的会被执行"（**注意：它不等于挂对了副本**，那是上面两行的事） |
> | **`活性自检结果`** | `❌ 探针没有被调用` | ⚠️ **不要再换挂载点**：补丁在本进程根本不执行 → 把整份 `path-probe.log` 发我，转查机制问题 |
> | `旁路取证开始` | `===== 旁路取证开始｜时间｜进程 N =====` | 取证通道活着 |
> | `程序集盘点（第 6 步之后` | 每条带 `ALC …｜路径` | 装配时的真实环境。**修好之后原版应显示 `ALC IsolatedComponentLoadContext(D:/…/sts2.dll)`**（与我们同一个 ALC）；显示 `Default` 就是旧 bug 复现 |
> | `按名字装配：跨 ALC 扫到原版程序集` | `… N 份`，**N ≥ 1** | 扫描时机正确；N=0 会自带 `★一份都没有` 文案。⚠️ N 现在是**精确程序集名**口径，不再把 `NationalSpireEconomy` 算进来 |
> | **`我们的补丁覆盖`** / `都已挂上` | `我们的补丁覆盖：1／1 份` + `结论：1 份副本都已挂上` | 落点体检结论；出现 `★结论：只挂上了 1 份` ⇒ 还有副本没覆盖，回传全文 |
> | `Harmony 补丁表` | 目标方法上所有补丁的 owner／类型 | 确认方案 A 前缀在、没有别的 owner 在拦 |
> | `装配完成` / `精确检查点` | `带探针的方法共 …个` / `5 个／挂上 5 个` | 探针与时间线检查点都齐 |
> | **`★私信专属`** | `进入 …AiService.ProcessPrivateAsync(…)｜★私信专属` | **走了私信那条链** |
> | **`★私信时间线`** | `进入 …PrivateTurn.set_Context(String)｜★私信时间线`，随后 `set_Status`/`set_ReplyOrder`/`set_Reply`/`set_Reasoning` | **流程走到哪一步** |
> | `进入 ` | 每次调用一行，带线程号与实参 | 完整执行轨迹 |
> | `★异常` | `★异常 …｜某异常: 消息` | 抛异常的那一环 |
> | `转移器 0` | 每个目标都显示 | 没人替换方法体 |
>
> **判读顺序（务必按这个顺序，别跳步）**：
> 0. **先看 `[Economy-Locator]`**：原版副本是不是 1 份、是不是 `IsolatedComponentLoadContext`、覆盖是不是 1／1。
>    这一步不过，后面全部无意义（阶段 3 那轮就是"挂载成功却零命中"）。
> 0.5 **再看 `★执行副本判据`**：必须是"落点正确"。它由"游戏正在执行的方法"反查得到，是副本问题的终结判据。
> 1. 然后看 `活性自检结果`：**❌ 时后面的 `★私信专属` 缺失是必然的**，不用再发私信验证；
> 2. 再看 `Harmony 补丁表`：确认 `RequestPrivateAsync` 上有本模组前缀；
> 3. 前几条都正常，`★私信专属` 仍缺失，才说明"私信走了别的代码路径"。
>
> ### 判定
>
> | 关键观察 | 结论 | 我下一步会做什么 |
> | --- | --- | --- |
> | `进入 …ProcessPrivateAsync` **且** `进入 …RequestPrivateAsync` 都有，但**没有** `[Economy-Inject-A] 注入成功` | 原版链路在跑、我们的前缀却没说话 ⇒ 问题在我们补丁内部 | 看 `★异常`；否则把注入改挂 `ProcessPrivateAsync` 状态机（你提的方案 B） |
> | 只有 `set_Context`/`set_Status` 等时间线，**没有** `ProcessPrivateAsync`/`RequestPrivateAsync` | 请求发出的路径不是 `ProcessPrivateAsync`（或它被挂在别处） | 按实际命中的方法名重选注入点 |
> | 连 `Conversation`/`Compose`/`Feed` 都没有 | 整个私信流程都绕过了原版 NationalSpire 代码 | 把全文发我，改查"谁在回话"（第三方 AI 接管类模组） |
> | 出现 `★异常 <某方法>` | 直接指向抛异常那一环 | 针对那一环做防御 |
> | `Harmony 补丁表` 里出现别的 owner | 有模组在拦这条链 | 按它的类型决定绕开还是共存 |
>
> 想关掉取证：把环境变量 `NSE_PATH_PROBE` 设为 `0` 再启动游戏。

**背景（已改判，务必读）**：阶段 3-A/3-B 的启动自证在真机上**全部通过**，同一会话里也确实发生了真实私信请求
（`national-spire.log` 里的 `private.request`），但"第一次被调用必定说话"的注入日志**一行都没有**。
第一版体检版的归因是"探针抛异常被吞"，**这个归因已被实测推翻**：最近三次会话里模组**正常加载并正常返回**，
既没有 `未启用`，也没有任何 Error —— 真正的问题是**整条输出通道在输出端被丢弃**
（初始化时 Godot 原生运行时未就绪 ⇒ 控制台通道被一票否决且从不重试 ⇒ 非警告行全部消失）。
所以本轮的第一目标是**先让日志出得来**，其次才是探针数据。

**怎么跑（约 3 分钟）**：

1. **先清旧日志**（否则新证据会混在旧内容里）：
```powershell
Remove-Item "$env:APPDATA\SlayTheSpire2\national_spire_economy\mod-debug.log" -Force -ErrorAction SilentlyContinue
Remove-Item "$env:APPDATA\SlayTheSpire2\logs\godot.log" -Force -ErrorAction SilentlyContinue
```
2. 部署本包（§3.2）并启动游戏，然后**第一件事**是打开
   `%APPDATA%\SlayTheSpire2\national_spire_economy\mod-debug.log`，确认：
   - 第 1 行有 `启动时间：…｜进程 …`；
   - 有 `模组入口被调用｜版本 …｜Godot 原生就绪 …｜文件落点 …`；
   - 有 `文件保底通道：` / `诊断文件通道：` / `控制台通道：` 三条结论。
   把这三部分的**实际文本**记下来（尤其"文件落点"那个路径——它可能不是你以为的那个）。
3. 在 `mod-debug.log` 里搜 **`[Economy-Probe]`**：这一段是**程序集/类型副本清单**（含每份的路径、大小、
   改动时间、元数据 token）与**探针装配结果**。整段复制给我。
4. 进生涯，**点一次「跳过四周」**让日期推进一次，再搜 `[Economy-Tick]`：
   那一行末尾会带 `｜探针 compose=N context=N request=N feed=N`（一次跳天即可读到四个计数）。
5. **给任意 NPC 发一条私信**（内容随便，**不要**要求它输出 `[Trade:]` 之类标记），然后搜
   **`[Economy-Inject-A]`**——这是方案 A 的全部结论。你要找的是这几行：
   - `身份来源首次实测：已登记身份｜世界 <…>｜人物 <…>｜第 N 天 / 第 M 赛季`
     ⇒ 身份来源（`ProcessPrivateAsync`）真的命中了；
   - `[Economy-Inject-A] 注入成功｜context=<private/…>｜…｜message 5 → 5 条（条数不变）｜S 段 注入成功｜V 段 注入成功`
     ⇒ **注入真的发生了**，且条数没变；
   - 如果看到 `注入未发生｜…｜原因 …`，把那行原文发我——**原因本身就是答案**（身份没拿到 / 人不在账本 / 幂等跳过…）。
6. 回传：`mod-debug.log` 全文 + `logs\` 目录 + `national_spire_diagnostics\` 目录（**不需要**你分析）。

**找文件的关键词对照**：

| 关键词 | 含义 |
| --- | --- |
| `启动时间` + `进程` | 文件通道活着（没有这一行 = `Init` 根本没跑，是另一类问题） |
| `模组入口被调用` | 模组入口真的执行了；后面还带着"Godot 就绪与否"和实际落点 |
| `Godot 原生就绪 False` | 正常现象；正因为如此才必须有文件通道 |
| `控制台通道：跳过：…尚未就绪` | 说明控制台这条确实不通——文件通道就是为它准备的兜底 |
| `方案 A 目标：发现 N 份` | 找到了 `RequestPrivateAsync(AiOptions, …)` 的副本数 |
| `挂载点自证：已挂 N 份｜第一份 token=0x…` | ✅ **注入点确实挂上了**（签名 + token 是第一手证据） |
| `身份来源挂载：成功` / `身份来源首次实测` | ✅ 身份来源（`ProcessPrivateAsync` 状态机）真的命中并登记了身份 |
| `[Economy-Inject-A] 注入成功` | ✅ **S/V 段真的被写进了消息列表**（本条是方案 A 的终局证据） |
| `[Economy-Inject-A] 注入未发生｜原因 …` | ⚠️ 没注入，但**原因写在行里**（身份/账本/幂等），把它发我 |
| `[Economy-Probe]` | 探针清单与装配结果（保留，用来对比"哪些挂载点真的在跑"） |
| `[Economy-Probe] 命中 …` | 某个挂载点真的被执行到了 |
| `[Economy-Tick]` | 影子存档推进记录（含四个探针计数） |

**判定表**：

| 你看到的 | 含义 |
| --- | --- |
| `mod-debug.log` 里连"启动时间"都没有 | 文件通道都没跑起来 → 把 `%TEMP%\mod-debug.log` 也发我（那是兜底落点） |
| 有 `挂载点自证：已挂 N 份` | ✅ 注入点就位；剩下的只看 `[Economy-Inject-A]` 是否"注入成功" |
| `挂载点自证：未挂上任何一份` | ❌ 注入不会生效 → 把上面几行签名发我（可能原版改了参数形状） |
| `[Economy-Inject-A] 注入成功｜…（条数不变）` | ✅✅ **方案 A 端到端成立**：身份、落点、条数三项都对上了 |
| `注入未发生｜原因 身份无法确定…` | 身份来源没命中 → 搜 `身份来源首次实测` 与 `轨迹`，一起发我 |
| `注入未发生｜原因 人物 <…> 不在影子账本里` | 那个人还没播种（或它是真人）→ 先点一次「跳过四周」再发私信 |
| `注入未发生｜原因 …幂等跳过` | 上个版本已经插过了 → 重启游戏再试（这是防双注入的设计，不是故障） |
| `命中 RequestPrivateAsync` 但**没有** `命中 Compose` | ✅ 与预期一致：Compose 不在真实路径上，注入已改挂最后一站 |
| `[Economy-Probe]` 显示**多份** `PrivateMessagePrompts` | 多副本环境；注入点是全副本挂载的，清单会写出挂了几份 |

### 4.4.1 影子存档是否真的在落盘（**2026-10-11 20:1x 新增判据**）

**为什么加这条**：落点修好之后，日期推进补丁**第一次真的执行**，于是立刻暴露了下面这个缺陷 ——
真实世界有 **1632 人**，"每人每季一条幂等收据" ⇒ 5 个赛季就把收据顶到 **8160 条**，
而 `EconomyStore` 的自检在收据 > 512×2 时**拒绝写入**，表现是：

```
[Error] economy.store: 世界 e35d0119 自检失败：收据条数 8160 异常膨胀，可能未裁剪
[Error] economy.store: 世界 e35d0119 落盘失败（推进到第 369 天）：InvalidDataException: …拒绝写入
```

**后果**：磁盘上那份影子存档**永远停在播种那天**（实测：1632 人 / 0 条收据 / `lastTickedDay=0`），
而内存里已经跑到第 398 天 —— 所有日期推进与发薪都没落盘。**这个缺陷已修**（收据会裁剪到上限内）。

**跑一轮后按这个看**（都在 `mod-debug.log`）：

| 搜索词 | 期望 | 含义 |
| --- | --- | --- |
| `世界 <WorldId> 已保存` | 出现，且人数是 1600 左右 | ✅ 影子存档在正常落盘 |
| `落盘失败` | **不出现** | 出现 ⇒ 把那一行连同前后 5 行发我（自检又拦下了什么） |
| `收据条数 … 异常膨胀` | **不出现** | 同上 |
| `收据兜底裁剪：N → 512` | 出现也无妨 | 兜底路径生效（说明裁剪量很大，但世界仍能写出） |
| `worlds\<WorldId>.json` 的 `lastTickedDay` | 跟着游戏日期涨 | ✅ 推进真的被持久化（上一轮它是 0） |

### 4.4.2 重启后的**第一条私信**是否注入（**2026-10-11 20:2x 新增判据**）

**为什么加这条**：影子存档原先**只在"游戏日期推进"时**才载入，所以"刚进游戏、还没跳过任何一天、
直接发一条私信"会被安全地拒绝注入（真机原文）：
`注入未发生｜…｜原因 世界 <e35d0119> 不在影子存档里（未播种或载入失败）`。
现在私信路径会按需载入（与跳天同一条幂等播种路径）。**这一版已修。**

跑一轮后（`mod-debug.log`）：

| 搜索词 | 期望 | 含义 |
| --- | --- | --- |
| `已在私信路径上按需载入（N 人）` | 刚进游戏就发私信时出现 | ✅ 按需载入生效（N 约等于当前世界人数） |
| `[Economy-Inject-A] 注入成功` | 出现 | ✅ 重启后的第一条私信也能注入 |
| `不在影子存档里` | **不出现** | 若仍出现 ⇒ 说明身份槽带的 `CareerData` 缺失或世界号不一致，把那一行发我 |
| `message N → N 条（条数不变）` | 两边数字相同 | ✅ 我们只改字符串、不新增 message |

### 4.9 阶段 4 第一步：只读成交预览 + 双人确认（**这一版新增，不动钱**）

完整判据表见 `HANDOFF-PHASE-4-TRADE.md` §3。速查：

| 搜索词（`mod-debug.log`） | 期望 | 含义 |
| --- | --- | --- |
| `识别出交易标记` | 出现（自然对话里 NPC 主动提交易时） | ★协议已教给模型：它自己发出了 `[Trade: …]` |
| `[Economy-Preview] 交易单` | 出现，带 `★只读预览，未动任何余额` | 预览生成、待确认单落地（Reserve 不动钱） |
| `方向 `（在同一行里） | `方向 npcToPlayer（tour-… → player）` | **意图补全生效**：方向来自标记显式或 `trade.kindDirections` |
| `无法补全` | 不出现 | 若出现 ⇒ 那个 `Kind` 没在 `trade.kindDirections` 里配方向（拒绝是刻意的，绝不猜钱的方向） |
| `BAD_SHAPE` | **不出现**（上一版每条交易都报它） | 出现 ⇒ 意图补全没生效，把那一行发我 |
| `模型只是在引用` | 出现也无妨 | 模型在引用玩家自己打的标记 ⇒ 已按"NPC 未提议/未同意"处理（防凭空替 NPC 答应） |
| `成交预览·只读` | 后续明细行（金额／双方余额 →／校验／幂等键） | 同一份文案也会出现在游戏里（V 段） |
| `[Economy-UI] 已弹出` | 出现 ⇒ 游戏里应看到官方确认框 | 点「确认」→ 看下一条 |
| `[Economy-UI]` + `非主线程`／`已有别的模态`／`NModalContainer` | 出现 ⇒ 这次没弹窗 | **不是失败**：改用聊天指令，并把这一行发我 |
| `【双人确认】…✅ 双方已确认，待结算` | 出现 | ★本步终局状态（**仍未动钱**） |
| 想关掉弹窗 | 环境变量 `NSE_CONFIRM_DIALOG=0` | 只用聊天指令确认 |

**钱不动的独立核对**：`worlds\<WorldId>.json` 里 NPC 的 `balance` 与 `ledger` 条数在确认前后**一字不变**
（`receipts` 会多两条——那是幂等记录，不是钱）；同一指令再来一次，`receipts` 条数不变（幂等）。


> **附注**：磁盘上那份旧的 `e35d0119.json`（第 0 天 + 1632 人）是**合法状态，不必删**；
> 下次进生涯会逐日补齐到当前游戏日再落盘。想从头体验就删 `worlds\e35d0119.json`（原版存档无损）。


**本轮同时修掉的两个噪音问题**：

- 我们的诊断行**不再走 `GD.PushError`**：`godot.log` 里不再有 `ERROR:` 前缀和一大段调用栈；
- `national-spire.log` 的下沉通道修好了（**以前从来没生效过**——2.1MB 的历史文件里 `economy.*` 也是 0），
  `economy.*` 明细应开始出现，作为第二条可见通道。

### 4.5 动态日志（方案 A 已接管）

> ✅ **本轮已恢复生效，但前缀换了**：注入补丁现在是 `[Economy-Inject-A]`（方案 A，
> 挂在 `RequestPrivateAsync` 前缀）。旧的 `[Economy-Inject]` / `首次实测注入` 属于已退场的
> Compose 注入补丁，**看不到它们是正常的**；方案 A 的"首次说话"写在
> `[Economy-Inject-A] 身份来源首次实测：…` 与 `[Economy-Inject-A] 注入成功/注入未发生｜…` 两行里。

阶段 3-A 你反馈过"发了私信但搜不到 `首次实测注入`"。根因是**打点缺陷**：补丁有 6 条静默提前返回，
只有走到足够远才会说话，于是分不清"补丁没被调用"和"被调用了但条件不满足"。现在：

- 补丁**第一次被调用时无论走到哪一步都会写一行**，并带上原因，例如：
  ```
  [Economy-Inject] 首次实测注入：未注入｜原因 人物 xxx 不在影子账本里（真人？或从未播种）｜账本 0 人
  [Economy-Inject] 首次实测注入：注入成功｜位置 anchor｜长度 131｜指纹 06fe15d0｜零数字 是｜message 4 条（条数不变）
  ```
  这行**每个进程只打一次**（否则每发一条私信都刷日志）。
- 另外，只要补丁**还一次都没被调用过**，玩家推进日期时 `[Economy-Tick]` 行会多一句提醒，
  命中过一次就自动消失：
  ```
  [Economy-Tick] 第 0 天 → 第 12 天（第 1 赛季）：推进 12 天｜游戏日期｜私信组装补丁 0 次（已发过私信的话，说明挂载点没被调用）
  ```

| 你看到的 | 结论 |
| --- | --- |
| `首次实测注入：注入成功｜位置 anchor…` + `[Economy-Inject-V] 首次实测注入：注入成功｜位置 appended…` | ✅ 两段都真的注入了 |
| `首次实测注入：未注入｜原因 …` | ⚠️ 按原因处理；把那行原文发我 |
| 推进日期后 tick 行仍带 `私信组装补丁 0 次`，而你确实发过私信 | ❌ 挂载点没被调用 → 把该行发我 |
| 完全没有 `首次实测注入`，也没有那句提醒 | ❌ 补丁没挂上 → 看 §4.2 的 `[Economy-Inject]` 结论 |

### 4.6 第 1 赛季发薪时机（阶段 3-A 的规则，复验一次即可）

推进日期，观察 `[Economy-Tick]`：

| 你看到的 | 含义 |
| --- | --- |
| 第 1 天到第 83 天的 tick 行里**没有**"发薪"字样 | ✅ 前 83 天不发薪 |
| 第 84 天那一行出现 `发薪 N 人共 …` | ✅ 第 1 赛季在赛季末结算 |
| 第 85 天那一行带 `跨赛季 第 1 → 第 2 赛季` 并再次发薪 | ✅ 第 2 赛季起回到赛季初发薪 |

### 4.7 确认没有弄坏原版（**同样重要**）

| 检查 | 期望 |
| --- | --- |
| 私信里让模型输出 `[Profile: 骄傲]` | 正常生效（性格被改写），且**不显示**在正文里 |
| 私信里让模型输出 `[Favour: 10]` | 正常生效，且不显示在正文里 |
| 正文里出现普通方括号，例如 `我觉得[很强]` | **照常显示** `[很强]` |
| 私信整体对话 | 正常回复、没有卡住 |
| 私信回复正文 | NPC **不该**复述"【经济状况】/【当前经济】"这些条目（提示词已要求不要复述） |

### 4.8 失败判定表

| 日志 | 含义 | 处理 |
| --- | --- | --- |
| `结果：成功｜…｜自证 通过` + `[Economy-Inject-V]` 两行 | ✅ 三项挂载、五项自证全过 | 继续 §4.3 / §4.5 |
| `S 段自证：未通过｜…零数字 失败` | ❌ 模板里混进了数字 | 把整行发我 |
| `V 段自证：未通过｜含余额 失败` | ❌ `volatileCurrent` 模板缺 `{balance}` | 把整行发我 |
| `V 段插入自证：未通过｜位置 no-current-message` | ❌ 锚点没命中（原版换了收尾文案） | 改 `volatileAnchor` 即可，不必重编译 |
| `S 段注入失败：…Compose 不存在或签名变了` | ❌ 原版改了方法签名 | 把整行发我 |
| `日期推进失败：…都不存在` | ❌ 原版把 `MoveToNextDay` 改名了 | 把整行发我 |
| `影子推进自证：未通过｜…` | ❌ 账本/落盘链路有问题 | 把整行发我 |
| `[Economy-Patch] 失败：找不到 NationalSpire 程序集` | ❌ 定位失败 | 把整行 + 候选路径发我 |
| `未启用（阶段 config）：…` | ❌ 配置有 Error，按设计拒绝启用 | 按日志点名改配置 |
| **一条我们的日志都没有** | ❌ 模组没被加载 | 见下 |

**一条日志都没有时**按顺序排查：

1. 搜 `Mod with ID`，看是否有模组因版本仲裁被静默禁用。
2. 清单 `NationalSpireEconomy.json` 必须是 `"has_dll": true`。
3. DLL 与清单必须同级；`config\` 也必须与 DLL 同级。
4. **原版 NationalSpire 必须先装好**（我们是它的适配模组）。
5. 看 `logs\godot.log` 里有没有 `Loading assembly DLL ...NationalSpireEconomy.dll`
   与 `Calling initializer method ... EconomyModEntry` 这两行。

---

## 5. 回滚

### 5.1 原版存档为什么安全

| 保证 | 依据 |
| --- | --- |
| 不写原版存档 | 影子存档写在我们自己的 `national_spire_economy\` 目录，原版 `national_spire_career.json` 只读不写 |
| 不覆盖原版文件 | 独立目录 + 唯一模组 id |
| 不改原版 DLL | 游戏程序集引用是 `<Private>false</Private>` |
| 不动原版源码 | 只有运行时 Harmony 补丁（按位注入参数，原版改参数名也不会静默失效） |

### 5.2 三种回滚

```powershell
$mods = 'D:\Steam\steamapps\common\Slay the Spire 2\mods'

# ① 临时停用（推荐）
Rename-Item "$mods\NationalSpireEconomy" 'NationalSpireEconomy.disabled'

# ② 恢复原版模组（如果做过 §3.1 的备份）
Remove-Item "$mods\NationalSpire" -Recurse -Force
Rename-Item "$mods\NationalSpire.backup" 'NationalSpire'

# ③ 彻底清除（含影子存档）
Remove-Item "$mods\NationalSpireEconomy" -Recurse -Force
Remove-Item "$env:APPDATA\SlayTheSpire2\national_spire_economy" -Recurse -Force
```

**崩溃或行为异常时先别删任何东西**：把 `godot.log` 与 `national-spire.log` 留给我。

---

## 6. 需要反馈给我的信息

**本轮（方案 A）只要这几样**：

1. **`%APPDATA%\SlayTheSpire2\national_spire_economy\mod-debug.log` 全文**（★ 第一现场；
   若该文件不存在，请把 `%TEMP%\mod-debug.log` 发我）。
2. **`[Economy-Inject-A]` 的全部行**——特别是：
   - `挂载点自证：已挂 N 份｜第一份 token=0x…`
   - `身份来源挂载：成功…` 与 `身份来源首次实测：…`
   - `注入成功｜… message N → N 条（条数不变）｜S 段 …｜V 段 …`
     （或者 `注入未发生｜原因 …`——**原因那半句就是答案**）
3. **`[Economy-Probe]` 清单段 + `[Economy-Tick]` 行 + `[Economy-Probe] 命中` 行**。
4. **`logs\` 与 `national_spire_diagnostics\` 两个目录**（原样打包即可）。

> ⚠️ **不要用这两项验收方案 A**：§4.3 的「提示词预览」（走 `Compose`，不经过发送，看不到注入）、
> §4.5 的旧 `首次实测注入`（属已退场的 Compose 注入补丁）。两者看不到是**设计如此**。

**方案 A 之外、下一轮才需要的**：

5. §4.2 的 `[Economy-Inject]` / `[Economy-Inject-V]` 自证行（模板健康度）。
6. §4.7 的复验结果：`[Profile:]` / `[Favour:]` / 普通方括号 是否都正常？私信回复有没有变味？

拿到第 1~4 项我就能判断方案 A 是否真的端到端成立。
