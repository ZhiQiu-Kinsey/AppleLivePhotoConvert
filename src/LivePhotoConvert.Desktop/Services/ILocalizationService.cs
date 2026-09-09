using System.Globalization;
namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 全局国际化本地化管理服务
/// </summary>
public interface ILocalizationService
{
    string CurrentLanguage { get; }

    /// <summary>
    /// 当前语言对应的 <see cref="CultureInfo"/>，供日期/数字等文化敏感格式化使用，
    /// 杜绝在业务代码内内联 "yyyy年M月d日" 之类的文化写死格式。
    /// </summary>
    CultureInfo CurrentCulture { get; }

    void SetLanguage(string languageCode);
    string GetString(string key);

    /// <summary>
    /// 标准参数化格式化：从当前语言字典取模板并调用 string.Format。
    /// 新增任何语言只需扩充对应资源字典，业务代码零改动。
    /// </summary>
    string GetFormat(string key, params object?[] args);

    event Action<string>? LanguageChanged;
}
