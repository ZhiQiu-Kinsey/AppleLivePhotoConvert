<#
.SYNOPSIS
    用 Velopack（vpk）把 Native AOT 发布目录打成安装版 Setup.exe、可自动更新的便携版、完整 / 增量更新包与更新清单。

.DESCRIPTION
    vpk 的版本取自桌面工程对 Velopack 库的 PackageReference，库与打包工具始终同版本（Dependabot 升级库时一并生效）。
    指定 -PreviousReleasesFrom 时先下载该仓库最新正式版的完整包，vpk 据此生成增量包；首次发布或下载失败时只生成完整包。
    产物（位于 -OutputDir）：
      LivePhotoConvert.App-win-Setup.exe      安装版（按当前用户安装到 %LocalAppData%\LivePhotoConvert.App）
      LivePhotoConvert.App-win-Portable.zip   可自动更新的便携版
      LivePhotoConvert.App-<版本>-full.nupkg   完整更新包
      LivePhotoConvert.App-<版本>-delta.nupkg  增量更新包（有上一版时）
      releases.win.json                       更新清单（应用据此检查更新）

.EXAMPLE
    pwsh .github/scripts/velopack-pack.ps1 -PublishDir dist/aot -Version 3.1.0 -OutputDir velopack -ReleaseNotes release-notes.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PublishDir,

    # 语义化版本号，不带 v 前缀
    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $OutputDir,

    # Markdown 更新说明，写入更新包
    [string] $ReleaseNotes,

    # 形如 https://github.com/owner/repo；为空时不生成增量包
    [string] $PreviousReleasesFrom,

    [string] $Token
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 不能用 LivePhotoConvert：安装根目录是 %LocalAppData%\<packId>，卸载时整体删除；
# 而日志、缩略图缓存与依赖工具在 %LocalAppData%\LivePhotoConvert，不能被连带删除。
$PackId = 'LivePhotoConvert.App'
$Title = 'LivePhotoConvert'
$MainExe = 'LivePhotoConvert.exe'
$Channel = 'win'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$Project = Join-Path $RepoRoot 'src/LivePhotoConvert.Desktop/LivePhotoConvert.Desktop.csproj'
$Icon = Join-Path $RepoRoot 'src/LivePhotoConvert.Desktop/LivePhotoConvert.ico'

function Get-VelopackVersion {
    $xml = [xml](Get-Content -LiteralPath $Project -Raw)
    $reference = @($xml.SelectNodes("//PackageReference[@Include='Velopack']"))
    if ($reference.Count -ne 1 -or -not $reference[0].GetAttribute('Version')) {
        throw "在 $Project 中找不到 Velopack 的 PackageReference。"
    }
    return $reference[0].GetAttribute('Version')
}

function Install-Vpk {
    $vpkVersion = Get-VelopackVersion
    $toolDir = Join-Path ($env:RUNNER_TEMP ?? [IO.Path]::GetTempPath()) "vpk-$vpkVersion"
    $exe = Join-Path $toolDir ($IsWindows ? 'vpk.exe' : 'vpk')
    if (-not (Test-Path -LiteralPath $exe)) {
        dotnet tool install vpk --version $vpkVersion --tool-path $toolDir | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "安装 vpk $vpkVersion 失败。" }
    }
    Write-Host "vpk $vpkVersion：$exe"
    return $exe
}

function Invoke-Vpk([string[]] $Arguments) {
    # 在非 Windows 上用 [win] 指令交叉打包（便于本地核对）；CI 与发布都在 Windows 上打包
    $directive = $IsWindows ? @() : @('[win]')
    # --yes：非交互环境下的提示一律取"是"；--legacyConsole：日志不含终端控制符
    & $script:Vpk @directive @Arguments --yes --legacyConsole --skip-updates | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "vpk $($Arguments[0]) 失败（退出码 $LASTEXITCODE）。" }
}

if (-not (Test-Path -LiteralPath (Join-Path $PublishDir $MainExe))) {
    throw "$PublishDir 中没有 $MainExe。"
}

$Vpk = Install-Vpk
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

if ($PreviousReleasesFrom) {
    $download = @('download', 'github', '--repoUrl', $PreviousReleasesFrom, '--channel', $Channel, '--outputDir', $OutputDir)
    if ($Token) { $download += @('--token', $Token) }
    try {
        Invoke-Vpk $download
    }
    catch {
        # 上一版没有 Velopack 资产（首次发布）或网络失败：只影响增量包，完整包照常生成
        Write-Host "::warning::未能下载上一版的完整包，本次不生成增量包：$_"
    }

    # 对已有 tag 重跑发布时，最新正式版就是本版本；同版本不能作为增量基准
    Get-ChildItem -LiteralPath $OutputDir -Filter "$PackId-$Version-*.nupkg" -ErrorAction SilentlyContinue | Remove-Item -Force
}

$pack = @(
    'pack',
    '--packId', $PackId,
    '--packVersion', $Version,
    '--packDir', $PublishDir,
    '--mainExe', $MainExe,
    '--packTitle', $Title,
    '--packAuthors', 'Kinsey.Qiu',
    '--icon', $Icon,
    '--runtime', 'win-x64',
    '--channel', $Channel,
    '--shortcuts', 'Desktop,StartMenuRoot',
    '--outputDir', $OutputDir
)
if ($ReleaseNotes) { $pack += @('--releaseNotes', $ReleaseNotes) }
Invoke-Vpk $pack

$expected = @("$PackId-win-Setup.exe", "$PackId-win-Portable.zip", "$PackId-$Version-full.nupkg", "releases.$Channel.json")
foreach ($name in $expected) {
    if (-not (Test-Path -LiteralPath (Join-Path $OutputDir $name))) {
        throw "vpk 没有生成 $name。"
    }
}

Get-ChildItem -LiteralPath $OutputDir -File | Sort-Object Name | ForEach-Object { Write-Host ("{0,12:N0}  {1}" -f $_.Length, $_.Name) }
