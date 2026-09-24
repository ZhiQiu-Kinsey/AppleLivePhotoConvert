using System.Globalization;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>
/// 界面文案的唯一入口：数据源是 Assets 下的两份 Strings XAML 字典。
/// </summary>
public interface ILocalizer
{
    /// <summary>当前语言代码："zh-CN" 或 "en-US"。</summary>
    string Language { get; }

    /// <summary>当前语言对应的区域信息，用于日期、数字格式化。</summary>
    CultureInfo Culture { get; }

    /// <summary>按键取当前语言文案；键不存在时返回键名。</summary>
    string this[string key] { get; }

    /// <summary>取当前语言的格式串并按 <see cref="Culture"/> 格式化；格式串与实参不匹配时返回格式串原文。</summary>
    string Format(string key, params object?[] args);

    /// <summary>语言切换完成（资源已替换）后触发。</summary>
    event EventHandler? LanguageChanged;

    /// <summary>切换语言；"en"/"en-US"（不区分大小写）为英文，其余一律为中文。</summary>
    void SetLanguage(string code);
}
