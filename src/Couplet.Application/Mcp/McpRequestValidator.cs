using Couplet.Core.Mcp;

namespace Couplet.Application.Mcp;

/// <summary>
/// 校验 MCP v1 的共享 workspace、revision 和预算字段。
/// </summary>
public static class McpRequestValidator
{
    /// <summary>
    /// 校验请求并在失败时返回稳定错误。
    /// </summary>
    /// <param name="request">typed 工具请求。</param>
    /// <param name="binding">连接绑定。</param>
    /// <param name="correlationId">安全 correlation ID。</param>
    /// <returns>校验错误；成功时为空。</returns>
    public static McpError? Validate(
        McpToolRequest request,
        WorkspaceBinding binding,
        string correlationId) => Validate(
            request,
            binding,
            correlationId,
            validateRevisionAvailability: true);

    internal static McpError? Validate(
        McpToolRequest request,
        WorkspaceBinding binding,
        string correlationId,
        bool validateRevisionAvailability)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        if (!string.Equals(request.ProtocolVersion, "1", StringComparison.Ordinal))
        {
            return Error(McpErrorCodes.InvalidRequest, "unsupported_protocol_version", false, binding, correlationId);
        }

        if (request.WorkspaceId is not null
            && !string.Equals(request.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal))
        {
            return Error(McpErrorCodes.WorkspaceNotFound, "workspace_not_bound_to_connection", false, binding, correlationId);
        }

        QueryBudget budget = request.Budget;
        if (budget.MaxItems <= 0 || budget.MaxItems > 1_000
            || budget.MaxTokens <= 0 || budget.MaxTokens > 65_536
            || budget.MaxBytes <= 0 || budget.MaxBytes > 4 * 1024 * 1024
            || budget.DeadlineMs <= 0 || budget.DeadlineMs > 120_000)
        {
            return Error(McpErrorCodes.InvalidRequest, "budget_out_of_range", false, binding, correlationId);
        }

        if (request.RevisionSelector is not null)
        {
            RevisionSelector selector = request.RevisionSelector;
            if (string.IsNullOrWhiteSpace(selector.Value)
                || selector.Kind is not ("source" or "index"))
            {
                return Error(McpErrorCodes.InvalidRequest, "invalid_revision_selector", false, binding, correlationId);
            }

            string? current = selector.Kind == "source" ? binding.SourceRevision : binding.IndexRevision;
            if (validateRevisionAvailability
                && !string.Equals(selector.Value, current, StringComparison.Ordinal))
            {
                return Error(McpErrorCodes.StaleRevision, "revision_not_available", false, binding, correlationId);
            }
        }

        return null;
    }

    internal static McpError Error(
        string code,
        string reason,
        bool retryable,
        WorkspaceBinding binding,
        string correlationId,
        string? capability = null,
        string? gapId = null) => new()
        {
            Code = code,
            Reason = reason,
            Retryable = retryable,
            Capability = capability,
            GapId = gapId,
            CurrentRevision = binding.IndexRevision,
            CorrelationId = correlationId,
        };
}
