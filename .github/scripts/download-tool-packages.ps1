<#
.SYNOPSIS
    按 tools.json 下载每个依赖包，保存为 <OutDirectory>/<包 id>，供 ToolManifestPackageTests 校验。

.DESCRIPTION
    只负责下载，不做校验：SHA256 / npm integrity、根目录、入口与筛选由 Core 的 ToolInstaller
    （ToolIntegrity）在 ToolManifestPackageTests 中验证，避免在工作流里重写一份校验逻辑。
    这里记录的 SHA256 只用于排查时对照。

    清单中带 githubRelease 的包，另外探测 githubMirrors 里的加速镜像是否可达；
    镜像是第三方服务，只给出警告，不让工作流失败。

    任一包的原始地址下载失败时退出码为 1（其余包仍会下载完，便于一次看全）。
#>
[CmdletBinding()]
param(
    [string] $Manifest = 'src/LivePhotoConvert.Core/External/Tools/tools.json',

    [Parameter(Mandatory)]
    [string] $OutDirectory,

    [string] $SummaryPath = $env:GITHUB_STEP_SUMMARY
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Manifest)) {
    Write-Host "::error::找不到依赖清单 $Manifest。"
    exit 1
}

$json = Get-Content -LiteralPath $Manifest -Raw -Encoding utf8 | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null

# 与桌面端下载器一致：不带浏览器 UA，SourceForge 等站点才会直接 302 到文件而不是返回下载页
$userAgent = 'LivePhotoConvert/ci (tools-manifest check)'
$curl = if ($IsWindows) { 'curl.exe' } else { 'curl' }
$nullDevice = if ($IsWindows) { 'NUL' } else { '/dev/null' }
$rows = [System.Collections.Generic.List[string]]::new()
$failures = 0
$warnings = 0

function Format-Size([long] $bytes) {
    if ($bytes -ge 1MB) { return '{0:0.0} MB' -f ($bytes / 1MB) }
    return '{0:0.0} KB' -f ($bytes / 1KB)
}

foreach ($tool in $json.tools) {
    foreach ($package in $tool.packages) {
        $target = Join-Path $OutDirectory $package.id
        Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
        Write-Host "::group::$($package.id) ← $($package.url)"
        $watch = [Diagnostics.Stopwatch]::StartNew()
        & $curl --fail --location --silent --show-error `
            --retry 3 --retry-delay 5 --retry-all-errors `
            --connect-timeout 30 --max-time 1200 `
            --user-agent $userAgent --output $target $package.url
        $exit = $LASTEXITCODE
        $watch.Stop()
        if ($exit -eq 0 -and (Test-Path -LiteralPath $target)) {
            $size = (Get-Item -LiteralPath $target).Length
            $sha = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
            $match = if ($sha -eq $package.sha256.ToLowerInvariant()) { '一致' } else { '**不一致**' }
            Write-Host "下载完成：$(Format-Size $size)，$([int]$watch.Elapsed.TotalSeconds)s，SHA256 $sha（与清单$($match.Trim('*'))）"
            $rows.Add("| $($package.id) | $($package.rid) | $(Format-Size $size) | $([int]$watch.Elapsed.TotalSeconds)s | $match |")
        }
        else {
            $failures++
            Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
            Write-Host "::error::$($package.id) 下载失败（curl 退出码 $exit）：$($package.url)"
            $rows.Add("| $($package.id) | $($package.rid) | — | — | **下载失败**（curl $exit） |")
        }

        $isGithubRelease = $package.PSObject.Properties['githubRelease'] -and $package.githubRelease
        if ($isGithubRelease) {
            foreach ($mirror in @($json.githubMirrors)) {
                $prefix = $mirror.TrimEnd('/') + '/'
                $mirrorUrl = $prefix + $package.url
                # 只取首字节确认镜像可达，不重复下载整包
                & $curl --fail --location --silent --show-error --range 0-0 `
                    --connect-timeout 20 --max-time 60 `
                    --user-agent $userAgent --output $nullDevice $mirrorUrl
                if ($LASTEXITCODE -ne 0) {
                    $warnings++
                    Write-Host "::warning::$($package.id) 的加速镜像 $prefix 不可达（curl 退出码 $LASTEXITCODE）。"
                    $rows.Add("| ↳ 镜像 $prefix | | | | 不可达（警告） |")
                }
            }
        }

        Write-Host '::endgroup::'
    }
}

$md = @(
    '### 依赖包下载'
    ''
    '| 包 | 平台 | 大小 | 耗时 | SHA256（仅对照，校验以测试为准） |'
    '| :--- | :--- | ---: | ---: | :--- |'
) + $rows + @('', "下载失败 $failures 个，镜像警告 $warnings 个。", '')

$md | ForEach-Object { Write-Host $_ }
if ($SummaryPath) {
    Add-Content -LiteralPath $SummaryPath -Value ($md -join "`n") -Encoding utf8
}

exit ($failures -gt 0 ? 1 : 0)
