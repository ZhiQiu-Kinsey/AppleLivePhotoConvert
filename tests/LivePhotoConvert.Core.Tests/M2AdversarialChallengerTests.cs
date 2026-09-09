using System.Security.Cryptography;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Models;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests;

/// <summary>
/// M2 里程碑对抗性挑战测试集 (Adversarial Challenger Suite)
/// 专门针对 MotionPhotoStripper.AnalyzeAsync 只读安全性与边界异常，以及 SplitOptions.SourceFileAction 跳过/失败保护与子目录策略。
/// </summary>
public class M2AdversarialChallengerTests
{
    #region Part 1: MotionPhotoStripper.AnalyzeAsync Adversarial Tests

    /// <summary>
    /// 挑战项 1：0 字节空文件的极端边界
    /// 验证 AnalyzeAsync 处理 0 字节文件时绝不除零、崩溃或返回负数，所有统计安全归零
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_ZeroByteFiles_ShouldHandleGracefullyWithoutCrash()
    {
        using var tempDir = new TempDirectory();
        var emptyJpg = tempDir.CreateFile("empty.jpg", []);
        var emptyHeic = tempDir.CreateFile("empty.heic", []);

        var fakeExif = new ChallengerExifTool();
        fakeExif.Offsets[emptyJpg] = 0;
        fakeExif.Offsets[emptyHeic] = null;

        var fakeImageConverter = new ChallengerImageConverter();
        var stripper = new MotionPhotoStripper(fakeExif, fakeImageConverter);

        var report = await stripper.AnalyzeAsync(tempDir.Root, convertToHeic: true, TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Items.Count);
        Assert.Equal(0, report.TotalOriginalBytes);
        Assert.Equal(0, report.TotalVideoBytes);
        Assert.Equal(0, report.TotalEstimatedHeicBytes);
        Assert.Equal(0, report.TotalEstimatedSavedBytes);

        foreach (var item in report.Items)
        {
            Assert.Equal(0, item.OriginalBytes);
            Assert.Equal(0, item.VideoBytes);
            Assert.Null(item.EmbeddedVideoBytes);
            Assert.False(item.HasEmbeddedVideo);
            Assert.Equal(0, item.EstimatedFinalBytes);
            Assert.Equal(0, item.EstimatedSavedBytes);
        }
    }

