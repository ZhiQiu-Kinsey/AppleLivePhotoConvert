using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Models;

namespace LivePhotoConvert.Core.Services;

/// <summary>
/// 多信号配对校验器，在合成前验证照片与视频确实属于同一张实况照片
/// </summary>
/// <remarks>
/// 依次检查 ContentIdentifier、拍摄时间差、视频时长三个维度。
/// 任一维度明确否定即拒绝；所有可评估维度均为可疑时也拒绝；
/// 全部维度不可评估时降级为仅文件名匹配（通过）。
/// </remarks>
/// <param name="exifTool">元数据读写</param>
public sealed class PairValidator(IExifTool exifTool)
{
    /// <summary>
    /// 拍摄时间差允许的最大秒数
    /// </summary>
    private const double MaxTimeDifferenceSeconds = 3.0;

    /// <summary>
    /// 视频时长超过此值视为可疑（典型实况视频约 2~3 秒）
    /// </summary>
    private static readonly TimeSpan SuspiciousVideoDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 视频时长在此范围内视为典型实况视频
    /// </summary>
    private static readonly TimeSpan TypicalLivePhotoDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 校验一组候选配对
    /// </summary>
    /// <param name="pair">照片与视频</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>校验结果</returns>
    public async Task<PairValidationResult> ValidateAsync(MediaPair pair, CancellationToken cancellationToken = default)
    {
        var reasons = new List<string>();
        var suspiciousCount = 0;
        var evaluatedCount = 0;

        // ── 信号 1：ContentIdentifier ──
        try
        {
            var photoId = await exifTool.TryReadContentIdentifierAsync(
                pair.PhotoPath, ContentIdentifierKind.Photo, cancellationToken);
            var videoId = await exifTool.TryReadContentIdentifierAsync(
                pair.VideoPath, ContentIdentifierKind.Video, cancellationToken);

            if (photoId is not null && videoId is not null)
            {
                evaluatedCount++;
                if (string.Equals(photoId, videoId, StringComparison.OrdinalIgnoreCase))
                {
                    // ContentIdentifier 一致是最强的正向信号，直接通过
                    reasons.Add($"ContentIdentifier 一致：{photoId}");
                    return PairValidationResult.Accept(reasons);
                }

                // ContentIdentifier 明确不一致，直接拒绝
                reasons.Add($"ContentIdentifier 不匹配：照片={photoId}，视频={videoId}");
                return PairValidationResult.Reject(reasons);
            }

            if (photoId is null && videoId is null)
            {
                reasons.Add("照片和视频均无 ContentIdentifier，跳过此项校验");
            }
            else
            {
                reasons.Add($"仅{(photoId is not null ? "照片" : "视频")}含 ContentIdentifier，跳过此项校验");
            }
        }
        catch (Exception)
        {
            reasons.Add("读取 ContentIdentifier 失败，跳过此项校验");
        }

        // ── 信号 2：拍摄时间差 ──
        try
        {
            var photoDate = await exifTool.TryReadCreateDateAsync(pair.PhotoPath, cancellationToken);
            var videoDate = await exifTool.TryReadCreateDateAsync(pair.VideoPath, cancellationToken);

            if (photoDate.HasValue && videoDate.HasValue)
            {
                evaluatedCount++;
                var diff = Math.Abs((photoDate.Value - videoDate.Value).TotalSeconds);
                if (diff <= MaxTimeDifferenceSeconds)
                {
                    reasons.Add($"拍摄时间差 {diff:F1} 秒，在 {MaxTimeDifferenceSeconds} 秒阈值内");
                }
                else
                {
                    suspiciousCount++;
                    reasons.Add($"拍摄时间差 {diff:F1} 秒，超出 {MaxTimeDifferenceSeconds} 秒阈值");
                }
            }
            else
            {
                reasons.Add("无法读取拍摄时间，跳过此项校验");
            }
        }
        catch (Exception)
        {
            reasons.Add("读取拍摄时间失败，跳过此项校验");
        }

        // ── 信号 3：视频时长 ──
        try
        {
            var duration = await exifTool.TryReadDurationAsync(pair.VideoPath, cancellationToken);

            if (duration.HasValue)
            {
                evaluatedCount++;
                if (duration.Value <= TypicalLivePhotoDuration)
                {
                    reasons.Add($"视频时长 {duration.Value.TotalSeconds:F1} 秒，符合实况照片特征");
                }
                else if (duration.Value > SuspiciousVideoDuration)
                {
                    suspiciousCount++;
                    reasons.Add($"视频时长 {duration.Value.TotalSeconds:F1} 秒，超过 {SuspiciousVideoDuration.TotalSeconds} 秒，不像实况视频");
                }
                else
                {
                    reasons.Add($"视频时长 {duration.Value.TotalSeconds:F1} 秒");
                }
            }
            else
            {
                reasons.Add("无法读取视频时长，跳过此项校验");
            }
        }
        catch (Exception)
        {
            reasons.Add("读取视频时长失败，跳过此项校验");
        }

        // ── 综合判定 ──
        if (evaluatedCount == 0)
        {
            // 所有信号均不可用，降级为仅文件名匹配
            reasons.Add("所有校验维度均不可用，降级为仅文件名匹配");
            return PairValidationResult.Accept(reasons);
        }

        if (suspiciousCount > 0 && suspiciousCount == evaluatedCount)
        {
            // 所有可评估信号均为可疑
            reasons.Add("所有可评估的校验维度均为可疑，判定为不匹配");
            return PairValidationResult.Reject(reasons);
        }

        return PairValidationResult.Accept(reasons);
    }
}
