using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 外部工具自动下载与完整解压服务
/// </summary>
public static class ToolDownloader
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 获取优先使用的工具存放目录（具有写权限的本地应用数据目录或程序目录）
    /// </summary>
    public static string LocalAppDataToolDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LivePhotoConvert", "tools");

    /// <summary>
    /// 获取当前可写的工具目录（优先程序根目录下的 tools，无权限时自动降级到 LocalAppData）
    /// </summary>
    public static string GetWritableToolDirectory()
    {
        var processDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(processDir))
        {
            var processTools = Path.Combine(processDir, "tools");
            if (TryEnsureDirectoryWritable(processTools))
            {
                return processTools;
            }
        }

        var appBaseTools = Path.Combine(AppContext.BaseDirectory, "tools");
        if (TryEnsureDirectoryWritable(appBaseTools))
        {
            return appBaseTools;
        }

        var localAppTools = LocalAppDataToolDirectory;
        Directory.CreateDirectory(localAppTools);
        return localAppTools;
    }

    /// <summary>
    /// 下载并完整解压外部工具组件（包含全部可执行文件及运行依赖模块）
    /// </summary>
    /// <param name="tool">工具下载元数据</param>
    /// <param name="customMirror">用户自定义镜像加速前缀</param>
    /// <param name="progress">下载进度回调接口</param>
    /// <param name="onSourceSwitch">当切换到备选源时的通知回调 (当前尝试的源, 切换原因)</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>最终生成并就绪的可执行文件完整路径</returns>
    /// <exception cref="InvalidOperationException">所有下载源均尝试失败</exception>
    public static async Task<string> DownloadAndExtractAsync(
        ToolDownloadInfo tool,
        string? customMirror = null,
        IProgress<DownloadProgressReport>? progress = null,
        Action<ToolDownloadSource, Exception?>? onSourceSwitch = null,
        CancellationToken cancellationToken = default)
    {
        var targetDir = GetWritableToolDirectory();
        Directory.CreateDirectory(targetDir);
        var finalExePath = Path.Combine(targetDir, tool.TargetExecutableName);
        var tempArchiveFile = Path.Combine(targetDir, $"{tool.TargetExecutableName}_{Guid.NewGuid():N}.tmp.bin");
        var sources = tool.GetEffectiveSources(customMirror);
        var failureReasons = new List<(string Source, string Error)>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onSourceSwitch?.Invoke(source, null);
            try
            {
                // 1. 下载完整压缩包
                await DownloadFileAsync(source, tempArchiveFile, progress, cancellationToken);
                // 2. 完整解压主程序及运行库依赖到 targetDir
                ExtractAllFromArchive(tempArchiveFile, targetDir, finalExePath, tool);
                // 3. 真实启动探测校验，确保可执行文件 100% 满血可用
                return ToolLocator.IsValidTool(finalExePath) ? finalExePath : throw new InvalidOperationException($"解压后的 {tool.TargetExecutableName} 无法正常启动执行。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failureReasons.Add((source.Name, ex.Message));
                onSourceSwitch?.Invoke(source, ex);
            }
            finally
            {
                FileHelper.TryDeleteFile(tempArchiveFile);
            }
        }

        var details = string.Join("; ", failureReasons.Select(f => $"[{f.Source}]: {f.Error}"));
        throw new InvalidOperationException($"所有下载源均尝试失败。\n失败原因：{details}\n" + $"您可以手动下载该工具（参考 {tool.ManualDownloadHelpUrl}），并将 {tool.TargetExecutableName} 放入以下目录之一：\n" + $"  1. 程序目录: {AppContext.BaseDirectory}\n" + $"  2. 工具目录: {targetDir}\n" + $"  3. 或将其所在目录加入系统环境变量 PATH。");
    }

    /// <summary>
    /// 从指定源流式下载文件
    /// </summary>
    private static async Task DownloadFileAsync(ToolDownloadSource source, string destinationFilePath, IProgress<DownloadProgressReport>? progress, CancellationToken cancellationToken)
    {
        using var clientHandler = new HttpClientHandler();
        clientHandler.AllowAutoRedirect = true;
        clientHandler.AutomaticDecompression = System.Net.DecompressionMethods.All;
        using var client = new HttpClient(clientHandler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(ConnectTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        var stopwatch = Stopwatch.StartNew();
        var lastReportTime = 0L;
        var lastReportBytes = 0L;
        var currentSpeed = 0.0;
        progress?.Report(new DownloadProgressReport(source.Name, 0, totalBytes, 0));
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;
            var elapsed = stopwatch.ElapsedMilliseconds;
            if (elapsed - lastReportTime >= 150 || (totalRead == totalBytes))
            {
                var timeDiff = (elapsed - lastReportTime) / 1000.0;
                if (timeDiff > 0)
                {
                    currentSpeed = (totalRead - lastReportBytes) / timeDiff;
                }
                lastReportTime = elapsed;
                lastReportBytes = totalRead;
                progress?.Report(new DownloadProgressReport(source.Name, totalRead, totalBytes, currentSpeed));
            }
        }
    }

    /// <summary>
    /// 从压缩包 (Zip 或 Tar.Gz / Tgz) 中完整解压主程序和所有关联运行库文件
    /// </summary>
    private static void ExtractAllFromArchive(string archivePath, string targetDirectory, string finalExePath, ToolDownloadInfo tool)
    {
        var isGzip = false;
        var is7z = false;
        using (var stream = File.OpenRead(archivePath))
        {
            var header = new byte[6];
            var read = stream.Read(header, 0, 6);
            if (read >= 2 && header[0] == 0x1F && header[1] == 0x8B)
            {
                isGzip = true;
            }
            else if (read >= 6 && header[0] == 0x37 && header[1] == 0x7A && header[2] == 0xBC && header[3] == 0xAF && header[4] == 0x27 && header[5] == 0x1C)
            {
                is7z = true;
            }
        }

        if (is7z || archivePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            var tempExtractDir = Path.Combine(targetDirectory, $"extract_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempExtractDir);
            try
            {
                var tarExe = ToolLocator.Find("tar.exe") ?? "tar.exe";
                var result = ProcessRunner.RunAsync(tarExe, ["-xf", archivePath, "-C", tempExtractDir]).GetAwaiter().GetResult();
                if (!result.Success)
                {
                    throw new InvalidOperationException($"解压 .7z 压缩包失败：{result.StandardError}");
                }

                // 递归遍历解压内容，扁平化复制所有 exe 和 dll 到 targetDirectory
                foreach (var file in Directory.EnumerateFiles(tempExtractDir, "*", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(file);
                    var destPath = Path.Combine(targetDirectory, fileName);
                    File.Copy(file, destPath, overwrite: true);
                }

                if (File.Exists(finalExePath))
                {
                    return;
                }

                throw new InvalidOperationException($"在 7z 压缩包中未匹配到目标可执行文件 {tool.TargetExecutableName}。");
            }
            finally
            {
                FileHelper.TryDeleteDirectory(tempExtractDir);
            }
        }

        if (isGzip || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) || archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(archivePath);
            using var gzip = new GZipStream(fs, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            var mainExeExtracted = false;
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                {
                    continue;
                }
                var normalized = entry.Name.Replace('\\', '/');
                // 1. 主可执行文件
                if (tool.ZipEntryFilter(entry.Name))
                {
                    using var outFs = File.Create(finalExePath);
                    entry.DataStream.CopyTo(outFs);
                    mainExeExtracted = true;
                    continue;
                }

                // 2. 关联依赖目录 (如 exiftool_files 运行库)
                var depIndex = normalized.IndexOf("exiftool_files/", StringComparison.OrdinalIgnoreCase);
                if (depIndex >= 0)
                {
                    var relativePath = normalized[depIndex..];
                    var outPath = Path.Combine(targetDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    var dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    using var outFs = File.Create(outPath);
                    entry.DataStream.CopyTo(outFs);
                }
            }

            if (mainExeExtracted)
            {
                return;
            }

            throw new InvalidOperationException($"在 Tar 压缩包中未匹配到目标可执行文件 {tool.TargetExecutableName}。");
        }

        using var archive = ZipFile.OpenRead(archivePath);
        var zipMainExtracted = false;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue;
            }
            var normalized = entry.FullName.Replace('\\', '/');
            // 1. 主可执行文件
            if (tool.ZipEntryFilter(entry.FullName))
            {
                entry.ExtractToFile(finalExePath, overwrite: true);
                zipMainExtracted = true;
                continue;
            }
            // 2. 关联依赖目录 (如 exiftool_files 运行库)
            var depIndex = normalized.IndexOf("exiftool_files/", StringComparison.OrdinalIgnoreCase);
            if (depIndex >= 0)
            {
                var relativePath = normalized[depIndex..];
                var outPath = Path.Combine(targetDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                entry.ExtractToFile(outPath, overwrite: true);
            }
        }
        if (!zipMainExtracted)
        {
            throw new InvalidOperationException($"在 Zip 压缩包中未匹配到目标可执行文件 {tool.TargetExecutableName}。");
        }
    }

    /// <summary>
    /// 测试目录是否可写
    /// </summary>
    private static bool TryEnsureDirectoryWritable(string directoryPath)
    {
        try
        {
            Directory.CreateDirectory(directoryPath);
            var testFile = Path.Combine(directoryPath, $".write_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "ok");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

