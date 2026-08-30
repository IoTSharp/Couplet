using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Couplet.Core.Capabilities;
using Couplet.Core.Evaluation;
using Couplet.Core.Indexing;
using Couplet.Core.Mcp;
using Couplet.Core.Workspaces;

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
    /// 序列化 workspace status typed MCP 响应。
    /// </summary>
    /// <param name="response">workspace status 响应。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(McpToolResponse<WorkspaceStatusItem> response) =>
        SerializeCore(response, CoupletJsonContext.Default.McpToolResponseWorkspaceStatusItem);

    /// <summary>
    /// 序列化 C0 evidence 报告。
    /// </summary>
    /// <param name="report">evidence 报告。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(C0EvidenceReport report) =>
        SerializeCore(report, CoupletJsonContext.Default.C0EvidenceReport);

    /// <summary>
    /// 序列化 C1 容量 evidence 报告。
    /// </summary>
    /// <param name="report">C1 容量报告。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(C1CapacityEvidenceReport report) =>
        SerializeCore(report, CoupletJsonContext.Default.C1CapacityEvidenceReport);

    /// <summary>
    /// 反序列化固定 fixture manifest。
    /// </summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>fixture manifest。</returns>
    public static FixtureManifest DeserializeFixtureManifest(string json) =>
        Deserialize(json, CoupletJsonContext.Default.FixtureManifest);

    /// <summary>
    /// 序列化 fixture 生成报告。
    /// </summary>
    /// <param name="report">fixture 生成报告。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(FixtureGenerationReport report) =>
        SerializeCore(report, CoupletJsonContext.Default.FixtureGenerationReport);

    /// <summary>
    /// 序列化 workspace discovery 报告。
    /// </summary>
    /// <param name="report">发现报告。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(WorkspaceDiscoveryResult report) =>
        SerializeCore(report, CoupletJsonContext.Default.WorkspaceDiscoveryResult);

    /// <summary>
    /// 序列化 C1 index staging 报告。
    /// </summary>
    /// <param name="report">staging 报告。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(IndexStageReport report) =>
        SerializeCore(report, CoupletJsonContext.Default.IndexStageReport);

    /// <summary>
    /// 序列化 generation 内的轻量增量规划 snapshot。
    /// </summary>
    /// <param name="snapshot">规划 snapshot。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(IndexPlanningSnapshot snapshot) =>
        SerializeCore(snapshot, CoupletJsonContext.Default.IndexPlanningSnapshot);

    /// <summary>
    /// 反序列化 generation 内的轻量增量规划 snapshot。
    /// </summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>规划 snapshot。</returns>
    public static IndexPlanningSnapshot DeserializeIndexPlanningSnapshot(string json) =>
        Deserialize(json, CoupletJsonContext.Default.IndexPlanningSnapshot);

    /// <summary>
    /// 序列化 C1 staging 重开检查报告。
    /// </summary>
    /// <param name="inspection">staging 检查报告。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(StagingGenerationInspection inspection) =>
        SerializeCore(inspection, CoupletJsonContext.Default.StagingGenerationInspection);

    /// <summary>
    /// 序列化 SonnetDB index document。
    /// </summary>
    /// <param name="document">存储记录。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(IndexStorageDocument document) =>
        SerializeCore(document, CoupletJsonContext.Default.IndexStorageDocument);

    /// <summary>
    /// 序列化 generation manifest。
    /// </summary>
    /// <param name="manifest">generation manifest。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(GenerationManifest manifest) =>
        SerializeCore(manifest, CoupletJsonContext.Default.GenerationManifest);

    /// <summary>
    /// 反序列化 generation manifest。
    /// </summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>generation manifest。</returns>
    public static GenerationManifest DeserializeGenerationManifest(string json) =>
        Deserialize(json, CoupletJsonContext.Default.GenerationManifest);

    /// <summary>
    /// 反序列化 SonnetDB index document。
    /// </summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>索引存储记录。</returns>
    public static IndexStorageDocument DeserializeIndexStorageDocument(string json) =>
        Deserialize(json, CoupletJsonContext.Default.IndexStorageDocument);

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
