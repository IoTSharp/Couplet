using Couplet.Core.Capabilities;

namespace Couplet.Application.Capabilities;

/// <summary>
/// 创建不夸大未实现能力的组件状态报告。
/// </summary>
public sealed class CapabilityReportService
{
    private readonly ISonnetDbCapabilityProbe _sonnetDbCapabilityProbe;

    /// <summary>
    /// 初始化能力报告服务。
    /// </summary>
    /// <param name="sonnetDbCapabilityProbe">SonnetDB Core 能力探针。</param>
    public CapabilityReportService(ISonnetDbCapabilityProbe sonnetDbCapabilityProbe)
    {
        ArgumentNullException.ThrowIfNull(sonnetDbCapabilityProbe);
        _sonnetDbCapabilityProbe = sonnetDbCapabilityProbe;
    }

    /// <summary>
    /// 为指定组件创建能力报告。
    /// </summary>
    /// <param name="component">组件类型。</param>
    /// <returns>版本化能力报告。</returns>
    public CapabilityReport Create(ComponentKind component)
    {
        string componentName = ComponentNames.Get(component);

        return new CapabilityReport
        {
            SchemaVersion = "cpl-007.capabilities.v1",
            ProductVersion = ProductVersion.Current,
            Component = componentName,
            OverallState = "capability_unavailable",
            SonnetDbCore = _sonnetDbCapabilityProbe.Probe(),
            Capabilities =
            [
                Available("diagnostics.version"),
                Available("diagnostics.capabilities"),
                component == ComponentKind.Daemon
                    ? Available("daemon.lifecycle")
                    : Unavailable("daemon.lifecycle", "not_applicable"),
                Unavailable("sonnetdb.capability_handshake", "capability_handshake_not_implemented"),
                Unavailable("workspace.index", "c1_not_implemented"),
                Unavailable("mcp.protocol", "cpl_006_not_implemented"),
            ],
        };
    }

    private static CapabilityStatus Available(string id) => new()
    {
        Id = id,
        State = "available",
        Reason = "implemented",
    };

    private static CapabilityStatus Unavailable(string id, string reason) => new()
    {
        Id = id,
        State = "unavailable",
        Reason = reason,
    };
}

internal static class ProductVersion
{
    internal static string Current { get; } =
        typeof(CapabilityReportService).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}

internal static class ComponentNames
{
    internal static string Get(ComponentKind component) => component switch
    {
        ComponentKind.Cli => "cli",
        ComponentKind.Daemon => "daemon",
        ComponentKind.McpServer => "mcp_server",
        _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unknown component kind."),
    };
}
