using System.Text.Json.Serialization;
using Couplet.Core.Graph;

namespace Couplet.Core.Languages;

/// <summary>
/// 语言适配器可保证的语义等级。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SemanticTier>))]
public enum SemanticTier
{
    /// <summary>适配器对声明范围提供完整精确语义。</summary>
    Exact,
    /// <summary>适配器只对明确支持的结构提供精确结果，其余结构保持未知。</summary>
    Partial,
    /// <summary>文件只进行文本切分，不产生语义符号。</summary>
    TextOnly,
    /// <summary>当前没有可用适配器。</summary>
    Unsupported,
}

/// <summary>
/// 描述一个语言适配器的稳定能力。
/// </summary>
public sealed class LanguageCapability
{
    /// <summary>获取适配器 ID。</summary>
    public required string AdapterId { get; init; }
    /// <summary>获取适配器版本。</summary>
    public required string AdapterVersion { get; init; }
    /// <summary>获取语言 family。</summary>
    public required string Family { get; init; }
    /// <summary>获取规范化语言标识符。</summary>
    public required string Language { get; init; }
    /// <summary>获取支持的文件扩展名。</summary>
    public required IReadOnlyList<string> Extensions { get; init; }
    /// <summary>获取语义等级。</summary>
    public required SemanticTier Tier { get; init; }
}

/// <summary>
/// 描述一个已解析的符号定义。
/// </summary>
public sealed class IndexedSymbol
{
    /// <summary>获取 symbol stable ID。</summary>
    public required string Id { get; init; }
    /// <summary>获取实体类型。</summary>
    public required CodeEntityKind Kind { get; init; }
    /// <summary>获取展示名称。</summary>
    public required string DisplayName { get; init; }
    /// <summary>获取包含容器和签名的限定身份。</summary>
    public required string QualifiedIdentity { get; init; }
    /// <summary>获取规范化签名。</summary>
    public required string Signature { get; init; }
    /// <summary>获取可选容器 symbol ID。</summary>
    public string? ContainerId { get; init; }
    /// <summary>获取语言。</summary>
    public required string Language { get; init; }
    /// <summary>获取定义范围。</summary>
    public required SourceSpan Definition { get; init; }
    /// <summary>获取来源信息。</summary>
    public required Provenance Provenance { get; init; }
    /// <summary>获取置信度。</summary>
    public required Confidence Confidence { get; init; }
}

/// <summary>
/// 描述一个按符号或稳定文本边界切分的源码片段。
/// </summary>
public sealed class IndexedChunk
{
    /// <summary>获取 chunk stable ID。</summary>
    public required string Id { get; init; }
    /// <summary>获取所属文件 ID。</summary>
    public required string FileId { get; init; }
    /// <summary>获取文件内稳定序号。</summary>
    public required int Ordinal { get; init; }
    /// <summary>获取 chunk 内容 SHA-256。</summary>
    public required string ContentHash { get; init; }
    /// <summary>获取源码正文。</summary>
    public required string Content { get; init; }
    /// <summary>获取源码范围。</summary>
    public required SourceSpan Span { get; init; }
    /// <summary>获取该 chunk 对应的 symbol ID。</summary>
    public string? SymbolId { get; init; }
}

/// <summary>
/// 描述一个进入 generation 的已解析文件。
/// </summary>
public sealed class IndexedFile
{
    /// <summary>获取 file stable ID。</summary>
    public required string Id { get; init; }
    /// <summary>获取工作区相对路径。</summary>
    public required string Path { get; init; }
    /// <summary>获取文件内容 SHA-256。</summary>
    public required string ContentHash { get; init; }
    /// <summary>获取文件字节长度。</summary>
    public required long Length { get; init; }
    /// <summary>获取语言。</summary>
    public required string Language { get; init; }
    /// <summary>获取实际语义等级。</summary>
    public required SemanticTier SemanticTier { get; init; }
    /// <summary>获取适配器 ID。</summary>
    public required string AdapterId { get; init; }
    /// <summary>获取适配器版本。</summary>
    public required string AdapterVersion { get; init; }
    /// <summary>获取确定性排序的符号定义。</summary>
    public required IReadOnlyList<IndexedSymbol> Symbols { get; init; }
    /// <summary>获取确定性排序的源码 chunks。</summary>
    public required IReadOnlyList<IndexedChunk> Chunks { get; init; }
}

/// <summary>
/// 描述一个文件的稳定解析失败。
/// </summary>
public sealed class FileIndexFailure
{
    /// <summary>获取工作区相对路径。</summary>
    public required string Path { get; init; }
    /// <summary>获取稳定问题码。</summary>
    public required string Code { get; init; }
    /// <summary>获取适配器 ID。</summary>
    public required string AdapterId { get; init; }
}
