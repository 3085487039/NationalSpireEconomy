# 把工作区变成 GitHub 仓库，并做第一次提交。
#
# 用法（在仓库根目录）：
#   .\init-repo.ps1 -Author "你的GitHub名" -RepoUrl "https://github.com/你的名/NationalSpireEconomy"
#
# 为什么要脚本而不是手敲：这个仓库有几个"必须做对"的点，
# 手敲很容易漏（漏了之后要么把构建产物提交上去，要么发布包不合规）。

param(
    [Parameter(Mandatory = $false)][string]$Author = "",
    [string]$RepoUrl = "",
    [string]$Branch = "main",
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($root)) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }

# ── -CheckOnly：只跑自检，不碰 git、不改任何文件 ───────────────────────────
# 用途：在真正初始化之前先看一眼"有没有不该入库的东西"。
if ($CheckOnly) {
    Write-Host "自检模式：只扫描，不初始化、不提交、不改文件。" -ForegroundColor Cyan
    Write-Host "（-Author 在自检模式下不需要）" -ForegroundColor DarkGray
    $bad = @()
    $bad += Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @('NationalSpire.dll', 'NationalSpire.json') }
    $bad += Get-ChildItem $root -Recurse -File -Filter '*.log' -ErrorAction SilentlyContinue
    $bad += Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\probe\\' -and $_.Extension -ne '.cs' }
    if ($bad) {
        Write-Host "⚠ 发现以下文件【不应入库】：" -ForegroundColor Yellow
        $bad | ForEach-Object { Write-Host "    $($_.FullName.Replace($root, '.'))" }
        exit 1
    }
    $n = (Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue |
          Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' }).Count
    Write-Host "✅ 无上游 DLL、无 .log、无 probe 产物（工作区非 bin/obj 文件 $n 个）" -ForegroundColor Green
    exit 0
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw "找不到 git，请先装 Git。" }
if ([string]::IsNullOrWhiteSpace($Author)) { throw "必须给 -Author（用作版权署名，会回填 LICENSE 与 NOTICE.md）。只想自检请加 -CheckOnly。" }
if (Test-Path (Join-Path $root '.git')) { throw "这里已经是 git 仓库了（.git 已存在），本脚本只用于首次初始化。" }

# ── 0. 回填署名占位符 ──────────────────────────────────────────────────────
Write-Host "[0/5] 回填署名：$Author" -ForegroundColor Cyan
$targets = @('LICENSE', 'NOTICE.md')
# release/ 下的发布包副本也一起回填（它已经打包好了，属于最终交付物）
$releaseDir = Join-Path $root 'release'
if (Test-Path $releaseDir) {
    Get-ChildItem $releaseDir -Recurse -File -Include 'LICENSE', 'NOTICE.md' -ErrorAction SilentlyContinue |
        ForEach-Object { $targets += $_.FullName }
}
foreach ($t in $targets) {
    $p = $t
    if (-not [System.IO.Path]::IsPathRooted($t)) { $p = Join-Path $root $t }
    if (-not (Test-Path $p)) { continue }
    $c = [System.IO.File]::ReadAllText($p)
    $c = $c.Replace('<你的 GitHub 名>', $Author)
    if ($RepoUrl) { $c = $c.Replace('<你的仓库地址>', $RepoUrl) }
    [System.IO.File]::WriteAllText($p, $c)
    Write-Host "      $($p.Replace($root, '.'))"
}
if ($RepoUrl) {
    $rp = Join-Path $root 'README.md'
    $c = [System.IO.File]::ReadAllText($rp).Replace('<你的仓库地址>', $RepoUrl)
    [System.IO.File]::WriteAllText($rp, $c)
}

# ── 1. 自检：绝不把不该进仓库的东西提交上去 ────────────────────────────────
Write-Host "[1/5] 自检：扫描禁止入库的文件" -ForegroundColor Cyan
$forbidden = @()
$forbidden += Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'NationalSpire.dll' -or $_.Name -eq 'NationalSpire.json' }
$forbidden += Get-ChildItem $root -Recurse -File -Filter '*.log' -ErrorAction SilentlyContinue
$forbidden += Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\probe\\' -and $_.Extension -ne '.cs' }
if ($forbidden) {
    Write-Host "  ⚠ 发现以下文件【不应入库】，请先处理：" -ForegroundColor Yellow
    $forbidden | ForEach-Object { Write-Host "      $($_.FullName.Replace($root,'.'))" }
    throw "自检未通过：先删掉或加进 .gitignore（上游 DLL 与日志绝不能提交）。"
}
Write-Host "  ✅ 无上游 DLL、无 .log" -ForegroundColor Green

# ── 2. 初始化 ──────────────────────────────────────────────────────────────
Write-Host "[2/5] git init（分支 $Branch）" -ForegroundColor Cyan
& git -C $root init -b $Branch
& git -C $root add -A

# ── 3. 让用户确认一遍将要提交的文件 ────────────────────────────────────────
Write-Host "[3/5] 将要提交的文件：" -ForegroundColor Cyan
& git -C $root status --short
$count = (& git -C $root diff --cached --name-only | Measure-Object).Count
Write-Host "  共 $count 个文件" -ForegroundColor Cyan
Write-Host "  ⚠ 请确认上面【没有】bin/ 、obj/ 、*.log 、NationalSpire.dll" -ForegroundColor Yellow
$ok = Read-Host "  确认无误请输入 yes"
if ($ok -ne 'yes') { throw "已取消。可以先调整 .gitignore 再重跑。" }

# ── 4. 首次提交 ────────────────────────────────────────────────────────────
Write-Host "[4/5] 首次提交" -ForegroundColor Cyan
& git -C $root commit -m @"
chore: 首次提交 —— NationalSpireEconomy v0.1.0（NationalSpire 二开）

为《Slay the Spire 2》的国运尖塔（NationalSpire）加入 NPC 经济系统。

- 玩家 ↔ NPC 双向结算闭环，全部经真机验收
- 只读成交预览 + 双人确认（确认绝不动钱）
- 真实动钱 + 审计落盘 audit/<WorldId>.jsonl，幂等可重放
- 账本真相通道：回执/未成立/审计回读，模型不能谎称"钱转了"
- 基线：零依赖 391/391、含游戏引用 523/523、三种构建 0 警告

上游：NationalSpire，作者 Catfish872，PolyForm Noncommercial 1.0.0
（许可证逐字副本见 licenses/LICENSE-NationalSpire.txt，禁止商业使用）
"@

# ── 5. 远端 ────────────────────────────────────────────────────────────────
if ($RepoUrl) {
    Write-Host "[5/5] 添加远端并推送" -ForegroundColor Cyan
    & git -C $root remote add origin ($RepoUrl + ".git")
    & git -C $root push -u origin $Branch
} else {
    Write-Host "[5/5] 未提供 -RepoUrl，跳过远端设置。" -ForegroundColor Yellow
    Write-Host "      稍后自行执行：" -ForegroundColor Yellow
    Write-Host "        git remote add origin <仓库地址>.git" -ForegroundColor Yellow
    Write-Host "        git push -u origin $Branch" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "完成。后续发布 checklist：" -ForegroundColor Green
Write-Host "  1) 把 release\NationalSpireEconomy-二开-v0.1.0.zip 传到 GitHub Releases"
Write-Host "  2) Release 说明里写明：二开、依赖上游 NationalSpire、禁止商业使用"
Write-Host "  3) 打 tag：git tag v0.1.0 -m 'v0.1.0' ; git push --tags"
