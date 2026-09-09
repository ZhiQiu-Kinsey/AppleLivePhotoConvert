using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Services;
using LivePhotoConvert.Desktop.ViewModels;

namespace LivePhotoConvert.E2E.Tier1_FeatureCoverage;

/// <summary>
/// 依赖引擎页「一键安装」与「自定义指定路径」能力的回归套件。
///
/// 设计约束（严格遵守，避免拖垮 CI）：
/// 1. 绝不触发真实网络下载——三大引擎的首个下载源均为公网 CDN（npmmirror），
///    无法在不联网的前提下让下载快速失败，因此本套件只覆盖「可判定的纯逻辑」：
///    忙碌态重复点击守卫、取消入口安全性、状态聚合、自定义路径校验与回写、提示文本生成。
/// 2. 所有设置写入均指向临时 settings.json，绝不污染用户真实配置。
/// 3. 「下载中 → 取消 / 失败」的完整异步状态机依赖网络，无法在无副作用前提下自动化，
///    列为遗留待手动冒烟项（见测试报告）。
/// </summary>
public class ToolsInstallFeatureTests
{
    #region 测试脚手架

    /// <summary>为单个用例提供隔离的临时配置文件，用完即删。</summary>
    private sealed class TempSettings : IDisposable
    {
        private readonly string _dir;

        public TempSettings()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"lpc_tools_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            SettingsPath = Path.Combine(_dir, "settings.json");
            Service = new SettingsService(SettingsPath);
        }

        public string SettingsPath { get; }

        public SettingsService Service { get; }

