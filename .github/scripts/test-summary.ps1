<#
.SYNOPSIS
    汇总 dotnet test 生成的 trx，写入 GitHub Job Summary，并检查跳过数是否超过阈值。

.DESCRIPTION
    按测试工程（取 trx 中测试方法所在程序集的文件名）统计通过 / 失败 / 跳过数，
    列出跳过与失败的用例及原因。跳过数以 UnitTestResult 的 outcome="NotExecuted" 为准
    （VSTest 的 Counters.notExecuted 不含 xunit 的 Skip）。

    失败条件（退出码 1）：
      - 目录中没有 trx；
      - 指定了 -ExpectedProjects 而缺少其中某个工程的结果，或某工程一条都没执行；
      - 指定了 -MaxSkipped（>= 0）且跳过总数超过阈值。
    测试失败本身由 dotnet test 步骤负责报告，这里只汇总，不因失败用例改变退出码。

.EXAMPLE
    pwsh .github/scripts/test-summary.ps1 -ResultsDirectory TestResults -Title 'Linux' -MaxSkipped 5 `
        -ExpectedProjects LivePhotoConvert.Core.Tests, LivePhotoConvert.Desktop.Tests, LivePhotoConvert.E2E
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ResultsDirectory,

    [string] $Title = '测试结果',

    # 跳过总数上限；小于 0 表示不检查
    [int] $MaxSkipped = -1,

    # 必须出现且至少执行一条用例的测试工程（程序集名，不含扩展名）
    [string[]] $ExpectedProjects = @(),

    # 跳过数超出阈值时附带的排查提示
    [string] $SkipHint = '检查外部工具是否安装、环境变量是否生效。',

    # 默认写到 GITHUB_STEP_SUMMARY；本地运行时未设置则只打印到控制台
    [string] $SummaryPath = $env:GITHUB_STEP_SUMMARY
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Annotation([string] $Level, [string] $Message) {
    # GitHub Actions 的工作流命令；本地运行时就是一行普通输出
    Write-Host "::${Level}::$Message"
}

function Get-FirstLine([string] $Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return '' }
    $line = ($Text -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
    if ($null -eq $line) { return '' }
    $line = $line.Trim()
    if ($line.Length -gt 200) { $line = $line.Substring(0, 200) + '…' }
    # 表格与列表里的竖线、反引号会破坏 Markdown
    return $line.Replace('|', '\|').Replace('`', "'")
}

$files = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -Recurse -File -ErrorAction SilentlyContinue)
if ($files.Count -eq 0) {
    Write-Annotation 'error' "在 $ResultsDirectory 中找不到 trx 测试结果。"
    exit 1
}

$ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
$projects = [ordered]@{}
$skippedTests = [System.Collections.Generic.List[object]]::new()
$failedTests = [System.Collections.Generic.List[object]]::new()

foreach ($file in $files | Sort-Object FullName) {
    [xml] $xml = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8

    # testId -> 程序集名；codeBase 保留原始大小写（storage 属性会被转成小写）
    $assemblyOf = @{}
    foreach ($node in Select-Xml -Xml $xml -XPath '//t:TestDefinitions/t:UnitTest' -Namespace $ns) {
        $method = $node.Node.SelectSingleNode('*[local-name()="TestMethod"]')
        $assemblyOf[$node.Node.id] = [IO.Path]::GetFileNameWithoutExtension($method.codeBase)
    }

    $times = (Select-Xml -Xml $xml -XPath '/t:TestRun/t:Times' -Namespace $ns | Select-Object -First 1)
    $duration = [TimeSpan]::Zero
    if ($times -and $times.Node.start -and $times.Node.finish) {
        $duration = [DateTimeOffset]::Parse($times.Node.finish) - [DateTimeOffset]::Parse($times.Node.start)
    }

    $results = @(Select-Xml -Xml $xml -XPath '//t:Results/t:UnitTestResult' -Namespace $ns | ForEach-Object Node)
    $fileProjects = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($result in $results) {
        $project = $assemblyOf[$result.testId]
        if (-not $project) { $project = [IO.Path]::GetFileNameWithoutExtension($file.Name) }
        if (-not $projects.Contains($project)) {
            $projects[$project] = [pscustomobject]@{ Name = $project; Passed = 0; Failed = 0; Skipped = 0; Total = 0; Duration = [TimeSpan]::Zero }
        }

        [void] $fileProjects.Add($project)
        $entry = $projects[$project]
        $entry.Total++
        $message = $result.SelectSingleNode('*[local-name()="Output"]/*[local-name()="ErrorInfo"]/*[local-name()="Message"]')
        $reason = if ($message) { Get-FirstLine $message.InnerText } else { '' }
        switch ($result.outcome) {
            'Passed' { $entry.Passed++ }
            'NotExecuted' {
                $entry.Skipped++
                $skippedTests.Add([pscustomobject]@{ Project = $project; Name = $result.testName; Reason = $reason })
            }
            default {
                # Failed / Error / Timeout / Aborted 等都算失败
                $entry.Failed++
                $failedTests.Add([pscustomobject]@{ Project = $project; Name = $result.testName; Reason = $reason })
            }
        }
    }

    foreach ($project in $fileProjects) {
        $projects[$project].Duration += $duration
    }
}

