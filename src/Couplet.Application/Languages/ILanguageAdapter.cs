using Couplet.Core.Languages;

namespace Couplet.Application.Languages;

/// <summary>
/// 语言适配器的确定性解析输入。
/// </summary>
public sealed class LanguageParseRequest
{
    /// <summary>获取工作区 ID。</summary>
    public required string WorkspaceId { get; init; }
    /// <summary>获取 source revision。</summary>
    public required string SourceRevision { get; init; }
    /// <summary>获取 index revision。</summary>
    public required string IndexRevision { get; init; }
    /// <summary>获取 workspace-relative path。</summary>
    public required string Path { get; init; }
    /// <summary>获取内容 SHA-256。</summary>
    public required string ContentHash { get; init; }
    /// <summary>获取 UTF-8 源码文本。</summary>
    public required string Content { get; init; }
}

/// <summary>
/// 从单个源码文件产生稳定符号和 chunks 的可替换适配器。
/// </summary>
public interface ILanguageAdapter
{
    /// <summary>获取适配器能力。</summary>
    LanguageCapability Capability { get; }

    /// <summary>
    /// 解析一个已冻结内容的源码文件。
    /// </summary>
    /// <param name="request">确定性解析输入。</param>
    /// <returns>已解析文件。</returns>
    IndexedFile Parse(LanguageParseRequest request);
}
