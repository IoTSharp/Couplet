using Couplet.Core.Graph;
using Couplet.Core.Mcp;

namespace Couplet.Application.Mcp;

/// <summary>
/// 创建显式本地工作区绑定，不从当前目录猜测输入。
/// </summary>
public static class McpWorkspaceBinder
{
    /// <summary>
    /// 绑定一个存在的本地工作区目录。
    /// </summary>
    /// <param name="workspace">显式工作区目录。</param>
    /// <returns>不泄露绝对路径的连接绑定。</returns>
    public static WorkspaceBinding Bind(string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        string fullPath = Path.GetFullPath(workspace);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("The explicitly configured workspace was not found.");
        }

        string workspaceId = StableId.CreateWorkspace(fullPath, "primary");
        return new WorkspaceBinding
        {
            WorkspaceId = workspaceId,
            RepositoryIdentity = $"local:{workspaceId}",
            SourceRevision = "unavailable",
            IndexRevision = null,
        };
    }

    /// <summary>
    /// 获取 C0 连接公开的诚实 capability 列表。
    /// </summary>
    /// <returns>八个工具所需能力的稳定状态。</returns>
    public static IReadOnlyList<McpCapability> CreateCapabilities() =>
    [
        Capability("exact", "unavailable", "generation_publish_blocked"),
        Capability("fulltext", "unavailable", "generation_publish_blocked"),
        Capability("vector", "unavailable", "c3_not_implemented"),
        Capability("graph", "unavailable", "c2_release_gate_not_passed"),
        Capability("hybrid", "unavailable", "c3_not_implemented"),
    ];

    /// <summary>
    /// 获取 source lane C1 active generation 查询连接公开的诚实 capability 列表。
    /// </summary>
    /// <returns>workspace status 与 C1 exact/fulltext 查询可见的能力状态。</returns>
    public static IReadOnlyList<McpCapability> CreateC1Capabilities() =>
    [
        Capability("exact", "preview", "active_generation_query_connected"),
        Capability("fulltext", "preview", "active_generation_query_connected"),
        Capability("vector", "unavailable", "c3_not_implemented"),
        Capability("graph", "unavailable", "c2_release_gate_not_passed"),
        Capability("hybrid", "unavailable", "c3_not_implemented"),
    ];

    private static McpCapability Capability(string id, string level, string reason) => new()
    {
        Id = id,
        Level = level,
        Reason = reason,
    };
}
