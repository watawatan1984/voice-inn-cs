<#
.SYNOPSIS
    Voice In のインストーラーを一括ビルドする。

.DESCRIPTION
    以下を順に行う:
      1. dotnet publish (VoiceIn.csproj, Release, win-x64, 自己完結型) を
         dist\publish へ出力する。
      2. Inno Setup コンパイラ (ISCC.exe) で installer\VoiceIn.iss を
         コンパイルし、dist\ にインストーラー exe (VoiceIn-Setup-*.exe) を
         生成する。

    このスクリプトは「インストーラーを作る」ところまでしか行わない。
    生成したインストーラーを実行 (=このマシンへの実際のインストール) する
    処理は含まれていない。実行するかどうかは別途判断すること。

.PARAMETER SkipPublish
    dist\publish が既に最新の状態であるとき、dotnet publish を省略して
    Inno Setup のコンパイルだけをやり直したい場合に指定する。

.EXAMPLE
    pwsh -File .\installer\build.ps1
    publish からインストーラー生成まで一括で行う。

.EXAMPLE
    pwsh -File .\installer\build.ps1 -SkipPublish
    dist\publish はそのままに、.iss の変更だけを反映してコンパイルし直す。
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Csproj     = Join-Path $RepoRoot "VoiceIn.csproj"
$DistDir    = Join-Path $RepoRoot "dist"
$PublishDir = Join-Path $DistDir "publish"
$IssPath    = Join-Path $PSScriptRoot "VoiceIn.iss"

# ISCC.exe (Inno Setup コンパイラ) の場所。既定のインストール先を最初に試し、
# 無ければ PATH 上から探す。
$IsccCandidates = @(
    "C:\Users\wata\AppData\Local\Programs\Inno Setup 6\ISCC.exe"
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$IsccPath = $IsccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $IsccPath) {
    $found = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($found) { $IsccPath = $found.Source }
}
if (-not $IsccPath) {
    throw "ISCC.exe (Inno Setup 6 コンパイラ) が見つかりません。Inno Setup 6 がインストールされているか確認してください。"
}
Write-Host "ISCC.exe: $IsccPath"

if (-not $SkipPublish) {
    Write-Host "==> [1/2] dotnet publish (Release, win-x64, self-contained) -> $PublishDir"
    if (Test-Path $PublishDir) {
        # 前回の publish 出力を残したまま重ねると、削除済みファイルが残留する
        # おそれがあるため、毎回まっさらな状態から publish し直す。
        Remove-Item -Recurse -Force $PublishDir
    }

    & dotnet publish $Csproj -c Release -r win-x64 --self-contained true -o $PublishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish に失敗しました (exit code $LASTEXITCODE)"
    }

    $publishFiles = Get-ChildItem $PublishDir -Recurse -File
    $publishSize = ($publishFiles | Measure-Object -Property Length -Sum).Sum
    Write-Host ("    publish 出力: {0} 個のファイル, 合計 {1:N1} MB ({2:N0} bytes)" -f $publishFiles.Count, ($publishSize / 1MB), $publishSize)
}
else {
    Write-Host "==> [1/2] -SkipPublish 指定のため dotnet publish をスキップします"
    if (-not (Test-Path $PublishDir)) {
        throw "$PublishDir が存在しません。-SkipPublish を付けずに一度実行してください。"
    }
}

if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
}

Write-Host "==> [2/2] ISCC.exe でインストーラーをコンパイル -> $DistDir"
& $IsccPath $IssPath
if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe (Inno Setup コンパイル) に失敗しました (exit code $LASTEXITCODE)"
}

$installerExe = Get-ChildItem $DistDir -Filter "VoiceIn-Setup-*.exe" -File |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($installerExe) {
    Write-Host ""
    Write-Host ("==> 完了: {0}" -f $installerExe.FullName)
    Write-Host ("    サイズ: {0:N1} MB ({1:N0} bytes)" -f ($installerExe.Length / 1MB), $installerExe.Length)
    Write-Host ""
    Write-Host "注意: このスクリプトはインストーラーを生成するだけで、実行はしません。"
}
else {
    Write-Warning "インストーラー exe が $DistDir に見つかりませんでした。ISCC.exe の出力を確認してください。"
}
