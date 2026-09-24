using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Tests.Support;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.E2E.Workflows;

/// <summary>
/// 黑盒端到端：真实外壳 + 真实外部工具，按用户的操作顺序点击界面（选相册 → 选动作 → 选输出目录 → 开始），
/// 只从磁盘上的结果判断成败。缺少所需工具时跳过。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class ConversionWorkflowTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly TestSandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    [AvaloniaFact]
    public async Task ToAndroid_ApplePairBecomesSingleMotionPhoto()
    {
        var exiftool = ExternalTools.RequireExifTool();
        var ffmpeg = ExternalTools.RequireFfmpeg();
        var photo = SampleAlbum.WriteJpeg(Path.Combine(_sandbox.InputDirectory, "IMG_0001.jpg"));
        var video = Path.Combine(_sandbox.InputDirectory, "IMG_0001.mov");
        // 配对校验要求两侧拍摄时间一致：照片带时区的本地时间，视频按 UTC 存储
        await GenerateVideoAsync(ffmpeg, video, "creation_time=2026-09-01T02:00:00Z");
        var tagged = await ProcessRunner.RunAsync(exiftool,
            ["-q", "-overwrite_original", "-DateTimeOriginal=2026:09:01 10:00:00", "-OffsetTimeOriginal=+08:00", photo], Token);
        Assert.True(tagged.Success, tagged.StandardError);

        using var session = new ShellSession();
        var report = await RunThroughUiAsync(session, ConversionAction.ToAndroid, expectedCards: 1);

        Assert.Equal((1, 0), (report.SuccessCount, report.FailedCount));
        var output = Assert.Single(Directory.GetFiles(_sandbox.OutputDirectory));
        var embedded = MotionPhotoLayout.Locate(output);
        Assert.NotNull(embedded);
        Assert.True(embedded.Length > 0);
        Assert.True(File.Exists(photo) && File.Exists(video), "默认“保留原片”时源文件必须原样保留");
    }

    [AvaloniaFact]
    public async Task ToApple_MotionPhotoBecomesPairedHeicAndMov()
    {
        var exiftool = ExternalTools.RequireExifTool();
        var ffmpeg = ExternalTools.RequireFfmpeg();
        RequireHeicEncoder();
        var source = await WriteMotionPhotoAsync(ffmpeg, "MVIMG_0001.jpg");

        using var session = new ShellSession();
        var report = await RunThroughUiAsync(session, ConversionAction.ToApple, expectedCards: 1);

        Assert.Equal((1, 0), (report.SuccessCount, report.FailedCount));
        var outputs = Directory.GetFiles(_sandbox.OutputDirectory).Order().ToList();
        Assert.Equal([".HEIC", ".MOV"], outputs.Select(o => Path.GetExtension(o).ToUpperInvariant()));
        Assert.True(IsHeic(outputs[0]), "还原的封面不是 HEIC");

        await using var metadata = ExifToolMetadataService.Create(exiftool);
        var tags = await metadata.ReadAsync(outputs, cancellationToken: Token);
        var identifier = tags[outputs[0]].ContentIdentifier;
        Assert.False(string.IsNullOrEmpty(identifier), "HEIC 没有写入配对 UUID");
        Assert.Equal(identifier, tags[outputs[1]].ContentIdentifier);
        Assert.True(File.Exists(source));
    }

    [AvaloniaFact]
    public async Task Extract_MotionPhotoSplitsIntoCoverAndLosslessVideo()
    {
        ExternalTools.RequireExifTool();
        var ffmpeg = ExternalTools.RequireFfmpeg();
        var source = await WriteMotionPhotoAsync(ffmpeg, "MVIMG_0002.jpg");
        var embedded = MotionPhotoLayout.Locate(source)!;

        using var session = new ShellSession();
        var report = await RunThroughUiAsync(session, ConversionAction.Extract, expectedCards: 1);

        Assert.Equal((1, 0), (report.SuccessCount, report.FailedCount));
        var mp4 = Assert.Single(Directory.GetFiles(_sandbox.OutputDirectory, "*.mp4"));
        Assert.Equal(ReadSegment(source, embedded.Offset, embedded.Length), await File.ReadAllBytesAsync(mp4, Token));
        var cover = Assert.Single(Directory.GetFiles(_sandbox.OutputDirectory), f => f != mp4);
        Assert.Null(MotionPhotoLayout.Locate(cover));
    }

    [AvaloniaFact]
    public async Task Strip_ExportsStillHeicAndKeepsOriginal()
    {
        ExternalTools.RequireExifTool();
        var ffmpeg = ExternalTools.RequireFfmpeg();
        RequireHeicEncoder();
        var source = await WriteMotionPhotoAsync(ffmpeg, "MVIMG_0003.jpg");
        var originalBytes = await File.ReadAllBytesAsync(source, Token);

        using var session = new ShellSession(settings: s => s.StripConvertToHeic = true);
        var report = await RunThroughUiAsync(session, ConversionAction.Strip, expectedCards: 1);

        Assert.Equal((1, 0), (report.SuccessCount, report.FailedCount));
        var output = Assert.Single(Directory.GetFiles(_sandbox.OutputDirectory));
        Assert.True(IsHeic(output), "瘦身输出不是 HEIC");
        Assert.Null(MotionPhotoLayout.Locate(output));
        Assert.True(new FileInfo(output).Length < originalBytes.Length);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(source, Token));
    }

    /// <summary>用户视角的完整操作：每一步都点击真实按钮，最后等任务中心出现报告。</summary>
    private async Task<TaskReportViewModel> RunThroughUiAsync(ShellSession session, ConversionAction action, int expectedCards)
    {
        var shell = session.Shell;
        var picker = session.Host.FilePicker;

        picker.NextResult = _sandbox.InputDirectory;
        session.Click(FirstVisibleButton(session, shell.Library.SelectAlbumFolderCommand));
        await session.WaitUntilAsync(() => shell.Library is { IsScanning: false, HasScannedFiles: true }, 30);

        session.Click(session.Descendants<Button>()
            .Single(b => ReferenceEquals(b.Command, shell.Inspector.SetActionCommand) && (string?)b.CommandParameter == action.ToString()));
        // 切换动作会按新扫描模式重新扫描
        await session.WaitUntilAsync(() => shell.Inspector.ApplicableCount == expectedCards, 30);

        picker.NextResult = _sandbox.OutputDirectory;
        var chooseOutput = action == ConversionAction.Strip ? shell.Inspector.SelectStripFolderCommand : shell.Inspector.SelectOutputFolderCommand;
        session.Click(FirstVisibleButton(session, chooseOutput));

        var tasks = session.Host.Get<TaskCenter>();
        session.Click(FirstVisibleButton(session, shell.Inspector.StartCommand));
        await session.WaitUntilAsync(() => tasks.History.Count == 1, 120);

        Assert.Null(session.Dialogs.Current);
        Assert.Equal(AppPage.Tasks, shell.CurrentPage);
        session.Log.AssertNoBindingErrors();
        var report = tasks.History[0];
        Assert.False(report.IsFatal, report.ErrorMessage);
        Assert.True(report.FailedCount == 0 && report.SkippedCount == 0,
            string.Join("\n", report.Items.Select(i => $"{i.Status} {i.FileName}: {i.Detail}")));
        return report;
    }

    private static Button FirstVisibleButton(ShellSession session, ICommand command) =>
        session.Descendants<Button>().First(b => ReferenceEquals(b.Command, command) && b.IsEffectivelyVisible);

    private async Task<string> WriteMotionPhotoAsync(string ffmpeg, string fileName)
    {
        var cover = SampleAlbum.WriteJpeg(Path.Combine(_sandbox.RootDirectory, "cover.jpg"), 4, 640, 480);
        var video = Path.Combine(_sandbox.RootDirectory, "clip.mp4");
        await GenerateVideoAsync(ffmpeg, video);
        var bytes = SyntheticMedia.MotionPhoto(await File.ReadAllBytesAsync(cover, Token), await File.ReadAllBytesAsync(video, Token));
        return _sandbox.CreateInputFile(fileName, bytes);
    }

    private static async Task GenerateVideoAsync(string ffmpeg, string path, string? metadata = null)
    {
        List<string> args =
        [
            "-nostdin", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=160x120:rate=30", "-t", "1", "-pix_fmt", "yuv420p"
        ];
        if (metadata is not null)
        {
            args.AddRange(["-metadata", metadata]);
        }

        args.Add(path);
        var result = await ProcessRunner.RunAsync(ffmpeg, args, Token);
        Assert.True(result.Success, result.StandardError);
    }

    /// <summary>按文件头判断，不信任扩展名。</summary>
    private static bool IsHeic(string path)
    {
        Span<byte> header = stackalloc byte[12];
        using (var stream = File.OpenRead(path))
        {
            stream.ReadExactly(header);
        }

        return header[4..8].SequenceEqual("ftyp"u8)
            && (header[8..12].SequenceEqual("heic"u8) || header[8..12].SequenceEqual("heix"u8) || header[8..12].SequenceEqual("mif1"u8));
    }

    private static byte[] ReadSegment(string path, long offset, long length)
    {
        using var stream = File.OpenRead(path);
        stream.Position = offset;
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static void RequireHeicEncoder()
    {
        if (!MagickImageConverter.SupportsHeicEncoding && ToolLocator.Find(HeifEncImageConverter.ExecutableName) is null)
        {
            Assert.Skip("没有可用的 HEIC 编码器（heif-enc 或带 HEIC 编码的 Magick.NET）。");
        }
    }
}
