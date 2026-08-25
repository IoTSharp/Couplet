using System.Text.Json.Serialization;
using Couplet.Core.Mcp;

namespace Couplet.Application.Mcp;

/// <summary>
/// initialize 时观察到的数据库状态。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceDatabaseState>))]
public enum WorkspaceDatabaseState
{
    /// <summary>工作区尚无数据库。</summary>
    Empty,
    /// <summary>数据库 schema 与校验均有效。</summary>
    Current,
    /// <summary>数据库 schema 早于当前支持版本。</summary>
    Legacy,
    /// <summary>数据库校验失败。</summary>
    Corrupt,
}

/// <summary>
/// initialize 评估结果。
/// </summary>
public sealed class WorkspaceInitializationResult
{
    /// <summary>获取成功响应。</summary>
    public InitializeWorkspaceResponse? Response { get; init; }
    /// <summary>获取失败错误。</summary>
    public McpError? Error { get; init; }
}

/// <summary>
/// 把空、旧版或损坏数据库映射为稳定 initialize 结果。
/// </summary>
public static class WorkspaceInitializationEvaluator
{
    /// <summary>
    /// 评估工作区数据库状态。
    /// </summary>
    /// <param name="binding">工作区绑定。</param>
    /// <param name="state">数据库状态。</param>
    /// <param name="capabilities">连接能力。</param>
    /// <param name="correlationId">correlation ID。</param>
    /// <returns>成功响应或稳定错误。</returns>
    public static WorkspaceInitializationResult Evaluate(
        WorkspaceBinding binding,
        WorkspaceDatabaseState state,
        IReadOnlyList<McpCapability> capabilities,
        string correlationId)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(capabilities);

        return state switch
        {
            WorkspaceDatabaseState.Corrupt => Failed(
                McpErrorCodes.IndexCorrupt,
                "database_integrity_check_failed",
                false,
                binding,
                correlationId),
            WorkspaceDatabaseState.Legacy => Failed(
                McpErrorCodes.CapabilityUnavailable,
                "database_schema_upgrade_required",
                false,
                binding,
                correlationId),
            WorkspaceDatabaseState.Empty or WorkspaceDatabaseState.Current => new WorkspaceInitializationResult
            {
                Response = new InitializeWorkspaceResponse
                {
                    Binding = binding,
                    Capabilities = capabilities,
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown database state."),
        };
    }

    private static WorkspaceInitializationResult Failed(
        string code,
        string reason,
        bool retryable,
        WorkspaceBinding binding,
        string correlationId) => new()
        {
            Error = McpRequestValidator.Error(code, reason, retryable, binding, correlationId),
        };
}
