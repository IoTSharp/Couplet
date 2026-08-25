using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Couplet.Core.Capabilities;
using Couplet.Core.Evaluation;
using Couplet.Core.Mcp;

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

    /// <summary>
    /// 序列化 MCP 错误。
    /// </summary>
    /// <param name="error">MCP 错误。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(McpError error) =>
        SerializeCore(error, CoupletJsonContext.Default.McpError);

    /// <summary>
    /// 序列化 initialize workspace 响应。
    /// </summary>
    /// <param name="response">initialize 响应。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(InitializeWorkspaceResponse response) =>
        SerializeCore(response, CoupletJsonContext.Default.InitializeWorkspaceResponse);

    /// <summary>
    /// 序列化 C0 evidence 报告。
    /// </summary>
    /// <param name="report">evidence 报告。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(C0EvidenceReport report) =>
        SerializeCore(report, CoupletJsonContext.Default.C0EvidenceReport);

    /// <summary>
    /// 序列化 fixture 生成报告。
    /// </summary>
    /// <param name="report">fixture 生成报告。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(FixtureGenerationReport report) =>
        SerializeCore(report, CoupletJsonContext.Default.FixtureGenerationReport);

    internal static string Serialize(CursorPayload payload) =>
        SerializeCore(payload, CoupletJsonContext.Default.CursorPayload);

    internal static T Deserialize<T>(string json, JsonTypeInfo<T> typeInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return JsonSerializer.Deserialize(json, typeInfo)
            ?? throw new JsonException($"JSON payload for {typeof(T).Name} was null.");
    }

    private static string SerializeCore<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, typeInfo);
    }
}
