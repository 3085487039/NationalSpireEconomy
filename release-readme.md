# NationalSpireEconomy（二开）v0.1.0

> **⚠️ 二开版本** —— 本包是 **NationalSpire** 的二次开发适配模组，**不是**上游本体。
> 文件名带"二开"是为了避免误下载与错误反馈渠道不清。

**国运尖塔 · NPC 经济系统**：给国运尖塔的 NPC 加上真正的钱 —— 余额、工资、赞助、打赏、雇佣，
以及一本可审计的账。**只有双方都确认**才会真的动钱。

---

## 一、安装

### 1. 先确认已装上游（必需）

本模组**依赖 NationalSpire**，它自己不能单独运行。

- 上游作品：**NationalSpire**（国运尖塔：当全世界的尖塔水平下降 1000 倍，只有你保持不变【生涯与社区】）
- 上游作者：**Catfish872**
- 上游创意工坊 ID：`3809501471`

### 2. 把本包放进 mods 目录

把**整个文件夹**（`NationalSpireEconomy-二开-v0.1.0`）拷进：

```
<Steam 库>\steamapps\common\Slay the Spire 2\mods\
```

放好之后**目录里直接就是**这些文件（不用再套一层）：

```
NationalSpireEconomy-二开-v0.1.0\
├── NationalSpireEconomy.json      ← 模组清单（必须在这一层）
├── NationalSpireEconomy.dll
├── config\economy_config.json     ← 主配置，可直接改
├── config\personality_economy.json
├── config\ai_prompt_economy.json
├── Catalog\items.json
├── LICENSE / NOTICE.md / licenses\   ← 授权与出处（请勿删除）
└── README-安装与授权.md              ← 本文件
```

> 如果你更喜欢叫 `NationalSpireEconomy` 这个名字（少一层"二开"字样），
> **可以自己改名**，游戏按清单里的 `id` 识别模组，不依赖文件夹名。

### 3. 启动游戏，先看启动日志

第一次启动请留意日志里的**结算开关状态**：

| 启动行 | 含义 |
| --- | --- |
| `结算未接线（默认）：…一分钱都不会动。` | **默认值**。交易能走完"双方已确认"，但不会真的转账 |
| `★结算已接线（配置 trade.settlementWired）：…会【真的动钱】…` | 已经把开关打开了 |

**默认是关的**，也就是说装好之后不会突然少钱。想让交易真的动钱：

1. 打开 `config\economy_config.json`，把 `"settlementWired": false` 改成 `true`；
2. **重启游戏**（改配置需要重启生效）；
3. 改配置请用能正确处理 JSON 的工具（例如 Python 的 `json` 模块）。
   不要用 `Set-Content -Encoding utf8` 这类会写入 BOM、或把 `0.40` 改成 `0.4` 的方式。

---

## 二、日志与数据在哪

| 文件 | 用途 |
| --- | --- |
| `%APPDATA%\SlayTheSpire2\national_spire_economy\mod-debug.log` | **出问题的第一现场** |
| `…\worlds\<WorldId>.json` | 影子存档；"这笔交易成没成"看 **`LastTrade` 键** |
| `…\audit\<WorldId>.jsonl` | 每一笔**真实结算**一行，含方向与双方余额前后 |

反馈问题时请附上 `mod-debug.log` 的**相关片段**（不用整份，很大）。

---

## 三、当前版本的能力与边界

**能做的**：NPC 出钱给你（赞助/打赏/奖金）、**你出钱给 NPC**（雇佣/转账）、
**你自己在私信里提出交易**（NPC 同意 + 你确认之后才成立）、
只读成交预览、双人确认、真实结算 + 审计落盘、重启后仍能看到回执。

**暂时不能做的**（不是 bug，是还没做的部分）：

- **玩家侧的即时结果弹窗**：如果私信窗口正开着，结果框会让位，
  此时结果只写进私信的经济段与日志。