$totalSkipped = ($projects.Values | Measure-Object -Property Skipped -Sum).Sum
$totalFailed = ($projects.Values | Measure-Object -Property Failed -Sum).Sum
$problems = [System.Collections.Generic.List[string]]::new()

# pwsh -File 传入的 "A,B" 是单个字符串，这里统一按逗号拆开
$ExpectedProjects = @($ExpectedProjects | ForEach-Object { $_ -split ',' } | ForEach-Object Trim | Where-Object { $_ })
foreach ($expected in $ExpectedProjects) {
    if (-not $projects.Contains($expected)) {
        $problems.Add("缺少测试工程 $expected 的结果。")
    }
    elseif ($projects[$expected].Passed + $projects[$expected].Failed -eq 0) {
        $problems.Add("测试工程 $expected 没有执行任何用例（全部跳过或为空）。")
    }
}

if ($MaxSkipped -ge 0 -and $totalSkipped -gt $MaxSkipped) {
    $problems.Add("跳过 $totalSkipped 条，超过阈值 $MaxSkipped。$SkipHint")
}

# ---- Markdown ----
$md = [System.Text.StringBuilder]::new()
$status = if ($totalFailed -gt 0 -or $problems.Count -gt 0) { '未通过' } else { '通过' }
[void] $md.AppendLine("### $Title：$status")
[void] $md.AppendLine()
[void] $md.AppendLine('| 测试工程 | 通过 | 失败 | 跳过 | 合计 | 耗时 |')
[void] $md.AppendLine('| :--- | ---: | ---: | ---: | ---: | ---: |')
foreach ($p in $projects.Values) {
    [void] $md.AppendLine(('| {0} | {1} | {2} | {3} | {4} | {5:0.0}s |' -f $p.Name, $p.Passed, $p.Failed, $p.Skipped, $p.Total, $p.Duration.TotalSeconds))
}

$sum = { param($name) ($projects.Values | Measure-Object -Property $name -Sum).Sum }
[void] $md.AppendLine(('| **合计** | **{0}** | **{1}** | **{2}** | **{3}** | |' -f (& $sum 'Passed'), $totalFailed, $totalSkipped, (& $sum 'Total')))
[void] $md.AppendLine()

if ($MaxSkipped -ge 0) {
    $verdict = if ($totalSkipped -le $MaxSkipped) { '未超出' } else { '**超出**' }
    [void] $md.AppendLine("跳过阈值：$totalSkipped / $MaxSkipped（$verdict）")
    [void] $md.AppendLine()
}

foreach ($problem in $problems) {
    [void] $md.AppendLine("> [!CAUTION]")
    [void] $md.AppendLine("> $problem")
    [void] $md.AppendLine()
}

if ($failedTests.Count -gt 0) {
    [void] $md.AppendLine("<details open><summary>失败的用例（$($failedTests.Count)）</summary>")
    [void] $md.AppendLine()
    foreach ($t in $failedTests | Select-Object -First 50) {
        [void] $md.AppendLine("- ``$($t.Name)``（$($t.Project)）：$($t.Reason)")
    }
    [void] $md.AppendLine()
    [void] $md.AppendLine('</details>')
    [void] $md.AppendLine()
}

if ($skippedTests.Count -gt 0) {
    [void] $md.AppendLine("<details><summary>跳过的用例（$($skippedTests.Count)）</summary>")
    [void] $md.AppendLine()
    foreach ($t in $skippedTests | Sort-Object Project, Name) {
        [void] $md.AppendLine("- ``$($t.Name)``（$($t.Project)）：$($t.Reason)")
    }
    [void] $md.AppendLine()
    [void] $md.AppendLine('</details>')
    [void] $md.AppendLine()
}

$text = $md.ToString()
Write-Host $text
if ($SummaryPath) {
    Add-Content -LiteralPath $SummaryPath -Value $text -Encoding utf8
}

foreach ($problem in $problems) {
    Write-Annotation 'error' $problem
}

exit ($problems.Count -gt 0 ? 1 : 0)
