namespace Couplet.Core.Capabilities;

/// <summary>
/// 描述一个 SonnetDB public capability 的联调与发布状态。
/// </summary>
public sealed class DependencyCapability
{
    /// <summary>获取稳定 capability ID。</summary>
    public required string Id { get; init; }
    /// <summary>获取 public contract 版本。</summary>
    public required string ContractVersion { get; init; }
    /// <summary>获取当前 package 是否提供所需 public API。</summary>
    public required string IntegrationState { get; init; }
    /// <summary>获取 Couplet 当前允许声明的发布等级。</summary>
    public required string ReleaseLevel { get; init; }
    /// <summary>获取状态原因码。</summary>
    public required string Reason { get; init; }
    /// <summary>获取阻塞发布的 capability gap。</summary>
    public required IReadOnlyList<string> BlockingGaps { get; init; }
}
