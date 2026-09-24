<#
.SYNOPSIS
    为发布文件生成 SHA256SUMS.txt，格式与 GNU sha256sum 相同（"<小写哈希>  <文件名>"），
    可用 `sha256sum -c SHA256SUMS.txt` 或 PowerShell 的 Get-FileHash 核对。

.EXAMPLE
    pwsh .github/scripts/write-sha256sums.ps1 -Directory release -OutFile release/SHA256SUMS.txt
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Directory,

    [string] $Filter = '*',

    [string] $OutFile = 'SHA256SUMS.txt'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$outPath = [IO.Path]::GetFullPath($OutFile)
$files = @(Get-ChildItem -LiteralPath $Directory -Filter $Filter -File |
    Where-Object { $_.FullName -ne $outPath } |
    Sort-Object Name)

if ($files.Count -eq 0) {
    Write-Host "::error::$Directory 中没有匹配 $Filter 的文件。"
    exit 1
}

$lines = foreach ($file in $files) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}

# UTF-8 无 BOM、LF 换行，sha256sum -c 可直接读取
[IO.File]::WriteAllText($outPath, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Get-Content -LiteralPath $outPath | ForEach-Object { Write-Host $_ }
