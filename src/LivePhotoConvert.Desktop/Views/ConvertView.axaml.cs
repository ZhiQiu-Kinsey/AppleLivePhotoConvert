using Avalonia.Controls;
using LivePhotoConvert.Desktop.ViewModels;

namespace LivePhotoConvert.Desktop.Views;

public partial class ConvertView : UserControl
{
    public ConvertView()
    {
        InitializeComponent();
        // 单层扁平化流：监听视口宽度变化，动态计算卡片宽度
        Loaded += (_, _) =>
        {
            var listBox = this.FindControl<ListBox>("GalleryListBox");
            if (listBox is not null && DataContext is ConvertViewModel vm)
            {
                vm.UpdateCardWidth(listBox.Bounds.Width);
                listBox.PropertyChanged += (_, e) =>
                {
                    if (e.Property.Name == "Bounds" && DataContext is ConvertViewModel vm2)
                    {
                        vm2.UpdateCardWidth(listBox.Bounds.Width);
                    }
                };
            }
        };
    }
}
