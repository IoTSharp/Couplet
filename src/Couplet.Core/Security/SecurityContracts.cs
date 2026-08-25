using System.Text.Json.Serialization;
using Couplet.Core.Contracts;

namespace Couplet.Core.Security;

/// <summary>
/// 外部 provider 的数据发送模式。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProviderMode>))]
public enum ProviderMode
{
    /// <summary>只允许本地处理。</summary>
    LocalOnly,
    /// <summary>用户明确选择的在线 provider。</summary>
    ExplicitOnline,
}

/// <summary>
/// 索引数据保留与清理策略。
/// </summary>
public sealed class DataLifecyclePolicy
{
    /// <summary>获取 retired generation 最长保留时间。</summary>
    public required TimeSpan RetiredGenerationRetention { get; init; }
    /// <summary>获取日志最长保留时间。</summary>
    public required TimeSpan LogRetention { get; init; }
    /// <summary>获取 provider cache 最长保留时间。</summary>
    public required TimeSpan ProviderCacheRetention { get; init; }
    /// <summary>获取工作区移除时是否删除本地索引。</summary>
    public required bool DeleteIndexOnWorkspaceRemoval { get; init; }
}

/// <summary>
/// 记录在线 provider 的显式授权边界。
/// </summary>
public sealed class ProviderPolicy
{
    /// <summary>获取 provider 模式。</summary>
    public required ProviderMode Mode { get; init; }
    /// <summary>获取 provider 标识符。</summary>
    public string? ProviderId { get; init; }
    /// <summary>获取模型标识符。</summary>
    public string? ModelId { get; init; }
    /// <summary>获取模型或协议版本。</summary>
    public string? ModelVersion { get; init; }
    /// <summary>获取允许发送的字段 allowlist。</summary>
    public required IReadOnlyList<string> AllowedFields { get; init; }
    /// <summary>获取是否已由用户显式启用。</summary>
    public required bool UserOptIn { get; init; }
}

/// <summary>
/// Couplet 本地优先安全与隐私策略。
/// </summary>
public sealed class SecurityPolicy
{
    /// <summary>获取安全合同版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.Security;
    /// <summary>获取允许绑定的规范化工作区身份。</summary>
    public required IReadOnlyList<string> WorkspaceAllowlist { get; init; }
    /// <summary>获取在语言识别前应用的 ignore glob。</summary>
    public required IReadOnlyList<string> IgnorePatterns { get; init; }
    /// <summary>获取优先于 ignore/include 的 deny glob。</summary>
    public required IReadOnlyList<string> DenyPatterns { get; init; }
    /// <summary>获取 provider 策略。</summary>
    public required ProviderPolicy Provider { get; init; }
    /// <summary>获取数据生命周期策略。</summary>
    public required DataLifecyclePolicy Lifecycle { get; init; }
    /// <summary>获取日志是否允许记录 workspace-relative path。</summary>
    public required bool LogRelativePaths { get; init; }
}

/// <summary>
/// 提供 C0 安全策略的确定性校验。
/// </summary>
public static class SecurityPolicyValidator
{
    /// <summary>
    /// 校验安全策略并返回稳定问题码。
    /// </summary>
    /// <param name="policy">待校验策略。</param>
    /// <returns>按码排序的问题列表。</returns>
    public static IReadOnlyList<string> Validate(SecurityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var errors = new SortedSet<string>(StringComparer.Ordinal);

        if (!string.Equals(policy.SchemaVersion, ContractVersions.Security, StringComparison.Ordinal))
        {
            errors.Add("unsupported_security_schema");
        }

        if (policy.WorkspaceAllowlist.Count == 0)
        {
            errors.Add("workspace_allowlist_required");
        }

        if (policy.DenyPatterns.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("empty_deny_pattern");
        }

        if (policy.Provider.Mode == ProviderMode.ExplicitOnline)
        {
            if (!policy.Provider.UserOptIn)
            {
                errors.Add("online_provider_requires_opt_in");
            }

            if (string.IsNullOrWhiteSpace(policy.Provider.ProviderId)
                || string.IsNullOrWhiteSpace(policy.Provider.ModelId)
                || string.IsNullOrWhiteSpace(policy.Provider.ModelVersion))
            {
                errors.Add("online_provider_identity_required");
            }

            if (policy.Provider.AllowedFields.Count == 0)
            {
                errors.Add("online_provider_field_allowlist_required");
            }
        }
        else if (policy.Provider.UserOptIn || policy.Provider.AllowedFields.Count > 0)
        {
            errors.Add("local_provider_must_not_allow_external_fields");
        }

        if (policy.Lifecycle.RetiredGenerationRetention < TimeSpan.Zero
            || policy.Lifecycle.LogRetention < TimeSpan.Zero
            || policy.Lifecycle.ProviderCacheRetention < TimeSpan.Zero)
        {
            errors.Add("negative_retention_not_allowed");
        }

        return errors.ToArray();
    }
}
