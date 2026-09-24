using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pipeline;

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
        var causes = new List<OutcomeCause>();

        switch (Normalize(photo.ContentIdentifier), Normalize(video.ContentIdentifier))
        {
            case ({ } photoId, { } videoId) when photoId.Equals(videoId, StringComparison.OrdinalIgnoreCase):
                return PairValidationResult.Accept(new OutcomeCause(OutcomeReason.PairContentIdentifierMatched, photoId));
            case ({ } photoId, { } videoId):
                return PairValidationResult.Reject(new OutcomeCause(OutcomeReason.PairContentIdentifierMismatch, photoId, videoId));
            case ({ }, null):
                return PairValidationResult.Reject(OutcomeReason.PairContentIdentifierPhotoOnly);
            case (null, { }):
                return PairValidationResult.Reject(OutcomeReason.PairContentIdentifierVideoOnly);
        }

        var evaluated = false;
        switch (photo.CaptureTime, video.CaptureTime)
        {
            case ({ } photoTime, { } videoTime):
                evaluated = true;
                var difference = photoTime.DistanceTo(videoTime);
                if (difference > MaxCaptureTimeDifference)
                {
                    return PairValidationResult.Reject(new OutcomeCause(OutcomeReason.PairCaptureTimeTooFar, difference.TotalSeconds, MaxCaptureTimeDifference.TotalSeconds));
                }

                causes.Add(new OutcomeCause(OutcomeReason.PairCaptureTimeClose, difference.TotalSeconds));
                break;
            case ({ }, null):
                return PairValidationResult.Reject(OutcomeReason.PairCaptureTimePhotoOnly);
            case (null, { }):
                return PairValidationResult.Reject(OutcomeReason.PairCaptureTimeVideoOnly);
        }

        if (video.Duration is { } duration)
        {
            evaluated = true;
            if (duration > MaxLivePhotoDuration)
            {
                return PairValidationResult.Reject([.. causes, new OutcomeCause(OutcomeReason.PairVideoTooLong, duration.TotalSeconds, MaxLivePhotoDuration.TotalSeconds)]);
            }

            causes.Add(new OutcomeCause(OutcomeReason.PairDurationWithinLimit, duration.TotalSeconds));
        }

        if (!evaluated)
        {
            causes.Add(OutcomeReason.PairNameOnly);
        }

        return PairValidationResult.Accept(causes);
    }

    private static string? Normalize(string? identifier) => string.IsNullOrWhiteSpace(identifier) ? null : identifier.Trim();
}
