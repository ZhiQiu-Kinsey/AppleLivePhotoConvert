using LivePhotoConvert.Core.Metadata;

namespace LivePhotoConvert.Core.Pairing;

/// <summary>
/// 校验照片与视频是否属于同一张实况照片，避免把无关的同名文件合成在一起。
/// </summary>
/// <remarks>
/// ContentIdentifier 一致直接通过；明确不一致、仅单边存在、拍摄时间差超过阈值、仅单边有拍摄时间或视频过长则拒绝；
/// 全部信号都缺失时退化为仅按文件名匹配并通过。
/// </remarks>
public static class PairValidator
{
    public static readonly TimeSpan MaxCaptureTimeDifference = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan MaxLivePhotoDuration = TimeSpan.FromSeconds(30);

    public static PairValidationResult Validate(MediaMetadata photo, MediaMetadata video)
    {
        var reasons = new List<string>();

        switch (Normalize(photo.ContentIdentifier), Normalize(video.ContentIdentifier))
        {
            case ({ } photoId, { } videoId) when photoId.Equals(videoId, StringComparison.OrdinalIgnoreCase):
                return PairValidationResult.Accept([$"ContentIdentifier 一致：{photoId}"]);
            case ({ } photoId, { } videoId):
                return PairValidationResult.Reject([$"ContentIdentifier 不匹配：照片={photoId}，视频={videoId}"]);
            case ({ }, null):
                return PairValidationResult.Reject(["仅照片含 ContentIdentifier，不像是同一张实况照片"]);
            case (null, { }):
                return PairValidationResult.Reject(["仅视频含 ContentIdentifier，不像是同一张实况照片"]);
        }

        var evaluated = false;
        switch (photo.CaptureTime, video.CaptureTime)
        {
            case ({ } photoTime, { } videoTime):
                evaluated = true;
                var difference = photoTime.DistanceTo(videoTime);
                if (difference > MaxCaptureTimeDifference)
                {
                    return PairValidationResult.Reject([$"拍摄时间差 {difference.TotalSeconds:F0} 秒，超过 {MaxCaptureTimeDifference.TotalSeconds:F0} 秒阈值"]);
                }

                reasons.Add($"拍摄时间差 {difference.TotalSeconds:F1} 秒");
                break;
            case ({ }, null):
                return PairValidationResult.Reject(["仅照片含拍摄时间，不像是同一张实况照片"]);
            case (null, { }):
                return PairValidationResult.Reject(["仅视频含拍摄时间，不像是同一张实况照片"]);
        }

        if (video.Duration is { } duration)
        {
            evaluated = true;
            if (duration > MaxLivePhotoDuration)
            {
                return PairValidationResult.Reject([.. reasons, $"视频时长 {duration.TotalSeconds:F1} 秒，超过 {MaxLivePhotoDuration.TotalSeconds:F0} 秒，不像实况视频"]);
            }

            reasons.Add($"视频时长 {duration.TotalSeconds:F1} 秒");
        }

        if (!evaluated)
        {
            reasons.Add("缺少可校验的元数据，按文件名匹配");
        }

        return PairValidationResult.Accept(reasons);
    }

    private static string? Normalize(string? identifier) => string.IsNullOrWhiteSpace(identifier) ? null : identifier.Trim();
}
