using Couplet.Core.Contracts;

namespace Couplet.Core.Evaluation;

/// <summary>
/// 固定语料档位定义。
/// </summary>
public sealed class CorpusScaleDefinition
{
    /// <summary>获取 small、medium 或 large 档位名。</summary>
    public required string Id { get; init; }
    /// <summary>获取目标代码行数。</summary>
    public required long TargetLinesOfCode { get; init; }
    /// <summary>获取最低符号数。</summary>
    public required long MinimumSymbols { get; init; }
    /// <summary>获取最低图关系数。</summary>
    public required long MinimumRelations { get; init; }
    /// <summary>获取确定性生成器 seed。</summary>
    public required int Seed { get; init; }
    /// <summary>获取各语言文件的权重。</summary>
    public required IReadOnlyList<LanguageShare> Languages { get; init; }
}

/// <summary>
/// 语料中一个 adapter family 的占比。
/// </summary>
public sealed class LanguageShare
{
    /// <summary>获取 adapter family ID。</summary>
    public required string Family { get; init; }
    /// <summary>获取语言标识符。</summary>
    public required string Language { get; init; }
    /// <summary>获取 0 到 1 的代码行占比。</summary>
    public required double Share { get; init; }
    /// <summary>获取声明的 semantic tier。</summary>
    public required string SemanticTier { get; init; }
}

/// <summary>
/// Small、Medium、Large 多语言 fixture manifest。
/// </summary>
public sealed class FixtureManifest
{
    /// <summary>获取 manifest schema 版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.FixtureManifest;
    /// <summary>获取 manifest ID。</summary>
    public required string Id { get; init; }
    /// <summary>获取确定性生成器版本。</summary>
    public required string GeneratorVersion { get; init; }
    /// <summary>获取语料许可证说明。</summary>
    public required string License { get; init; }
    /// <summary>获取语料档位。</summary>
    public required IReadOnlyList<CorpusScaleDefinition> Scales { get; init; }
    /// <summary>获取必须覆盖的边界场景。</summary>
    public required IReadOnlyList<string> RequiredScenarios { get; init; }
}

/// <summary>
/// 一条可版本审查的 golden answer。
/// </summary>
public sealed class GoldenAnswer
{
    /// <summary>获取 answer ID。</summary>
    public required string Id { get; init; }
    /// <summary>获取适用档位。</summary>
    public required string Scale { get; init; }
    /// <summary>获取工具名。</summary>
    public required string Tool { get; init; }
    /// <summary>获取稳定查询身份。</summary>
    public required string QueryId { get; init; }
    /// <summary>获取必须返回的限定符号身份。</summary>
    public required IReadOnlyList<string> RequiredSymbols { get; init; }
    /// <summary>获取必须返回的关系身份。</summary>
    public required IReadOnlyList<string> RequiredRelations { get; init; }
    /// <summary>获取必须出现的 source span 身份。</summary>
    public required IReadOnlyList<string> RequiredSpans { get; init; }
}

/// <summary>
/// golden answer 集合。
/// </summary>
public sealed class GoldenAnswerSet
{
    /// <summary>获取 schema 版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.GoldenAnswers;
    /// <summary>获取绑定的 fixture manifest ID。</summary>
    public required string FixtureManifestId { get; init; }
    /// <summary>获取 golden answers。</summary>
    public required IReadOnlyList<GoldenAnswer> Answers { get; init; }
}

/// <summary>
/// paired Agent eval 的一项任务。
/// </summary>
public sealed class AgentEvalTask
{
    /// <summary>获取任务 ID。</summary>
    public required string Id { get; init; }
    /// <summary>获取 locate、modify、impact、test_selection 或 large_context 类别。</summary>
    public required string Category { get; init; }
    /// <summary>获取 fixture 档位。</summary>
    public required string Scale { get; init; }
    /// <summary>获取 golden patch/test 身份。</summary>
    public required string GoldenId { get; init; }
}

/// <summary>
/// Codex 与 Claude Code paired eval manifest。
/// </summary>
public sealed class AgentEvalManifest
{
    /// <summary>获取 schema 版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.AgentEval;
    /// <summary>获取绑定的 fixture manifest ID。</summary>
    public required string FixtureManifestId { get; init; }
    /// <summary>获取客户端名称。</summary>
    public required IReadOnlyList<string> Clients { get; init; }
    /// <summary>获取冻结模型身份。</summary>
    public required string ModelIdentity { get; init; }
    /// <summary>获取冻结提示身份。</summary>
    public required string PromptIdentity { get; init; }
    /// <summary>获取 MCP schema 版本。</summary>
    public required string ToolSchemaVersion { get; init; }
    /// <summary>获取每个非确定性条件的重复次数。</summary>
    public required int Repetitions { get; init; }
    /// <summary>获取预注册任务。</summary>
    public required IReadOnlyList<AgentEvalTask> Tasks { get; init; }
}

