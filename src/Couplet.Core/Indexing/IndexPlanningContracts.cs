using Couplet.Core.Contracts;
using Couplet.Core.Languages;

namespace Couplet.Core.Indexing;

/// <summary>
/// 一个 generation 内持久化的文件级增量规划输入。
/// </summary>
public sealed class IndexPlanningFile
{
    /// <summary>获取 workspace-relative path。</summary>
    public required string Path { get; init; }

    /// <summary>获取内容 SHA-256。</summary>
    public required string ContentHash { get; init; }

    /// <summary>获取语义等级。</summary>
    public required SemanticTier SemanticTier { get; init; }

    /// <summary>获取适配器 ID。</summary>
    public required string AdapterId { get; init; }

    /// <summary>获取适配器版本。</summary>
    public required string AdapterVersion { get; init; }
}

/// <summary>
/// 与已发布 generation 一同租用的轻量增量规划 snapshot。
/// </summary>
/// <remarks>
/// 该合同只保存比较文件变化所需的元数据，不复制符号、chunk 或源码正文。
/// </remarks>
public sealed class IndexPlanningSnapshot
{
    /// <summary>获取合同版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.IndexPlanningSnapshot;

    /// <summary>获取工作区 ID。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>获取不含凭证和本机绝对路径的仓库身份。</summary>
    public string? RepositoryIdentity { get; init; }

    /// <summary>获取隔离不同 worktree 的稳定身份。</summary>
    public string? WorktreeIdentity { get; init; }

    /// <summary>获取 snapshot 对应的 Git branch。</summary>
    public string? Branch { get; init; }

    /// <summary>获取 snapshot 对应的 Git HEAD。</summary>
    public string? HeadRevision { get; init; }

    /// <summary>获取源码 revision。</summary>
    public required string SourceRevision { get; init; }

    /// <summary>获取索引 revision。</summary>
    public required string IndexRevision { get; init; }

    /// <summary>获取 producer 版本。</summary>
    public required IReadOnlyList<string> ProducerVersions { get; init; }

    /// <summary>获取按 path 排序的文件级规划输入。</summary>
    public required IReadOnlyList<IndexPlanningFile> Files { get; init; }
}
