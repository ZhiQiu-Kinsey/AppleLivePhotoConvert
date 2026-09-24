using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LivePhotoConvert.Desktop.Assets;

// 带 x:Class 的字典由 XAML 编译器生成填充代码，可直接 new 出实例，无需运行期按 URI 反射加载。
internal sealed partial class EnUsStrings : ResourceDictionary
{
    public EnUsStrings() => AvaloniaXamlLoader.Load(this);
}
