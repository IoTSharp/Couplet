using System.Text.Json;
using Couplet.Core.Capabilities;

namespace Couplet.Application.Serialization;

/// <summary>
/// 使用 source-generated 元数据序列化 Couplet 生产 JSON。
/// </summary>
public static class CoupletJsonSerializer
{
    /// <summary>
    /// 序列化能力报告。
    /// </summary>
    /// <param name="report">能力报告。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(CapabilityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, CoupletJsonContext.Default.CapabilityReport);
    }

    /// <summary>
    /// 序列化生命周期报告。
    /// </summary>
    /// <param name="report">生命周期报告。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(LifecycleReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, CoupletJsonContext.Default.LifecycleReport);
    }

    /// <summary>
    /// 序列化错误报告。
    /// </summary>
    /// <param name="report">错误报告。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(ErrorReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, CoupletJsonContext.Default.ErrorReport);
    }
}
