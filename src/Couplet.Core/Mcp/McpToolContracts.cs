using Couplet.Core.Graph;

namespace Couplet.Core.Mcp;

/// <summary>请求当前工作区与索引状态。</summary>
public sealed class WorkspaceStatusRequest : McpToolRequest;

/// <summary>工作区状态结果项。</summary>
public sealed class WorkspaceStatusItem
{
    /// <summary>获取文件数。</summary>
    public required long Files { get; init; }
    /// <summary>获取符号数。</summary>
    public required long Symbols { get; init; }
    /// <summary>获取关系数。</summary>
    public required long Relations { get; init; }
    /// <summary>获取 chunk 数。</summary>
    public required long Chunks { get; init; }
    /// <summary>获取 parser 版本。</summary>
    public required IReadOnlyList<string> ParserVersions { get; init; }
    /// <summary>获取 embedding 模型版本。</summary>
    public string? EmbeddingModel { get; init; }
    /// <summary>获取数据库字节数。</summary>
    public required long DatabaseBytes { get; init; }
    /// <summary>获取阻塞 gap。</summary>
    public required IReadOnlyList<string> BlockingGaps { get; init; }
    /// <summary>获取是否需要重建。</summary>
    public required bool RebuildRequired { get; init; }
}

/// <summary>请求 exact、全文、向量或混合代码搜索。</summary>
public sealed class CodeSearchRequest : McpToolRequest
{
    /// <summary>获取查询文本。</summary>
    public required string Query { get; init; }
    /// <summary>获取 exact、fulltext、vector 或 hybrid 模式。</summary>
    public required string Mode { get; init; }
    /// <summary>获取 workspace-relative path glob。</summary>
    public string? Path { get; init; }
    /// <summary>获取语言过滤器。</summary>
    public string? Language { get; init; }
    /// <summary>获取实体类型过滤器。</summary>
    public CodeEntityKind? Kind { get; init; }
    /// <summary>获取显式在线 provider ID；本地模式为空。</summary>
    public string? ProviderId { get; init; }
}

/// <summary>代码搜索结果项。</summary>
public sealed class CodeSearchItem
{
    /// <summary>获取 file、symbol 或 chunk stable ID。</summary>
    public required string Id { get; init; }
    /// <summary>获取结果类型。</summary>
    public required string Kind { get; init; }
    /// <summary>获取显示名称。</summary>
    public required string DisplayName { get; init; }
    /// <summary>获取总分。</summary>
    public required double Score { get; init; }
    /// <summary>获取分数组成。</summary>
    public required IReadOnlyList<ScorePart> ScoreParts { get; init; }
    /// <summary>获取证据 ID。</summary>
    public required IReadOnlyList<string> EvidenceIds { get; init; }
}

/// <summary>搜索分数组成。</summary>
public sealed class ScorePart
{
    /// <summary>获取分数来源。</summary>
    public required string Name { get; init; }
    /// <summary>获取分数值。</summary>
    public required double Value { get; init; }
}

/// <summary>按 stable ID 或限定身份查询符号。</summary>
public sealed class SymbolGetRequest : McpToolRequest
{
    /// <summary>获取 stable symbol ID。</summary>
    public string? SymbolId { get; init; }
    /// <summary>获取限定符号身份。</summary>
    public string? QualifiedIdentity { get; init; }
    /// <summary>获取语言过滤器。</summary>
    public string? Language { get; init; }
}

/// <summary>符号详情结果项。</summary>
public sealed class SymbolDetailsItem
{
    /// <summary>获取 symbol stable ID。</summary>
    public required string Id { get; init; }
    /// <summary>获取符号类型。</summary>
    public required CodeEntityKind Kind { get; init; }
    /// <summary>获取限定身份。</summary>
    public required string QualifiedIdentity { get; init; }
    /// <summary>获取签名。</summary>
    public required string Signature { get; init; }
    /// <summary>获取容器 stable ID。</summary>
    public string? ContainerId { get; init; }
    /// <summary>获取语言。</summary>
    public required string Language { get; init; }
    /// <summary>获取置信度。</summary>
    public required Confidence Confidence { get; init; }
    /// <summary>获取证据 ID。</summary>
    public required IReadOnlyList<string> EvidenceIds { get; init; }
}

