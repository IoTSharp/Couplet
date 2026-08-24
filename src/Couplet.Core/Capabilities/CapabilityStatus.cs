namespace Couplet.Core.Capabilities;

/// <summary>
/// 描述一个 Couplet 能力的当前可用状态。
/// </summary>
public sealed class CapabilityStatus
{
    /// <summary>
    /// 获取稳定能力标识符。
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// 获取当前状态。
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// 获取解释当前状态的稳定原因码。
    /// </summary>
    public required string Reason { get; init; }
}
