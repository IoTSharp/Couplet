using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Couplet.Application.Indexing;
using Couplet.Application.Serialization;
using Couplet.Core.Indexing;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Engine.Retention;
using SonnetDB.FullText;
using SonnetDB.Kv;

namespace Couplet.Infrastructure.SonnetDb;

/// <summary>
/// 把 C1 generation 写入固定 SonnetDB package 的 Document/FullText staging collection。
/// </summary>
public sealed class SonnetDbIndexGenerationStore : IDisposable
{
    private const string _controlKeyspaceName = "couplet_control";
    private const string _fullTextIndexName = "code_search";
    private readonly Tsdb _database;
    private readonly KvKeyspace _control;
    private readonly bool _backgroundMaintenanceEnabled;
    private bool _disposed;

    /// <summary>
    /// 打开一个以数据库目录为持久化边界的 SonnetDB store。
    /// </summary>
    /// <param name="databaseRoot">显式数据库目录。</param>
    public SonnetDbIndexGenerationStore(string databaseRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRoot);
        string root = Path.GetFullPath(databaseRoot);
        Directory.CreateDirectory(root);
        _backgroundMaintenanceEnabled = RuntimeFeature.IsDynamicCodeSupported;
        TsdbOptions options = new() { RootDirectory = root };
        if (!_backgroundMaintenanceEnabled)
        {
            options = options with
            {
                BackgroundFlush = BackgroundFlushOptions.Default with { Enabled = false },
                Compaction = CompactionPolicy.Default with { Enabled = false },
                Retention = RetentionPolicy.Default with { Enabled = false },
                Kv = KvOptions.Default with
                {
                    ExpirerEnabled = false,
                    CleanupEnabled = false,
                },
            };
        }

