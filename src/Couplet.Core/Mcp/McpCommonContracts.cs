using Couplet.Core.Contracts;
using Couplet.Core.Graph;

namespace Couplet.Core.Mcp;

/// <summary>
/// MCP v1 稳定错误码。
/// </summary>
public static class McpErrorCodes
{
    /// <summary>请求 schema、范围、预算或参数不合法。</summary>
    public const string InvalidRequest = "invalid_request";
    /// <summary>工作区不存在或不允许访问。</summary>
    public const string WorkspaceNotFound = "workspace_not_found";
    /// <summary>尚无可查询 generation。</summary>
    public const string IndexNotReady = "index_not_ready";
    /// <summary>revision 或 cursor 已失效。</summary>
    public const string StaleRevision = "stale_revision";
    /// <summary>所需能力尚未达到公开门禁。</summary>
    public const string CapabilityUnavailable = "capability_unavailable";
    /// <summary>未产生可靠结果前预算耗尽。</summary>
    public const string BudgetExhausted = "budget_exhausted";
    /// <summary>显式选择的 provider 不可用。</summary>
    public const string ProviderUnavailable = "provider_unavailable";
    /// <summary>客户端已取消请求。</summary>
    public const string Cancelled = "cancelled";
    /// <summary>请求 deadline 已到达。</summary>
    public const string DeadlineExceeded = "deadline_exceeded";
    /// <summary>索引校验失败。</summary>
    public const string IndexCorrupt = "index_corrupt";
    /// <summary>未分类内部错误。</summary>
    public const string InternalError = "internal_error";
}

/// <summary>
/// 指定 source 或 index revision。
/// </summary>
public sealed class RevisionSelector
{
    /// <summary>获取 selector 类型；值为 source 或 index。</summary>
    public required string Kind { get; init; }
    /// <summary>获取精确 revision 值。</summary>
    public required string Value { get; init; }
}

/// <summary>
/// 单次 MCP 请求的资源预算。
/// </summary>
public sealed class QueryBudget
{
    /// <summary>获取最大结构化结果项。</summary>
    public required int MaxItems { get; init; }
    /// <summary>获取最大上下文 token 数。</summary>
    public required int MaxTokens { get; init; }
    /// <summary>获取最大序列化与 hydration 字节数。</summary>
    public required int MaxBytes { get; init; }
    /// <summary>获取最大执行毫秒数。</summary>
    public required int DeadlineMs { get; init; }
}

/// <summary>
/// 八个 MCP 工具共享的请求字段。
/// </summary>
public abstract class McpToolRequest
{
    /// <summary>获取协议 major；v1 固定为 1。</summary>
    public string ProtocolVersion { get; init; } = "1";
    /// <summary>获取显式工作区 ID；为空时使用连接绑定。</summary>
    public string? WorkspaceId { get; init; }
    /// <summary>获取 revision selector；为空时使用 active generation。</summary>
    public RevisionSelector? RevisionSelector { get; init; }
    /// <summary>获取调用预算。</summary>
    public required QueryBudget Budget { get; init; }
    /// <summary>获取 revision-bound opaque cursor。</summary>
    public string? Cursor { get; init; }
}

/// <summary>
/// 描述 initialize 绑定的工作区请求。
/// </summary>
public sealed class InitializeWorkspaceRequest
{
    /// <summary>获取 Couplet 工具协议 major。</summary>
    public string ProtocolVersion { get; init; } = "1";
    /// <summary>获取显式工作区路径或已注册 ID。</summary>
    public required string Workspace { get; init; }
    /// <summary>获取客户端名称。</summary>
    public required string ClientName { get; init; }
    /// <summary>获取客户端版本。</summary>
    public required string ClientVersion { get; init; }
}

/// <summary>
/// 描述一次连接的工作区绑定。
/// </summary>
public sealed class WorkspaceBinding
{
    /// <summary>获取工作区 stable ID。</summary>
    public required string WorkspaceId { get; init; }
    /// <summary>获取不包含本机绝对路径的规范化仓库身份。</summary>
    public required string RepositoryIdentity { get; init; }
    /// <summary>获取当前 source revision。</summary>
    public required string SourceRevision { get; init; }
    /// <summary>获取 active index revision；空索引时为空。</summary>
    public string? IndexRevision { get; init; }
}

