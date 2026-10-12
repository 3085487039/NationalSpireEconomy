# One-shot release build + deployment package.
#
# NOTE: messages and paths are deliberately ASCII-only / variable-derived.
# Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI, so any non-ASCII
# literal in this file (including a hard-coded Chinese project path) would be
# mangled into garbage and the build would fail with MSB1009 "project not found".
# Hence: derive every path from $PSScriptRoot, never inline it.
#
# Requires the sandbox environment variables to be redirected first (see DEPLOY.md):
#   APPDATA / USERPROFILE / DOTNET_CLI_HOME / NUGET_PACKAGES
#
# Usage:  & .\build-deploy.ps1

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($root)) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }

$proj = Join-Path $root 'NationalSpireEconomy.csproj'
$out  = Join-Path $root 'bin\Release\net9.0'
$stg  = Join-Path $root 'deploy\NationalSpireEconomy'

Write-Host "[1/4] Cleaning previous output" -ForegroundColor Cyan
Get-ChildItem $root -Recurse -Directory -Include bin, obj -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
# Clean the package folder only. Do NOT remove deploy\ itself: DEPLOY.md sits there
# and wiping the whole folder would take the doc with it.
Remove-Item $stg -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "[2/4] Release build (GameRefsEnabled=true)" -ForegroundColor Cyan
# src/Game needs the game assemblies and NationalSpire itself, so GameRefsEnabled=true
# is mandatory here. The csproj target NseResolveNationalSpire picks NationalSpire's
# path: Steam Workshop first (that is the copy the game actually loads), then local mods.
& dotnet build $proj -c Release -p:GameRefsEnabled=true -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit=$LASTEXITCODE)" }

Write-Host "[3/4] Assembling deployment package" -ForegroundColor Cyan
New-Item -ItemType Directory -Path (Join-Path $stg 'config')  -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stg 'Catalog') -Force | Out-Null
Copy-Item (Join-Path $out 'NationalSpireEconomy.dll') $stg -Force
Copy-Item (Join-Path $root 'NationalSpireEconomy.json') $stg -Force
Copy-Item (Join-Path $out 'config\*.json') (Join-Path $stg 'config') -Force
Copy-Item (Join-Path $out 'Catalog\items.json') (Join-Path $stg 'Catalog') -Force

# ---------------------------------------------------------------------------
# 授权与出处文件：**必须随包分发**，不是可选项。
#
# 上游 NationalSpire 用的是 PolyForm Noncommercial 1.0.0，它的 Notices 条要求
# "拿到软件任何一部分副本的人，同时也拿到这份条款（或它的 URL）"；
# 上游 README 也明确写了"转载或再分发时请一并保留 LICENSE-NationalSpire.txt 与上游出处"。
#
# 上面第 [1/4] 步的 Remove-Item $stg 会把上次拷进去的这几个文件一起删掉，
# 所以这里必须重新拷 —— 少了这一步，重新打包出来的发布包就【不合规】了。
# ---------------------------------------------------------------------------
New-Item -ItemType Directory -Path (Join-Path $stg 'licenses') -Force | Out-Null
Copy-Item (Join-Path $root 'LICENSE') $stg -Force
Copy-Item (Join-Path $root 'NOTICE.md') $stg -Force
Copy-Item (Join-Path $root 'licenses\LICENSE-NationalSpire.txt') (Join-Path $stg 'licenses') -Force

Write-Host "[4/4] Writing SHA256 manifest" -ForegroundColor Cyan
$lines = Get-ChildItem $stg -Recurse -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object FullName |
    ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        $rel  = $_.FullName.Substring($stg.Length + 1)
        "{0}  {1}" -f $hash, ($rel -replace '\\', '/')
    }
$lines | Set-Content (Join-Path $stg 'SHA256SUMS.txt') -Encoding UTF8

# Guard: the package must never carry game assemblies.
$leaked = Get-ChildItem $stg -Recurse -File -Filter *.dll |
    Where-Object { $_.Name -match '^(sts2|0Harmony|GodotSharp|Steamworks|NationalSpire)\.dll$' }
if ($leaked) { throw "Package leaked game assemblies: $($leaked.Name -join ', ')" }

Write-Host ""
Write-Host "Package ready: $stg" -ForegroundColor Green
Get-ChildItem $stg -Recurse -File |
    Select-Object @{n='File';e={$_.FullName.Replace($stg,'')}}, @{n='Bytes';e={$_.Length}} |
    Format-Table -AutoSize
