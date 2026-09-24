using System.Globalization;

namespace LivePhotoConvert.Desktop.Tests.Harness;

/// <summary>
/// Localizer.SetLanguage 会改写进程级默认区域；用例结束时恢复，避免影响之后执行的用例。
/// </summary>
public sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? _defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
    private readonly CultureInfo? _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
        CultureInfo.DefaultThreadCurrentCulture = _defaultCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
    }
}

/// <summary>
/// 改写进程级区域或 Avalonia 应用级状态（资源字典、主题、日志接收器）的用例：
/// 不与任何用例并行，避免后台线程在界面用例运行期间改动同一个 Application。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessStateCollection
{
    public const string Name = "ProcessState";
}
