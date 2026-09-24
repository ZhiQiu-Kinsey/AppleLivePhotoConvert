using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using LivePhotoConvert.Desktop.Assets;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>
/// 以 Strings.zh-CN.axaml / Strings.en-US.axaml 为唯一数据源的本地化服务。
/// </summary>
public sealed class Localizer : ILocalizer
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";

    /// <summary>尚未接入依赖注入的调用方使用的共享实例。</summary>
    public static ILocalizer Current { get; } = new Localizer();

    private readonly LanguagePack _zh;
    private readonly LanguagePack _en;

    // 语言、区域、文案与格式缓存按语言打包、整体切换：后台线程不会读到混合状态，格式缓存也随语言失效
    private volatile LanguagePack _active;

    public Localizer()
    {
        // 使用 x:Class 编译生成的字典类型：AvaloniaXamlLoader.Load(Uri) 依赖反射（AOT 裁剪警告），且要求平台服务已注册
        _zh = new LanguagePack(Chinese, new ZhCnStrings());
        _en = new LanguagePack(English, new EnUsStrings());
        _active = _zh;
    }

    public string Language => _active.Code;

    public CultureInfo Culture => _active.Culture;

    public event EventHandler? LanguageChanged;

    public string this[string key] => Lookup(_active, key);

    public string Format(string key, params object?[] args)
    {
        var pack = _active;
        var template = Lookup(pack, key);
        if (args is null || args.Length == 0)
        {
            return template;
        }

        try
        {
            var format = pack.Formats.GetOrAdd(key, static (_, t) => CompositeFormat.Parse(t), template);
            return string.Format(pack.Culture, format, args);
        }
        catch (FormatException)
        {
            // 译文占位符写错不应让界面崩溃，退回原文便于发现问题
            return template;
        }
    }

    public void SetLanguage(string code)
    {
        var target = Normalize(code) == English ? _en : _zh;
        _active = target;

        // 线程池线程与后续新线程都要跟随界面语言格式化日期与数字
        CultureInfo.CurrentCulture = target.Culture;
        CultureInfo.CurrentUICulture = target.Culture;
        CultureInfo.DefaultThreadCurrentCulture = target.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = target.Culture;

        if (Application.Current is { } app)
        {
            var merged = app.Resources.MergedDictionaries;
            merged.Remove(_zh.Resources);
            merged.Remove(_en.Resources);
            merged.Add(target.Resources);
        }

        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Normalize(string? code) => code?.ToLowerInvariant() switch
    {
        "en" or "en-us" => English,
        _ => Chinese
    };

    private static string Lookup(LanguagePack pack, string key)
    {
        if (pack.Strings.TryGetValue(key, out var value))
        {
            return value;
        }

        Debug.Fail($"缺失本地化键: {key}");
        return key;
    }

    private sealed class LanguagePack
    {
        public LanguagePack(string code, ResourceDictionary resources)
        {
            Code = code;
            Culture = CultureInfo.GetCultureInfo(code);
            Resources = resources;

            // 预先取出全部字符串：编译后的字典条目可能延迟实例化，首次读取会改写内部存储，不宜在后台线程并发触发
            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in resources.Keys)
            {
                if (key is string name && resources.TryGetValue(name, out var value) && value is string text)
                {
                    strings[name] = text;
                }
            }

            Strings = strings.ToFrozenDictionary(StringComparer.Ordinal);
        }

        public string Code { get; }

        public CultureInfo Culture { get; }

        public ResourceDictionary Resources { get; }

        public FrozenDictionary<string, string> Strings { get; }

        public ConcurrentDictionary<string, CompositeFormat> Formats { get; } = new(StringComparer.Ordinal);
    }
}
