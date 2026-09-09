namespace LivePhotoConvert.Desktop.Models;

/// <summary>
/// “关于”页面的静态元数据：项目身份、链接与开源鸣谢清单。
/// 数据集中在此处，避免散落到视图或业务逻辑中；描述文本仅保存本地化键，
/// 由调用方通过 <see cref="Services.LocalizationService"/> 解析为当前语言。
/// </summary>
public static class AboutInfo
{
    /// <summary>项目主仓库地址。</summary>
    public const string RepositoryUrl = "https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert";

    /// <summary>Issue 反馈地址。</summary>
    public const string IssuesUrl = RepositoryUrl + "/issues";

    /// <summary>Pull Request 地址。</summary>
    public const string PullRequestsUrl = RepositoryUrl + "/pulls";

    /// <summary>版本发布与下载页面。</summary>
    public const string ReleasesUrl = RepositoryUrl + "/releases";

    /// <summary>许可证全文地址。</summary>
    public const string LicenseUrl = RepositoryUrl + "/blob/main/LICENSE";

    /// <summary>项目主分支许可证标识。</summary>
    public const string LicenseName = "MIT License";

    /// <summary>作者 / 维护者展示名。</summary>
    public const string AuthorName = "Kinsey.Qiu";

    /// <summary>作者主页。</summary>
    public const string AuthorUrl = "https://github.com/ZhiQiu-Kinsey";

    /// <summary>运行时调用外部可执行文件（ExifTool / FFmpeg / heif-enc）之外的内置依赖。</summary>
    public static readonly CreditEntry[] RuntimeCredits =
    [
        new("ExifTool", "CreditExifToolDesc", "https://exiftool.org/", "Artistic-2.0"),
        new("FFmpeg", "CreditFfmpegDesc", "https://ffmpeg.org/", "LGPL-2.1+"),
        new("libheif / x265", "CreditLibheifDesc", "https://github.com/strukturag/libheif", "LGPL-3.0"),
        new("Magick.NET", "CreditMagickDesc", "https://github.com/dlemstra/Magick.NET", "Apache-2.0")
    ];

    /// <summary>构建期依赖的框架与组件。</summary>
    public static readonly CreditEntry[] BuildCredits =
    [
        new(".NET 10", "CreditDotNetDesc", "https://dotnet.microsoft.com/", "MIT"),
        new("Avalonia UI", "CreditAvaloniaDesc", "https://avaloniaui.net/", "MIT"),
        new("CommunityToolkit.Mvvm", "CreditToolkitDesc", "https://github.com/CommunityToolkit/dotnet", "MIT"),
        new("FluentIcons.Avalonia", "CreditFluentIconsDesc", "https://github.com/davidxuang/FluentIcons", "MIT"),
        new("Google Motion Photo", "CreditMotionPhotoSpecDesc",
            "https://developer.android.com/media/platform/motion-photo-format", "Spec")
    ];

    /// <summary>核心贡献者（含维护者）。</summary>
    public static readonly CreditEntry[] Contributors =
    [
        new(AuthorName, "CreditContributorAuthorDesc", AuthorUrl, "Author")
    ];

    /// <summary>
    /// 一条未本地化的鸣谢原始条目，由视图模型解析为 <see cref="AboutCredit"/>。
    /// </summary>
    /// <param name="Name">展示名称（专有名词，不参与本地化）。</param>
    /// <param name="DescriptionKey">描述文本的本地化键。</param>
    /// <param name="Url">点击时打开的外部链接。</param>
    /// <param name="Badge">徽标文本（许可证名、角色或规范标识）。</param>
    public readonly record struct CreditEntry(string Name, string DescriptionKey, string Url, string Badge);
}
