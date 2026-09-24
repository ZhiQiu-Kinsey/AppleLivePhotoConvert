using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Library;

/// <summary>拖入文件夹打开相册：条目解析规则，以及真实外壳中的提示层与落盘到设置。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class AlbumDropTests : IDisposable
{
    private readonly TestSandbox _album = new();

    public void Dispose() => _album.Dispose();

    [Fact]
    public void Resolve_TakesFirstFolder_ElseDirectoryOfFirstFile()
    {
        var a = Path.Combine(_album.RootDirectory, "a");
        var b = Path.Combine(_album.RootDirectory, "b");
        var file = Path.Combine(_album.RootDirectory, "c", "IMG_0001.jpg");

        Assert.Equal(a, AlbumDrop.Resolve([(file, false), (a, true), (b, true)]));
        Assert.Equal(Path.GetDirectoryName(file), AlbumDrop.Resolve([(file, false), (Path.Combine(b, "x.jpg"), false)]));
        Assert.Equal(b, AlbumDrop.Resolve([("", true), (b, true)]));
        Assert.Null(AlbumDrop.Resolve([]));
        Assert.Null(AlbumDrop.Resolve((IDataTransfer?)null));
    }

    [AvaloniaFact]
    public async Task DraggingFolder_ShowsOverlay_AndDropOpensItLikeThePicker()
    {
        SampleAlbum.WriteApplePairs(_album.InputDirectory, 2);
        using var session = new ShellSession();
        var library = session.Shell.Library;
        var view = session.Descendants<LibraryView>().Single();
        var overlay = view.FindControl<Border>("DropOverlay")!;
        var point = Center(session, view);
        var data = Transfer(await Folder(session, _album.InputDirectory), await Folder(session, _album.OutputDirectory));

        session.Window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Copy | DragDropEffects.Link, RawInputModifiers.None);
        session.Pump();
        Assert.True(overlay.IsVisible);
        Assert.Equal(_album.InputDirectory, view.FindControl<TextBlock>("DropOverlayPath")!.Text);
        Assert.Equal(session.Localizer["DropOverlayTitle"], overlay.GetVisualDescendants().OfType<TextBlock>().First().Text);
        Screenshots.Save(session, "library-drop-overlay");

        session.Window.DragDrop(point, RawDragEventType.DragLeave, data, DragDropEffects.Copy | DragDropEffects.Link, RawInputModifiers.None);
        session.Pump();
        Assert.False(overlay.IsVisible);

        session.Window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
        session.Window.DragDrop(point, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
        session.Pump();
        Assert.False(overlay.IsVisible);
        Assert.Equal(_album.InputDirectory, library.AlbumDirectory);
        Assert.Equal(_album.InputDirectory, session.Host.Settings.Current.LastScanDirectory);
        await session.WaitUntilAsync(() => library.AllCards.Count == 2);

        // 下次打开选择器从拖入的目录开始
        await library.SelectAlbumFolderCommand.ExecuteAsync(null);
        Assert.Equal([_album.InputDirectory], session.Host.FilePicker.StartLocations);
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaFact]
    public async Task DroppingFile_OpensItsFolder()
    {
        var photo = SampleAlbum.WriteJpeg(Path.Combine(_album.InputDirectory, "IMG_0001.jpg"));
        using var session = new ShellSession();
        var view = session.Descendants<LibraryView>().Single();
        var data = Transfer(await FileItem(session, photo));

        session.Window.DragDrop(Center(session, view), RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
        session.Window.DragDrop(Center(session, view), RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
        session.Pump();
        Assert.Equal(_album.InputDirectory, session.Shell.Library.AlbumDirectory);
        await session.WaitUntilAsync(() => !session.Shell.Library.IsScanning);
    }

    /// <summary>没有本地文件（如拖入的文字）不显示提示层，也不改变相册；弹窗打开时不接受拖放。</summary>
    [AvaloniaFact]
    public async Task NonFileDataOrOpenDialog_IsRefused()
    {
        using var session = new ShellSession();
        var library = session.Shell.Library;
        var view = session.Descendants<LibraryView>().Single();
        var overlay = view.FindControl<Border>("DropOverlay")!;
        var text = new DataTransfer();
        text.Add(DataTransferItem.CreateText("hello"));
        Assert.True(view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == session.Localizer["EmptyStateDropHint"]).IsEffectivelyVisible);
        Screenshots.Save(session, "library-empty-drop-hint");

        session.Window.DragDrop(Center(session, view), RawDragEventType.DragEnter, text, DragDropEffects.Copy, RawInputModifiers.None);
        session.Pump();
        Assert.False(overlay.IsVisible);
        session.Window.DragDrop(Center(session, view), RawDragEventType.Drop, text, DragDropEffects.Copy, RawInputModifiers.None);
        Assert.Equal(string.Empty, library.AlbumDirectory);

        _ = session.Dialogs.ShowAsync(new ConfirmDialogViewModel { Title = "t", Message = "m", ConfirmText = "ok", CancelText = "cancel" });
        session.Pump();
        var folder = Transfer(await Folder(session, _album.InputDirectory));
        session.Window.DragDrop(Center(session, view), RawDragEventType.DragEnter, folder, DragDropEffects.Copy, RawInputModifiers.None);
        session.Window.DragDrop(Center(session, view), RawDragEventType.Drop, folder, DragDropEffects.Copy, RawInputModifiers.None);
        session.Pump();
        Assert.False(overlay.IsVisible);
        Assert.Equal(string.Empty, library.AlbumDirectory);
    }

    private static Point Center(ShellSession session, Control control)
    {
        session.Pump();
        return control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), session.Window)!.Value;
    }

    /// <summary>用窗口的存储提供者取得真实条目：IStorageItem 不允许由用户代码实现。</summary>
    private static async Task<IStorageItem> Folder(ShellSession session, string path) =>
        await session.Window.StorageProvider.TryGetFolderFromPathAsync(path) ?? throw new InvalidOperationException("存储提供者不支持本地目录。");

    private static async Task<IStorageItem> FileItem(ShellSession session, string path) =>
        await session.Window.StorageProvider.TryGetFileFromPathAsync(path) ?? throw new InvalidOperationException("存储提供者不支持本地文件。");

    private static DataTransfer Transfer(params IStorageItem[] items)
    {
        var data = new DataTransfer();
        foreach (var item in items)
        {
            data.Add(DataTransferItem.CreateFile(item));
        }

        return data;
    }
}
