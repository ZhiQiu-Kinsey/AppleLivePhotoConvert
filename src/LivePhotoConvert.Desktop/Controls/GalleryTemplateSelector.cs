using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Controls;

/// <summary>
/// 扁平化相册流的编译期数据模板选择器
/// </summary>
public sealed class GalleryTemplateSelector : IDataTemplate
{
    [Content]
    public Dictionary<string, IDataTemplate> Templates { get; } = [];

    public IDataTemplate? HeaderTemplate { get; set; }
    public IDataTemplate? CardTemplate { get; set; }

    public Control? Build(object? param)
    {
        return param switch
        {
            TimelineHeaderItemViewModel => HeaderTemplate?.Build(param),
            PhotoCardItemViewModel => CardTemplate?.Build(param),
            _ => null
        };
    }

    public bool Match(object? data)
    {
        return data is IGalleryDisplayItem;
    }
}
