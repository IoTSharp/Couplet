using System.Text.Json.Serialization;

namespace Couplet.Core.Workspaces;

/// <summary>
/// 工作区文件发现后的处理结果。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceFileDisposition>))]
public enum WorkspaceFileDisposition
{
    /// <summary>文件进入索引输入。</summary>
    Included,
    /// <summary>文件被 Git 或用户 ignore 规则排除。</summary>
    Ignored,
    /// <summary>文件被优先级更高的 deny 规则排除。</summary>
    Denied,
    /// <summary>文件被识别为二进制。</summary>
    Binary,
    /// <summary>文件被识别为生成文件。</summary>
    Generated,
    /// <summary>符号链接解析后越过工作区边界。</summary>
    SymlinkOutside,
    /// <summary>文件在发现期间无法稳定读取。</summary>
    Unreadable,
}

/// <summary>
/// 描述一个工作区文件的规范化发现结果。
/// </summary>
public sealed class WorkspaceFileDescriptor
{
    /// <summary>获取使用正斜杠的工作区相对路径。</summary>
    public required string Path { get; init; }
    /// <summary>获取文件字节长度。</summary>
    public required long Length { get; init; }
    /// <summary>获取内容 SHA-256；未读取内容时为空。</summary>
    public string? ContentHash { get; init; }
    /// <summary>获取文件处理结果。</summary>
    public required WorkspaceFileDisposition Disposition { get; init; }
    /// <summary>获取稳定原因码。</summary>
    public required string Reason { get; init; }
    /// <summary>获取规范化语言标识符。</summary>
    public required string Language { get; init; }
    /// <summary>获取是否只能按文本处理而不执行语义解析。</summary>
    public required bool TextOnly { get; init; }
    /// <summary>获取是否为工作区内的符号链接。</summary>
    public required bool IsSymlink { get; init; }
}

/// <summary>
/// 描述一个 Git 或普通目录工作区的确定性发现结果。
/// </summary>
public sealed class WorkspaceDiscoveryResult
{
    /// <summary>获取 workspace discovery 合同版本。</summary>
    public string SchemaVersion { get; init; } = Contracts.ContractVersions.WorkspaceDiscovery;
    /// <summary>获取工作区 stable ID。</summary>
    public required string WorkspaceId { get; init; }
    /// <summary>获取不含凭证和本机绝对路径的仓库身份。</summary>
    public required string RepositoryIdentity { get; init; }
    /// <summary>获取隔离不同 worktree 的稳定身份。</summary>
    public required string WorktreeIdentity { get; init; }
    /// <summary>获取当前 Git branch；非 Git 或 detached HEAD 时为空。</summary>
    public string? Branch { get; init; }
    /// <summary>获取当前 Git HEAD；无提交或非 Git 时为空。</summary>
    public string? HeadRevision { get; init; }
    /// <summary>获取包含 dirty digest 的源码 revision。</summary>
    public required string SourceRevision { get; init; }
    /// <summary>获取工作区是否包含影响索引输入的未提交内容。</summary>
    public required bool IsDirty { get; init; }
    /// <summary>获取按路径排序的全部发现结果。</summary>
    public required IReadOnlyList<WorkspaceFileDescriptor> Files { get; init; }
}

/// <summary>
/// 工作区发现时使用的安全和文件策略。
/// </summary>
public sealed class WorkspaceDiscoveryPolicy
{
    /// <summary>获取用户追加的 ignore glob。</summary>
    public required IReadOnlyList<string> IgnorePatterns { get; init; }
    /// <summary>获取优先于 ignore/include 的 deny glob。</summary>
    public required IReadOnlyList<string> DenyPatterns { get; init; }
    /// <summary>获取默认不进入索引的生成文件 glob。</summary>
    public required IReadOnlyList<string> GeneratedPatterns { get; init; }
    /// <summary>获取执行语义解析的最大单文件字节数；更大文本仍以 text-only 进入索引。</summary>
    public required long MaxSemanticFileBytes { get; init; }
}

/// <summary>
/// 一批需要重新发现和规划的工作区文件变化。
/// </summary>
public sealed class WorkspaceChangeBatch
{
    /// <summary>获取稳定排序且去重的 workspace-relative paths。</summary>
    public required IReadOnlyList<string> Paths { get; init; }
    /// <summary>获取是否因溢出或 watcher 错误必须执行完整重新发现。</summary>
    public required bool RequiresFullRescan { get; init; }
    /// <summary>获取 full rescan 的稳定原因码。</summary>
    public string? Reason { get; init; }
}