### 你自己发起交易时会发生什么

在私信里打一行交易标记（例如 `[Trade: 雇佣, Amount: 3000, Direction: playerToNpc]`）之后：

1. **你提出** —— 模组落一张待确认单，钱一分不动；
2. **NPC 表态** —— 它同意就会回一行 `[Deal: accept]`；不同意就不写，并照它的性格说明理由；
3. **你确认** —— 回一句 `确认 <单号>`（或点弹窗），这时才真的转账。

也就是说 **NPC 可以拒绝你**，而且它点头之后**还要你自己再确认一次**，钱才会走。

> 顺带一提：这些括号标记（`[Trade: …]`、`[Deal: accept]`）**不会显示在聊天里**，
> 模组会把它们剥掉 —— 你看到的只有对话本身与你自己的经济段。

**一个要知道的设计选择**：你花掉的钱来自本模组管理的"影子余额"
（安装时从原版数字**一次性只读导入**）。此后**游戏自带界面上的钱数不会再变化**，
这是刻意的，不是 bug。

---

## 四、许可证与出处（**请勿删除这些文件**）

本包**必须**随附以下内容，这是上游许可证的硬性要求：

| 文件 | 内容 |
| --- | --- |
| `LICENSE` | 本项目采用的 **PolyForm Noncommercial License 1.0.0** 全文 + 出处声明 |
| `licenses\LICENSE-NationalSpire.txt` | **上游** NationalSpire 的许可证**逐字副本**（4563 字节） |
| `NOTICE.md` | 上游出处、技术关系、再分发要求 |

- **许可**：PolyForm Noncommercial License 1.0.0 —— 允许非商业用途的分享与修改，
  **禁止商业使用**。
- **上游**：NationalSpire，作者 **Catfish872**，"NationalSpire"与"国运尖塔"的名称与著作权归其所有。
- **技术关系**：本模组**不修改**上游的源码或 DLL，而是运行时通过补丁挂接在它的流程上；
  它在编译期引用上游 DLL，但**不随包分发**那个 DLL。
- **再分发要求**（上游许可证 Notices 条 + 上游 README 明确要求）：
  任何转载或再分发都**必须**同时保留上述许可证文件与上游出处。
- **本包在任何情况下都不包含 `NationalSpire.dll`。**

## 五、再做一个二开怎么办

如果你基于本模组再改一版对外发布：

1. **文件名必须标明"二开"**（并写上你自己的名字/版本号，例如
   `NationalSpireEconomy-二开-你的名字-v0.2.0`）；
2. 随包带上 `LICENSE` + `licenses\LICENSE-NationalSpire.txt` + `NOTICE.md`（或至少许可证 URL）；
3. 写清你自己的来源与反馈渠道，避免玩家把问题报给上游作者；
4. **仍然禁止商业使用。**

## 六、校验完整性

`SHA256SUMS.txt` 是本包内**每个文件**的 SHA256 清单（含本 README），由打包脚本
`build-deploy.ps1` 自动生成。核对方式：

```powershell
Get-Content .\SHA256SUMS.txt | ForEach-Object {
  $p = $_ -split '\s+', 2
  $f = Join-Path $PWD ($p[1].Trim() -replace '/', '\')
  "{0}  {1}" -f (Get-FileHash $f -Algorithm SHA256).Hash, $p[1]
}
```

> **不要在本文档里手写某个文件的哈希**：它会在下一次重新打包时立刻过期，
> 而且会与脚本生成的 `SHA256SUMS.txt` 互相矛盾。以包内 `SHA256SUMS.txt` 为唯一准据。

> **ZIP 文件本身**的哈希无法写在包里（重新打包会改变它，形成循环）。
> 它由 `build-deploy.ps1` 在打包后写入仓库根目录的 `SHA256SUMS-release.txt`，
> 发布页（GitHub Release）说明里也从那里取。
