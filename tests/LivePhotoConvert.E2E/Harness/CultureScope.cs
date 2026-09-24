using System.Globalization;

namespace LivePhotoConvert.E2E.Harness;

/// <summary>
/// Localizer.SetLanguage 会改写进程级默认区域；用例结束时恢复，避免影响并行执行的其它用例。
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

/// <summary>会调用 Localizer.SetLanguage 的用例放入该集合串行执行，避免并行用例互相改写进程级区域。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessCultureCollection
{
    public const string Name = "ProcessCulture";
}
