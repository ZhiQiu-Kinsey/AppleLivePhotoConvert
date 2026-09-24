using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using LivePhotoConvert.Core.External.Tools;

namespace LivePhotoConvert.Core.Tests.Support;

/// <summary>
/// 构造工具下载包、清单与假 HTTP 源，供安装器测试使用。
/// </summary>
internal static class ToolPackages
{
    public const string ToolExecutable = "fake-tool";
    public const string InstallDirectory = "fake";

    public static string ExecutableFileName => OperatingSystem.IsWindows() ? ToolExecutable + ".exe" : ToolExecutable;

    /// <summary>py7zr 生成：pkg/fake-tool.exe、pkg/lib/dep.dll、other/readme.txt（BCJ + LZMA2）。</summary>
    public const string SevenZipGood =
        "N3q8ryccAAQQVTzpwAAAAAAAAAAWAAAAAAAAAElHKuQBAB8jIS9iaW4vc2gKZWNobyAxLjIuMwpkZXBlbmRlbmN5eADgAMoAlF0AAIEzB64P0Es5PJ85EJxt+2aOKsiMI2VEGWxfJMkDwiH7u5rZiV4R8HbGrs6ZsMbh9eCKcd27zsQ6vhR//Ouf2SZHdmKvZ1zISz3ziM6Vvv0AEpkYYhL7hbhBrxKrvm+N+uI5j8lBGfIf1Ctn8dC4x47WN1yiN418qkrXjO3q6h7krmjEkXqkiMn8XNYfn84YZZcAAAAXBiQBCYCcAAcLAQABISEBGAyAywAA";

    /// <summary>py7zr 生成：pkg/fake-tool.exe 与越界条目 ../evil.txt。</summary>
    public const string SevenZipTraversal =
        "N3q8ryccAAQNDjNjjwAAAAAAAAAWAAAAAAAAAO6b4m0BAAd0b29sZXZpbADgAJQAe10AAIEzB64PzpwGxQkKkA9ecaK8AbVkHbyg4QOK1D0Iot0YaLCJGrdCNMtVdeh7IVtyC0ZMJR+isHsS7m/rvNqMX3GGsfWdVE2f/2Yyj0ugAdjk0HumtYSoY7xilK0MAiBA0emt5BvY+it/Qynxk1Q22/v8XXDou29oAAAAABcGDAEJgIMABwsBAAEhIQEYDICVAAA=";

    public static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    public static byte[] Tgz(params TarEntry[] entries)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                writer.WriteEntry(entry);
            }
        }

        return buffer.ToArray();
    }

    public static PaxTarEntry TarFile(string name, string content) =>
        new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)) };

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Integrity(byte[] data) => "sha512-" + Convert.ToBase64String(SHA512.HashData(data));

    public static ToolPackage Package(
        string id,
        string url,
        byte[]? content,
        ToolArchiveFormat format = ToolArchiveFormat.Tgz,
        string root = "package",
        string? entry = null,
        bool githubRelease = false,
        string? sha256 = null,
        string? integrity = null,
        IReadOnlyList<string>? include = null) =>
        new(id, "ToolSourceTest", ToolRuntime.CurrentRid, "1.2.3", url, format, root, entry ?? ExecutableFileName,
            sha256 ?? (content is null ? null : Sha256(content)), integrity, include, githubRelease);

    public static ToolManifest Manifest(params ToolPackage[] packages) => Manifest([], packages);

    public static ToolManifest Manifest(IReadOnlyList<string> githubMirrors, params ToolPackage[] packages) =>
        new(ToolManifest.CurrentSchemaVersion, githubMirrors,
            [new ToolDefinition(ToolId.ExifTool, "Fake Tool", ToolExecutable, InstallDirectory, ["--version"], "https://example.com/", packages)]);

    /// <summary>不启动进程的试运行：主程序存在即视为可运行。</summary>
    public static ToolInstallerOptions Options(TimeSpan? idleTimeout = null, Action<string, string>? moveDirectory = null) => new()
    {
        IdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(10),
        Probe = (_, executable, _) => Task.FromResult<string?>(File.Exists(executable) ? File.ReadAllText(executable).Trim() : throw new FileNotFoundException(executable)),
        MoveDirectory = moveDirectory
    };
}

/// <summary>按 URL 返回预设内容的 HTTP 处理器，记录请求顺序。</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<string> Requests { get; } = [];

    public FakeHttpHandler Serve(string url, byte[] content)
    {
        _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        return this;
    }

    public FakeHttpHandler Fail(string url, HttpStatusCode status = HttpStatusCode.NotFound)
    {
        _routes[url] = () => new HttpResponseMessage(status);
        return this;
    }

    /// <summary>先发出一部分数据，然后一直不再发送。</summary>
    public FakeHttpHandler Stall(string url, byte[] prefix)
    {
        _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(prefix)) };
        return this;
    }

    public HttpClient CreateClient() => new(this) { Timeout = Timeout.InfiniteTimeSpan };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        lock (Requests)
        {
            Requests.Add(url);
        }

        return Task.FromResult(_routes.TryGetValue(url, out var respond) ? respond() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class StallingStream(byte[] prefix) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