        public string CreateStubFile(string fileName, string content = "stub")
        {
            var path = Path.Combine(_dir, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>返回一个确定不存在于磁盘上的路径，用于校验「无效路径」分支。</summary>
        public string MissingFilePath(string fileName) => Path.Combine(_dir, "missing", fileName);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响测试结果
            }
        }
    }

    private static ToolsViewModel BuildVm(TempSettings temp) => new(temp.Service);

    private static Func<Task> InstallActionFor(ToolsViewModel vm, ToolKind kind) => kind switch
    {
        ToolKind.ExifTool => () => vm.InstallExifToolAsync(),
        ToolKind.Ffmpeg => () => vm.InstallFfmpegAsync(),
        _ => () => vm.InstallHeifEncAsync()
    };

    private static Func<Task> PickActionFor(ToolsViewModel vm, ToolKind kind) => kind switch
    {
        ToolKind.ExifTool => () => vm.PickExifToolPathAsync(),
        ToolKind.Ffmpeg => () => vm.PickFfmpegPathAsync(),
        _ => () => vm.PickHeifEncPathAsync()
    };

    private static Action<bool> InstallingSetterFor(ToolsViewModel vm, ToolKind kind) => kind switch
    {
        ToolKind.ExifTool => v => vm.IsInstallingExifTool = v,
        ToolKind.Ffmpeg => v => vm.IsInstallingFfmpeg = v,
        _ => v => vm.IsInstallingHeifEnc = v
    };

    private static Func<bool> InstallingGetterFor(ToolsViewModel vm, ToolKind kind) => kind switch
    {
        ToolKind.ExifTool => () => vm.IsInstallingExifTool,
        ToolKind.Ffmpeg => () => vm.IsInstallingFfmpeg,
        _ => () => vm.IsInstallingHeifEnc
    };

    private static Func<DesktopSettings, string> SettingsFieldFor(ToolKind kind) => kind switch
    {
        ToolKind.ExifTool => s => s.ExifToolPath,
        ToolKind.Ffmpeg => s => s.FfmpegPath,
        _ => s => s.HeifEncPath
    };

    private static string DisplayNameFor(ToolKind kind) => kind switch
    {
        ToolKind.ExifTool => "ExifTool",
        ToolKind.Ffmpeg => "FFmpeg",
        _ => "heif-enc"
    };

    /// <summary>
    /// 该引擎期望的可执行文件名。手动指定路径会对文件名做匹配校验
    /// （<c>ToolsViewModel.MatchesExpectedExecutable</c>），因此桩文件必须按引擎命名。
    /// </summary>
    private static string StubNameFor(ToolKind kind) => kind switch
    {
        ToolKind.ExifTool => "exiftool.exe",
        ToolKind.Ffmpeg => "ffmpeg.exe",
        _ => "heif-enc.exe"
    };

    #endregion

    #region 1. 忙碌态防重复点击守卫（不触网）

    [Theory]
    [InlineData(ToolKind.ExifTool)]
    [InlineData(ToolKind.Ffmpeg)]
    [InlineData(ToolKind.HeifEnc)]
    public async Task InstallCommand_WhenSameEngineAlreadyInstalling_IsIgnoredWithoutSecondDownload(ToolKind kind)
    {
        using var temp = new TempSettings();
        var vm = BuildVm(temp);

        // Arrange：模拟该引擎正处于「正在下载...」忙碌态
        InstallingSetterFor(vm, kind)(true);

        // Act：再次触发同一引擎的安装命令
        var install = InstallActionFor(vm, kind)();

        // 守卫命中时方法在任何 await 之前即返回，任务同步完成 => 未发起第二次下载
        Assert.True(install.IsCompleted, "忙碌态下安装命令应立即返回，不应再次进入下载流程");
        await install;

        // Assert：忙碌态保持，且未写入「正在安装 ...」起始提示
        Assert.True(InstallingGetterFor(vm, kind)(), "重复点击不应清除忙碌态");
        Assert.Equal(string.Empty, vm.ActionMessageText);
        Assert.False(vm.HasActionMessage);
        Assert.Equal(string.Empty, vm.InstallProgressText);
    }

    [Theory]
    [InlineData(ToolKind.ExifTool)]
    [InlineData(ToolKind.Ffmpeg)]
    [InlineData(ToolKind.HeifEnc)]
    public async Task InstallCommand_BusyGuard_DoesNotLeakProgressAcrossEngines(ToolKind kind)
    {
        using var temp = new TempSettings();
        var vm = BuildVm(temp);

        // 只有一个引擎忙碌时，其余两个引擎仍应处于非忙碌态（守卫按引擎独立判定）
        InstallingSetterFor(vm, kind)(true);

        foreach (var other in new[] { ToolKind.ExifTool, ToolKind.Ffmpeg, ToolKind.HeifEnc })
        {
            if (other == kind)
            {
                continue;
            }

            Assert.False(InstallingGetterFor(vm, other)(), $"{other} 不应被 {kind} 的安装态污染");
        }

        Assert.True(vm.IsInstallingAny);
        await Task.CompletedTask;
    }

    #endregion

    #region 2. 取消入口安全性

    [Fact]
    public void CancelInstallCommand_WithNoDownloadInProgress_DoesNotThrow()
    {
        using var temp = new TempSettings();
        var vm = BuildVm(temp);

        Assert.True(vm.CancelInstallCommand.CanExecute(null));

        // 无进行中的下载时 _installCts 为 null，取消命令必须静默降级
        vm.CancelInstallCommand.Execute(null);

        Assert.False(vm.IsInstallingAny);
        Assert.Equal(string.Empty, vm.ActionMessageText);
    }

    [Theory]
    [InlineData(ToolKind.ExifTool)]
    [InlineData(ToolKind.Ffmpeg)]
    [InlineData(ToolKind.HeifEnc)]
    public void CancelInstallCommand_WhileEngineBusy_DoesNotThrowAndKeepsBusyState(ToolKind kind)
    {
        using var temp = new TempSettings();
        var vm = BuildVm(temp);

        InstallingSetterFor(vm, kind)(true);

        vm.CancelInstallCommand.Execute(null);

        // 取消命令只负责发信号，状态复位由 InstallToolAsync 的 finally 负责
        Assert.True(InstallingGetterFor(vm, kind)());
        Assert.Equal(string.Empty, vm.ActionMessageText);
    }

    #endregion

    #region 3. 状态聚合

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, true, true)]
    public void IsInstallingAny_AggregatesAllThreeEngines(bool exif, bool ffmpeg, bool heif, bool expected)
    {
        using var temp = new TempSettings();
        var vm = BuildVm(temp);

        vm.IsInstallingExifTool = exif;
        vm.IsInstallingFfmpeg = ffmpeg;
        vm.IsInstallingHeifEnc = heif;

        Assert.Equal(expected, vm.IsInstallingAny);
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, false, false)]
    public void AllToolsReady_RequiresAllThreeEngines(bool exif, bool ffmpeg, bool heif, bool expected)
    {
        using var temp = new TempSettings();
        var vm = BuildVm(temp);

        vm.IsExifToolReady = exif;
        vm.IsFfmpegReady = ffmpeg;
        vm.IsHeifEncReady = heif;

        Assert.Equal(expected, vm.AllToolsReady);
    }

    [Fact]
    public void ActionMessage_HasActionMessageFollowsText()
    {
        using var temp = new TempSettings();
        var vm = BuildVm(temp);

        Assert.False(vm.HasActionMessage);

        vm.ActionMessageText = "任意提示";
        Assert.True(vm.HasActionMessage);
        Assert.False(vm.IsActionMessageError);

        vm.IsActionMessageError = true;
        Assert.True(vm.IsActionMessageError);

        vm.ActionMessageText = string.Empty;
        Assert.False(vm.HasActionMessage);
    }

    #endregion

    #region 4. 自定义指定路径（修复「按钮无命令」）

    [Theory]
    [InlineData(ToolKind.ExifTool)]
    [InlineData(ToolKind.Ffmpeg)]
    [InlineData(ToolKind.HeifEnc)]
    public async Task PickToolPathCommand_WithoutHostPicker_DoesNothingAndDoesNotThrow(ToolKind kind)
    {
        using var temp = new TempSettings();
        var vm = BuildVm(temp);

        // 宿主未注入文件选择器（RequestPickToolFile 为 null）时必须静默返回
        Assert.Null(vm.RequestPickToolFile);

        await PickActionFor(vm, kind)();

        Assert.Equal(string.Empty, vm.ActionMessageText);
        Assert.False(vm.IsActionMessageError);
        Assert.Equal(string.Empty, SettingsFieldFor(kind)(temp.Service.Current));
        Assert.False(File.Exists(temp.SettingsPath), "未选择文件时不应落盘写设置");
    }

    [Theory]
    [InlineData(ToolKind.ExifTool)]
    [InlineData(ToolKind.Ffmpeg)]
    [InlineData(ToolKind.HeifEnc)]
    public async Task PickToolPathCommand_WhenUserCancelsPicker_ShowsNoMessage(ToolKind kind)
    {
        using var temp = new TempSettings();
        var vm = BuildVm(temp);
        vm.RequestPickToolFile = () => Task.FromResult<string?>(null);

        await PickActionFor(vm, kind)();

        Assert.Equal(string.Empty, vm.ActionMessageText);
        Assert.False(vm.HasActionMessage);
        Assert.Equal(string.Empty, SettingsFieldFor(kind)(temp.Service.Current));
    }

    [Theory]
    [InlineData(ToolKind.ExifTool)]
    [InlineData(ToolKind.Ffmpeg)]
    [InlineData(ToolKind.HeifEnc)]
    public async Task PickToolPathCommand_WithMissingFile_ShowsErrorAndPersistsNothing(ToolKind kind)
    {
        using var temp = new TempSettings();
        var vm = BuildVm(temp);
        var missing = temp.MissingFilePath("engine_stub.exe");
        vm.RequestPickToolFile = () => Task.FromResult<string?>(missing);

        await PickActionFor(vm, kind)();

        Assert.True(vm.IsActionMessageError, "文件不存在时应判定为无效引擎");
        Assert.Equal(
            LocalizationService.Instance.GetFormat("ToolPathInvalidFormat", missing),
            vm.ActionMessageText);
        Assert.Equal(string.Empty, SettingsFieldFor(kind)(temp.Service.Current));
    }

    [Theory]
    [InlineData(ToolKind.ExifTool)]
    [InlineData(ToolKind.Ffmpeg)]
    [InlineData(ToolKind.HeifEnc)]
    public async Task PickToolPathCommand_WithUsableFile_WritesMatchingSettingsFieldOnly(ToolKind kind)
    {
        using var temp = new TempSettings();
        var vm = BuildVm(temp);

        // 文件名与该引擎期望的可执行文件名一致，用于通过 MatchesExpectedExecutable 校验。
        // 真实启动探测由可替换成员 ValidateToolExecutable 承担，此处注入恒真以脱离真实引擎环境。
        var stub = temp.CreateStubFile(StubNameFor(kind));
        vm.RequestPickToolFile = () => Task.FromResult<string?>(stub);
        vm.ValidateToolExecutable = _ => true;

        await PickActionFor(vm, kind)();

        // 路径必须落到该引擎对应的设置字段，且不得串写到其它两个字段
        Assert.Equal(stub, SettingsFieldFor(kind)(temp.Service.Current));
        foreach (var other in new[] { ToolKind.ExifTool, ToolKind.Ffmpeg, ToolKind.HeifEnc })
        {
            if (other != kind)
            {
                Assert.Equal(string.Empty, SettingsFieldFor(other)(temp.Service.Current));
            }
        }

        Assert.True(File.Exists(temp.SettingsPath), "选择有效路径后应立即持久化");
        Assert.False(vm.IsActionMessageError);
        Assert.Equal(
            LocalizationService.Instance.GetFormat("ToolPathAppliedFormat", DisplayNameFor(kind)),
            vm.ActionMessageText);
    }

    [Fact]
    public async Task PickToolPathCommand_WithNonExecutableFile_RejectsAfterHardening_QA1()
    {
        using var temp = new TempSettings();
        var vm = BuildVm(temp);

        // 原缺陷 QA-1：仅靠 ToolLocator.IsValidTool 校验时，它对「文件名不含 exiftool/ffmpeg/heif-enc」
        // 的任意真实存在文件一律返回 true，导致 notes.txt 这类根本不是可执行程序的文件也会被接受并写入设置。
        // 现已由 ToolsViewModel.PickCustomToolPathAsync 的三重校验（存在性 + 文件名匹配 + 启动探测）修复，
        // 本用例随之固化为「必须拒绝且不落盘」。此处刻意不注入 ValidateToolExecutable，
        // 以真实校验链验证文件名守卫本身即可拦截。
        var textFile = temp.CreateStubFile("notes.txt", "not an executable");
        vm.RequestPickToolFile = () => Task.FromResult<string?>(textFile);

        await vm.PickExifToolPathAsync();

        Assert.True(vm.IsActionMessageError, "非可执行文件必须判定为无效引擎");
        Assert.Equal(
            LocalizationService.Instance.GetFormat("ToolPathInvalidFormat", textFile),
            vm.ActionMessageText);
        Assert.Equal(string.Empty, temp.Service.Current.ExifToolPath);
        Assert.False(File.Exists(temp.SettingsPath), "拒绝后不应持久化任何设置");
    }

    #endregion

    #region 5. 安装提示模板（杜绝占位符错配导致用户看到原始模板）

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public void InstallPromptTemplates_RenderArgumentsInBothLanguages(string language)
    {
        var loc = new LocalizationService();
        loc.SetLanguage(language);

        // 若模板占位符与实参不匹配，GetFormat 会静默返回原始模板（用户看到 {0} 原文），此处提前拦截
        var starting = loc.GetFormat("InstallStartingFormat", "ExifTool");
        Assert.Contains("ExifTool", starting);
        Assert.DoesNotContain("{0}", starting);

        var completed = loc.GetFormat("InstallCompletedFormat", "ExifTool");
        Assert.Contains("ExifTool", completed);
        Assert.DoesNotContain("{0}", completed);

        var failed = loc.GetFormat("InstallFailedFormat", "FFmpeg", "timeout");
        Assert.Contains("FFmpeg", failed);
        Assert.Contains("timeout", failed);
        Assert.DoesNotContain("{0}", failed);
        Assert.DoesNotContain("{1}", failed);

        var invalid = loc.GetFormat("ToolPathInvalidFormat", "/tmp/x.exe");
        Assert.Contains("/tmp/x.exe", invalid);
        Assert.DoesNotContain("{0}", invalid);

        var applied = loc.GetFormat("ToolPathAppliedFormat", "heif-enc");
        Assert.Contains("heif-enc", applied);
        Assert.DoesNotContain("{0}", applied);

        Assert.NotEqual(string.Empty, loc.GetString("InstallCanceled"));
        Assert.NotEqual(string.Empty, loc.GetString("InstallToolBtn"));
        Assert.NotEqual(string.Empty, loc.GetString("InstallingStatus"));
    }

    #endregion
}
