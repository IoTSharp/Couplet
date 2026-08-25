namespace Couplet.Core.Capabilities;

/// <summary>
/// 描述 Couplet 当前使用的 SonnetDB Core 依赖基线。
/// </summary>
public sealed class DependencyReport
{
    /// <summary>
    /// 获取依赖标识符。
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// 获取依赖解析模式。
    /// </summary>
    public required string Mode { get; init; }

    /// <summary>
    /// 获取请求的固定依赖标识。
    /// </summary>
    public required string Requested { get; init; }

    /// <summary>
    /// 获取实际程序集版本。
    /// </summary>
    public required string ResolvedVersion { get; init; }

    /// <summary>
    /// 获取固定的源码提交。
    /// </summary>
    public required string ResolvedCommit { get; init; }

    /// <summary>
    /// 获取 capability handshake 合同版本。
    /// </summary>
    public required string HandshakeVersion { get; init; }

    /// <summary>
    /// 获取依赖能力握手状态。
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// 获取解释当前状态的稳定原因码。
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// 获取依赖程序集是否声明支持裁剪。
    /// </summary>
    public required bool DeclaresTrimCompatible { get; init; }

    /// <summary>
    /// 获取依赖程序集是否声明支持 Native AOT。
    /// </summary>
    public required bool DeclaresAotCompatible { get; init; }

    /// <summary>
    /// 获取当前源码基线是否包含公开图 API。
    /// </summary>
    public required bool GraphApiPresent { get; init; }

    /// <summary>
    /// 获取按 public API 和联合 gate 拆分的能力矩阵。
    /// </summary>
    public required IReadOnlyList<DependencyCapability> Capabilities { get; init; }
}
