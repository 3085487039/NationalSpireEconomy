# NOTICE — 出处与署名

本文件说明 NationalSpireEconomy 的来源、所依赖的上游作品，以及再分发时必须保留的东西。

---

## 一、本作品

| 项 | 值 |
| --- | --- |
| 名称 | **NationalSpireEconomy（国运尖塔 · NPC 经济系统）** |
| 性质 | **二次开发（二开）作品** —— 以独立适配模组的形式为上游加入经济系统 |
| 作者 | `Dora` |
| 许可证 | PolyForm Noncommercial License 1.0.0（见仓库根目录 `LICENSE`） |
| 仓库 | `https://github.com/3085487039/NationalSpireEconomy` |

## 二、上游作品（本作品依赖它运行，但不修改它）

| 项 | 值 |
| --- | --- |
| 名称 | **NationalSpire** —— 国运尖塔：当全世界的尖塔水平下降 1000 倍，只有你保持不变【生涯与社区】 |
| 作者 | **Catfish872** |
| 许可证 | **PolyForm Noncommercial License 1.0.0** |
| 上游许可证全文 | `licenses/LICENSE-NationalSpire.txt`（逐字副本，4563 字节） |
| 许可证原文地址 | <https://polyformproject.org/licenses/noncommercial/1.0.0> |
| 验证过的上游版本 | 创意工坊 **0.26.11**；本地 `mods\NationalSpire` **0.25.39** |

> "NationalSpire" 与 "国运尖塔" 的名称与著作权归 **Catfish872** 所有。

## 三、本作品与上游的技术关系（决定"它算不算二开"）

**算。** 理由是：

1. **编译期**：本项目引用 `NationalSpire.dll` 作为程序集引用
   （`NationalSpireEconomy.csproj` 的 `NseResolveNationalSpire` 目标解析其路径，
   `<Private>false</Private>` 表示**不随本模组分发**该 DLL）。共 18 个源文件引用其命名空间。
2. **运行期**：本项目通过 Harmony **给上游与游戏本体的方法打补丁**
   （`AiService.RequestPrivateAsync` 前缀、`AiService.ProcessPrivateAsync`、
   `CareerEngine.MoveToNextDay` 后缀、`PrivateStreamParser.Feed` 前缀），
   并把注入内容送进上游组装好的私信 messages 数组。
3. **但是**：本项目**不修改** NationalSpire 的源码或 DLL 文件
   （因为上游只有 DLL，本来也改不了），也不写原版存档（只读）。

所以它**不是**独立原创模组，而是**在上游之上的二开适配模组**。按项目约定，
对外文件名与标题一律标明"二开"，避免误下载与错误反馈。

## 四、再分发要求（两条，缺一不可）

### 1. 上游许可证要求的（PolyForm Notices 条）

> 你必须确保**拿到软件任何一部分副本的人，同时也拿到这份条款（或它的 URL）**。

⇒ 因此**任何**发布包/源码包的根目录都**必须**带有：
- `LICENSE`（含 PolyForm 全文），或
- `licenses/LICENSE-NationalSpire.txt`（上游逐字副本），或
- 至少给出上面的许可证 URL。

上游 README 也明确写了同一件事：
> 转载或再分发时请一并保留 `LICENSE-NationalSpire.txt` 与上游出处。

### 2. 上游没有提供 `Required Notice:` 行

PolyForm 的 Notices 条还要求传递 licensor 提供的、以 `Required Notice:` 开头的纯文本行。
**经核对，上游发布的文件里不存在这样的行**（`LICENSE-NationalSpire.txt` 第 21 行的那一条
只是许可证模板自带的示例 `Required Notice: Copyright Yoyodyne, Inc. (http://example.com)`，
不是上游的署名行）。因此本作品需要传递的只有**许可证本身 + 上述出处声明**。

## 五、商业使用

**禁止。** 上游是 PolyForm **Noncommercial**，本作品沿用同一许可证。
个人游玩、研究、实验、面向公众知识的测试、业余爱好项目，都属允许用途；
**任何商业用途都不被允许**。

## 六、本地开发环境的一个特例（与授权无关，但接手者会撞到）

本项目的开发工作区里有两个**构建产物**目录，它们**都不进 Git**：

| 目录 | 内容 | `.gitignore` |
| --- | --- | --- |
| `deploy/NationalSpireEconomy/` | 真机部署包（`build-deploy.ps1` ← 第 [3/4] 步产出） | 被 `deploy/*` 排除 |
| `release/NationalSpireEconomy-二开-v0.1.0/`（+ `.zip`） | 面向玩家的发布包（同脚本 ← 第 [5/5] 步产出） | 被 `release/` 排除 |

两者中的 `NationalSpireEconomy.dll` 都是**本项目自己的产物**，不是上游的 DLL。
`deploy/` 与 `release/` 目录本身**保留在磁盘上**（打包脚本要用它们），
但 `.gitignore` 里的 `deploy/*`、`release/` 规则会把里面的二进制挡在仓库之外 ——
唯一的例外是 `!deploy/DEPLOY.md`（那份部署文档是需要入库的）。

> 这两个目录**都由 `build-deploy.ps1` 重新生成**，不要手工往里拷 DLL：
> 手工维护正是 `release/` 曾经落后一版（DLL 与 `ai_prompt_economy.json` 都停在旧版）
> 的原因。跑一次脚本，`bin` / `deploy` / `release` 三处的 DLL 哈希必然一致。

**本仓库在任何情况下都不得包含 `NationalSpire.dll`。** 上游的 DLL 属于上游作品，
不是我们有权再分发的东西；本项目只在编译期引用它。
