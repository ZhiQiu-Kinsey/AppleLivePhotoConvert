using System.Security.Cryptography;

namespace LivePhotoConvert.Core.External.Tools;

/// <summary>下载包完整性校验失败（哈希不符或清单未提供哈希）。</summary>
public sealed class ToolIntegrityException(string message) : Exception(message);

/// <summary>
/// 边下载边计算哈希，避免下载完再整包读一遍。
/// </summary>
internal sealed class ToolIntegrity : IDisposable
{
    private readonly IncrementalHash _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly IncrementalHash? _sha512;
    private readonly ToolPackage _package;

    public ToolIntegrity(ToolPackage package)
    {
        if (!package.IsVerified)
        {
            throw new ToolIntegrityException($"下载包 {package.Id} 没有可信的 SHA256，拒绝安装。");
        }

        _package = package;
        if (package.Integrity is not null)
        {
            _sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        _sha256.AppendData(data);
        _sha512?.AppendData(data);
    }

    /// <exception cref="ToolIntegrityException">任一哈希与清单不符</exception>
    public void Verify()
    {
        Span<byte> sha256 = stackalloc byte[32];
        _sha256.GetHashAndReset(sha256);
        var actual = Convert.ToHexStringLower(sha256);
        if (!actual.Equals(_package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolIntegrityException($"SHA256 不匹配：期望 {_package.Sha256}，实际 {actual}。");
        }

        if (_sha512 is not null && TryParseSha512(_package.Integrity, out var expected))
        {
            Span<byte> sha512 = stackalloc byte[64];
            _sha512.GetHashAndReset(sha512);
            if (!sha512.SequenceEqual(expected))
            {
                throw new ToolIntegrityException($"integrity 不匹配：期望 {_package.Integrity}，实际 sha512-{Convert.ToBase64String(sha512)}。");
            }
        }
    }

    /// <summary>解析 npm 的 <c>sha512-Base64</c> 形式。</summary>
    public static bool TryParseSha512(string? integrity, out byte[] digest)
    {
        const string Prefix = "sha512-";
        digest = [];
        if (integrity is null || !integrity.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var buffer = new byte[64];
        if (!Convert.TryFromBase64String(integrity[Prefix.Length..], buffer, out var written) || written != 64)
        {
            return false;
        }

        digest = buffer;
        return true;
    }

    public void Dispose()
    {
        _sha256.Dispose();
        _sha512?.Dispose();
    }
}
