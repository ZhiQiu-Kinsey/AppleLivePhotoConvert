namespace LivePhotoConvert.Core.Models;

/// <summary>
/// 合成成功后对输入目录中【已匹配】原始文件的处理方式
/// </summary>
public enum SourceFileAction
{
    /// <summary>
    /// 保留原始文件（默认）
    /// </summary>
    Keep = 0,

    /// <summary>
    /// 移动到输入目录下的子文件夹
    /// </summary>
    Move = 1,

    /// <summary>
    /// 删除到回收站（仅 Windows）
    /// </summary>
    Recycle = 2,

    /// <summary>
    /// 永久删除（不可恢复）
    /// </summary>
    Delete = 3
}
