using System.Globalization;
using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Tools;

/// <summary>安装进度与失败原因的界面文案。</summary>
public static class ToolTexts
{
    public static string FailureKind(ILocalizer localizer, ToolFailureKind kind) => kind switch
    {
        ToolFailureKind.Unverified => localizer["ToolFailureUnverified"],
        ToolFailureKind.Network => localizer["ToolFailureNetwork"],
        ToolFailureKind.Stalled => localizer["ToolFailureStalled"],
        ToolFailureKind.IntegrityMismatch => localizer["ToolFailureIntegrity"],
        ToolFailureKind.UnsafeArchive => localizer["ToolFailureUnsafeArchive"],
        ToolFailureKind.InvalidArchive => localizer["ToolFailureInvalidArchive"],
        ToolFailureKind.ProbeFailed => localizer["ToolFailureProbe"],
        _ => localizer["ToolFailureCommit"]
    };

    /// <summary>下载源名称；经加速前缀时附上代理主机名，便于用户判断是哪个节点出了问题。</summary>
    public static string Source(ILocalizer localizer, string nameKey, string? mirrorPrefix)
    {
        var name = localizer[nameKey];
        return mirrorPrefix is not null && Uri.TryCreate(mirrorPrefix, UriKind.Absolute, out var mirror)
            ? localizer.Format("ToolSourceViaFormat", name, mirror.Host)
            : name;
    }

    public static string Source(ILocalizer localizer, ToolDownloadCandidate candidate) =>
        Source(localizer, candidate.Package.NameKey, candidate.MirrorPrefix);

    public static string Stage(ILocalizer localizer, ToolInstallProgress progress) => progress.Stage switch
    {
        ToolInstallStage.Downloading => localizer.Format("ToolStageDownloadingFormat", Source(localizer, progress.Candidate), Transfer(progress)),
        ToolInstallStage.Verifying => localizer["ToolStageVerifying"],
        ToolInstallStage.Extracting => localizer["ToolStageExtracting"],
        ToolInstallStage.Probing => localizer["ToolStageProbing"],
        ToolInstallStage.Committing => localizer["ToolStageCommitting"],
        _ => localizer.Format(
            "ToolStageSourceFailedFormat",
            Source(localizer, progress.Candidate),
            progress.Failure is { } failure ? FailureKind(localizer, failure.Kind) : localizer["ToolFailureNetwork"])
    };

    /// <summary>每个失败源一行："源：原因"。</summary>
    public static string Failures(ILocalizer localizer, IToolInstaller installer, IReadOnlyList<ToolSourceFailure> failures) =>
        string.Join('\n', failures
            .Select(f => localizer.Format("ToolSourceFailureFormat", Source(localizer, f.NameKey, MirrorOf(installer, f)), FailureKind(localizer, f.Kind)))
            .Distinct());

    /// <summary>失败记录只带实际地址；与清单地址主机不同即说明走了加速前缀。</summary>
    private static string? MirrorOf(IToolInstaller installer, ToolSourceFailure failure)
    {
        var package = installer.Manifest.Tools.SelectMany(t => t.Packages).FirstOrDefault(p => p.Id == failure.PackageId);
        if (package is null || !Uri.TryCreate(package.Url, UriKind.Absolute, out var original)
            || string.Equals(original.Host, failure.Url.Host, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"{failure.Url.Scheme}://{failure.Url.Host}/";
    }

    private static string Transfer(ToolInstallProgress progress)
    {
        const double Mb = 1024.0 * 1024.0;
        var downloaded = (progress.DownloadedBytes / Mb).ToString("F1", CultureInfo.InvariantCulture);
        var text = progress.TotalBytes is { } total and > 0
            ? $"{downloaded} / {(total / Mb).ToString("F1", CultureInfo.InvariantCulture)} MB"
            : $"{downloaded} MB";
        return progress.BytesPerSecond > 0
            ? $"{text} · {(progress.BytesPerSecond / Mb).ToString("F1", CultureInfo.InvariantCulture)} MB/s"
            : text;
    }
}