/// <summary>
/// initialize 返回的单项工具能力。
/// </summary>
public sealed class McpCapability
{
    /// <summary>获取 capability ID。</summary>
    public required string Id { get; init; }
    /// <summary>获取 unavailable、preview、beta 或 production 等级。</summary>
    public required string Level { get; init; }
    /// <summary>获取状态原因码。</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// initialize workspace handshake 响应。
/// </summary>
public sealed class InitializeWorkspaceResponse
{
    /// <summary>获取响应 schema 版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.Mcp;
    /// <summary>获取连接绑定。</summary>
    public required WorkspaceBinding Binding { get; init; }
    /// <summary>获取本连接可见的能力。</summary>
    public required IReadOnlyList<McpCapability> Capabilities { get; init; }
}

/// <summary>
/// MCP v1 稳定错误对象。
/// </summary>
public sealed class McpError
{
    /// <summary>获取错误 schema 版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.Mcp;
    /// <summary>获取稳定错误码。</summary>
    public required string Code { get; init; }
    /// <summary>获取稳定原因码。</summary>
    public required string Reason { get; init; }
    /// <summary>获取错误是否可在状态变化后重试。</summary>
    public required bool Retryable { get; init; }
    /// <summary>获取相关 capability ID。</summary>
    public string? Capability { get; init; }
    /// <summary>获取相关 capability gap ID。</summary>
    public string? GapId { get; init; }
    /// <summary>获取当前索引 revision。</summary>
    public string? CurrentRevision { get; init; }
    /// <summary>获取不包含源码、凭证和绝对路径的 correlation ID。</summary>
    public required string CorrelationId { get; init; }
}

/// <summary>
/// 描述结果相对工作区和索引的实时性。
/// </summary>
public sealed class Freshness
{
    /// <summary>获取 clean、dirty 或 unknown 源码状态。</summary>
    public required string SourceState { get; init; }
    /// <summary>获取 empty、indexing、current、stale 或 corrupt 索引状态。</summary>
    public required string IndexState { get; init; }
    /// <summary>获取 0 到 1 的文件覆盖率。</summary>
    public required double Coverage { get; init; }
    /// <summary>获取待处理文件数。</summary>
    public required long PendingFiles { get; init; }
    /// <summary>获取失败文件数。</summary>
    public required long FailedFiles { get; init; }
    /// <summary>获取状态原因码。</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// 描述一条可去重引用的源码或关系证据。
/// </summary>
public sealed class Evidence
{
    /// <summary>获取 evidence ID。</summary>
    public required string Id { get; init; }
    /// <summary>获取 file、span、symbol 或 relation 类型。</summary>
    public required string Kind { get; init; }
    /// <summary>获取源码范围。</summary>
    public SourceSpan? Span { get; init; }
    /// <summary>获取关联符号 ID。</summary>
    public string? SymbolId { get; init; }
    /// <summary>获取关联关系 ID。</summary>
    public string? RelationId { get; init; }
    /// <summary>获取 source revision。</summary>
    public required string SourceRevision { get; init; }
    /// <summary>获取 index revision。</summary>
    public required string IndexRevision { get; init; }
}

/// <summary>
/// 描述实际执行路径与资源消耗。
/// </summary>
public sealed class QueryDiagnostics
{
    /// <summary>获取实际 access path。</summary>
    public required string AccessPath { get; init; }
    /// <summary>获取候选数。</summary>
    public required long Candidates { get; init; }
    /// <summary>获取检查数。</summary>
    public required long Examined { get; init; }
    /// <summary>获取返回数。</summary>
    public required long Returned { get; init; }
    /// <summary>获取展开边数。</summary>
    public required long ExpandedEdges { get; init; }
    /// <summary>获取 frontier 峰值。</summary>
    public required long FrontierPeak { get; init; }
    /// <summary>获取 fallback 原因；未 fallback 时为空。</summary>
    public string? FallbackReason { get; init; }
    /// <summary>获取执行耗时毫秒。</summary>
    public required double ElapsedMs { get; init; }
    /// <summary>获取已消耗结果项数。</summary>
    public required int ConsumedItems { get; init; }
    /// <summary>获取已消耗 token 数。</summary>
    public required int ConsumedTokens { get; init; }
    /// <summary>获取已消耗字节数。</summary>
    public required int ConsumedBytes { get; init; }
}

/// <summary>
/// 八个工具共享的 typed 成功响应。
/// </summary>
/// <typeparam name="TItem">工具特定结果项类型。</typeparam>
public sealed class McpToolResponse<TItem>
{
    /// <summary>获取响应 schema 版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.Mcp;
    /// <summary>获取工作区 ID。</summary>
    public required string WorkspaceId { get; init; }
    /// <summary>获取 source revision。</summary>
    public required string SourceRevision { get; init; }
    /// <summary>获取 index revision。</summary>
    public required string IndexRevision { get; init; }
    /// <summary>获取结果新鲜度。</summary>
    public required Freshness Freshness { get; init; }
    /// <summary>获取本次使用的 capability。</summary>
    public required IReadOnlyList<McpCapability> Capabilities { get; init; }
    /// <summary>获取 typed 结果项。</summary>
    public required IReadOnlyList<TItem> Items { get; init; }
    /// <summary>获取去重后的证据。</summary>
    public required IReadOnlyList<Evidence> Evidence { get; init; }
    /// <summary>获取实际执行诊断。</summary>
    public required QueryDiagnostics Diagnostics { get; init; }
    /// <summary>获取结果是否被截断。</summary>
    public required bool Truncated { get; init; }
    /// <summary>获取截断原因。</summary>
    public string? TruncationReason { get; init; }
    /// <summary>获取 revision-bound 续页 cursor。</summary>
    public string? NextCursor { get; init; }
}

/// <summary>
/// cursor 中经过完整性保护的最小声明。
/// </summary>
public sealed class CursorPayload
{
    /// <summary>获取 cursor 合同版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.Mcp;
    /// <summary>获取工作区 ID。</summary>
    public required string WorkspaceId { get; init; }
    /// <summary>获取工具名。</summary>
    public required string Tool { get; init; }
    /// <summary>获取规范化请求摘要。</summary>
    public required string QueryHash { get; init; }
    /// <summary>获取绑定的 index revision。</summary>
    public required string IndexRevision { get; init; }
    /// <summary>获取下一页偏移。</summary>
    public required long Offset { get; init; }
    /// <summary>获取 cursor 失效 UTC 时间。</summary>
    public required DateTimeOffset ExpiresAtUtc { get; init; }
}