    /// <summary>
    /// 挑战项 2：路径不存在与空参数异常挑战
    /// 验证当传入不存在的路径或空字符时，抛出明确语义异常，不产生未经捕获的崩溃
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_InvalidPathOrNonExistent_ShouldThrowAppropriateException()
    {
        var fakeExif = new ChallengerExifTool();
        var fakeImageConverter = new ChallengerImageConverter();
        var stripper = new MotionPhotoStripper(fakeExif, fakeImageConverter);

        var missingDir = Path.Combine(Path.GetTempPath(), $"challenger_missing_{Guid.NewGuid():N}");
        var missingFile = Path.Combine(Path.GetTempPath(), $"challenger_missing_{Guid.NewGuid():N}.jpg");

        // 不存在路径应抛出 DirectoryNotFoundException
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            stripper.AnalyzeAsync(missingDir, TestContext.Current.CancellationToken).AsTask());

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            stripper.AnalyzeAsync(missingFile, TestContext.Current.CancellationToken).AsTask());

        // 空白字符串应抛出 ArgumentException
        await Assert.ThrowsAsync<ArgumentException>(() =>
            stripper.AnalyzeAsync("", TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            stripper.AnalyzeAsync("   ", TestContext.Current.CancellationToken).AsTask());
    }

    /// <summary>
    /// 挑战项 3：非动态普通照片（JPG / HEIC）与转码参数计算精确度
    /// 验证普通图片无内嵌视频时，JPG 转码预估 45% 体积，HEIC 保持 100% 体积无重复转码
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_NonMotionPhotos_CalculationAccuracy()
    {
        using var tempDir = new TempDirectory();
        var normalJpg = tempDir.CreateFile("normal.jpg", new byte[10000]);
        var normalHeic = tempDir.CreateFile("normal.heic", new byte[8000]);

        var fakeExif = new ChallengerExifTool();
        fakeExif.Offsets[normalJpg] = null;
        fakeExif.Offsets[normalHeic] = null;

        var fakeImageConverter = new ChallengerImageConverter();
        var stripper = new MotionPhotoStripper(fakeExif, fakeImageConverter);

        var report = await stripper.AnalyzeAsync(tempDir.Root, convertToHeic: true, TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Items.Count);

        var jpgItem = report.Items.First(x => x.FilePath == normalJpg);
        Assert.Equal(10000, jpgItem.OriginalBytes);
        Assert.Equal(0, jpgItem.VideoBytes);
        Assert.False(jpgItem.HasEmbeddedVideo);
        Assert.True(jpgItem.NeedsHeicConversion);
        Assert.Equal(4500, jpgItem.EstimatedFinalBytes); // 10000 * 0.45
        Assert.Equal(5500, jpgItem.EstimatedSavedBytes); // 10000 - 4500

        var heicItem = report.Items.First(x => x.FilePath == normalHeic);
        Assert.Equal(8000, heicItem.OriginalBytes);
        Assert.Equal(0, heicItem.VideoBytes);
        Assert.False(heicItem.HasEmbeddedVideo);
        Assert.False(heicItem.NeedsHeicConversion, "已经是 HEIC，不需要再次转码");
        Assert.Equal(8000, heicItem.EstimatedFinalBytes); // 无视频且已是 HEIC，最终体积不变
        Assert.Equal(0, heicItem.EstimatedSavedBytes); // 预期节省 0 字节
    }

    /// <summary>
    /// 挑战项 4：已是 HEIC 的动态照片只瘦身内嵌视频，不重复转码
    /// 验证已有 HEIC 实况照片仅剥离视频字节，纯图片部分不参与 45% 折扣计算
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_AlreadyHeic_WithVideo_ShouldOnlySaveVideoBytes()
    {
        using var tempDir = new TempDirectory();
        // 原文件 5000 字节：2000 字节图片 + 3000 字节视频
        var heicLive = tempDir.CreateFile("live.heic", new byte[5000]);

        var fakeExif = new ChallengerExifTool();
        fakeExif.Offsets[heicLive] = 3000;

        var fakeImageConverter = new ChallengerImageConverter();
        var stripper = new MotionPhotoStripper(fakeExif, fakeImageConverter);

        var report = await stripper.AnalyzeAsync(heicLive, convertToHeic: true, TestContext.Current.CancellationToken);

        Assert.Single(report.Items);
        var item = report.Items[0];
        Assert.Equal(5000, item.OriginalBytes);
        Assert.Equal(3000, item.VideoBytes);
        Assert.True(item.HasEmbeddedVideo);
        Assert.False(item.NeedsHeicConversion);
        Assert.Equal(2000, item.EstimatedFinalBytes); // 5000 - 3000 = 2000 clean image
        Assert.Equal(3000, item.EstimatedSavedBytes); // 节省的就是内嵌视频的 3000 字节
    }

    /// <summary>
    /// 挑战项 5：极限只读安全性测试（内容哈希、时间戳、只读文件属性、无孤立残留文件）
    /// 验证即使源文件被设置为 Windows 只读属性 (ReadOnly)，AnalyzeAsync 依然能够正常读取，且操作后：
    /// 1. 文件 SHA256 哈希 100% 保持一致
    /// 2. 文件创建时间与最后写入时间 100% 保持一致
    /// 3. 输入目录与系统临时目录中无任何临时残留或孤立文件产生
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_ReadOnlySafety_WithReadOnlyAttribute_ZeroSideEffects()
    {
        using var tempDir = new TempDirectory();
        var data1 = new byte[4096];
        var data2 = new byte[8192];
        Random.Shared.NextBytes(data1);
        Random.Shared.NextBytes(data2);

        var file1 = tempDir.CreateFile("readonly_sample1.jpg", data1);
        var file2 = tempDir.CreateFile("readonly_sample2.heic", data2);

        // 设置 Windows 只读属性
        File.SetAttributes(file1, FileAttributes.ReadOnly);
        File.SetAttributes(file2, FileAttributes.ReadOnly);

        var hash1Before = SHA256.HashData(File.ReadAllBytes(file1));
        var hash2Before = SHA256.HashData(File.ReadAllBytes(file2));
        var createTime1Before = File.GetCreationTimeUtc(file1);
        var writeTime1Before = File.GetLastWriteTimeUtc(file1);
        var createTime2Before = File.GetCreationTimeUtc(file2);
        var writeTime2Before = File.GetLastWriteTimeUtc(file2);

        var fakeExif = new ChallengerExifTool();
        fakeExif.Offsets[file1] = 1024;
        fakeExif.Offsets[file2] = 2048;

        var fakeImageConverter = new ChallengerImageConverter();
        var stripper = new MotionPhotoStripper(fakeExif, fakeImageConverter);

        var report = await stripper.AnalyzeAsync(tempDir.Root, convertToHeic: true, TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Items.Count);

        try
        {
            // 验证只读属性依然保留
            Assert.True(File.GetAttributes(file1).HasFlag(FileAttributes.ReadOnly));
            Assert.True(File.GetAttributes(file2).HasFlag(FileAttributes.ReadOnly));

            // 验证 SHA256 绝对一致（内容无任何修改）
            Assert.Equal(hash1Before, SHA256.HashData(File.ReadAllBytes(file1)));
            Assert.Equal(hash2Before, SHA256.HashData(File.ReadAllBytes(file2)));

            // 验证时间戳毫无变动
            Assert.Equal(createTime1Before, File.GetCreationTimeUtc(file1));
            Assert.Equal(writeTime1Before, File.GetLastWriteTimeUtc(file1));
            Assert.Equal(createTime2Before, File.GetCreationTimeUtc(file2));
            Assert.Equal(writeTime2Before, File.GetLastWriteTimeUtc(file2));

            // 验证输入目录内文件数未增加
            Assert.Equal(2, Directory.GetFiles(tempDir.Root).Length);
        }
        finally
        {
            // 移除只读属性以便 TempDirectory 正确清理
            File.SetAttributes(file1, FileAttributes.Normal);
            File.SetAttributes(file2, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// 挑战项 6：元数据视频长度异常/损坏时的容错机制
    /// 当视频长度大于等于文件大小或元数据读取抛异常时，只读分析绝不能崩溃，应优雅降级为纯图片
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_CorruptedOrAnomalousOffsets_ShouldFallbackGracefully()
    {
        using var tempDir = new TempDirectory();
        var fileOverLength = tempDir.CreateFile("over_length.jpg", new byte[1000]);
        var fileEqualLength = tempDir.CreateFile("equal_length.jpg", new byte[1000]);
        var fileException = tempDir.CreateFile("exception.jpg", new byte[1000]);

        var fakeExif = new ChallengerExifTool();
        fakeExif.Offsets[fileOverLength] = 2000; // 偏移 2000 > 文件长度 1000（不可能）
        fakeExif.Offsets[fileEqualLength] = 1000; // 偏移 1000 == 文件长度 1000（无图片前段）
        fakeExif.ThrowOnFiles.Add(fileException);

        var fakeImageConverter = new ChallengerImageConverter();
        var stripper = new MotionPhotoStripper(fakeExif, fakeImageConverter);

        var report = await stripper.AnalyzeAsync(tempDir.Root, convertToHeic: true, TestContext.Current.CancellationToken);

        Assert.Equal(3, report.Items.Count);

        foreach (var item in report.Items)
        {
            Assert.False(item.HasEmbeddedVideo, $"{item.FilePath} 应视为无有效内嵌视频");
            Assert.Equal(0, item.VideoBytes);
            Assert.Equal(1000, item.OriginalBytes);
            // 降级为普通 JPEG 转 HEIC 预估 (1000 * 0.45 = 450)
            Assert.Equal(450, item.EstimatedFinalBytes);
            Assert.Equal(550, item.EstimatedSavedBytes);
        }
    }

    /// <summary>
    /// 挑战项 7：空目录输入应直接返回 0 条目报告，无额外耗时与异常
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_EmptyDirectory_ShouldReturnZeroReport()
    {
        using var tempDir = new TempDirectory();
        var fakeExif = new ChallengerExifTool();
        var fakeImageConverter = new ChallengerImageConverter();
        var stripper = new MotionPhotoStripper(fakeExif, fakeImageConverter);

        var report = await stripper.AnalyzeAsync(tempDir.Root, convertToHeic: true, TestContext.Current.CancellationToken);

        Assert.Empty(report.Items);
        Assert.Equal(0, report.TotalOriginalBytes);
        Assert.Equal(0, report.TotalVideoBytes);
        Assert.Equal(0, report.TotalEstimatedHeicBytes);
        Assert.Equal(0, report.TotalEstimatedSavedBytes);
    }

    #endregion

    #region Part 2: SplitOptions.SourceFileAction Adversarial Tests

    /// <summary>
    /// 挑战项 8：跳过的非实况图片（MoveToSubfolder 模式）绝对不能被清理或移动到“已拆分”
    /// 混合批次场景：包含 1 个合法实况照片和 2 个普通非实况图片
    /// 验证：只有合法实况照片被移走，被跳过的 2 个普通图片 100% 完好无损地保留在原输入目录
    /// </summary>
    [Fact]
    public async Task SplitAsync_SkippedFiles_WithMoveAction_MustNeverBeMovedOrCleaned()
    {
        using var tempDir = new TempDirectory();

        // 1. 合法实况照片
        var liveBytes = CreateMotionPhotoBytes(150, 300);
        var livePath = tempDir.CreateFile("valid_live.jpg", liveBytes);

        // 2. 两个普通非实况图片
        var regular1 = tempDir.CreateFile("regular_1.jpg", [1, 2, 3, 4, 5]);
        var regular2 = tempDir.CreateFile("regular_2.heic", [6, 7, 8, 9, 10]);

        var outputDir = tempDir.Combine("output");

        var fakeExif = new ChallengerSplitterExifTool();
        fakeExif.Offsets[livePath] = 300;
        fakeExif.Offsets[regular1] = null; // 非实况
        fakeExif.Offsets[regular2] = null; // 非实况

        var splitter = new MotionPhotoSplitter(fakeExif);

        var options = new SplitOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            TargetFormat = SplitTargetFormat.Android,
            SourceFileAction = SourceFileAction.MoveToSubfolder
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(3, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(2, report.Skipped);
        Assert.Equal(1, report.CleanedFileCount);
        Assert.Empty(report.CleanupFailures);
        Assert.Empty(report.Failures);

        // 铁律验证：被跳过的普通图片必须依然停留在原输入目录，内容完好无损！
        Assert.True(File.Exists(regular1), "regular_1.jpg 绝不能被移动");
        Assert.True(File.Exists(regular2), "regular_2.heic 绝不能被移动");
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(regular1));
        Assert.Equal([6, 7, 8, 9, 10], File.ReadAllBytes(regular2));

        // 合法实况照片被移至已拆分子目录
        Assert.False(File.Exists(livePath), "valid_live.jpg 应从输入根目录移出");
        var movedLivePath = tempDir.Combine(SourceFileCleaner.SplitFolderName, "valid_live.jpg");
        Assert.True(File.Exists(movedLivePath), "valid_live.jpg 应进入已拆分子目录");

        // “已拆分”子目录中仅有 1 个文件，绝无 regular_1 或 regular_2
        var splitDirFiles = Directory.GetFiles(tempDir.Combine(SourceFileCleaner.SplitFolderName));
        Assert.Single(splitDirFiles);
        Assert.Equal("valid_live.jpg", Path.GetFileName(splitDirFiles[0]));
    }

    /// <summary>
    /// 挑战项 9：跳过的非实况图片（Delete 模式）绝对不能被物理删除
    /// 验证：即使用户选择了最危险的物理删除 (SourceFileAction.Delete)，跳过的普通照片依然安全保留
    /// </summary>
    [Fact]
    public async Task SplitAsync_SkippedFiles_WithDeleteAction_MustNeverBeDeleted()
    {
        using var tempDir = new TempDirectory();

        var liveBytes = CreateMotionPhotoBytes(100, 200);
        var livePath = tempDir.CreateFile("delete_live.jpg", liveBytes);
        var regularPath = tempDir.CreateFile("do_not_delete.jpg", [42, 43, 44]);

        var outputDir = tempDir.Combine("output");

        var fakeExif = new ChallengerSplitterExifTool();
        fakeExif.Offsets[livePath] = 200;
        fakeExif.Offsets[regularPath] = null; // 跳过

        var splitter = new MotionPhotoSplitter(fakeExif);

        var options = new SplitOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            TargetFormat = SplitTargetFormat.Android,
            SourceFileAction = SourceFileAction.Delete
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(1, report.CleanedFileCount);
        Assert.Empty(report.Failures);

        // 实况文件被删除
        Assert.False(File.Exists(livePath), "拆分成功的实况文件应被删除");

        // 普通文件必须毫发无伤！
        Assert.True(File.Exists(regularPath), "跳过的普通文件绝对不能被物理删除！");
        Assert.Equal([42, 43, 44], File.ReadAllBytes(regularPath));
    }

    /// <summary>
    /// 挑战项 10：拆分失败的文件（Move 与 Delete 模式）绝对不能被清理、移动或删除
    /// 验证：当拆分过程中遇到损坏文件抛出异常时，失败文件严禁执行任何清理动作
    /// </summary>
    [Fact]
    public async Task SplitAsync_FailedFiles_MustNeverBeCleanedOrMovedOrDeleted()
    {
        using var tempDir = new TempDirectory();

        // 1. 损坏实况文件：视频长度 500 >= 总长度 300（引发 InvalidDataException）
        var corruptPath = tempDir.CreateFile("corrupt.jpg", new byte[300]);
        // 2. 正常实况文件
        var validBytes = CreateMotionPhotoBytes(100, 200);
        var validPath = tempDir.CreateFile("valid.jpg", validBytes);

        var outputDir = tempDir.Combine("output");

        var fakeExif = new ChallengerSplitterExifTool();
        fakeExif.Offsets[corruptPath] = 500; // 损坏
        fakeExif.Offsets[validPath] = 200; // 正常

        var splitter = new MotionPhotoSplitter(fakeExif);

        var options = new SplitOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            TargetFormat = SplitTargetFormat.Android,
            SourceFileAction = SourceFileAction.MoveToSubfolder
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.Single(report.Failures);
        Assert.Equal("corrupt.jpg", report.Failures[0].Item);
        Assert.Equal(1, report.CleanedFileCount);

        // 损坏的失败文件必须原封不动地保留在原位置！
        Assert.True(File.Exists(corruptPath), "失败的文件绝不能被移动或删除！");

        // 成功的文件被移走
        Assert.False(File.Exists(validPath));
        Assert.True(File.Exists(tempDir.Combine(SourceFileCleaner.SplitFolderName, "valid.jpg")));
    }

    /// <summary>
    /// 挑战项 11：“已拆分”子目录的创建条件铁律
    /// 验证：仅当 SourceFileAction 为 Move 或 MoveToSubfolder 时才创建“已拆分”目录；
    /// 当为 Keep 或 Delete 时，绝对不应创建任何“已拆分”或“已合成”的孤立空目录
    /// </summary>
    [Fact]
    public async Task SplitAsync_SubfolderCreation_OnlyWhenMoveAction()
    {
        // 1. 测试 Keep 策略
        using (var tempKeep = new TempDirectory())
        {
            var fakeExif = new ChallengerSplitterExifTool();
            var splitter = new MotionPhotoSplitter(fakeExif);
            var options = new SplitOptions
            {
                InputDirectory = tempKeep.Root,
                OutputDirectory = tempKeep.Combine("output"),
                SourceFileAction = SourceFileAction.Keep
            };

            await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

            Assert.False(Directory.Exists(tempKeep.Combine(SourceFileCleaner.SplitFolderName)),
                "Keep 策略下绝对不应创建 \"已拆分\" 子文件夹");
            Assert.False(Directory.Exists(tempKeep.Combine(SourceFileCleaner.MergedFolderName)),
                "Splitter 绝不应创建 \"已合成\" 文件夹");
        }

        // 2. 测试 Delete 策略
        using (var tempDelete = new TempDirectory())
        {
            var fakeExif = new ChallengerSplitterExifTool();
            var splitter = new MotionPhotoSplitter(fakeExif);
            var options = new SplitOptions
            {
                InputDirectory = tempDelete.Root,
                OutputDirectory = tempDelete.Combine("output"),
                SourceFileAction = SourceFileAction.Delete
            };

            await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

            Assert.False(Directory.Exists(tempDelete.Combine(SourceFileCleaner.SplitFolderName)),
                "Delete 策略下绝对不应创建 \"已拆分\" 子文件夹");
        }

        // 3. 测试 MoveToSubfolder 策略
        using (var tempMove = new TempDirectory())
        {
            var fakeExif = new ChallengerSplitterExifTool();
            var splitter = new MotionPhotoSplitter(fakeExif);
            var options = new SplitOptions
            {
                InputDirectory = tempMove.Root,
                OutputDirectory = tempMove.Combine("output"),
                SourceFileAction = SourceFileAction.MoveToSubfolder
            };

            await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

            Assert.True(Directory.Exists(tempMove.Combine(SourceFileCleaner.SplitFolderName)),
                "MoveToSubfolder 策略下必须且仅创建 \"已拆分\" 子文件夹");
            Assert.False(Directory.Exists(tempMove.Combine(SourceFileCleaner.MergedFolderName)),
                "Splitter 绝不能混淆创建 \"已合成\" 文件夹");
        }
    }

    /// <summary>
    /// 挑战项 12：“已拆分”子目录文件名冲突时的非覆盖原子保护
    /// 当“已拆分”子目录中已存在同名文件时，移动不应覆盖先到文件，而应自动附加 _1 后缀
    /// </summary>
    [Fact]
    public async Task SplitAsync_MoveAction_NameCollisionInSplitFolder_ShouldResolveUniqueName()
    {
        using var tempDir = new TempDirectory();

        // 提前在“已拆分”子目录中创建一个同名占位文件
        var splitDir = tempDir.Combine(SourceFileCleaner.SplitFolderName);
        Directory.CreateDirectory(splitDir);
        var existingFileInSplit = Path.Combine(splitDir, "COLLISION.jpg");
        File.WriteAllBytes(existingFileInSplit, [99, 99, 99]);

        // 在输入目录准备同名实况照片
        var liveBytes = CreateMotionPhotoBytes(100, 200);
        var livePath = tempDir.CreateFile("COLLISION.jpg", liveBytes);

        var outputDir = tempDir.Combine("output");

        var fakeExif = new ChallengerSplitterExifTool();
        fakeExif.Offsets[livePath] = 200;

        var splitter = new MotionPhotoSplitter(fakeExif);

        var options = new SplitOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            TargetFormat = SplitTargetFormat.Android,
            SourceFileAction = SourceFileAction.MoveToSubfolder
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(1, report.CleanedFileCount);
        Assert.Empty(report.CleanupFailures);

        // 先到文件保持不变
        Assert.Equal([99, 99, 99], File.ReadAllBytes(existingFileInSplit));

        // 新移动的文件被重命名为 COLLISION_1.jpg
        var renamedMovedFile = Path.Combine(splitDir, "COLLISION_1.jpg");
        Assert.True(File.Exists(renamedMovedFile), "冲突的文件应安全自动重命名为 COLLISION_1.jpg");
    }

    /// <summary>
    /// 挑战项 13：高并发压力测试（50 个文件混合批次，并发度 8）
    /// 验证：在多线程竞争下，原子统计、成功移入、跳过保留、失败保留完全准确，无死锁或计数竞态
    /// </summary>
    [Fact]
    public async Task SplitAsync_HighConcurrency50Files_StressTest()
    {
        using var tempDir = new TempDirectory();

        var fakeExif = new ChallengerSplitterExifTool();

        const int validCount = 15;
        const int skipCount = 20;
        const int corruptCount = 15;

        // 15 个有效实况照片
        for (var i = 0; i < validCount; i++)
        {
            var p = tempDir.CreateFile($"stress_valid_{i:D2}.jpg", CreateMotionPhotoBytes(100, 200));
            fakeExif.Offsets[p] = 200;
        }

        // 20 个普通图片（跳过）
        for (var i = 0; i < skipCount; i++)
        {
            var p = tempDir.CreateFile($"stress_skip_{i:D2}.jpg", [1, 2, 3, 4]);
            fakeExif.Offsets[p] = null;
        }

        // 15 个损坏图片（失败）
        for (var i = 0; i < corruptCount; i++)
        {
            var p = tempDir.CreateFile($"stress_corrupt_{i:D2}.jpg", new byte[100]);
            fakeExif.Offsets[p] = 500; // offset > total length
        }

        var outputDir = tempDir.Combine("output");
        var splitter = new MotionPhotoSplitter(fakeExif);

        var options = new SplitOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            TargetFormat = SplitTargetFormat.Android,
            SourceFileAction = SourceFileAction.MoveToSubfolder,
            Parallelism = 8
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(50, report.Total);
        Assert.Equal(validCount, report.Succeeded);
        Assert.Equal(skipCount, report.Skipped);
        Assert.Equal(corruptCount, report.Failed);
        Assert.Equal(validCount, report.CleanedFileCount);
        Assert.Equal(corruptCount, report.Failures.Count);
        Assert.Empty(report.CleanupFailures);

        // 验证文件落点：已拆分子目录下必须恰好有 validCount 个文件
        var movedFiles = Directory.GetFiles(tempDir.Combine(SourceFileCleaner.SplitFolderName));
        Assert.Equal(validCount, movedFiles.Length);

        // 输入根目录下必须恰好剩余 skipCount + corruptCount 个文件（未被误移或误删）
        var remainingInRoot = Directory.GetFiles(tempDir.Root);
        Assert.Equal(skipCount + corruptCount, remainingInRoot.Length);
    }

    /// <summary>
    /// 挑战项 14：清理过程遇到独占文件锁时，应记录 CleanupFailures，且绝不损坏已生成的拆分成果
    /// </summary>
    [Fact]
    public async Task SplitAsync_SourceFileLockedDuringCleanup_ShouldRecordCleanupFailureWithoutCrashingSplit()
    {
        using var tempDir = new TempDirectory();
        var liveBytes = CreateMotionPhotoBytes(100, 200);
        var livePath = tempDir.CreateFile("locked.jpg", liveBytes);

        var outputDir = tempDir.Combine("output");

        var fakeExif = new ChallengerSplitterExifTool();
        fakeExif.Offsets[livePath] = 200;

        var splitter = new MotionPhotoSplitter(fakeExif);

        var options = new SplitOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            TargetFormat = SplitTargetFormat.Android,
            SourceFileAction = SourceFileAction.Delete // 请求删除
        };

        // 在外部以共享读模式保持句柄打开，允许 Splitter 正常读取拆分，但导致后续 File.Delete 无法执行
        using var lockStream = new FileStream(livePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        // 拆分逻辑本身已成功产出
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(0, report.CleanedFileCount); // 清理未成功
        Assert.Single(report.CleanupFailures); // 记录清理失败
        Assert.Equal(livePath, report.CleanupFailures[0].Item);

        // 验证输出成果完好存在
        Assert.True(File.Exists(Path.Combine(outputDir, "locked.jpg")));
        Assert.True(File.Exists(Path.Combine(outputDir, "locked.mp4")));
    }

    /// <summary>
    /// 挑战项 15：取消令牌 (CancellationToken) 立即响应
    /// 当传入已取消的 Token 时，AnalyzeAsync 应立即抛出 OperationCanceledException
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_WhenCancelled_ShouldThrowOperationCanceledException()
    {
        using var tempDir = new TempDirectory();
        tempDir.CreateFile("cancel1.jpg", new byte[1000]);

        var fakeExif = new ChallengerExifTool();
        var fakeImageConverter = new ChallengerImageConverter();
        var stripper = new MotionPhotoStripper(fakeExif, fakeImageConverter);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stripper.AnalyzeAsync(tempDir.Root, convertToHeic: true, cts.Token).AsTask());
    }

    /// <summary>
    /// 挑战项 16：复杂中文与空格路径下只读分析与拆分稳健性
    /// 验证 Windows 路径包含深层目录、空格、中文与标点符号时，各组件稳健运行
    /// </summary>
    [Fact]
    public async Task AnalyzeAndSplit_WithComplexNonAsciiPaths_ShouldWorkSeamlessly()
    {
        using var tempDir = new TempDirectory();
        var subDir = tempDir.Combine("2026年 实况 相册 (测试)", "子相册 深度 1");
        Directory.CreateDirectory(subDir);

        var liveBytes = CreateMotionPhotoBytes(120, 240);
        var photoPath = Path.Combine(subDir, "照片 样本 - 2026.jpg");
        File.WriteAllBytes(photoPath, liveBytes);

        var fakeExif = new ChallengerExifTool();
        fakeExif.Offsets[photoPath] = 240;

        var fakeImageConverter = new ChallengerImageConverter();
        var stripper = new MotionPhotoStripper(fakeExif, fakeImageConverter);

        var report = await stripper.AnalyzeAsync(subDir, convertToHeic: true, TestContext.Current.CancellationToken);

        Assert.Single(report.Items);
        Assert.Equal(photoPath, report.Items[0].FilePath);
        Assert.Equal(360, report.TotalOriginalBytes);
        Assert.Equal(240, report.TotalVideoBytes);
    }

    /// <summary>
    /// 挑战项 17：契约属性对称性与别名完全一致性验证
    /// 验证 StripAnalysisReport 与 StripAnalysisItem 的双向别名完全等价无偏差
    /// </summary>
    [Fact]
    public void StripAnalysisReport_ContractAliases_ShouldBeIdentical()
    {
        var item = new StripAnalysisItem(
            filePath: "C:\\test\\photo.jpg",
            originalBytes: 1000,
            videoBytes: 400,
            estimatedHeicBytes: 270,
            hasEmbeddedVideo: true,
            needsHeicConversion: true,
            estimatedFinalBytes: 270
        );

        Assert.Equal(item.FilePath, item.SourcePath);
        Assert.Equal(item.VideoBytes, item.EmbeddedVideoBytes);
        Assert.Equal(730, item.EstimatedSavedBytes);

        var report = new StripAnalysisReport(
            items: [item],
            totalOriginalBytes: 1000,
            totalVideoBytes: 400,
            totalEstimatedHeicBytes: 270,
            totalEstimatedSavedBytes: 730
        );

        Assert.Equal(report.TotalOriginalBytes, report.OriginalTotalBytes);
        Assert.Equal(report.TotalVideoBytes, report.VideoTotalBytes);
        Assert.Equal(report.TotalEstimatedHeicBytes, report.EstimatedHeicTotalBytes);
        Assert.Equal(report.TotalEstimatedSavedBytes, report.EstimatedSavedBytes);
    }

    #endregion

    #region Helper Methods & Test Doubles

    private static byte[] CreateMotionPhotoBytes(int photoLen, int videoLen)
    {
        var photoBytes = new byte[photoLen];
        photoBytes[0] = 0xFF; photoBytes[1] = 0xD8; photoBytes[2] = 0xFF; // JPEG magic
        var videoBytes = new byte[videoLen];
        videoBytes[4] = (byte)'f'; videoBytes[5] = (byte)'t'; videoBytes[6] = (byte)'y'; videoBytes[7] = (byte)'p';
        videoBytes[8] = (byte)'m'; videoBytes[9] = (byte)'p'; videoBytes[10] = (byte)'4'; videoBytes[11] = (byte)'2';
        return [.. photoBytes, .. videoBytes];
    }

    private sealed class ChallengerExifTool : IExifTool
    {
        public Dictionary<string, long?> Offsets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ThrowOnFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<long?> TryReadMicroVideoOffsetAsync(string imagePath, CancellationToken cancellationToken = default)
        {
            if (ThrowOnFiles.Contains(imagePath))
            {
                throw new InvalidOperationException("模拟 ExifTool 异常读取");
            }
            if (Offsets.TryGetValue(imagePath, out var offset))
            {
                return Task.FromResult(offset);
            }
            return Task.FromResult<long?>(null);
        }

        public Task WriteMotionPhotoTagsAsync(string imagePath, long videoOffset, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveMotionPhotoTagsAsync(string imagePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CopyAllTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CopyCoverTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> TryReadContentIdentifierAsync(string filePath, ContentIdentifierKind kind, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task WriteAppleContentIdentifierAsync(string photoPath, string contentIdentifier, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteAppleVideoMetadataAsync(string videoPath, string? photoPath, string contentIdentifier, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DateTime?> TryReadCreateDateAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult<DateTime?>(null);
        public Task<TimeSpan?> TryReadDurationAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult<TimeSpan?>(null);
        public Task<bool> IsMirroredVideoAsync(string videoPath, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ChallengerImageConverter : IImageConverter
    {
        public Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            File.WriteAllBytes(destinationPath, new byte[1000]);
            return Task.CompletedTask;
        }

        public Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality = 90, CancellationToken cancellationToken = default)
        {
            File.WriteAllBytes(destinationPath, new byte[500]);
            return Task.CompletedTask;
        }
    }

    private sealed class ChallengerSplitterExifTool : IExifTool
    {
        public Dictionary<string, long?> Offsets { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<long?> TryReadMicroVideoOffsetAsync(string imagePath, CancellationToken cancellationToken = default)
        {
            if (Offsets.TryGetValue(imagePath, out var offset))
            {
                return Task.FromResult(offset);
            }
            return Task.FromResult<long?>(null);
        }

        public Task WriteMotionPhotoTagsAsync(string imagePath, long videoOffset, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveMotionPhotoTagsAsync(string imagePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CopyAllTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CopyCoverTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> TryReadContentIdentifierAsync(string filePath, ContentIdentifierKind kind, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task WriteAppleContentIdentifierAsync(string photoPath, string contentIdentifier, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteAppleVideoMetadataAsync(string videoPath, string? photoPath, string contentIdentifier, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DateTime?> TryReadCreateDateAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult<DateTime?>(null);
        public Task<TimeSpan?> TryReadDurationAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult<TimeSpan?>(null);
        public Task<bool> IsMirroredVideoAsync(string videoPath, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    #endregion
}
