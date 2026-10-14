# 上游签名核对工具（阶段 5 §3.1 的护栏）

## 它是做什么的

上游（`NationalSpire`）更新后，**几秒内**告诉你：我们所有的 Harmony 落点是否还成立。

我们的补丁是按**方法名 + 参数类型**挂的。上游哪天改了某个方法的名字或参数，
Harmony 会**安静地挂不上** —— 没有异常、没有日志，模组看起来"加载成功"但什么都不做。
本项目历史上正是栽在这个盲区（见 `docs/HANDOFF-PHASE-3-PROBE.md` §3.1）。

本工具用**纯反射**读上游二进制的元数据，**不执行任何上游代码、不写任何文件**。

## 怎么跑

```powershell
$h = 'D:\蓝色大肥鱼\sandbox\dotnethome'
$env:APPDATA = "$h\Roaming"; $env:USERPROFILE = $h
$env:DOTNET_CLI_HOME = $h; $env:NUGET_PACKAGES = "$h\packages"

dotnet run --project tools\sig-audit\nsesig.csproj -c Release -- `
  'D:\Steam\steamapps\workshop\content\2868840\3809501471\NationalSpire.dll'
```

> 路径就是游戏**实际加载**的那一份（Creative Workshop 订阅目录）。
> 若上游换了 workshop id，用 `docs/HANDOFF-PHASE-3-PROBE.md` §2.9 的方法重新定位。

## 怎么判读

**正常**：每个挂载点都打印 `[找到] ... token=0x...`。

**报警（必须改代码）**：任何一行出现

- `[缺失类型] <类型名>` —— 整个类没了/改名了
- `[缺失方法] <类型名>.<方法名>` —— 方法改名了

以及底部 `---- 相关类型是否存在 ----` 里出现任何 `[无]`。

**⚠ token 变化本身不是问题**：Harmony 按签名匹配，token 只用于诊断打印。
2026-10-14 实测：0.26.16 下四个 token 全部前移（+546/+547/+8），但**签名逐字未变** ——
那是"上游新增了成员"的形态，**不是**签名变动。详见
`docs/HANDOFF-PHASE-5-REVIEW-AND-PLAYER-TRADE.md` §3.1。

## 覆盖的 10 个落点

| 类 | 方法 | 用途 |
| --- | --- | --- |
| `AiService` | `RequestPrivateAsync` | 方案 A 主挂载点（S/V 段注入） |
| `AiService` | `ProcessPrivateAsync` | 身份跟踪（补在状态机 `MoveNext` 上） |
| `CareerEngine` | `MoveToNextDay` | 日期推进（首选） |
| `CareerEngine` | `AdvanceToDay` | 日期推进（后备） |
| `PrivateStreamParser` | `Feed` / `Finish` | `[Trade:]` 流式抽取 |
| `PrivateMessagePrompts` | `Compose` / `Context` | 旧补丁（已退场）/ 上下文诊断 |
| `LeadingReasoningStream` | `Feed` / `Finish` | 并行可见流剥离 |

> 改代码时若新增/删除了挂载点，**请同步更新 `Audit.cs` 里的 `wanted` 数组**，
> 否则这个护栏会漏掉新落点。
