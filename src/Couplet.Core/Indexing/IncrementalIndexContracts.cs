using System.Text.Json.Serialization;
using Couplet.Core.Contracts;
using Couplet.Core.Graph;
using Couplet.Core.Languages;

namespace Couplet.Core.Indexing;

/// <summary>
/// 两个 workspace snapshots 间的文件变化类型。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<IndexFileChangeKind>))]
public enum IndexFileChangeKind
{
    /// <summary>新增文件。</summary>
    Added,
    /// <summary>内容或 producer 发生变化。</summary>
    Modified,
    /// <summary>文件从新 snapshot 删除。</summary>
    Deleted,
    /// <summary>内容相同但规范化路径发生变化。</summary>
    Renamed,
    /// <summary>文件与 producer 均未变化。</summary>
    Unchanged,
}

/// <summary>
/// 描述一个确定性的文件变化。
/// </summary>
public sealed class IndexFileChange
{
    /// <summary>获取变化类型。</summary>
    public required IndexFileChangeKind Kind { get; init; }
    /// <summary>获取新 snapshot 路径；删除时为空。</summary>
    public string? Path { get; init; }
    /// <summary>获取旧 snapshot 路径；新增时为空。</summary>
    public string? PreviousPath { get; init; }
    /// <summary>获取新内容 SHA-256；删除时为空。</summary>
    public string? ContentHash { get; init; }
}

/// <summary>
/// 一个待 staging 的确定性索引 snapshot。
/// </summary>
public sealed class WorkspaceIndexSnapshot
{
    /// <summary>获取工作区 ID。</summary>
    public required string WorkspaceId { get; init; }
    /// <summary>获取不含凭证和本机绝对路径的仓库身份。</summary>
    public string? RepositoryIdentity { get; init; }
    /// <summary>获取隔离不同 worktree 的稳定身份。</summary>
    public string? WorktreeIdentity { get; init; }
    /// <summary>获取 snapshot 对应的 Git branch；非 Git 或 detached HEAD 时为空。</summary>
    public string? Branch { get; init; }
    /// <summary>获取 snapshot 对应的 Git HEAD；无提交或非 Git 时为空。</summary>
    public string? HeadRevision { get; init; }
    /// <summary>获取源码 revision。</summary>
    public required string SourceRevision { get; init; }
    /// <summary>获取确定性索引 revision。</summary>
    public required string IndexRevision { get; init; }
    /// <summary>获取前一 active index revision。</summary>
    public string? PreviousIndexRevision { get; init; }
    /// <summary>获取 producer 版本。</summary>
    public required IReadOnlyList<string> ProducerVersions { get; init; }
    /// <summary>获取按 path 排序的已解析文件。</summary>
    public required IReadOnlyList<IndexedFile> Files { get; init; }
    /// <summary>获取按 path 排序的解析失败。</summary>
    public required IReadOnlyList<FileIndexFailure> Failures { get; init; }
}

/// <summary>
/// 描述一个 generation 的增量变化计划。
/// </summary>
public sealed class IncrementalIndexPlan
{
    /// <summary>获取工作区 ID。</summary>
    public required string WorkspaceId { get; init; }
    /// <summary>获取目标 source revision。</summary>
    public required string SourceRevision { get; init; }
    /// <summary>获取目标 index revision。</summary>
    public required string IndexRevision { get; init; }
    /// <summary>获取基线 index revision。</summary>
    public string? PreviousIndexRevision { get; init; }
    /// <summary>获取稳定排序的文件变化。</summary>
    public required IReadOnlyList<IndexFileChange> Changes { get; init; }
    /// <summary>获取是否因为 schema/parser 变化必须重建。</summary>
    public required bool RebuildRequired { get; init; }
    /// <summary>获取重建原因码。</summary>
    public string? RebuildReason { get; init; }
}

/// <summary>
/// SonnetDB Document/FullText staging collection 中的统一记录类型。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<IndexStorageRecordType>))]
public enum IndexStorageRecordType
{
    /// <summary>文件元数据。</summary>
    File,
    /// <summary>符号定义。</summary>
    Symbol,
    /// <summary>源码 chunk。</summary>
    Chunk,
}

