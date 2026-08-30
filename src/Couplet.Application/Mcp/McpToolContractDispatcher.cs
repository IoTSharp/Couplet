using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Couplet.Application.Serialization;
using Couplet.Core.Mcp;

namespace Couplet.Application.Mcp;

/// <summary>
/// MCP 工具合同分派结果。
/// </summary>
public sealed class McpDispatchResult
{
    /// <summary>获取失败错误；成功时为空。</summary>
    public required McpError? Error { get; init; }

    /// <summary>获取 workspace status 成功响应；其他结果或失败时为空。</summary>
    public McpToolResponse<WorkspaceStatusItem>? WorkspaceStatus { get; init; }

    /// <summary>获取在 active generation lease 内冻结的 workspace status JSON；其他结果或失败时为空。</summary>
    public string? SerializedWorkspaceStatus { get; init; }

    /// <summary>获取 code search 成功响应；其他结果或失败时为空。</summary>
    public McpToolResponse<CodeSearchItem>? CodeSearch { get; init; }

    /// <summary>获取在 active generation lease 内冻结的 code search JSON；其他结果或失败时为空。</summary>
    public string? SerializedCodeSearch { get; init; }

    /// <summary>获取 symbol details 成功响应；其他结果或失败时为空。</summary>
    public McpToolResponse<SymbolDetailsItem>? SymbolDetails { get; init; }

    /// <summary>获取在 active generation lease 内冻结的 symbol details JSON；其他结果或失败时为空。</summary>
    public string? SerializedSymbolDetails { get; init; }

    /// <summary>
    /// 创建稳定错误结果。
    /// </summary>
    /// <param name="error">稳定 MCP 错误。</param>
    /// <returns>失败结果。</returns>
    public static McpDispatchResult FromError(McpError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new McpDispatchResult { Error = error };
    }

    /// <summary>
    /// 创建 workspace status typed 成功结果。
    /// </summary>
    /// <param name="response">typed 成功响应。</param>
    /// <param name="serializedResponse">在 active generation lease 内完成的 source-generated JSON。</param>
    /// <returns>成功结果。</returns>
    public static McpDispatchResult FromWorkspaceStatus(
        McpToolResponse<WorkspaceStatusItem> response,
        string serializedResponse)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedResponse);
        return new McpDispatchResult
        {
            Error = null,
            WorkspaceStatus = response,
            SerializedWorkspaceStatus = serializedResponse,
        };
    }

    /// <summary>
    /// 创建 code search typed 成功结果。
    /// </summary>
    /// <param name="response">typed 成功响应。</param>
    /// <param name="serializedResponse">在 active generation lease 内完成的 source-generated JSON。</param>
    /// <returns>成功结果。</returns>
    public static McpDispatchResult FromCodeSearch(
        McpToolResponse<CodeSearchItem> response,
        string serializedResponse)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedResponse);
        return new McpDispatchResult
        {
            Error = null,
            CodeSearch = response,
            SerializedCodeSearch = serializedResponse,
        };
    }

    /// <summary>
    /// 创建 symbol details typed 成功结果。
    /// </summary>
    /// <param name="response">typed 成功响应。</param>
    /// <param name="serializedResponse">在 active generation lease 内完成的 source-generated JSON。</param>
    /// <returns>成功结果。</returns>
    public static McpDispatchResult FromSymbolDetails(
        McpToolResponse<SymbolDetailsItem> response,
        string serializedResponse)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedResponse);
        return new McpDispatchResult
        {
            Error = null,
            SymbolDetails = response,
            SerializedSymbolDetails = serializedResponse,
        };
    }
}

/// <summary>
/// 对八个 typed 工具执行确定性反序列化、校验和 capability gate。
/// </summary>
public static class McpToolContractDispatcher
{
    /// <summary>
    /// 分派一个工具请求；C0 不执行索引查询，只返回真实 capability 状态。
    /// </summary>
    /// <param name="tool">稳定工具名。</param>
    /// <param name="arguments">工具参数。</param>
    /// <param name="binding">连接绑定。</param>
    /// <param name="correlationId">correlation ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>typed 成功响应或稳定错误。</returns>
    public static McpDispatchResult Dispatch(
        string tool,
        JsonElement arguments,
        WorkspaceBinding binding,
        string correlationId,
        CancellationToken cancellationToken) => Dispatch(
            tool,
            arguments,
            binding,
            correlationId,
            executor: null,
            cancellationToken);