/// <summary>查询符号的原生图关系。</summary>
public sealed class SymbolRelationsRequest : McpToolRequest
{
    /// <summary>获取起点 symbol ID。</summary>
    public required string SymbolId { get; init; }
    /// <summary>获取关系 allowlist。</summary>
    public required IReadOnlyList<CodeRelationKind> RelationKinds { get; init; }
    /// <summary>获取 outgoing、incoming 或 both 方向。</summary>
    public required string Direction { get; init; }
    /// <summary>获取最大深度。</summary>
    public required int MaxDepth { get; init; }
    /// <summary>获取最大 frontier。</summary>
    public required int MaxFrontier { get; init; }
}

/// <summary>符号关系结果项。</summary>
public sealed class SymbolRelationItem
{
    /// <summary>获取关系 stable ID。</summary>
    public required string RelationId { get; init; }
    /// <summary>获取关系类型。</summary>
    public required CodeRelationKind Kind { get; init; }
    /// <summary>获取起点 ID。</summary>
    public required string SourceId { get; init; }
    /// <summary>获取终点 ID。</summary>
    public required string TargetId { get; init; }
    /// <summary>获取相对请求起点的深度。</summary>
    public required int Depth { get; init; }
    /// <summary>获取置信度。</summary>
    public required Confidence Confidence { get; init; }
    /// <summary>获取证据 ID。</summary>
    public required IReadOnlyList<string> EvidenceIds { get; init; }
}

/// <summary>查询两个符号或构建目标间的有界依赖路径。</summary>
public sealed class DependencyPathRequest : McpToolRequest
{
    /// <summary>获取起点 stable ID。</summary>
    public required string FromId { get; init; }
    /// <summary>获取终点 stable ID。</summary>
    public required string ToId { get; init; }
    /// <summary>获取关系 allowlist。</summary>
    public required IReadOnlyList<CodeRelationKind> RelationKinds { get; init; }
    /// <summary>获取遍历方向。</summary>
    public required string Direction { get; init; }
    /// <summary>获取最大深度。</summary>
    public required int MaxDepth { get; init; }
    /// <summary>获取最大路径数。</summary>
    public required int MaxPaths { get; init; }
    /// <summary>获取最大 frontier。</summary>
    public required int MaxFrontier { get; init; }
}

/// <summary>依赖路径中的一步。</summary>
public sealed class DependencyPathStep
{
    /// <summary>获取节点 ID。</summary>
    public required string NodeId { get; init; }
    /// <summary>获取到达该节点的关系 ID；起点为空。</summary>
    public string? RelationId { get; init; }
    /// <summary>获取证据 ID。</summary>
    public required IReadOnlyList<string> EvidenceIds { get; init; }
}

/// <summary>依赖路径结果项。</summary>
public sealed class DependencyPathItem
{
    /// <summary>获取有序路径步骤。</summary>
    public required IReadOnlyList<DependencyPathStep> Steps { get; init; }
    /// <summary>获取累计路径代价。</summary>
    public required double Cost { get; init; }
}

/// <summary>分析文件、符号或 change set 的影响。</summary>
public sealed class ImpactAnalyzeRequest : McpToolRequest
{
    /// <summary>获取输入文件路径。</summary>
    public required IReadOnlyList<string> Files { get; init; }
    /// <summary>获取输入符号 ID。</summary>
    public required IReadOnlyList<string> SymbolIds { get; init; }
    /// <summary>获取关系 allowlist。</summary>
    public required IReadOnlyList<CodeRelationKind> RelationKinds { get; init; }
    /// <summary>获取最大深度。</summary>
    public required int MaxDepth { get; init; }
    /// <summary>获取最大 frontier。</summary>
    public required int MaxFrontier { get; init; }
    /// <summary>获取是否包含候选测试。</summary>
    public required bool IncludeTests { get; init; }
    /// <summary>获取是否包含构建目标。</summary>
    public required bool IncludeBuildTargets { get; init; }
}

/// <summary>影响分析结果项。</summary>
public sealed class ImpactItem
{
    /// <summary>获取受影响实体 ID。</summary>
    public required string Id { get; init; }
    /// <summary>获取实体类型。</summary>
    public required CodeEntityKind Kind { get; init; }
    /// <summary>获取 direct 或 transitive 分类。</summary>
    public required string ImpactKind { get; init; }
    /// <summary>获取传播原因码。</summary>
    public required string Reason { get; init; }
    /// <summary>获取置信度。</summary>
    public required Confidence Confidence { get; init; }
    /// <summary>获取证据 ID。</summary>
    public required IReadOnlyList<string> EvidenceIds { get; init; }
}

