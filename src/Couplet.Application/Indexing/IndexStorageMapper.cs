using System.Security.Cryptography;
using System.Text;
using Couplet.Core.Contracts;
using Couplet.Core.Graph;
using Couplet.Core.Indexing;
using Couplet.Core.Languages;

namespace Couplet.Application.Indexing;

/// <summary>
/// 将 C1 snapshot 映射为 SonnetDB Document/FullText 记录与 manifest。
/// </summary>
public static class IndexStorageMapper
{
    /// <summary>
    /// 创建按 stable ID 排序的统一存储记录。
    /// </summary>
    /// <param name="snapshot">待 staging snapshot。</param>
    /// <returns>Document/FullText 记录。</returns>
    public static IReadOnlyList<IndexStorageDocument> CreateDocuments(WorkspaceIndexSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var documents = new List<IndexStorageDocument>();
        foreach (IndexedFile file in snapshot.Files)
        {
            documents.Add(new IndexStorageDocument
            {
                RecordType = IndexStorageRecordType.File,
                StableId = file.Id,
                WorkspaceId = snapshot.WorkspaceId,
                SourceRevision = snapshot.SourceRevision,
                IndexRevision = snapshot.IndexRevision,
                Path = file.Path,
                Language = file.Language,
                ContentHash = file.ContentHash,
                SemanticTier = file.SemanticTier,
                AdapterId = file.AdapterId,
                AdapterVersion = file.AdapterVersion,
                DisplayName = Path.GetFileName(file.Path),
                EntityKind = CodeEntityKind.File,
                SearchText = file.Path,
            });

            foreach (IndexedSymbol symbol in file.Symbols)
            {
                documents.Add(new IndexStorageDocument
                {
                    RecordType = IndexStorageRecordType.Symbol,
                    StableId = symbol.Id,
                    WorkspaceId = snapshot.WorkspaceId,
                    SourceRevision = snapshot.SourceRevision,
                    IndexRevision = snapshot.IndexRevision,
                    Path = file.Path,
                    Language = symbol.Language,
                    ContentHash = symbol.Provenance.ContentHash,
                    SemanticTier = file.SemanticTier,
                    AdapterId = symbol.Provenance.AdapterId,
                    AdapterVersion = symbol.Provenance.AdapterVersion,
                    DisplayName = symbol.DisplayName,
                    QualifiedIdentity = symbol.QualifiedIdentity,
                    Signature = symbol.Signature,
                    ContainerId = symbol.ContainerId,
                    EntityKind = symbol.Kind,
                    Span = symbol.Definition,
                    Confidence = symbol.Confidence,
                    SearchText = string.Join(' ', symbol.DisplayName, symbol.QualifiedIdentity, symbol.Signature),
                });
            }

            foreach (IndexedChunk chunk in file.Chunks)
            {
                documents.Add(new IndexStorageDocument
                {
                    RecordType = IndexStorageRecordType.Chunk,
                    StableId = chunk.Id,
                    WorkspaceId = snapshot.WorkspaceId,
                    SourceRevision = snapshot.SourceRevision,
                    IndexRevision = snapshot.IndexRevision,
                    Path = file.Path,
                    Language = file.Language,
                    ContentHash = chunk.ContentHash,
                    SemanticTier = file.SemanticTier,
                    AdapterId = file.AdapterId,
                    AdapterVersion = file.AdapterVersion,
                    ContainerId = chunk.SymbolId,
                    EntityKind = CodeEntityKind.Chunk,
                    Span = chunk.Span,
                    Ordinal = chunk.Ordinal,
                    Content = chunk.Content,
                    SearchText = chunk.Content,
                });
            }
        }

        var collapsed = new List<IndexStorageDocument>(documents.Count);
        foreach (IGrouping<string, IndexStorageDocument> group in documents.GroupBy(
                     document => document.StableId,
                     StringComparer.Ordinal))
        {
            IndexStorageDocument[] candidates = group.ToArray();
            if (candidates.Length == 1)
            {
                collapsed.Add(candidates[0]);
                continue;
            }

            bool isRepeatedLogicalSymbol = candidates.All(document =>
                document.RecordType == IndexStorageRecordType.Symbol
                && string.Equals(
                    document.QualifiedIdentity,
                    candidates[0].QualifiedIdentity,
                    StringComparison.Ordinal));
            if (!isRepeatedLogicalSymbol)
            {
                throw new InvalidDataException("index_stable_id_collision");
            }

            collapsed.Add(candidates
                .OrderBy(document => document.Path, StringComparer.Ordinal)
                .ThenBy(document => document.Span?.StartByte ?? long.MaxValue)
                .First());
        }

        return collapsed.OrderBy(document => document.StableId, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// 根据 snapshot 和存储记录创建 staging manifest。
    /// </summary>
    /// <param name="snapshot">待 staging snapshot。</param>
    /// <param name="documents">确定性存储记录。</param>
    /// <param name="createdAtUtc">创建 UTC 时间。</param>
    /// <returns>generation manifest。</returns>
    public static GenerationManifest CreateManifest(
        WorkspaceIndexSnapshot snapshot,
        IReadOnlyList<IndexStorageDocument> documents,
        DateTimeOffset createdAtUtc) =>
        CreateManifest(snapshot, documents, createdAtUtc, GenerationState.Staging);

    /// <summary>
    /// 根据 snapshot 和存储记录创建指定生命周期状态的 generation manifest。
    /// </summary>
    /// <param name="snapshot">generation snapshot。</param>
    /// <param name="documents">确定性存储记录。</param>
    /// <param name="createdAtUtc">创建 UTC 时间。</param>
    /// <param name="state">写入 generation 资源的生命周期状态。</param>
    /// <returns>generation manifest。</returns>
    public static GenerationManifest CreateManifest(
        WorkspaceIndexSnapshot snapshot,
        IReadOnlyList<IndexStorageDocument> documents,
        DateTimeOffset createdAtUtc,
        GenerationState state)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(documents);
        var checksum = new StringBuilder();
        foreach (IndexStorageDocument document in documents)
        {
            checksum.Append(document.StableId)
                .Append('\0')
                .Append(document.RecordType)
                .Append('\0')
                .Append(document.ContentHash)
                .Append('\n');
        }

        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checksum.ToString())))
            .ToLowerInvariant();
        return new GenerationManifest
        {
            SchemaVersion = ContractVersions.Generation,
            WorkspaceId = snapshot.WorkspaceId,
            SourceRevision = snapshot.SourceRevision,
            IndexRevision = snapshot.IndexRevision,
            PreviousIndexRevision = snapshot.PreviousIndexRevision,
            CodeGraphSchemaVersion = ContractVersions.CodeGraph,
            ProducerVersions = snapshot.ProducerVersions,
            Counts = new GenerationCounts
            {
                Files = documents.Count(document => document.RecordType == IndexStorageRecordType.File),
                Symbols = documents.Count(document => document.RecordType == IndexStorageRecordType.Symbol),
                Chunks = documents.Count(document => document.RecordType == IndexStorageRecordType.Chunk),
                FullTextDocuments = documents.Count,
                Vectors = 0,
                GraphNodes = 0,
                GraphEdges = 0,
            },
            Checksum = digest,
            State = state,
            CreatedAtUtc = createdAtUtc,
        };
    }
}
