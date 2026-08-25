using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Couplet.Core.Graph;

/// <summary>
/// 创建跨进程、平台和索引 revision 稳定的代码实体标识符。
/// </summary>
public static class StableId
{
    private const int _hashBytes = 20;

    /// <summary>
    /// 根据规范化仓库和 worktree 身份创建工作区 ID。
    /// </summary>
    /// <param name="repositoryIdentity">不含凭证的规范化仓库身份。</param>
    /// <param name="worktreeIdentity">规范化 worktree 身份。</param>
    /// <returns>带类型前缀的稳定 ID。</returns>
    public static string CreateWorkspace(string repositoryIdentity, string worktreeIdentity) =>
        Create("workspace", NormalizeIdentity(repositoryIdentity), NormalizeIdentity(worktreeIdentity));

    /// <summary>
    /// 根据工作区和规范化相对路径创建文件 ID；rename 会得到新 ID。
    /// </summary>
    /// <param name="workspaceId">工作区 ID。</param>
    /// <param name="workspaceRelativePath">工作区相对路径。</param>
    /// <returns>带类型前缀的稳定 ID。</returns>
    public static string CreateFile(string workspaceId, string workspaceRelativePath) =>
        Create("file", Require(workspaceId), NormalizePath(workspaceRelativePath));

    /// <summary>
    /// 根据语义身份创建符号 ID；仅移动源码位置不会改变 ID。
    /// </summary>
    /// <param name="workspaceId">工作区 ID。</param>
    /// <param name="language">语言标识符。</param>
    /// <param name="qualifiedIdentity">包含容器、签名和重载信息的限定身份。</param>
    /// <returns>带类型前缀的稳定 ID。</returns>
    public static string CreateSymbol(string workspaceId, string language, string qualifiedIdentity) =>
        Create("symbol", Require(workspaceId), NormalizeToken(language), Require(qualifiedIdentity).Normalize());

    /// <summary>
    /// 根据文件、内容和序号创建 chunk ID。
    /// </summary>
    /// <param name="fileId">文件 ID。</param>
    /// <param name="contentHash">内容 SHA-256。</param>
    /// <param name="ordinal">文件内稳定序号。</param>
    /// <returns>带类型前缀的稳定 ID。</returns>
    public static string CreateChunk(string fileId, string contentHash, int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        return Create("chunk", Require(fileId), NormalizeToken(contentHash), ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 根据边的端点、类型和证据身份创建关系 ID。
    /// </summary>
    /// <param name="sourceId">起点 ID。</param>
    /// <param name="targetId">终点 ID。</param>
    /// <param name="kind">关系类型。</param>
    /// <param name="evidenceIdentity">区分平行边的证据身份。</param>
    /// <returns>带类型前缀的稳定 ID。</returns>
    public static string CreateRelation(
        string sourceId,
        string targetId,
        CodeRelationKind kind,
        string evidenceIdentity) =>
        Create(
            "relation",
            Require(sourceId),
            Require(targetId),
            kind.ToString().ToLowerInvariant(),
            Require(evidenceIdentity).Normalize());

    private static string Create(string domain, params string[] fields)
    {
        var writer = new ArrayBufferWriter<byte>();
        WriteField(writer, "couplet.stable_id.v1");
        WriteField(writer, domain);
        foreach (string field in fields)
        {
            WriteField(writer, field);
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(writer.WrittenSpan, hash);
        return $"cpl_{domain}_{Convert.ToHexString(hash[.._hashBytes]).ToLowerInvariant()}";
    }

    private static void WriteField(ArrayBufferWriter<byte> writer, string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> destination = writer.GetSpan(sizeof(int) + byteCount);
        BinaryPrimitives.WriteInt32BigEndian(destination, byteCount);
        Encoding.UTF8.GetBytes(value, destination[sizeof(int)..]);
        writer.Advance(sizeof(int) + byteCount);
    }

    private static string NormalizeIdentity(string value) =>
        Require(value).Replace('\\', '/').TrimEnd('/').Normalize();

    private static string NormalizePath(string value)
    {
        string path = Require(value).Replace('\\', '/').TrimStart('/').Normalize();
        if (path.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Path must be normalized and workspace-relative.", nameof(value));
        }

        return path;
    }

    private static string NormalizeToken(string value) => Require(value).Trim().ToLowerInvariant();

    private static string Require(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}
