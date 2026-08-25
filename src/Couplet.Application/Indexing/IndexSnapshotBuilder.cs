using System.Security.Cryptography;
using System.Text;
using Couplet.Application.Languages;
using Couplet.Application.Workspaces;
using Couplet.Core.Graph;
using Couplet.Core.Indexing;
using Couplet.Core.Languages;
using Couplet.Core.Workspaces;

namespace Couplet.Application.Indexing;

/// <summary>
/// 从冻结的 workspace discovery 构建确定性 C1 索引 snapshot。
/// </summary>
public static class IndexSnapshotBuilder
{
    private const int _textChunkCharacters = 64 * 1024;

    /// <summary>
    /// 构建一个待 staging 的完整 snapshot。
    /// </summary>
    /// <param name="workspace">已发现工作区。</param>
    /// <param name="previousIndexRevision">前一 active index revision。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文件、符号、chunk 与失败清单。</returns>
    public static async Task<WorkspaceIndexSnapshot> BuildAsync(
        DiscoveredWorkspace workspace,
        string? previousIndexRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        IReadOnlyList<string> producerVersions = BuiltinLanguageAdapters.All
            .Select(adapter => adapter.Capability.AdapterId + "@" + adapter.Capability.AdapterVersion)
            .Append("couplet.text@1.0.0")
            .Order(StringComparer.Ordinal)
            .ToArray();
        string indexRevision = IndexRevisionFactory.Create(
            workspace.Result.WorkspaceId,
            workspace.Result.SourceRevision,
            previousIndexRevision,
            producerVersions);

        var files = new List<IndexedFile>();
        var failures = new List<FileIndexFailure>();
        foreach (WorkspaceFileDescriptor descriptor in workspace.Result.Files
                     .Where(file => file.Disposition == WorkspaceFileDisposition.Included)
                     .OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string adapterId = BuiltinLanguageAdapters.Find(descriptor.Language)?.Capability.AdapterId ?? "couplet.text";
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(workspace.IncludedPaths[descriptor.Path], cancellationToken)
                    .ConfigureAwait(false);
                string observedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(observedHash, descriptor.ContentHash, StringComparison.Ordinal))
                {
                    failures.Add(Failure(descriptor.Path, "file_changed_during_snapshot", adapterId));
                    continue;
                }

                string content = new UTF8Encoding(false, true).GetString(bytes);
                ILanguageAdapter? adapter = descriptor.TextOnly ? null : BuiltinLanguageAdapters.Find(descriptor.Language);
                files.Add(adapter is null
                    ? CreateTextOnlyFile(workspace.Result, descriptor, content, indexRevision)
                    : adapter.Parse(new LanguageParseRequest
                    {
                        WorkspaceId = workspace.Result.WorkspaceId,
                        SourceRevision = workspace.Result.SourceRevision,
                        IndexRevision = indexRevision,
                        Path = descriptor.Path,
                        ContentHash = observedHash,
                        Content = content,
                    }));
            }
            catch (DecoderFallbackException)
            {
                failures.Add(Failure(descriptor.Path, "utf8_decode_failed", adapterId));
            }
            catch (IOException)
            {
                failures.Add(Failure(descriptor.Path, "file_read_failed", adapterId));
            }
            catch (UnauthorizedAccessException)
            {
                failures.Add(Failure(descriptor.Path, "file_access_denied", adapterId));
            }
        }

        return new WorkspaceIndexSnapshot
        {
            WorkspaceId = workspace.Result.WorkspaceId,
            RepositoryIdentity = workspace.Result.RepositoryIdentity,
            WorktreeIdentity = workspace.Result.WorktreeIdentity,
            Branch = workspace.Result.Branch,
            HeadRevision = workspace.Result.HeadRevision,
            SourceRevision = workspace.Result.SourceRevision,
            IndexRevision = indexRevision,
            PreviousIndexRevision = previousIndexRevision,
            ProducerVersions = producerVersions,
            Files = files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(),
            Failures = failures.OrderBy(failure => failure.Path, StringComparer.Ordinal).ToArray(),
        };
    }

    private static IndexedFile CreateTextOnlyFile(
        WorkspaceDiscoveryResult workspace,
        WorkspaceFileDescriptor descriptor,
        string content,
        string indexRevision)
    {
        string fileId = StableId.CreateFile(workspace.WorkspaceId, descriptor.Path);
        var positions = new Utf8PositionMap(descriptor.Path, content);
        var chunks = new List<IndexedChunk>();
        int start = 0;
        while (start < content.Length)
        {
            int end = Math.Min(content.Length, start + _textChunkCharacters);
            if (end < content.Length && char.IsHighSurrogate(content[end - 1]))
            {
                end--;
            }

            string value = content[start..end];
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
            int ordinal = chunks.Count;
            chunks.Add(new IndexedChunk
            {
                Id = StableId.CreateChunk(fileId, hash, ordinal),
                FileId = fileId,
                Ordinal = ordinal,
                ContentHash = hash,
                Content = value,
                Span = positions.Span(start, end),
            });
            start = end;
        }

        return new IndexedFile
        {
            Id = fileId,
            Path = descriptor.Path,
            ContentHash = descriptor.ContentHash!,
            Length = descriptor.Length,
            Language = descriptor.Language,
            SemanticTier = SemanticTier.TextOnly,
            AdapterId = "couplet.text",
            AdapterVersion = "1.0.0",
            Symbols = [],
            Chunks = chunks,
        };
    }

    private static FileIndexFailure Failure(string path, string code, string adapterId) => new()
    {
        Path = path,
        Code = code,
        AdapterId = adapterId,
    };
}