/// <summary>描述一个 Git diff hunk。</summary>
public sealed class DiffHunk
{
    /// <summary>获取 workspace-relative path。</summary>
    public required string Path { get; init; }
    /// <summary>获取旧起始行。</summary>
    public required int OldStart { get; init; }
    /// <summary>获取旧行数。</summary>
    public required int OldCount { get; init; }
    /// <summary>获取新起始行。</summary>
    public required int NewStart { get; init; }
    /// <summary>获取新行数。</summary>
    public required int NewCount { get; init; }
}

/// <summary>查询 Git revision、working tree 或显式 hunk 的变更上下文。</summary>
public sealed class ChangeContextRequest : McpToolRequest
{
    /// <summary>获取 base revision。</summary>
    public string? BaseRevision { get; init; }
    /// <summary>获取 head revision。</summary>
    public string? HeadRevision { get; init; }
    /// <summary>获取是否包含 working tree。</summary>
    public required bool IncludeWorkingTree { get; init; }
    /// <summary>获取显式 diff hunks。</summary>
    public required IReadOnlyList<DiffHunk> Hunks { get; init; }
    /// <summary>获取最大图深度。</summary>
    public required int MaxDepth { get; init; }
    /// <summary>获取最大 frontier。</summary>
    public required int MaxFrontier { get; init; }
}

/// <summary>变更上下文结果项。</summary>
public sealed class ChangeContextItem
{
    /// <summary>获取 changed_file、symbol、contract、dependency 或 test 类型。</summary>
    public required string Kind { get; init; }
    /// <summary>获取实体 ID。</summary>
    public required string Id { get; init; }
    /// <summary>获取选择原因码。</summary>
    public required string Reason { get; init; }
    /// <summary>获取是否属于未知或部分覆盖区域。</summary>
    public required bool Partial { get; init; }
    /// <summary>获取证据 ID。</summary>
    public required IReadOnlyList<string> EvidenceIds { get; init; }
}

/// <summary>构建按 token、item 和 byte 预算约束的上下文包。</summary>
public sealed class ContextPackRequest : McpToolRequest
{
    /// <summary>获取编码任务描述。</summary>
    public required string Task { get; init; }
    /// <summary>获取可选入口符号 ID。</summary>
    public string? EntrySymbolId { get; init; }
    /// <summary>获取可选 Git base revision。</summary>
    public string? BaseRevision { get; init; }
    /// <summary>获取启用的召回模式。</summary>
    public required IReadOnlyList<string> RetrievalModes { get; init; }
    /// <summary>获取 required 或 best_effort evidence 策略。</summary>
    public required string EvidencePolicy { get; init; }
    /// <summary>获取显式在线 provider ID；本地模式为空。</summary>
    public string? ProviderId { get; init; }
}

/// <summary>上下文包中的一个去重片段。</summary>
public sealed class ContextPackItem
{
    /// <summary>获取 definitions、implementation、constraints、dependencies 或 tests 分区。</summary>
    public required string Section { get; init; }
    /// <summary>获取来源实体 ID。</summary>
    public required string Id { get; init; }
    /// <summary>获取有预算的源码片段。</summary>
    public required string Content { get; init; }
    /// <summary>获取选择原因码。</summary>
    public required string SelectionReason { get; init; }
    /// <summary>获取估算 token 数。</summary>
    public required int Tokens { get; init; }
    /// <summary>获取证据 ID。</summary>
    public required IReadOnlyList<string> EvidenceIds { get; init; }
}

/// <summary>
/// MCP v1 八个只读工具的稳定名称。
/// </summary>
public static class McpToolNames
{
    /// <summary>工作区状态工具。</summary>
    public const string WorkspaceStatus = "workspace_status";
    /// <summary>代码搜索工具。</summary>
    public const string CodeSearch = "code_search";
    /// <summary>符号详情工具。</summary>
    public const string SymbolGet = "symbol_get";
    /// <summary>符号关系工具。</summary>
    public const string SymbolRelations = "symbol_relations";
    /// <summary>依赖路径工具。</summary>
    public const string DependencyPath = "dependency_path";
    /// <summary>影响分析工具。</summary>
    public const string ImpactAnalyze = "impact_analyze";
    /// <summary>变更上下文工具。</summary>
    public const string ChangeContext = "change_context";
    /// <summary>上下文包工具。</summary>
    public const string ContextPack = "context_pack";

    /// <summary>获取按稳定名称排序的全部 v1 工具。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ChangeContext,
        CodeSearch,
        ContextPack,
        DependencyPath,
        ImpactAnalyze,
        SymbolGet,
        SymbolRelations,
        WorkspaceStatus,
    ];
}
