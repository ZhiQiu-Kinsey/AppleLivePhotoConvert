<#
.SYNOPSIS
    用 GitHub API 拉取仓库贡献者，生成头像墙写入 README 的标记区段。

.DESCRIPTION
    贡献者取自 repos/{owner}/{repo}/contributors（按提交数排序，不含匿名提交），
    排除 type 不是 User 的账号（Bot、组织）以及登录名以 [bot] 结尾的账号，
    替换每个文件中 <!-- CONTRIBUTORS:START --> 与 <!-- CONTRIBUTORS:END --> 之间的内容。
    需要已登录的 gh（工作流中通过 GH_TOKEN 提供）。内容有变化时输出 changed=true 到 GITHUB_OUTPUT。

.EXAMPLE
    pwsh .github/scripts/update-contributors.ps1 -Repository ZhiQiu-Kinsey/AppleLivePhotoConvert -Path README.md, docs/README.en.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Repository,

    [Parameter(Mandatory)]
    [string[]] $Path,

    # 头像显示尺寸（像素）；请求两倍尺寸的图片以适配高分屏
    [int] $AvatarSize = 64
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$startMarker = '<!-- CONTRIBUTORS:START -->'
$endMarker = '<!-- CONTRIBUTORS:END -->'

# --paginate 逐页输出 JSON 数组，--jq 对每页展开，得到每行一个贡献者对象
$lines = gh api --paginate "repos/$Repository/contributors?per_page=100" --jq '.[] | {login, html_url, avatar_url, type}'
if ($LASTEXITCODE -ne 0) {
    Write-Host '::error::读取贡献者列表失败。'
    exit 1
}

$contributors = @($lines | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object {
        $_.type -eq 'User' -and -not $_.login.EndsWith('[bot]', [StringComparison]::OrdinalIgnoreCase)
    })

if ($contributors.Count -eq 0) {
    # 接口偶发返回空列表时不要把已有名单清空
    Write-Host '::warning::没有读到贡献者，README 保持不变。'
    exit 0
}

$cells = foreach ($person in $contributors) {
    $login = [System.Net.WebUtility]::HtmlEncode($person.login)
    $separator = if ($person.avatar_url.Contains('?')) { '&' } else { '?' }
    $avatar = [System.Net.WebUtility]::HtmlEncode("$($person.avatar_url)$($separator)s=$($AvatarSize * 2)")
    "  <a href=`"$($person.html_url)`" title=`"$login`"><img src=`"$avatar`" width=`"$AvatarSize`" height=`"$AvatarSize`" alt=`"$login`" /></a>"
}

$block = (@($startMarker, '<p>') + @($cells) + @('</p>', $endMarker)) -join "`n"
$pattern = [regex]::Escape($startMarker) + '[\s\S]*?' + [regex]::Escape($endMarker)

$changed = $false
foreach ($file in $Path) {
    $original = [System.IO.File]::ReadAllText($file)
    if (-not [regex]::IsMatch($original, $pattern)) {
        Write-Host "::error file=$file::找不到贡献者标记区段（$startMarker … $endMarker）。"
        exit 1
    }

    $updated = [regex]::Replace($original, $pattern, { param($m) $block }, 'None')
    if ($updated -ne $original) {
        [System.IO.File]::WriteAllText($file, $updated, [System.Text.UTF8Encoding]::new($false))
        Write-Host "已更新 $file（$($contributors.Count) 位贡献者）"
        $changed = $true
    }
}

if ($env:GITHUB_OUTPUT) {
    "changed=$($changed.ToString().ToLowerInvariant())" >> $env:GITHUB_OUTPUT
}
