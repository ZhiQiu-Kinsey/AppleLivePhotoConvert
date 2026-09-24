using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LivePhotoConvert.Desktop.Views.Dialogs;

public partial class DeleteConfirmDialog : UserControl
{
    public DeleteConfirmDialog()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        PasswordBox.Focus();
    }
}
