using System.Text.Json.Serialization;
using Couplet.Core.Contracts;

namespace Couplet.Core.Indexing;

/// <summary>
/// generation 生命周期状态。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GenerationState>))]
public enum GenerationState
{
    /// <summary>尚未对查询可见。</summary>
    Staging,
    /// <summary>已原子发布且可租用。</summary>
    Published,
    /// <summary>已被更新 generation 替代。</summary>
    Retired,
    /// <summary>租约释放后已完成清理。</summary>
    Deleted,
}

/// <summary>
/// 一个索引 generation 的跨模型计数。
/// </summary>
public sealed class GenerationCounts
{
    /// <summary>获取文件数。</summary>
    public required long Files { get; init; }
    /// <summary>获取符号数。</summary>
    public required long Symbols { get; init; }
    /// <summary>获取 chunk 数。</summary>
    public required long Chunks { get; init; }
    /// <summary>获取全文文档数。</summary>
    public required long FullTextDocuments { get; init; }
    /// <summary>获取向量数。</summary>
    public required long Vectors { get; init; }
    /// <summary>获取图节点数。</summary>
    public required long GraphNodes { get; init; }
    /// <summary>获取图边数。</summary>
    public required long GraphEdges { get; init; }
}

/// <summary>
/// 冻结一个待发布或已发布 generation 的 manifest。
/// </summary>
public sealed class GenerationManifest
{
    /// <summary>获取合同版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.Generation;
    /// <summary>获取工作区 ID。</summary>
    public required string WorkspaceId { get; init; }
    /// <summary>获取源码 revision。</summary>
    public required string SourceRevision { get; init; }
    /// <summary>获取单调索引 revision。</summary>
    public required string IndexRevision { get; init; }
    /// <summary>获取前一个已发布索引 revision。</summary>
    public string? PreviousIndexRevision { get; init; }
    /// <summary>获取 schema 版本。</summary>
    public required string CodeGraphSchemaVersion { get; init; }
    /// <summary>获取 parser/model 版本身份。</summary>
    public required IReadOnlyList<string> ProducerVersions { get; init; }
    /// <summary>获取跨模型记录计数。</summary>
    public required GenerationCounts Counts { get; init; }
    /// <summary>获取 manifest 内容校验和。</summary>
    public required string Checksum { get; init; }
    /// <summary>获取 generation 状态。</summary>
    public required GenerationState State { get; init; }
    /// <summary>获取 UTC 创建时间。</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>
/// 描述一个 generation 的确定性删除和清理合同。
/// </summary>
public sealed class GenerationDeletion
{
    /// <summary>获取工作区 ID。</summary>
    public required string WorkspaceId { get; init; }
    /// <summary>获取待删除索引 revision。</summary>
    public required string IndexRevision { get; init; }
    /// <summary>获取替代该 generation 的 revision。</summary>
    public required string SupersededBy { get; init; }
    /// <summary>获取删除原因码。</summary>
    public required string Reason { get; init; }
    /// <summary>获取删除前必须归零的查询租约数。</summary>
    public required long RequiredLeaseCount { get; init; }
}

/// <summary>
/// 校验 generation 发布与清理不变量。
/// </summary>
public static class GenerationContractValidator
{
    /// <summary>
    /// 校验 generation manifest。
    /// </summary>
    /// <param name="manifest">待校验 manifest。</param>
    /// <returns>稳定排序的问题码。</returns>
    public static IReadOnlyList<string> Validate(GenerationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        if (manifest.SchemaVersion != ContractVersions.Generation)
        {
            problems.Add("generation_schema_unsupported");
        }

        if (string.IsNullOrWhiteSpace(manifest.WorkspaceId)
            || string.IsNullOrWhiteSpace(manifest.SourceRevision)
            || string.IsNullOrWhiteSpace(manifest.IndexRevision)
            || string.IsNullOrWhiteSpace(manifest.Checksum))
        {
            problems.Add("generation_identity_incomplete");
        }

        if (manifest.CodeGraphSchemaVersion != ContractVersions.CodeGraph)
        {
            problems.Add("code_graph_schema_unsupported");
        }

        if (manifest.ProducerVersions.Count == 0)
        {
            problems.Add("generation_producer_missing");
        }

        GenerationCounts counts = manifest.Counts;
        if (counts.Files < 0 || counts.Symbols < 0 || counts.Chunks < 0
            || counts.FullTextDocuments < 0 || counts.Vectors < 0
            || counts.GraphNodes < 0 || counts.GraphEdges < 0)
        {
            problems.Add("generation_count_negative");
        }

        if (manifest.PreviousIndexRevision == manifest.IndexRevision)
        {
            problems.Add("generation_previous_revision_self_reference");
        }

        return problems.ToArray();
    }

    /// <summary>
    /// 校验 retired generation 的删除前置条件。
    /// </summary>
    /// <param name="deletion">删除合同。</param>
    /// <param name="activeIndexRevision">当前 active revision。</param>
    /// <returns>稳定排序的问题码。</returns>
    public static IReadOnlyList<string> ValidateDeletion(
        GenerationDeletion deletion,
        string activeIndexRevision)
    {
        ArgumentNullException.ThrowIfNull(deletion);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeIndexRevision);
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        if (deletion.IndexRevision == activeIndexRevision)
        {
            problems.Add("active_generation_cannot_be_deleted");
        }

        if (deletion.SupersededBy != activeIndexRevision)
        {
            problems.Add("generation_superseding_revision_mismatch");
        }

        if (deletion.RequiredLeaseCount != 0)
        {
            problems.Add("generation_query_leases_active");
        }

        return problems.ToArray();
    }
}
