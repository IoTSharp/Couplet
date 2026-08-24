namespace Couplet.Core.Capabilities;

/// <summary>
/// 描述长运行组件的生命周期事件。
/// </summary>
public sealed class LifecycleReport
{
    /// <summary>
    /// 获取生命周期 schema 版本。
    /// </summary>
    public required string SchemaVersion { get; init; }

    /// <summary>
    /// 获取组件标识符。
    /// </summary>
    public required string Component { get; init; }

    /// <summary>
    /// 获取生命周期事件名。
    /// </summary>
    public required string Event { get; init; }
}