/// <summary>
/// paired eval 的一次 baseline 或 enabled 观测。
/// </summary>
public sealed class AgentEvalObservation
{
    /// <summary>获取客户端。</summary>
    public required string Client { get; init; }
    /// <summary>获取任务 ID。</summary>
    public required string TaskId { get; init; }
    /// <summary>获取重复序号。</summary>
    public required int Repetition { get; init; }
    /// <summary>获取 baseline 或 enabled 条件。</summary>
    public required string Condition { get; init; }
    /// <summary>获取是否产生通过验证的 patch。</summary>
    public required bool Succeeded { get; init; }
    /// <summary>获取到验证完成的毫秒数。</summary>
    public required double TimeToValidatedPatchMs { get; init; }
    /// <summary>获取注入代码上下文 token 数。</summary>
    public required int ContextTokens { get; init; }
    /// <summary>获取工具调用次数。</summary>
    public required int ToolCalls { get; init; }
    /// <summary>获取 evidence citation 正确率。</summary>
    public required double CitationAccuracy { get; init; }
    /// <summary>获取 golden 必需测试 recall。</summary>
    public required double TestRecall { get; init; }
    /// <summary>获取测试选择 precision。</summary>
    public required double TestPrecision { get; init; }
}

/// <summary>
/// paired eval 输入结果集。
/// </summary>
public sealed class AgentEvalResultSet
{
    /// <summary>获取 schema 版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.AgentEval;
    /// <summary>获取 not_run 或 completed 状态。</summary>
    public required string State { get; init; }
    /// <summary>获取 eval manifest 内容 SHA-256。</summary>
    public required string ManifestHash { get; init; }
    /// <summary>获取观测。</summary>
    public required IReadOnlyList<AgentEvalObservation> Observations { get; init; }
}

/// <summary>
/// paired Agent eval runner 的合同校验结果。
/// </summary>
public sealed class AgentEvalValidationResult
{
    /// <summary>获取 runner 是否可执行当前 manifest。</summary>
    public required bool RunnerReady { get; init; }
    /// <summary>获取结果是否形成完整 baseline/enabled 配对。</summary>
    public required bool Complete { get; init; }
    /// <summary>获取预期观测数。</summary>
    public required int ExpectedObservations { get; init; }
    /// <summary>获取实际观测数。</summary>
    public required int ActualObservations { get; init; }
    /// <summary>获取稳定排序的问题码。</summary>
    public required IReadOnlyList<string> Problems { get; init; }
}

/// <summary>
/// 确定性 fixture 生成结果。
/// </summary>
public sealed class FixtureGenerationReport
{
    /// <summary>获取档位 ID。</summary>
    public required string Scale { get; init; }
    /// <summary>获取生成文件数。</summary>
    public required long Files { get; init; }
    /// <summary>获取生成代码行数。</summary>
    public required long LinesOfCode { get; init; }
    /// <summary>获取生成符号数。</summary>
    public required long Symbols { get; init; }
    /// <summary>获取生成关系估算数。</summary>
    public required long Relations { get; init; }
}

/// <summary>
/// C0 runner 记录的硬件与运行时指纹。
/// </summary>
public sealed class HardwareFingerprint
{
    /// <summary>获取 CPU 身份。</summary>
    public required string Cpu { get; init; }
    /// <summary>获取逻辑核数。</summary>
    public required int LogicalCores { get; init; }
    /// <summary>获取 GC 可用内存字节数。</summary>
    public required long AvailableMemoryBytes { get; init; }
    /// <summary>获取 OS 描述。</summary>
    public required string OperatingSystem { get; init; }
    /// <summary>获取运行时描述。</summary>
    public required string Runtime { get; init; }
    /// <summary>获取存储卷文件系统。</summary>
    public required string FileSystem { get; init; }
    /// <summary>获取存储设备身份；无法自动读取时为 explicit_unknown。</summary>
    public required string Storage { get; init; }
}

/// <summary>
/// C0 合同基准的分位数指标。
/// </summary>
public sealed class EvidenceMetric
{
    /// <summary>获取指标名。</summary>
    public required string Name { get; init; }
    /// <summary>获取样本数。</summary>
    public required int Samples { get; init; }
    /// <summary>获取 P50。</summary>
    public required double P50 { get; init; }
    /// <summary>获取 P95。</summary>
    public required double P95 { get; init; }
    /// <summary>获取 P99。</summary>
    public required double P99 { get; init; }
    /// <summary>获取单位。</summary>
    public required string Unit { get; init; }
    /// <summary>获取实际 access path。</summary>
    public required string AccessPath { get; init; }
}

/// <summary>
/// 本地或 CI 可生成的 C0 evidence 报告。
/// </summary>
public sealed class C0EvidenceReport
{
    /// <summary>获取 schema 版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.C0Evidence;
    /// <summary>获取 Couplet commit 或 working_tree 身份。</summary>
    public required string Commit { get; init; }
    /// <summary>获取 fixture manifest SHA-256。</summary>
    public required string FixtureManifestHash { get; init; }
    /// <summary>获取 golden answer SHA-256。</summary>
    public required string GoldenAnswersHash { get; init; }
    /// <summary>获取 Agent eval manifest SHA-256。</summary>
    public required string AgentEvalManifestHash { get; init; }
    /// <summary>获取硬件指纹。</summary>
    public required HardwareFingerprint Hardware { get; init; }
    /// <summary>获取合同校验是否通过。</summary>
    public required bool ContractsPassed { get; init; }
    /// <summary>获取 paired eval runner 是否就绪。</summary>
    public required bool AgentEvalRunnerReady { get; init; }
    /// <summary>获取 paired eval 当前状态。</summary>
    public required string AgentEvalState { get; init; }
    /// <summary>获取稳定排序的问题码。</summary>
    public required IReadOnlyList<string> Problems { get; init; }
    /// <summary>获取合同 runner 指标。</summary>
    public required IReadOnlyList<EvidenceMetric> Metrics { get; init; }
    /// <summary>获取生成 UTC 时间。</summary>
    public required DateTimeOffset GeneratedAtUtc { get; init; }
}
