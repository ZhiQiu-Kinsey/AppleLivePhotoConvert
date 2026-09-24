<#
.SYNOPSIS
    在 Windows 上验证 Velopack 安装包：静默安装旧版本 → 检查目录、快捷方式与卸载注册 → 启动 → 用本地更新包升级到新版本 → 再启动 → 静默卸载，
    确认程序目录被移除而 %LocalAppData%\LivePhotoConvert 下的用户数据保留。

.DESCRIPTION
    升级走的是应用内更新所用的同一个更新程序：有增量包时先 Update.exe patch 重建完整包，再 Update.exe apply；更新包来自本地文件夹，不联网。
    只能在一次性的 CI 机器上运行：会安装、卸载当前用户下的 LivePhotoConvert.App，并在用户数据目录写入标记文件。

.EXAMPLE
    pwsh .github/scripts/installer-smoke.ps1 -OldSetup velopack-old/Setup.exe -OldVersion 0.0.1-ci -NewPackage velopack/LivePhotoConvert.App-0.0.2-ci-full.nupkg -NewVersion 0.0.2-ci
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OldSetup,

    [Parameter(Mandatory)]
    [string] $OldVersion,

    [Parameter(Mandatory)]
    [string] $NewPackage,

    [Parameter(Mandatory)]
    [string] $NewVersion,

    # 新版本的增量包：指定时先用安装附带的旧版完整包与它重建新版完整包（应用内增量更新的同一步骤），再安装重建结果
    [string] $NewDelta,

    # 启动后至少存活多久才算启动成功
    [int] $LaunchSeconds = 8
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$PackId = 'LivePhotoConvert.App'
$Title = 'LivePhotoConvert'
$InstallRoot = Join-Path $env:LOCALAPPDATA $PackId
$Exe = Join-Path $InstallRoot 'current\LivePhotoConvert.exe'
$UpdateExe = Join-Path $InstallRoot 'Update.exe'
$UninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$PackId"
$UserData = Join-Path $env:LOCALAPPDATA 'LivePhotoConvert'
$Marker = Join-Path $UserData 'tools\ci-marker\keep.txt'
$ShortcutDirs = @(
    [Environment]::GetFolderPath('Desktop'),
    [Environment]::GetFolderPath('StartMenu'),
    (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs')
)

function Step([string] $Text) { Write-Host "`n==> $Text" }

function Fail([string] $Text) {
    Write-Host "::error::$Text"
    $log = Join-Path $env:LOCALAPPDATA "velopack\velopack_$PackId.log"
    if (Test-Path -LiteralPath $log) {
        Write-Host '--- Velopack 日志（末尾） ---'
        Get-Content -LiteralPath $log -Tail 80 | ForEach-Object { Write-Host $_ }
    }
    exit 1
}

function Assert-Exists([string] $Path, [string] $What) {
    if (-not (Test-Path -LiteralPath $Path)) { Fail "$What 不存在：$Path" }
    Write-Host "  ok  $What：$Path"
}

# 升级替换 current 目录的瞬间清单可能不存在，此时返回 $null
function Get-InstalledVersion {
    $manifest = Join-Path $InstallRoot 'current\sq.version'
    if (-not (Test-Path -LiteralPath $manifest)) { return $null }
    return ([xml](Get-Content -LiteralPath $manifest -Raw)).package.metadata.version
}

function Find-Shortcuts {
    @($ShortcutDirs | Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter "$Title.lnk" -File -ErrorAction SilentlyContinue })
}

function Test-Launch([string] $Context) {
    Step "启动程序（$Context）"
    $process = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds $LaunchSeconds
    if ($process.HasExited) {
        Fail "程序启动后 $LaunchSeconds 秒内退出（退出码 $($process.ExitCode)）。错误日志：$(Join-Path $UserData 'logs\error.log')"
    }
    Write-Host "  ok  进程 $($process.Id) 运行中，窗口标题：'$($process.MainWindowTitle)'"
    Stop-Process -Id $process.Id -Force
    $process.WaitForExit(10000) | Out-Null
}

