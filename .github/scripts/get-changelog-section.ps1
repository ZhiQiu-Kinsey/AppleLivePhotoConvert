<#
.SYNOPSIS
    从 CHANGELOG.md 提取指定版本的段落，作为 GitHub Release 说明。

.DESCRIPTION
    段落从 "## [x.y.z] - YYYY-MM-DD" 标题的下一行开始，到下一个二级标题为止；
    去掉首尾空行与段落间的 "---" 分隔线。版本号可带前缀 v（取自 tag）。
    找不到段落或段落为空时退出码为 1，并输出 GitHub 错误注释。

.EXAMPLE
    pwsh .github/scripts/get-changelog-section.ps1 -Version v3.0.3 -OutFile release-notes.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [string] $Path = 'CHANGELOG.md',

    # 不指定时输出到标准输出
    [string] $OutFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$normalized = $Version.Trim() -replace '^[vV]', ''
if (-not $normalized) {
    Write-Host '::error::版本号为空。'
    exit 1
}

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "::error::找不到 $Path。"
    exit 1
}

$lines = Get-Content -LiteralPath $Path -Encoding utf8
# 兼容 "## [3.0.3] - 日期"、"## 3.0.3"、"## [v3.0.3]" 等写法；版本号后必须是 ] 、空白或行尾，避免 3.0.1 匹配到 3.0.10
$heading = '^##\s+\[?v?' + [regex]::Escape($normalized) + '(\]|\s|$)'

$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match $heading) {
        $start = $i + 1
        break
    }
}

if ($start -lt 0) {
    Write-Host "::error file=$Path::CHANGELOG 中找不到版本 $normalized 的段落。发布前请在顶部新增 ""## [$normalized] - YYYY-MM-DD"" 条目（见 AGENTS.md 第 7 节）。"
    exit 1
}

$end = $lines.Count
for ($i = $start; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^##\s') {
        $end = $i
        break
    }
}

$body = [System.Collections.Generic.List[string]]::new()
if ($end -gt $start) {
    $body.AddRange([string[]] $lines[$start..($end - 1)])
}

# 去掉尾部的空行与 "---" 分隔线，再去掉开头空行
while ($body.Count -gt 0 -and ($body[$body.Count - 1].Trim() -eq '' -or $body[$body.Count - 1].Trim() -match '^-{3,}$')) {
    $body.RemoveAt($body.Count - 1)
}
while ($body.Count -gt 0 -and $body[0].Trim() -eq '') {
    $body.RemoveAt(0)
}

if ($body.Count -eq 0) {
    Write-Host "::error file=$Path::CHANGELOG 中版本 $normalized 的段落为空。"
    exit 1
}

$text = ($body -join "`n") + "`n"
if ($OutFile) {
    # UTF-8 无 BOM、LF 换行
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutFile), $text, [Text.UTF8Encoding]::new($false))
    Write-Host "已提取 $normalized 的更新说明（$($body.Count) 行）到 $OutFile。"
}
else {
    [Console]::Out.Write($text)
}
