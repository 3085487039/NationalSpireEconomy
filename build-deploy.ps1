# One-shot release build + deployment package.
#
# NOTE: messages and paths are deliberately ASCII-only / variable-derived.
# Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI, so any non-ASCII
# literal in this file (including a hard-coded Chinese project path) would be
# mangled into garbage and the build would fail with MSB1009 "project not found".
# Hence: derive every path from $PSScriptRoot, never inline it.
# For the same reason the release folder name ("-erKai-") is assembled from a
# Unicode escape instead of being written literally.
#
# Requires the sandbox environment variables to be redirected first (see DEPLOY.md):
#   APPDATA / USERPROFILE / DOTNET_CLI_HOME / NUGET_PACKAGES
#
# Usage:  & .\build-deploy.ps1
#
# Outputs:
#   deploy\NationalSpireEconomy\                    development deployment pack
#   release\NationalSpireEconomy-<erKai>-v0.1.0\     player-facing pack (same content + README)
#   release\NationalSpireEconomy-<erKai>-v0.1.0.zip  the zip players download
#   SHA256SUMS-release.txt                          byte count + SHA256 of that zip

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($root)) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }

$proj = Join-Path $root 'NationalSpireEconomy.csproj'
$out  = Join-Path $root 'bin\Release\net9.0'
$stg  = Join-Path $root 'deploy\NationalSpireEconomy'

# "erKai" (the Chinese marker for secondary development) via Unicode escapes:
# keeps this whole file ASCII-only (see NOTE above).
$erKai   = [char]0x4E8C + [char]0x5F00
$relName = "NationalSpireEconomy-$erKai-v0.1.0"
$relDir  = Join-Path $root "release\$relName"
$relZip  = Join-Path $root "release\$relName.zip"


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

Write-Host "[4/5] Writing SHA256 manifest" -ForegroundColor Cyan
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

# ---------------------------------------------------------------------------
# [5/5] Player-facing release pack + zip + SHA256SUMS-release.txt
#
# Why this step exists: release\ used to be assembled BY HAND, and it silently
# went stale -- the shipped DLL was one build behind deploy\ and the bundled
# ai_prompt_economy.json was a day old, so players who installed from the zip
# did not get what the repo contained. Meanwhile SHA256SUMS-release.txt still
# carried a literal unexpanded "$zipHash", so the published checksum was
# unusable. Regenerating both here makes the zip reproducible from one command.
#
# The release tree is exactly the deploy tree plus README-<install>; both are
# rebuilt from scratch so a stale file can never survive.
# ---------------------------------------------------------------------------
Write-Host "[5/5] Assembling release pack + zip" -ForegroundColor Cyan

Remove-Item $relDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $relZip -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $relDir -Force | Out-Null
Copy-Item (Join-Path $stg '*') $relDir -Recurse -Force

# The player-facing install/licence README is release-only (deploy\ is the
# developer slot). Kept ASCII-named on disk so `git` and PowerShell agree.
$relReadme = Join-Path $root 'release-readme.md'
if (Test-Path $relReadme) {
    $readmeName = 'README-' + [char]0x5B89 + [char]0x88C5 + [char]0x4E0E + [char]0x6388 + [char]0x6743 + '.md'
    Copy-Item $relReadme (Join-Path $relDir $readmeName) -Force
    Write-Host "    note: $readmeName taken from release-readme.md" -ForegroundColor DarkGray
} else {
    Write-Warning "release-readme.md not found; release pack will ship without the install/licence README."
}

# Refresh the in-pack manifest so it covers every file actually shipped (the
# README included), then hash the zip and write it into SHA256SUMS-release.txt.
$relLines = Get-ChildItem $relDir -Recurse -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object FullName |
    ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        $rel  = $_.FullName.Substring($relDir.Length + 1)
        "{0}  {1}" -f $hash, ($rel -replace '\\', '/')
    }
$relLines | Set-Content (Join-Path $relDir 'SHA256SUMS.txt') -Encoding UTF8

# Deterministic zip: Compress-Archive stores each entry's file mtime, so two runs
# with identical bytes produced different zips (verified: 210330 vs 210673 bytes).
# A "regenerate and compare the hash" workflow is worthless if the hash is not
# reproducible, so every entry is written with a fixed timestamp via ZipArchive.
# Entry names use '/' separators (zip spec) and include the folder name so the zip
# extracts to release\<name>\ rather than scattering files.
$zipStamp = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$relDirFull = (Resolve-Path $relDir).Path
$zipStream  = [System.IO.File]::Open($relZip, [System.IO.FileMode]::Create)
try {
    $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $files = Get-ChildItem $relDirFull -Recurse -File | Sort-Object FullName
        foreach ($f in $files) {
            $entryName = $relName + '/' + ($f.FullName.Substring($relDirFull.Length + 1) -replace '\\', '/')
            $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $zipStamp
            $entryStream = $entry.Open()
            try {
                $src = [System.IO.File]::OpenRead($f.FullName)
                try { $src.CopyTo($entryStream) } finally { $src.Dispose() }
            } finally { $entryStream.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $zipStream.Dispose() }

$zipHash  = (Get-FileHash $relZip -Algorithm SHA256).Hash
$zipBytes = (Get-Item $relZip).Length
$sumsPath = Join-Path $root 'SHA256SUMS-release.txt'

# ASCII-only body with explicit escapes, so the file is written identically no
# matter how PowerShell 5.1 guesses the encoding of this script.
$L = @()
$L += '# ' + [char]0x53D1 + [char]0x5E03 + [char]0x5305 + [char]0x54C8 + [char]0x5E0C + ' (outside the pack, so no circular reference)'
$L += ''
$L += '| ' + [char]0x53D1 + [char]0x5E03 + [char]0x5305 + ' | ' + [char]0x5B57 + [char]0x8282 + ' | SHA256 |'
$L += '| --- | --- | --- |'
$L += "| release/$relName.zip | $zipBytes | $zipHash |"
$L += ''
$L += 'Per-file hashes inside the pack: see its bundled SHA256SUMS.txt.'
$L += ''
$L += 'Regenerate (do not hand-edit):  & .\build-deploy.ps1'
$L += ''
$L += '> The zip hash depends on the archiving tool/version. Prefer the bundled'
$L += '> SHA256SUMS.txt when verifying content integrity.'
$L | Set-Content $sumsPath -Encoding UTF8

$relLeaked = Get-ChildItem $relDir -Recurse -File -Filter *.dll |
    Where-Object { $_.Name -match '^(sts2|0Harmony|GodotSharp|Steamworks|NationalSpire)\.dll$' }
if ($relLeaked) { throw "Release pack leaked game assemblies: $($relLeaked.Name -join ', ')" }

Write-Host ""
Write-Host "Deployment package ready: $stg" -ForegroundColor Green
Get-ChildItem $stg -Recurse -File |
    Select-Object @{n='File';e={$_.FullName.Replace($stg,'')}}, @{n='Bytes';e={$_.Length}} |
    Format-Table -AutoSize

Write-Host "Release pack ready: $relDir" -ForegroundColor Green
Write-Host ("Release zip:        {0}  ({1} bytes)" -f $relZip, $zipBytes) -ForegroundColor Green
Write-Host ("  SHA256:           {0}" -f $zipHash) -ForegroundColor Green
Write-Host ("Manifest updated:   {0}" -f $sumsPath) -ForegroundColor Green