# Update.exe 是窗口子系统程序，用 & 调用时 PowerShell 不等待它结束，退出码也拿不到
function Invoke-Updater([string[]] $Arguments) {
    $quoted = $Arguments | ForEach-Object { $_ -match '\s' ? "`"$_`"" : $_ }
    $process = Start-Process -FilePath $UpdateExe -ArgumentList $quoted -Wait -PassThru
    return $process.ExitCode
}

function Wait-Until([scriptblock] $Condition, [int] $Seconds, [string] $What) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (& $Condition)) {
        if ((Get-Date) -gt $deadline) { Fail "等待超时：$What" }
        Start-Sleep -Milliseconds 500
    }
}

if (Test-Path -LiteralPath $InstallRoot) { Fail "测试机上已有 $InstallRoot，本脚本只能在干净的 CI 机器上运行。" }

# 用户数据目录的标记文件：卸载后必须仍在
New-Item -ItemType Directory -Force -Path (Split-Path $Marker) | Out-Null
Set-Content -LiteralPath $Marker -Value 'keep'

Step "静默安装 $OldVersion"
$setup = Start-Process -FilePath $OldSetup -ArgumentList '--silent' -Wait -PassThru
if ($setup.ExitCode -ne 0) { Fail "安装程序退出码 $($setup.ExitCode)" }
Assert-Exists $Exe '主程序'
Assert-Exists $UpdateExe '更新程序'
$installed = Get-InstalledVersion
if ($installed -ne $OldVersion) { Fail "安装的版本是 $installed，期望 $OldVersion" }
Write-Host "  ok  已安装版本 $installed"

$shortcuts = @(Find-Shortcuts)
if ($shortcuts.Count -lt 2) { Fail "期望桌面与开始菜单各一个快捷方式，实际：$($shortcuts.FullName -join '; ')" }
$shortcuts | ForEach-Object { Write-Host "  ok  快捷方式：$($_.FullName)" }

if (-not (Test-Path -LiteralPath $UninstallKey)) { Fail "缺少卸载注册：$UninstallKey" }
$entry = Get-ItemProperty -LiteralPath $UninstallKey
if ($entry.DisplayName -ne $Title) { Fail "卸载注册的显示名是 '$($entry.DisplayName)'，期望 '$Title'" }
Write-Host "  ok  卸载注册：$($entry.DisplayName) $($entry.DisplayVersion)"

Test-Launch $OldVersion

$package = (Resolve-Path -LiteralPath $NewPackage).Path
if ($NewDelta) {
    Step "用增量包重建 $NewVersion 的完整包"
    $base = Join-Path $InstallRoot "packages\$PackId-$OldVersion-full.nupkg"
    Assert-Exists $base '安装附带的旧版完整包（增量更新的基准）'
    $package = Join-Path ($env:RUNNER_TEMP ?? [IO.Path]::GetTempPath()) "rebuilt-$NewVersion-full.nupkg"
    $patch = Invoke-Updater @('patch', '--old', $base, '--delta', (Resolve-Path -LiteralPath $NewDelta).Path, '--output', $package)
    if ($patch -ne 0) { Fail "Update.exe patch 退出码 $patch" }
    Assert-Exists $package '重建的完整包'
}

Step "用本地更新包升级到 $NewVersion"
$apply = Invoke-Updater @('apply', '--silent', '--norestart', '--package', $package)
if ($apply -ne 0) { Fail "Update.exe apply 退出码 $apply" }
Wait-Until { (Get-InstalledVersion) -eq $NewVersion } 60 "升级到 $NewVersion"
Write-Host "  ok  已升级到 $(Get-InstalledVersion)"
$entry = Get-ItemProperty -LiteralPath $UninstallKey
Write-Host "  ok  卸载注册版本：$($entry.DisplayVersion)"
Assert-Exists $Marker '升级后的用户数据'

Test-Launch $NewVersion

Step '静默卸载'
$uninstall = Invoke-Updater @('uninstall', '--silent')
if ($uninstall -ne 0) { Fail "Update.exe uninstall 退出码 $uninstall" }
# 更新程序删除自身前会等几秒，整个目录随后消失
Wait-Until { -not (Test-Path -LiteralPath (Join-Path $InstallRoot 'current')) } 60 '删除程序目录'
Wait-Until { -not (Test-Path -LiteralPath $InstallRoot) -or -not (Get-ChildItem -LiteralPath $InstallRoot -Force) } 60 '清空安装根目录'
Write-Host "  ok  程序目录已移除：$InstallRoot"
# 严格模式下空结果是 $null，没有 Count 属性，需先包成数组
$remaining = @(Find-Shortcuts)
if ($remaining.Count -gt 0) { Fail "卸载后仍有快捷方式：$($remaining.FullName -join '; ')" }
if (Test-Path -LiteralPath $UninstallKey) { Fail "卸载后仍有卸载注册：$UninstallKey" }
Assert-Exists $Marker '卸载后的用户数据'

Write-Host "`n安装、启动、升级与卸载验证通过。"
