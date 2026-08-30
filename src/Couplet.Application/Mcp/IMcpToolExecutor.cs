using Couplet.Core.Mcp;

namespace Couplet.Application.Mcp;

/// <summary>
/// 在完成 MCP 合同反序列化和公共校验后执行已接线的 typed 工具。
/// </summary>
public interface IMcpToolExecutor
{
    /// <summary>
    /// 执行一个已通过公共合同校验的工具请求。
    /// </summary>
    /// <param name="request">typed 工具请求。</param>
    /// <param name="binding">连接工作区绑定。</param>
    /// <param name="correlationId">安全 correlation ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>typed 成功响应或稳定错误。</returns>
    McpDispatchResult Execute(
        McpToolRequest request,
        WorkspaceBinding binding,
        string correlationId,
        CancellationToken cancellationToken);
}