/// <summary>
/// 写入 SonnetDB Document/FullText 的 source-generated JSON 记录。
/// </summary>
public sealed class IndexStorageDocument
{
    /// <summary>获取代码图 schema 版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.CodeGraph;
    /// <summary>获取记录类型。</summary>
    public required IndexStorageRecordType RecordType { get; init; }
    /// <summary>获取记录 stable ID。</summary>
    public required string StableId { get; init; }
    /// <summary>获取工作区 ID。</summary>
    public required string WorkspaceId { get; init; }
    /// <summary>获取 source revision。</summary>
    public required string SourceRevision { get; init; }
    /// <summary>获取 index revision。</summary>
    public required string IndexRevision { get; init; }
    /// <summary>获取 workspace-relative path。</summary>
    public required string Path { get; init; }
    /// <summary>获取语言。</summary>
    public required string Language { get; init; }
    /// <summary>获取内容 SHA-256。</summary>
    public required string ContentHash { get; init; }
    /// <summary>获取语义等级。</summary>
    public required SemanticTier SemanticTier { get; init; }
    /// <summary>获取适配器 ID。</summary>
    public required string AdapterId { get; init; }
    /// <summary>获取适配器版本。</summary>
    public required string AdapterVersion { get; init; }
    /// <summary>获取展示名称。</summary>
    public string? DisplayName { get; init; }
    /// <summary>获取限定符号身份。</summary>
    public string? QualifiedIdentity { get; init; }
    /// <summary>获取签名。</summary>
    public string? Signature { get; init; }
    /// <summary>获取容器 ID。</summary>
    public string? ContainerId { get; init; }
    /// <summary>获取实体类型。</summary>
    public CodeEntityKind? EntityKind { get; init; }
    /// <summary>获取定义或 chunk 范围。</summary>
    public SourceSpan? Span { get; init; }
    /// <summary>获取置信度。</summary>
    public Confidence? Confidence { get; init; }
    /// <summary>获取 chunk 序号。</summary>
    public int? Ordinal { get; init; }
    /// <summary>获取 chunk 正文。</summary>
    public string? Content { get; init; }
    /// <summary>获取用于 FullText 的有界检索文本。</summary>
    public required string SearchText { get; init; }
}

/// <summary>
/// 真实 SonnetDB staging 结果和发布边界。
/// </summary>
public sealed class IndexStageReport
{
    /// <summary>获取报告 schema 版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.IndexStage;
    /// <summary>获取 generation manifest。</summary>
    public required GenerationManifest Manifest { get; init; }
    /// <summary>获取 SonnetDB collection 名称。</summary>
    public required string CollectionName { get; init; }
    /// <summary>获取 staging 是否通过一致性校验。</summary>
    public required bool Staged { get; init; }
    /// <summary>获取 generation 是否已公开发布。</summary>
    public required bool Published { get; init; }
    /// <summary>获取阻塞发布的 capability gap。</summary>
    public string? BlockingGap { get; init; }
    /// <summary>获取不影响本次 staging 完整性但限制运行模式的稳定能力说明。</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];
    /// <summary>获取稳定排序的问题码。</summary>
    public required IReadOnlyList<string> Problems { get; init; }
}

/// <summary>
/// 一个查询不可见的 staging generation 重开检查结果。
/// </summary>
public sealed class StagingGenerationInspection
{
    /// <summary>获取检查合同版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.StagingInspection;
    /// <summary>获取工作区 ID。</summary>
    public required string WorkspaceId { get; init; }
    /// <summary>获取索引 revision。</summary>
    public required string IndexRevision { get; init; }
    /// <summary>获取 SonnetDB collection 名称。</summary>
    public required string CollectionName { get; init; }
    /// <summary>获取能够通过 source-generated JSON 读取的 manifest。</summary>
    public GenerationManifest? Manifest { get; init; }
    /// <summary>获取 manifest、Document、FullText 和 path indexes 是否构成完整 staging。</summary>
    public required bool Complete { get; init; }
    /// <summary>获取稳定排序的问题码。</summary>
    public required IReadOnlyList<string> Problems { get; init; }
}
