using Avalonia.Controls;

namespace LivePhotoConvert.Desktop.Controls;

public partial class PhotoCardControl : UserControl
{
    public PhotoCardControl()
    {
        InitializeComponent();
    }

    protected override void OnPointerEntered(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (DataContext is Models.PhotoCardItemViewModel card)
        {
            Services.PlaybackHost.Instance.OnPointerEnter(card);
        }
    }

    protected override void OnPointerExited(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (DataContext is Models.PhotoCardItemViewModel card)
        {
            Services.PlaybackHost.Instance.OnPointerLeave(card);
        }
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount == 2 && DataContext is Models.PhotoCardItemViewModel card)
        {
            card.RequestQuickLook();
            e.Handled = true;
        }
    }
}