    /// <summary>
    /// 使用 typed executor 分派一个工具请求。
    /// </summary>
    /// <param name="tool">稳定工具名。</param>
    /// <param name="arguments">工具参数。</param>
    /// <param name="binding">连接绑定。</param>
    /// <param name="correlationId">correlation ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="executor">可选 typed 工具执行器。</param>
    /// <param name="toolGateCapabilities">可选的当前连接初始化能力；为空时保持 C0 默认门禁。</param>
    /// <returns>typed 成功响应或稳定错误。</returns>
    public static McpDispatchResult Dispatch(
        string tool,
        JsonElement arguments,
        WorkspaceBinding binding,
        string correlationId,
        IMcpToolExecutor? executor,
        CancellationToken cancellationToken,
        IReadOnlyList<McpCapability>? toolGateCapabilities = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(binding);

        if (cancellationToken.IsCancellationRequested)
        {
            return Result(McpRequestValidator.Error(
                McpErrorCodes.Cancelled,
                "client_cancelled",
                false,
                binding,
                correlationId));
        }

        McpToolRequest? request;
        try
        {
            request = Deserialize(tool, arguments);
        }
        catch (JsonException)
        {
            return Result(McpRequestValidator.Error(
                McpErrorCodes.InvalidRequest,
                "request_schema_mismatch",
                false,
                binding,
                correlationId));
        }

        if (request is null)
        {
            return Result(McpRequestValidator.Error(
                McpErrorCodes.InvalidRequest,
                "unknown_tool",
                false,
                binding,
                correlationId));
        }

        bool canExecute = executor is not null
            && (request is WorkspaceStatusRequest
                || request is CodeSearchRequest { Mode: "exact" or "fulltext" }
                || request is SymbolGetRequest);
        bool deferRevisionAvailability = canExecute;
        McpError? commonError = McpRequestValidator.Validate(
            request,
            binding,
            correlationId,
            validateRevisionAvailability: !deferRevisionAvailability);
        if (commonError is not null)
        {
            return Result(commonError);
        }

        McpError? shapeError = ValidateToolShape(request, binding, correlationId);
        if (shapeError is not null)
        {
            return Result(shapeError);
        }

        string? providerId = request switch
        {
            CodeSearchRequest search => search.ProviderId,
            ContextPackRequest context => context.ProviderId,
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            return Result(McpRequestValidator.Error(
                McpErrorCodes.ProviderUnavailable,
                "online_provider_not_configured",
                true,
                binding,
                correlationId,
                "embedding.provider"));
        }

        if (canExecute)
        {
            return executor!.Execute(request, binding, correlationId, cancellationToken);
        }

        (string capability, string reason, string gap) = ToolGate(
            tool,
            request,
            toolGateCapabilities);
        return Result(McpRequestValidator.Error(
            McpErrorCodes.CapabilityUnavailable,
            reason,
            true,
            binding,
            correlationId,
            capability,
            gap));
    }

    private static McpToolRequest? Deserialize(string tool, JsonElement arguments) => tool switch
    {
        McpToolNames.WorkspaceStatus => Read(arguments, CoupletJsonContext.Default.WorkspaceStatusRequest),
        McpToolNames.CodeSearch => Read(arguments, CoupletJsonContext.Default.CodeSearchRequest),
        McpToolNames.SymbolGet => Read(arguments, CoupletJsonContext.Default.SymbolGetRequest),
        McpToolNames.SymbolRelations => Read(arguments, CoupletJsonContext.Default.SymbolRelationsRequest),
        McpToolNames.DependencyPath => Read(arguments, CoupletJsonContext.Default.DependencyPathRequest),
        McpToolNames.ImpactAnalyze => Read(arguments, CoupletJsonContext.Default.ImpactAnalyzeRequest),
        McpToolNames.ChangeContext => Read(arguments, CoupletJsonContext.Default.ChangeContextRequest),
        McpToolNames.ContextPack => Read(arguments, CoupletJsonContext.Default.ContextPackRequest),
        _ => null,
    };

    private static T Read<T>(JsonElement arguments, JsonTypeInfo<T> typeInfo) where T : McpToolRequest =>
        CoupletJsonSerializer.Deserialize(arguments.GetRawText(), typeInfo);

