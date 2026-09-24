using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Tests.Support;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Features.Library;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Tasks;

/// <summary>「保留 HDR」开关：检查器 → 设置 → 任务 → 合成引擎的增益图解码器 → 报告中的 Ultra HDR 附注。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class PreserveHdrTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly TestSandbox _album = new();
    private readonly FakeEngines _engines = new();
    private readonly FakeGainMapDecoder _decoder = new();

    public PreserveHdrTests() => _engines.GainMapDecoder = _decoder;

    public void Dispose() => _album.Dispose();

    [Fact]
    public void Inspector_PreserveHdr_DefaultsOnAndIsPersisted()
    {
        using var fixture = new InspectorFixture();
        Assert.True(fixture.Inspector.PreserveHdr);
        Assert.True(new DesktopSettings().PreserveHdr);

        fixture.Inspector.PreserveHdr = false;
        fixture.Host.Settings.Flush();

        using var reloaded = new SettingsStore(fixture.Host.SettingsPath);
        Assert.False(reloaded.Current.PreserveHdr);
        using var restarted = new InspectorFixture(settingsPath: fixture.Host.SettingsPath);
        Assert.False(restarted.Inspector.PreserveHdr);
    }

    [Fact]
    public void OldSettingsFile_WithoutField_KeepsHdr()
    {
        var path = Path.Combine(_album.RootDirectory, "settings.json");
        File.WriteAllText(path, $$"""{ "schemaVersion": {{SettingsStore.CurrentSchemaVersion}}, "language": "en" }""");

        using var store = new SettingsStore(path);

        Assert.True(store.Current.PreserveHdr);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void JobFactory_CarriesPreserveHdrFromSettings(bool preserve)
    {
        var job = JobFactory.Build(ConversionAction.ToAndroid, [Cards.ApplePair("IMG_1")], new DesktopSettings { PreserveHdr = preserve }, "/album");

        Assert.Equal(preserve, job.Options.PreserveHdr);
    }

    [Fact]
    public async Task PreserveHdrOn_PassesGainMapDecoderToMerger_AndReportShowsUltraHdrNote()
    {
        var (card, photo) = HdrPair();
        using var fixture = new TaskCenterFixture(new ConversionRunner(_engines));
        var job = JobFactory.Build(ConversionAction.ToAndroid, [card], Settings(preserve: true), _album.InputDirectory);

        var report = await fixture.Center.RunAsync(job).Within(30);

        Assert.Equal(1, _engines.GainMapDecodersCreated);
        Assert.Equal([photo], _decoder.Decoded);
        var item = Assert.Single(report.Items);
        Assert.Contains(new TaskReportTag(fixture.Host.Localizer["OutcomeNoteUltraHdrWritten"], null, true), item.Tags);
        var output = Assert.Single(Directory.GetFiles(_album.OutputDirectory, "*.jpg", SearchOption.AllDirectories));
        Assert.True(MotionPhotoLayout.Inspect(output).HasGainMap);
    }

    [Fact]
    public async Task PreserveHdrOff_DoesNotLookForDecoder_AndWritesSdrCover()
    {
        var (card, photo) = HdrPair();
        var runner = new ConversionRunner(_engines);
        var job = JobFactory.Build(ConversionAction.ToAndroid, [card], Settings(preserve: false), _album.InputDirectory);

        var report = await runner.RunAsync(job, null, Token);

        Assert.Equal(0, _engines.GainMapDecodersCreated);
        Assert.Empty(_decoder.Decoded);
        var outcome = Assert.Single(report.Items);
        Assert.Equal(OutcomeKind.Succeeded, outcome.Kind);
        Assert.Empty(outcome.Notes);
        Assert.Contains(photo, _engines.Images.JpegConversions);
        Assert.False(MotionPhotoLayout.Inspect(Assert.Single(outcome.Outputs)).HasGainMap);
    }

    /// <summary>检查器上的开关只在「转安卓」下出现，点击即写入设置。</summary>
    [AvaloniaFact]
    public void InspectorSwitch_IsShownForToAndroidOnly_AndSavesTheChoice()
    {
        using var session = new ShellSession();
        var inspector = session.Shell.Inspector;
        var toggle = session.Descendants<InspectorView>().Single().GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "PreserveHdrSwitch");
        Assert.True(toggle.IsEffectivelyVisible);
        Assert.True(toggle.IsChecked);

        session.Click(toggle);
        Assert.False(inspector.PreserveHdr);
        Assert.False(session.Host.Settings.Current.PreserveHdr);

        inspector.SetActionCommand.Execute(nameof(ConversionAction.ToApple));
        session.Pump();
        Assert.False(toggle.IsEffectivelyVisible);
        session.Log.AssertNoBindingErrors();
    }

    private DesktopSettings Settings(bool preserve) => new()
    {
        PreserveHdr = preserve,
        OutputDirectory = _album.OutputDirectory,
        KeepSubfolderHierarchy = false
    };

    /// <summary>带 Apple 增益图标记与余量元数据的 HEIC 实况对（元数据由内存引擎提供）。</summary>
    private (Desktop.Models.PhotoCardItemViewModel Card, string Photo) HdrPair()
    {
        var photo = _album.CreateInputFile("IMG_0001.HEIC", SyntheticMedia.Heic());
        var video = _album.CreateInputFile("IMG_0001.MP4", SyntheticMedia.Mp4(5000));
        _engines.Metadata.Set(photo, new MediaMetadata
        {
            Path = photo,
            HasAppleGainMap = true,
            HdrGainMapVersion = 65536,
            AppleHdrHeadroom = 1.0255059,
            AppleHdrGain = 0.001685693743
        });
        var item = new LibraryItem(LibraryItemKind.ApplePair, Cards.File(photo, new FileInfo(photo).Length))
        {
            Video = Cards.File(video, new FileInfo(video).Length),
            PairCandidates = [new MediaPair(photo, video)],
            PairValidation = PairValidationResult.Accept()
        };
        return (Cards.Of(item), photo);
    }
}