        _database = Tsdb.Open(options);
        _control = _database.Keyspaces.Open(_controlKeyspaceName);
    }

    /// <summary>
    /// 写入并验证一个查询不可见的 staging generation。
    /// </summary>
    /// <param name="snapshot">完整索引 snapshot。</param>
    /// <param name="plan">对应的增量变化计划。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>staging 报告；C1 发布门禁未满足时 Published 固定为 false。</returns>
    public IndexStageReport Stage(
        WorkspaceIndexSnapshot snapshot,
        IncrementalIndexPlan plan,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(plan);
        if (snapshot.WorkspaceId != plan.WorkspaceId
            || snapshot.SourceRevision != plan.SourceRevision
            || snapshot.IndexRevision != plan.IndexRevision)
        {
            throw new ArgumentException("Snapshot and plan identities must match.", nameof(plan));
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<IndexStorageDocument> documents = IndexStorageMapper.CreateDocuments(snapshot);
        GenerationManifest manifest = IndexStorageMapper.CreateManifest(snapshot, documents, DateTimeOffset.UtcNow);
        string collectionName = CollectionName(snapshot.WorkspaceId, snapshot.IndexRevision);
        string stagingKey = StagingKey(snapshot.WorkspaceId, snapshot.IndexRevision);

        // A completed marker must never survive replacement of its collection.
        _control.Delete(stagingKey);
        _control.CreateSnapshot();

        if (_database.Documents.Catalog.TryGet(collectionName) is not null)
        {
            _database.Documents.Drop(collectionName);
        }

        cancellationToken.ThrowIfCancellationRequested();

        DocumentCollectionSchema schema = DocumentCollectionSchema.Create(
            collectionName,
            indexes:
            [
                new DocumentPathIndexDefinition("by_stable_id", "$.stable_id", IsUnique: true),
                new DocumentPathIndexDefinition("by_record_type", "$.record_type"),
                new DocumentPathIndexDefinition("by_path", "$.path"),
                new DocumentPathIndexDefinition("by_qualified_identity", "$.qualified_identity", IsSparse: true),
            ],
            fullTextIndexes:
            [
                new DocumentFullTextIndexDefinition(
                    _fullTextIndexName,
                    ["$.search_text"],
                    "unicode",
                    DateTimeOffset.UtcNow.UtcTicks),
            ]);
        _database.Documents.Create(schema);
        DocumentCollectionStore collection = _database.Documents.Open(collectionName);

        const int batchSize = 512;
        for (int offset = 0; offset < documents.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<DocumentWriteRequest> writes = documents
                .Skip(offset)
                .Take(Math.Min(batchSize, documents.Count - offset))
                .Select(document => new DocumentWriteRequest(
                    document.StableId,
                    CoupletJsonSerializer.Serialize(document),
                    null));
            DocumentWriteResult result;
            try
            {
                result = collection.InsertMany(writes, ordered: true);
            }
            catch (IOException exception) when (IsCheckpointBudgetRejection(exception))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _database.Documents.CheckpointAll();
                result = collection.InsertMany(writes, ordered: true);
            }

            if (!result.Committed || result.HasErrors)
            {
                string[] writeProblems = result.Errors
                    .Select(error => "document_batch_write_failed:" + NormalizeProblemCode(error.Code))
                    .Append("document_batch_write_failed")
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                return Report(manifest, collectionName, false, writeProblems);
            }

        }

        DocumentIndexConsistencyReport consistency = collection.VerifyIndexConsistency();
        var problems = new SortedSet<string>(GenerationContractValidator.Validate(manifest), StringComparer.Ordinal);
        if (collection.Count() != documents.Count)
        {
            problems.Add("document_count_mismatch");
        }

        if (collection.GetFullTextDocumentCount(schema.TryGetFullTextIndex(_fullTextIndexName)!) != documents.Count)
        {
            problems.Add("fulltext_count_mismatch");
        }

        if (!consistency.IsConsistent)
        {
            problems.Add("document_index_inconsistent");
        }

        if (problems.Count == 0)
        {
            _database.Documents.CheckpointAll();
            byte[] manifestBytes = Encoding.UTF8.GetBytes(CoupletJsonSerializer.Serialize(manifest));
            _control.Put(stagingKey, manifestBytes);
            _control.CreateSnapshot();

            StagingGenerationInspection inspection = InspectStaging(
                snapshot.WorkspaceId,
                snapshot.IndexRevision);
            problems.UnionWith(inspection.Problems);
        }

        return Report(manifest, collectionName, problems.Count == 0, problems.ToArray());
    }

    /// <summary>
    /// 读取一个已完成 staging 的 manifest。
    /// </summary>
    /// <param name="workspaceId">工作区 ID。</param>
    /// <param name="indexRevision">索引 revision。</param>
    /// <returns>manifest；不存在时为空。</returns>
    public GenerationManifest? ReadStagingManifest(string workspaceId, string indexRevision)
    {
        StagingGenerationInspection inspection = InspectStaging(workspaceId, indexRevision);
        return inspection.Complete ? inspection.Manifest : null;
    }

    /// <summary>
    /// 检查一个 staging generation 在当前进程或重开后是否仍完整且不可发布。
    /// </summary>
    /// <param name="workspaceId">工作区 ID。</param>
    /// <param name="indexRevision">索引 revision。</param>
    /// <returns>manifest、Document、FullText 和 path index 的一致性结果。</returns>
    public StagingGenerationInspection InspectStaging(string workspaceId, string indexRevision)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexRevision);

        string collectionName = CollectionName(workspaceId, indexRevision);
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        GenerationManifest? manifest = null;
        byte[]? bytes = _control.Get(StagingKey(workspaceId, indexRevision));
        if (bytes is null)
        {
            problems.Add("staging_manifest_missing");
        }
        else
        {
            try
            {
                manifest = CoupletJsonSerializer.DeserializeGenerationManifest(Encoding.UTF8.GetString(bytes));
            }
            catch (System.Text.Json.JsonException)
            {
                problems.Add("staging_manifest_invalid");
            }
        }

        if (manifest is not null)
        {
            problems.UnionWith(GenerationContractValidator.Validate(manifest));
            if (!string.Equals(manifest.WorkspaceId, workspaceId, StringComparison.Ordinal)
                || !string.Equals(manifest.IndexRevision, indexRevision, StringComparison.Ordinal))
            {
                problems.Add("staging_manifest_identity_mismatch");
            }

            if (manifest.State != GenerationState.Staging)
            {
                problems.Add("staging_manifest_state_invalid");
            }
        }

        DocumentCollectionSchema? schema = _database.Documents.Catalog.TryGet(collectionName);
        if (schema is null)
        {
            problems.Add("staging_collection_missing");
        }
        else
        {
            if (schema.TryGetIndex("by_stable_id") is null
                || schema.TryGetIndex("by_record_type") is null
                || schema.TryGetIndex("by_path") is null
                || schema.TryGetIndex("by_qualified_identity") is null)
            {
                problems.Add("staging_path_index_missing");
            }

            DocumentFullTextIndex? fullTextIndex = schema.TryGetFullTextIndex(_fullTextIndexName);
            if (fullTextIndex is null)
            {
                problems.Add("staging_fulltext_index_missing");
            }

            DocumentCollectionStore collection = _database.Documents.Open(collectionName);
            DocumentIndexConsistencyReport consistency = collection.VerifyIndexConsistency();
            if (!consistency.IsConsistent)
            {
                problems.Add("staging_document_index_inconsistent");
            }

            if (manifest is not null)
            {
                long expectedDocuments = manifest.Counts.Files + manifest.Counts.Symbols + manifest.Counts.Chunks;
                if (collection.Count() != expectedDocuments)
                {
                    problems.Add("staging_document_count_mismatch");
                }

                if (fullTextIndex is not null
                    && collection.GetFullTextDocumentCount(fullTextIndex) != manifest.Counts.FullTextDocuments)
                {
                    problems.Add("staging_fulltext_count_mismatch");
                }
            }
        }

        return new StagingGenerationInspection
        {
            WorkspaceId = workspaceId,
            IndexRevision = indexRevision,
            CollectionName = collectionName,
            Manifest = manifest,
            Complete = problems.Count == 0,
            Problems = problems.ToArray(),
        };
    }

    internal StagingQueryProbeResult ProbeExact(
        string workspaceId,
        string indexRevision,
        string stableId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        string collectionName = CollectionName(workspaceId, indexRevision);
        DocumentCollectionSchema? schema = _database.Documents.Catalog.TryGet(collectionName);
        if (schema?.TryGetIndex("by_stable_id") is not { } index)
        {
            return new StagingQueryProbeResult("unavailable", null, null, []);
        }

        DocumentCollectionStore collection = _database.Documents.Open(collectionName);
        IReadOnlyList<DocumentRow> rows = collection.GetByIndex(index, stableId, 2);
        return new StagingQueryProbeResult(
            "document_path_index:by_stable_id",
            rows.Count,
            rows.Count,
            rows.Select(row => CoupletJsonSerializer.DeserializeIndexStorageDocument(row.Json)).ToArray());
    }

    internal StagingQueryProbeResult ProbeFullText(
        string workspaceId,
        string indexRevision,
        string query,
        int topK)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        string collectionName = CollectionName(workspaceId, indexRevision);
        DocumentCollectionSchema? schema = _database.Documents.Catalog.TryGet(collectionName);
        if (schema?.TryGetFullTextIndex(_fullTextIndexName) is not { } index)
        {
            return new StagingQueryProbeResult("unavailable", null, null, []);
        }

        DocumentCollectionStore collection = _database.Documents.Open(collectionName);
        IReadOnlyList<DocumentFullTextSearchHit> hits = collection.SearchFullText(
            index,
            "$.search_text",
            query,
            topK);
        var documents = new List<IndexStorageDocument>(hits.Count);
        foreach (DocumentFullTextSearchHit hit in hits)
        {
            DocumentRow? row = collection.Get(hit.DocumentId);
            if (row is not null)
            {
                documents.Add(CoupletJsonSerializer.DeserializeIndexStorageDocument(row.Json));
            }
        }

        return new StagingQueryProbeResult("document_fulltext:code_search", null, null, documents);
    }

    /// <summary>
    /// 释放 SonnetDB 数据库目录。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _database.Dispose();
        _disposed = true;
    }

    private IndexStageReport Report(
        GenerationManifest manifest,
        string collectionName,
        bool staged,
        IReadOnlyList<string> problems) => new()
        {
            Manifest = manifest,
            CollectionName = collectionName,
            Staged = staged,
            Published = false,
            BlockingGap = "CG-005",
            Limitations = _backgroundMaintenanceEnabled
                ? []
                : ["CG-006:sonnetdb_background_maintenance_disabled"],
            Problems = problems,
        };

    private static string StagingKey(string workspaceId, string indexRevision) =>
        $"staging/{workspaceId}/{indexRevision}";

    private static string NormalizeProblemCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "unknown";
        }

        string normalized = new(code
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized.Length == 0 ? "unknown" : normalized;
    }

    private static bool IsCheckpointBudgetRejection(IOException exception) =>
        exception.Message.StartsWith(
            "KV atomic mutation batch was rejected before WAL append because it exceeds the current checkpoint budget",
            StringComparison.Ordinal);

    private static string CollectionName(string workspaceId, string indexRevision)
    {
        string value = workspaceId + "\0" + indexRevision;
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return "cplg_" + digest[..32];
    }
}

internal sealed record StagingQueryProbeResult(
    string AccessPath,
    long? Candidates,
    long? Examined,
    IReadOnlyList<IndexStorageDocument> Documents);
