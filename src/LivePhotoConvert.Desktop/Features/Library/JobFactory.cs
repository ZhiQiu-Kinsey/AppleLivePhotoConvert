using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Features.Library;

/// <summary>动作运行需要的外部能力。</summary>
public enum RequiredTool
{
    ExifTool,
    Ffmpeg,

    /// <summary>heif-enc，或带 HEIC 编码器的 Magick。</summary>
    HeicEncoder
}

/// <summary>
/// 把图库卡片与检查器参数固定成 <see cref="ConversionJob"/>；参数一律取自设置，检查器的改动已即时写入设置。
/// </summary>
public static class JobFactory
{
    public static string DefaultOutputDirectory => DefaultUnderPictures("LivePhotoConverted", "output");

    public static string DefaultStripDirectory => DefaultUnderPictures("LivePhotoStripped", "stripped");

    /// <summary>卡片能否参与该动作：合成只接受苹果实况对，还原与解包只接受安卓动态照片，瘦身两者都接受。</summary>
    public static bool IsApplicable(ConversionAction action, PhotoCardItemViewModel card) => action switch
    {
        ConversionAction.ToAndroid => IsApplePair(card),
        ConversionAction.ToApple or ConversionAction.Extract => card.IsMotionPhoto,
        ConversionAction.Strip => card.IsMotionPhoto || IsApplePair(card),
        _ => false
    };

    public static List<PhotoCardItemViewModel> Applicable(ConversionAction action, IEnumerable<PhotoCardItemViewModel> cards) =>
        [.. cards.Where(c => IsApplicable(action, c))];

    /// <summary>
    /// 卡片源文件的磁盘字节数。动态照片只计照片本身：播放时切出的临时视频不属于源文件。
    /// </summary>
    public static long SourceBytes(IEnumerable<PhotoCardItemViewModel> cards)
    {
        long sum = 0;
        foreach (var card in cards)
        {
            sum += SafeFileLength(card.PhotoPath);
            if (!card.IsMotionPhoto)
            {
                sum += SafeFileLength(card.VideoPath);
            }
        }

        return sum;
    }

    /// <summary>与 <see cref="ConversionRunner"/> 创建的引擎一一对应。</summary>
    public static IReadOnlyList<RequiredTool> RequiredTools(ConversionAction action, bool stripConvertToHeic) => action switch
    {
        ConversionAction.ToAndroid => [RequiredTool.ExifTool, RequiredTool.Ffmpeg],
        ConversionAction.ToApple => [RequiredTool.ExifTool, RequiredTool.Ffmpeg, RequiredTool.HeicEncoder],
        ConversionAction.Strip when stripConvertToHeic => [RequiredTool.ExifTool, RequiredTool.HeicEncoder],
        _ => [RequiredTool.ExifTool]
    };

    public static string ResolveOutputDirectory(DesktopSettings settings) =>
        string.IsNullOrWhiteSpace(settings.OutputDirectory) ? DefaultOutputDirectory : settings.OutputDirectory;

    public static string ResolveStripDirectory(DesktopSettings settings) =>
        string.IsNullOrWhiteSpace(settings.StripOutputDirectory) ? DefaultStripDirectory : settings.StripOutputDirectory;

    /// <summary>瘦身就地替换时写回相册目录，其余情况写到输出目录。</summary>
    public static string ResolveTargetDirectory(ConversionAction action, DesktopSettings settings, string? albumDirectory) => action switch
    {
        ConversionAction.Strip when settings.InPlaceStrip => albumDirectory ?? string.Empty,
        ConversionAction.Strip => ResolveStripDirectory(settings),
        _ => ResolveOutputDirectory(settings)
    };

    /// <param name="action">动作</param>
    /// <param name="cards">候选卡片；不适用于该动作的卡片会被忽略</param>
    /// <param name="settings">参数来源</param>
    /// <param name="albumDirectory">图库目录：保留子目录层级的根，也是就地瘦身的位置</param>
    public static ConversionJob Build(ConversionAction action, IReadOnlyList<PhotoCardItemViewModel> cards, DesktopSettings settings, string? albumDirectory)
    {
        var applicable = Applicable(action, cards);
        var album = string.IsNullOrWhiteSpace(albumDirectory) ? null : albumDirectory;
        var heicQuality = settings.HeicQuality > 0 ? settings.HeicQuality : ConversionDefaults.HeicQuality;

        OutputOptions Output(string directory) => new(directory)
        {
            Conflict = settings.ConflictPolicy,
            PreserveHierarchyFrom = settings.KeepSubfolderHierarchy ? album : null
        };

        ConversionInputs inputs;
        if (action == ConversionAction.ToAndroid)
        {
            var pairs = applicable.Select(c => (Card: c, Pair: c.Pair ?? new MediaPair(c.PhotoPath, c.VideoPath!))).ToList();
            inputs = new ConversionInputs
            {
                Pairs = [.. pairs.Select(x => x.Pair)],
                ForceAccepted = [.. pairs.Where(x => x.Card.IsForceAccepted).Select(x => x.Pair)]
            };
        }
        else
        {
            inputs = new ConversionInputs { Files = [.. applicable.Select(c => c.PhotoPath)] };
        }

        var options = action == ConversionAction.Strip
            ? new ConversionOptions
            {
                Output = settings.InPlaceStrip ? null : Output(ResolveStripDirectory(settings)),
                InPlaceLocation = settings.InPlaceStrip ? album : null,
                ConvertToHeic = settings.StripConvertToHeic,
                HeicQuality = heicQuality
            }
            : new ConversionOptions
            {
                Output = Output(ResolveOutputDirectory(settings)),
                Naming = (MergeNamingFormat)settings.NamingFormat,
                SourceAction = (SourceFileAction)settings.SourceAction,
                HeicQuality = heicQuality
            };

        return new ConversionJob(action, options, inputs)
        {
            Tools = ToolPaths.From(settings),
            Parallelism = Math.Clamp(settings.Concurrency, 1, 8)
        };
    }

    private static bool IsApplePair(PhotoCardItemViewModel card) => !card.IsMotionPhoto && !string.IsNullOrEmpty(card.VideoPath);

    private static long SafeFileLength(string? path)
    {
        try
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    private static string DefaultUnderPictures(string folder, string fallback)
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return !string.IsNullOrEmpty(pictures)
            ? Path.Combine(pictures, folder)
            : Path.Combine(AppContext.BaseDirectory, fallback);
    }
}
