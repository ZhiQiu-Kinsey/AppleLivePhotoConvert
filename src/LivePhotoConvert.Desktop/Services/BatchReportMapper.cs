using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.ViewModels;

namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 把 Core 的批处理结果转换为报告页模型；每条记录都直接来自对应条目的处理结果。
/// </summary>
public static class BatchReportMapper
{
    /// <param name="localizer">状态与汇总文案来源</param>
    /// <param name="report">批处理结果</param>
    /// <param name="modeName">任务名称</param>
    /// <param name="successDescription">成功条目的说明</param>
    /// <param name="targetFormat">输出格式标签</param>
    /// <param name="outputDirectory">输出目录</param>
    public static BatchReportModel ToModel(ILocalizer localizer, BatchReport report, string modeName, string successDescription, string targetFormat, string outputDirectory)
    {
        var records = new List<ReportItemRecord>(report.Items.Count);
        foreach (var item in report.Items.OrderBy(item => item.Kind).ThenBy(item => item.Source, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(item.Source);
            var sourceFormat = Path.GetExtension(item.Source).TrimStart('.').ToUpperInvariant();
            records.Add(item.Kind switch
            {
                OutcomeKind.Succeeded => new ReportItemRecord(name, localizer["ReportStatusSuccess"], successDescription, false, "Success", sourceFormat, targetFormat),
                OutcomeKind.Skipped => new ReportItemRecord(name, localizer["ReportStatusSkipped"], item.Message ?? string.Empty, false, "Skipped", sourceFormat, targetFormat),
                _ => new ReportItemRecord(name, localizer["ReportStatusFailed"], item.Message ?? string.Empty, true, "Failed", sourceFormat, targetFormat)
            });

            if (item.CleanupError is not null)
            {
                records.Add(new ReportItemRecord(name, localizer["ReportStatusCleanup"], item.CleanupError, false, "Cleanup"));
            }
        }

        var summary = localizer.Format("ReportSummaryFormat", report.Succeeded, report.Failed + report.CleanupFailures);
        return new BatchReportModel
        {
            SummaryBadge = report.Canceled ? summary + localizer["ReportCanceledSuffix"] : summary,
            Records = records,
            TotalCount = report.Items.Count,
            SuccessCount = report.Succeeded,
            FailedCount = report.Failed,
            SkippedCount = report.Skipped,
            Elapsed = report.Elapsed,
            OutputDirectory = outputDirectory,
            ModeName = modeName,
            WasCanceled = report.Canceled
        };
    }

    /// <summary>
    /// 批处理无法开始（如缺少外部工具）时的报告。
    /// </summary>
    public static BatchReportModel FromError(ILocalizer localizer, string message, string modeName, string outputDirectory)
    {
        return new BatchReportModel
        {
            SummaryBadge = localizer.Format("ReportSummaryFormat", 0, 1),
            Records = [new ReportItemRecord(string.Empty, localizer["ReportStatusFailed"], message, false, "Failed")],
            TotalCount = 1,
            FailedCount = 1,
            OutputDirectory = outputDirectory,
            ModeName = modeName
        };
    }
}
