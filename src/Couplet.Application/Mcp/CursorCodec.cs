using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Couplet.Application.Serialization;
using Couplet.Core.Mcp;

namespace Couplet.Application.Mcp;

/// <summary>
/// 编解码带完整性校验且绑定 revision 的 opaque cursor。
/// </summary>
public sealed class CursorCodec
{
    private readonly byte[] _key;

    /// <summary>
    /// 初始化 cursor codec。
    /// </summary>
    /// <param name="key">至少 32 字节的进程密钥。</param>
    public CursorCodec(ReadOnlySpan<byte> key)
    {
        if (key.Length < 32)
        {
            throw new ArgumentException("Cursor key must contain at least 32 bytes.", nameof(key));
        }

        _key = key.ToArray();
    }

    /// <summary>
    /// 编码 cursor payload。
    /// </summary>
    /// <param name="payload">不包含源码正文或凭证的 payload。</param>
    /// <returns>opaque cursor。</returns>
    public string Encode(CursorPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, CoupletJsonContext.Default.CursorPayload);
        byte[] signature = HMACSHA256.HashData(_key, body);
        return $"{Base64Url(body)}.{Base64Url(signature)}";
    }

    /// <summary>
    /// 解码并校验 cursor 绑定。
    /// </summary>
    /// <param name="cursor">opaque cursor。</param>
    /// <param name="workspaceId">预期工作区 ID。</param>
    /// <param name="tool">预期工具名。</param>
    /// <param name="queryHash">预期请求摘要。</param>
    /// <param name="indexRevision">预期索引 revision。</param>
    /// <param name="now">当前 UTC 时间。</param>
    /// <param name="payload">成功时返回 payload。</param>
    /// <returns>成功为 true；篡改、过期或绑定不匹配为 false。</returns>
    public bool TryDecode(
        string cursor,
        string workspaceId,
        string tool,
        string queryHash,
        string indexRevision,
        DateTimeOffset now,
        out CursorPayload? payload)
    {
        payload = null;
        string[] parts = cursor.Split('.');
        if (parts.Length != 2 || !TryBase64Url(parts[0], out byte[] body) || !TryBase64Url(parts[1], out byte[] signature))
        {
            return false;
        }

        byte[] expected = HMACSHA256.HashData(_key, body);
        if (signature.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(signature, expected))
        {
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize(body, CoupletJsonContext.Default.CursorPayload);
        }
        catch (JsonException)
        {
            return false;
        }

        return payload is not null
            && payload.ExpiresAtUtc > now
            && string.Equals(payload.WorkspaceId, workspaceId, StringComparison.Ordinal)
            && string.Equals(payload.Tool, tool, StringComparison.Ordinal)
            && string.Equals(payload.QueryHash, queryHash, StringComparison.Ordinal)
            && string.Equals(payload.IndexRevision, indexRevision, StringComparison.Ordinal);
    }

    /// <summary>
    /// 计算不暴露请求正文的稳定 SHA-256 摘要。
    /// </summary>
    /// <param name="canonicalRequest">规范化请求 JSON。</param>
    /// <returns>小写十六进制 SHA-256。</returns>
    public static string HashRequest(string canonicalRequest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRequest);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant();
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64Url(string value, out byte[] bytes)
    {
        try
        {
            string padded = value.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch
            {
                2 => "==",
                3 => "=",
                _ => string.Empty,
            };
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
