using Avalonia.Headless.XUnit;
using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Library;

/// <summary>各选择器从上次选择的目录开始：相册、输出目录、瘦身导出目录与工具可执行文件。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class PickerStartLocationTests : IDisposable
{
    private readonly TestSandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    [AvaloniaFact]
    public async Task AlbumPicker_StartsFromRememberedAlbum_AndFromTheNewChoiceAfterwards()
    {
        var other = Directory.CreateDirectory(Path.Combine(_sandbox.RootDirectory, "other")).FullName;
        using var session = new ShellSession(settings: s => s.LastScanDirectory = _sandbox.InputDirectory);
        var library = session.Shell.Library;
        var picker = session.Host.FilePicker;

        picker.NextResult = other;
        await library.SelectAlbumFolderCommand.ExecuteAsync(null);
        picker.NextResult = null;
        await library.SelectAlbumFolderCommand.ExecuteAsync(null);

        Assert.Equal([_sandbox.InputDirectory, other], picker.StartLocations);
        Assert.Equal(other, session.Host.Settings.Current.LastScanDirectory);
    }

    [AvaloniaFact]
    public async Task OutputPickers_StartFromTheirLastChoice()
    {
        var output = Path.Combine(_sandbox.RootDirectory, "out");
        var strip = Path.Combine(_sandbox.RootDirectory, "slim");
        using var session = new ShellSession(settings: s =>
        {
            s.OutputDirectory = _sandbox.OutputDirectory;
            s.StripOutputDirectory = _sandbox.OutputDirectory;
        });
        var inspector = session.Shell.Inspector;
        var picker = session.Host.FilePicker;

        picker.NextResult = output;
        await inspector.SelectOutputFolderCommand.ExecuteAsync(null);
        await inspector.SelectOutputFolderCommand.ExecuteAsync(null);
        picker.NextResult = strip;
        await inspector.SelectStripFolderCommand.ExecuteAsync(null);
        await inspector.SelectStripFolderCommand.ExecuteAsync(null);

        Assert.Equal([_sandbox.OutputDirectory, output, _sandbox.OutputDirectory, strip], picker.StartLocations);
        Assert.Equal(output, session.Host.Settings.Current.OutputDirectory);
        Assert.Equal(strip, session.Host.Settings.Current.StripOutputDirectory);
    }

    [AvaloniaFact]
    public async Task ToolPicker_StartsFromThatToolsPath_ElseAnotherConfiguredTool()
    {
        var exifTool = Path.Combine(_sandbox.RootDirectory, "tools", "exiftool");
        using var session = new ShellSession();
        var tools = session.Shell.Tools;
        var picker = session.Host.FilePicker;

        await tools.PickPathAsync(ToolId.Ffmpeg);
        session.Host.Get<ToolPathSettings>().Set(ToolId.ExifTool, exifTool);
        await tools.PickPathAsync(ToolId.Ffmpeg);
        await tools.PickPathAsync(ToolId.ExifTool);

        Assert.Equal([null, exifTool, exifTool], picker.StartLocations);
    }

    [Fact]
    public void NearestExistingDirectory_WalksUpFromMissingFoldersAndFiles()
    {
        var file = _sandbox.CreateInputFile("a.jpg", [1]);
        Assert.Equal(_sandbox.InputDirectory, FilePicker.NearestExistingDirectory(_sandbox.InputDirectory));
        Assert.Equal(_sandbox.InputDirectory, FilePicker.NearestExistingDirectory(file));
        Assert.Equal(_sandbox.OutputDirectory, FilePicker.NearestExistingDirectory(Path.Combine(_sandbox.OutputDirectory, "not", "yet", "created")));
    }
}
