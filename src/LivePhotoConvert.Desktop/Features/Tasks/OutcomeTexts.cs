using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Tasks;

/// <summary>
/// Core 原因码与附注类别到界面文案的映射；键写成字面量，字符串资源测试才能核对引用。
/// </summary>
public static class OutcomeTexts
{
    public static string ReasonKey(OutcomeReason reason) => reason switch
    {
        OutcomeReason.NotMotionPhoto => "OutcomeReasonNotMotionPhoto",
        OutcomeReason.NothingToStrip => "OutcomeReasonNothingToStrip",
        OutcomeReason.GainMapPreserved => "OutcomeReasonGainMapPreserved",
        OutcomeReason.PairContentIdentifierMatched => "OutcomeReasonPairIdMatchedFormat",
        OutcomeReason.PairContentIdentifierMismatch => "OutcomeReasonPairIdMismatchFormat",
        OutcomeReason.PairContentIdentifierPhotoOnly => "OutcomeReasonPairIdPhotoOnly",
        OutcomeReason.PairContentIdentifierVideoOnly => "OutcomeReasonPairIdVideoOnly",
        OutcomeReason.PairCaptureTimeClose => "OutcomeReasonPairTimeCloseFormat",
        OutcomeReason.PairCaptureTimeTooFar => "OutcomeReasonPairTimeTooFarFormat",
        OutcomeReason.PairCaptureTimePhotoOnly => "OutcomeReasonPairTimePhotoOnly",
        OutcomeReason.PairCaptureTimeVideoOnly => "OutcomeReasonPairTimeVideoOnly",
        OutcomeReason.PairDurationWithinLimit => "OutcomeReasonPairDurationFormat",
        OutcomeReason.PairVideoTooLong => "OutcomeReasonPairVideoTooLongFormat",
        OutcomeReason.PairNameOnly => "OutcomeReasonPairNameOnly",
        OutcomeReason.PairManuallyConfirmed => "OutcomeReasonPairManual",
        OutcomeReason.SourceMissingOrEmpty => "OutcomeReasonSourceMissingFormat",
        OutcomeReason.VideoConversionFailed => "OutcomeReasonVideoConversionFailed",
        OutcomeReason.HdrEncoderUnavailable => "OutcomeReasonHdrEncoderUnavailable",
        OutcomeReason.ToolMissing => "ToolMissingFormat",
        OutcomeReason.VerificationFailed => "OutcomeReasonVerificationFailed",
        _ => "OutcomeReasonUnexpected"
    };

    public static string NoteKey(OutcomeNoteKind kind) => kind switch
    {
        OutcomeNoteKind.UltraHdrWritten => "OutcomeNoteUltraHdrWritten",
        OutcomeNoteKind.HdrGainMapMissing => "OutcomeNoteHdrGainMapMissing",
        OutcomeNoteKind.HdrToneMapNotSupported => "OutcomeNoteHdrToneMapNotSupported",
        OutcomeNoteKind.HdrDecoderUnavailable => "OutcomeNoteHdrDecoderUnavailable",
        OutcomeNoteKind.HdrMetadataMissing => "OutcomeNoteHdrMetadataMissing",
        _ => "OutcomeNoteHdrConversionFailed"
    };

    public static string Describe(ILocalizer localizer, OutcomeCause cause) =>
        localizer.Format(ReasonKey(cause.Reason), [.. cause.Arguments]);

    /// <summary>多条原因各占一行，不依赖随语言变化的分隔符。</summary>
    public static string Describe(ILocalizer localizer, IReadOnlyList<OutcomeCause> causes) =>
        string.Join(Environment.NewLine, causes.Select(cause => Describe(localizer, cause)));

    public static string Describe(ILocalizer localizer, OutcomeNoteKind kind) => localizer[NoteKey(kind)];
}
