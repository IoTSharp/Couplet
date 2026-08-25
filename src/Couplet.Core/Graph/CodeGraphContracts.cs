using System.Text.Json.Serialization;
using Couplet.Core.Contracts;

namespace Couplet.Core.Graph;

/// <summary>
/// 代码图实体类型。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CodeEntityKind>))]
public enum CodeEntityKind
{
    /// <summary>工作区。</summary>
    Workspace,
    /// <summary>源码仓库。</summary>
    Repository,
    /// <summary>项目。</summary>
    Project,
    /// <summary>构建目标。</summary>
    BuildTarget,
    /// <summary>模块。</summary>
    Module,
    /// <summary>命名空间。</summary>
    Namespace,
    /// <summary>文件。</summary>
    File,
    /// <summary>类型。</summary>
    Type,
    /// <summary>成员。</summary>
    Member,
    /// <summary>通用符号。</summary>
    Symbol,
    /// <summary>测试。</summary>
    Test,
    /// <summary>源码片段。</summary>
    Chunk,
}

/// <summary>
/// 代码图关系类型。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CodeRelationKind>))]
public enum CodeRelationKind
{
    /// <summary>包含关系。</summary>
    Contains,
    /// <summary>定义关系。</summary>
    Defines,
    /// <summary>引用关系。</summary>
    References,
    /// <summary>调用关系。</summary>
    Calls,
    /// <summary>导入关系。</summary>
    Imports,
    /// <summary>继承关系。</summary>
    Inherits,
    /// <summary>接口实现关系。</summary>
    Implements,
    /// <summary>重写关系。</summary>
    Overrides,
    /// <summary>依赖关系。</summary>
    DependsOn,
    /// <summary>构建关系。</summary>
    Builds,
    /// <summary>测试覆盖关系。</summary>
    Covers,
}

/// <summary>
/// 证据置信度等级。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConfidenceKind>))]
public enum ConfidenceKind
{
    /// <summary>由语言语义精确证明。</summary>
    Exact,
    /// <summary>由可解释规则推导。</summary>
    Inferred,
    /// <summary>当前适配器无法确定。</summary>
    Unknown,
}

/// <summary>
/// 描述 workspace-relative UTF-8 源码范围。
/// </summary>
public sealed class SourceSpan
{
    /// <summary>获取使用正斜杠的工作区相对路径。</summary>
    public required string Path { get; init; }
    /// <summary>获取从 1 开始的起始行。</summary>
    public required int StartLine { get; init; }
    /// <summary>获取从 1 开始的起始 UTF-8 列。</summary>
    public required int StartColumn { get; init; }
    /// <summary>获取包含边界的起始 UTF-8 字节偏移。</summary>
    public required long StartByte { get; init; }
    /// <summary>获取从 1 开始的结束行。</summary>
    public required int EndLine { get; init; }
    /// <summary>获取从 1 开始的结束 UTF-8 列。</summary>
    public required int EndColumn { get; init; }
    /// <summary>获取不包含边界的结束 UTF-8 字节偏移。</summary>
    public required long EndByte { get; init; }
}

/// <summary>
/// 描述代码知识的来源和生成版本。
/// </summary>
public sealed class Provenance
{
    /// <summary>获取工作区标识符。</summary>
    public required string WorkspaceId { get; init; }
    /// <summary>获取源码 revision。</summary>
    public required string SourceRevision { get; init; }
    /// <summary>获取索引 revision。</summary>
    public required string IndexRevision { get; init; }
    /// <summary>获取语言适配器标识符。</summary>
    public required string AdapterId { get; init; }
    /// <summary>获取语言适配器版本。</summary>
    public required string AdapterVersion { get; init; }
    /// <summary>获取原始内容的 SHA-256。</summary>
    public required string ContentHash { get; init; }
}

/// <summary>
/// 描述一项结论的置信度与解释。
/// </summary>
public sealed class Confidence
{
    /// <summary>获取置信度等级。</summary>
    public required ConfidenceKind Kind { get; init; }
    /// <summary>获取 0 到 1 的归一化分数。</summary>
    public required double Score { get; init; }
    /// <summary>获取稳定推导规则标识符。</summary>
    public required string Rule { get; init; }
}

/// <summary>
/// 版本化代码图节点合同。
/// </summary>
public sealed class CodeGraphNode
{
    /// <summary>获取 schema 版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.CodeGraph;
    /// <summary>获取 stable ID。</summary>
    public required string Id { get; init; }
    /// <summary>获取实体类型。</summary>
    public required CodeEntityKind Kind { get; init; }
    /// <summary>获取展示名称。</summary>
    public required string DisplayName { get; init; }
    /// <summary>获取区分重载和同名符号的限定身份。</summary>
    public required string QualifiedIdentity { get; init; }
    /// <summary>获取规范化语言标识符。</summary>
    public required string Language { get; init; }
    /// <summary>获取定义位置；无源码位置的合成节点可为空。</summary>
    public SourceSpan? Definition { get; init; }
    /// <summary>获取来源信息。</summary>
    public required Provenance Provenance { get; init; }
    /// <summary>获取置信度信息。</summary>
    public required Confidence Confidence { get; init; }
}

/// <summary>
/// 版本化代码图边合同。
/// </summary>
public sealed class CodeGraphEdge
{
    /// <summary>获取 schema 版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.CodeGraph;
    /// <summary>获取关系 stable ID。</summary>
    public required string Id { get; init; }
    /// <summary>获取起点 stable ID。</summary>
    public required string SourceId { get; init; }
    /// <summary>获取终点 stable ID。</summary>
    public required string TargetId { get; init; }
    /// <summary>获取关系类型。</summary>
    public required CodeRelationKind Kind { get; init; }
    /// <summary>获取产生该关系的证据位置。</summary>
    public required SourceSpan Evidence { get; init; }
    /// <summary>获取来源信息。</summary>
    public required Provenance Provenance { get; init; }
    /// <summary>获取置信度信息。</summary>
    public required Confidence Confidence { get; init; }
}

/// <summary>
/// 提供冻结的代码图 v1 类型集合。
/// </summary>
public static class CodeGraphSchema
{
    /// <summary>获取 v1 支持的节点类型。</summary>
    public static IReadOnlyList<CodeEntityKind> EntityKinds { get; } = Enum.GetValues<CodeEntityKind>();

    /// <summary>获取 v1 支持的关系类型。</summary>
    public static IReadOnlyList<CodeRelationKind> RelationKinds { get; } = Enum.GetValues<CodeRelationKind>();
}