    private static McpError? ValidateToolShape(
        McpToolRequest request,
        WorkspaceBinding binding,
        string correlationId)
    {
        bool valid = request switch
        {
            WorkspaceStatusRequest status => status.Cursor is null,
            CodeSearchRequest search => !string.IsNullOrWhiteSpace(search.Query)
                && search.Mode is "exact" or "fulltext" or "vector" or "hybrid",
            SymbolGetRequest symbol =>
                string.IsNullOrWhiteSpace(symbol.SymbolId) != string.IsNullOrWhiteSpace(symbol.QualifiedIdentity),
            SymbolRelationsRequest relations => !string.IsNullOrWhiteSpace(relations.SymbolId)
                && relations.RelationKinds.Count > 0
                && relations.Direction is "outgoing" or "incoming" or "both"
                && relations.MaxDepth > 0
                && relations.MaxFrontier > 0,
            DependencyPathRequest path => !string.IsNullOrWhiteSpace(path.FromId)
                && !string.IsNullOrWhiteSpace(path.ToId)
                && path.RelationKinds.Count > 0
                && path.Direction is "outgoing" or "incoming" or "both"
                && path.MaxDepth > 0
                && path.MaxPaths > 0
                && path.MaxFrontier > 0,
            ImpactAnalyzeRequest impact => (impact.Files.Count > 0 || impact.SymbolIds.Count > 0)
                && impact.RelationKinds.Count > 0
                && impact.MaxDepth > 0
                && impact.MaxFrontier > 0,
            ChangeContextRequest change =>
                (!string.IsNullOrWhiteSpace(change.BaseRevision)
                    || !string.IsNullOrWhiteSpace(change.HeadRevision)
                    || change.IncludeWorkingTree
                    || change.Hunks.Count > 0)
                && change.MaxDepth > 0
                && change.MaxFrontier > 0,
            ContextPackRequest context => !string.IsNullOrWhiteSpace(context.Task)
                && context.RetrievalModes.Count > 0
                && context.EvidencePolicy is "required" or "best_effort",
            _ => false,
        };

        return valid
            ? null
            : McpRequestValidator.Error(
                McpErrorCodes.InvalidRequest,
                "tool_argument_invalid",
                false,
                binding,
                correlationId);
    }

    private static (string Capability, string Reason, string Gap) ToolGate(
        string tool,
        McpToolRequest request,
        IReadOnlyList<McpCapability>? capabilities)
    {
        (string capability, string reason, string gap) gate = DefaultToolGate(tool);
        if (capabilities is null)
        {
            return gate;
        }

        (string Id, string Capability, string Gap)? declaredGate = request switch
        {
            CodeSearchRequest search => search.Mode switch
            {
                "exact" => ("exact", "workspace.index", "CG-005"),
                "fulltext" => ("fulltext", "workspace.index", "CG-005"),
                "vector" => ("vector", "vector.document", "CG-002"),
                "hybrid" => ("hybrid", "hybrid.shared_plan", "CG-002"),
                _ => null,
            },
            SymbolGetRequest => ("exact", "workspace.index", "CG-005"),
            SymbolRelationsRequest or DependencyPathRequest or ImpactAnalyzeRequest
                or ChangeContextRequest => ("graph", "graph.native", "CG-001"),
            ContextPackRequest => ("hybrid", "hybrid.shared_plan", "CG-002"),
            _ => null,
        };
        if (declaredGate is not { } target)
        {
            return gate;
        }

        McpCapability? declared = capabilities.FirstOrDefault(
            capability => string.Equals(
                capability.Id,
                target.Id,
                StringComparison.Ordinal));
        return declared is not null
            && string.Equals(declared.Level, "unavailable", StringComparison.Ordinal)
            ? (target.Capability, declared.Reason, target.Gap)
            : gate;
    }

    private static (string Capability, string Reason, string Gap) DefaultToolGate(string tool) => tool switch
    {
        McpToolNames.WorkspaceStatus or McpToolNames.CodeSearch or McpToolNames.SymbolGet =>
            ("workspace.index", "generation_publish_blocked", "CG-005"),
        McpToolNames.SymbolRelations or McpToolNames.DependencyPath or McpToolNames.ImpactAnalyze
            or McpToolNames.ChangeContext => ("graph.native", "c2_release_gate_not_passed", "CG-001"),
        McpToolNames.ContextPack => ("hybrid.shared_plan", "c3_not_implemented", "CG-002"),
        _ => ("mcp.tool", "unknown_tool", "CG-004"),
    };

    private static McpDispatchResult Result(McpError error) => McpDispatchResult.FromError(error);
}
