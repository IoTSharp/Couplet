namespace Couplet.Core.Capabilities;

/// <summary>
/// 描述一个 Couplet 可执行组件的版本与能力状态。
/// </summary>
public sealed class CapabilityReport
{
    /// <summary>
    /// 获取报告 schema 版本。
    /// </summary>
    public required string SchemaVersion { get; init; }

    /// <summary>
    /// 获取 Couplet 产品版本。
    /// </summary>
    public required string ProductVersion { get; init; }

    /// <summary>
    /// 获取组件标识符。
    /// </summary>
    public required string Component { get; init; }

    /// <summary>
    /// 获取产品能力总状态。
    /// </summary>
    public required string OverallState { get; init; }

    /// <summary>
    /// 获取 SonnetDB Core 依赖状态。
    /// </summary>
    public required DependencyReport SonnetDbCore { get; init; }

    /// <summary>
    /// 获取当前组件公开的能力状态。
    /// </summary>
    public required IReadOnlyList<CapabilityStatus> Capabilities { get; init; }
}
