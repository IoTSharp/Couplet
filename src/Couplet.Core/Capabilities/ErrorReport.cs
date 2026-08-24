namespace Couplet.Core.Capabilities;

/// <summary>
/// 描述最小可执行面的稳定错误响应。
/// </summary>
public sealed class ErrorReport
{
    /// <summary>
    /// 获取错误 schema 版本。
    /// </summary>
    public required string SchemaVersion { get; init; }

    /// <summary>
    /// 获取稳定错误码。
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// 获取组件标识符。
    /// </summary>
    public required string Component { get; init; }

    /// <summary>
    /// 获取稳定原因码。
    /// </summary>
    public required string Reason { get; init; }
}
