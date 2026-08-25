using Couplet.Core.Mcp;

namespace Couplet.Application.Mcp;

/// <summary>
/// 在执行路径上统一检查取消、deadline 和可用响应预算。
/// </summary>
public static class McpRequestGuard
{
    /// <summary>
    /// 检查执行是否必须在产生下一项前停止。
    /// </summary>
    /// <param name="budget">请求预算。</param>
    /// <param name="elapsed">已经消耗的墙钟时间。</param>
    /// <param name="consumedItems">已消耗结果项。</param>
    /// <param name="consumedTokens">已消耗 token。</param>
    /// <param name="consumedBytes">已消耗字节。</param>
    /// <param name="binding">工作区绑定。</param>
    /// <param name="correlationId">correlation ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应终止时返回稳定错误，否则为空。</returns>
    public static McpError? Check(
        QueryBudget budget,
        TimeSpan elapsed,
        int consumedItems,
        int consumedTokens,
        int consumedBytes,
        WorkspaceBinding binding,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(binding);

        if (cancellationToken.IsCancellationRequested)
        {
            return McpRequestValidator.Error(
                McpErrorCodes.Cancelled,
                "client_cancelled",
                false,
                binding,
                correlationId);
        }

        if (elapsed.TotalMilliseconds >= budget.DeadlineMs)
        {
            return McpRequestValidator.Error(
                McpErrorCodes.DeadlineExceeded,
                "request_deadline_reached",
                true,
                binding,
                correlationId);
        }

        if (consumedItems >= budget.MaxItems
            || consumedTokens >= budget.MaxTokens
            || consumedBytes >= budget.MaxBytes)
        {
            return McpRequestValidator.Error(
                McpErrorCodes.BudgetExhausted,
                "no_budget_for_reliable_item",
                true,
                binding,
                correlationId);
        }

        return null;
    }
}
