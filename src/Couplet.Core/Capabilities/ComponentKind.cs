namespace Couplet.Core.Capabilities;

/// <summary>
/// Couplet 可执行组件类型。
/// </summary>
public enum ComponentKind
{
    /// <summary>
    /// 命令行客户端。
    /// </summary>
    Cli,

    /// <summary>
    /// 本地后台宿主。
    /// </summary>
    Daemon,

    /// <summary>
    /// typed MCP Server 进程边界。
    /// </summary>
    McpServer,
}
