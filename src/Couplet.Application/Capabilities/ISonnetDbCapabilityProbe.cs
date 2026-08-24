using Couplet.Core.Capabilities;

namespace Couplet.Application.Capabilities;

/// <summary>
/// 提供 SonnetDB Core 构建基线和能力握手状态。
/// </summary>
public interface ISonnetDbCapabilityProbe
{
    /// <summary>
    /// 读取当前进程使用的 SonnetDB Core 依赖状态。
    /// </summary>
    /// <returns>依赖状态报告。</returns>
    DependencyReport Probe();
}
