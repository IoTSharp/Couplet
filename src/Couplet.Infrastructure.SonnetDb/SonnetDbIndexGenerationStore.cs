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
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
using SonnetDB.Generations;
#endif

namespace Couplet.Infrastructure.SonnetDb;

/// <summary>
/// 把 C1 generation 写入固定 SonnetDB package 的 Document/FullText staging collection。
/// </summary>
public sealed class SonnetDbIndexGenerationStore : IDisposable
{
    private const string _controlKeyspaceName = "couplet_control";
    private const string _fullTextIndexName = "code_search";
    private static readonly string[] _aotMaintenanceLimitations =
        ["CG-006:sonnetdb_background_maintenance_disabled"];
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
    private const string _documentsRole = "code_documents";
    private const string _fullTextRole = "code_search";
    private const string _planningRole = "index_planning";
    private const string _planningSnapshotKey = "planning_snapshot";
    private const string _publishedManifestKey = "generation_manifest";
    private static readonly string[] _sourceGenerationLimitations =
        ["CPL-015:retired_generation_retention_policy_not_connected"];
    private static readonly string[] _cleanupFailureLimitations =
    [
        "CPL-015:retired_generation_cleanup_retry_required",
        "CPL-015:retired_generation_retention_policy_not_connected",
    ];
#endif
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
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
        _backgroundMaintenanceEnabled = true;
#else
        _backgroundMaintenanceEnabled = RuntimeFeature.IsDynamicCodeSupported;
#endif
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

#if COUPLET_SONNETDB_SOURCE_GENERATIONS
    internal ActiveIndexPlanningSnapshot? ReadActivePlanningSnapshot(string workspaceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        DatabaseGenerationQueryLease lease;
        try
        {
            lease = _database.Generations.AcquireActive(workspaceId);
        }
        catch (DatabaseGenerationException exception)
            when (exception.Code == DatabaseGenerationErrorCodes.NoActiveGeneration)
        {
            return null;
        }

        using (lease)
        {
            DatabaseGenerationResource resource = lease.GetRequiredResource(
                _planningRole,
                DatabaseGenerationResourceKind.KvKeyspace);
            byte[]? payload = _database.Keyspaces.Open(resource.Name).Get(_planningSnapshotKey);
            if (payload is null)
            {
                throw new InvalidDataException("active_index_planning_snapshot_missing");
            }

            IndexPlanningSnapshot planning;
            try
            {
                planning = CoupletJsonSerializer.DeserializeIndexPlanningSnapshot(
                    Encoding.UTF8.GetString(payload));
            }
            catch (System.Text.Json.JsonException exception)
            {
                throw new InvalidDataException("active_index_planning_snapshot_invalid", exception);
            }

            if (planning.SchemaVersion != Couplet.Core.Contracts.ContractVersions.IndexPlanningSnapshot
                || !string.Equals(planning.WorkspaceId, workspaceId, StringComparison.Ordinal)
                || !string.Equals(planning.IndexRevision, lease.Generation.GenerationId, StringComparison.Ordinal)
                || planning.ProducerVersions.Count == 0
                || planning.Files.GroupBy(file => file.Path, StringComparer.Ordinal).Any(group => group.Count() != 1))
            {
                throw new InvalidDataException("active_index_planning_snapshot_invalid");
            }

            return new ActiveIndexPlanningSnapshot(lease.Generation.Revision, planning);
        }
    }

    internal IndexStageReport StageAndPublish(
        WorkspaceIndexSnapshot snapshot,
        IncrementalIndexPlan plan,
        long expectedDatabaseGenerationRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedDatabaseGenerationRevision);
        cancellationToken.ThrowIfCancellationRequested();
        IndexStageReport? noOp = TryReuseActiveGeneration(
            snapshot,
            plan,
            expectedDatabaseGenerationRevision);
        if (noOp is not null)
        {
            return noOp;
        }

        IndexStageReport staging = Stage(snapshot, plan, cancellationToken);
        if (!staging.Staged)
        {
            return staging;
        }

        string planningKeyspaceName = PlanningKeyspaceName(snapshot.WorkspaceId, snapshot.IndexRevision);
        KvKeyspace planningKeyspace = _database.Keyspaces.Open(planningKeyspaceName);
        IndexPlanningSnapshot planning = IndexPlanningSnapshotMapper.Create(snapshot);
        GenerationManifest manifest = PublishedManifest(staging.Manifest);
        planningKeyspace.Put(
            _planningSnapshotKey,
            Encoding.UTF8.GetBytes(CoupletJsonSerializer.Serialize(planning)));
        planningKeyspace.Put(
            _publishedManifestKey,
            Encoding.UTF8.GetBytes(CoupletJsonSerializer.Serialize(manifest)));
        planningKeyspace.CreateSnapshot();

        DatabaseGeneration generation = _database.Generations.Publish(
            new DatabaseGenerationPublishRequest
            {
                Stream = snapshot.WorkspaceId,
                GenerationId = snapshot.IndexRevision,
                ExpectedRevision = expectedDatabaseGenerationRevision,
                Resources =
                [
                    new DatabaseGenerationResource(
                        _planningRole,
                        DatabaseGenerationResourceKind.KvKeyspace,
                        planningKeyspaceName),
                    new DatabaseGenerationResource(
                        _documentsRole,
                        DatabaseGenerationResourceKind.DocumentCollection,
                        staging.CollectionName),
                    new DatabaseGenerationResource(
                        _fullTextRole,
                        DatabaseGenerationResourceKind.DocumentFullTextIndex,
                        _fullTextIndexName,
                        staging.CollectionName),
                ],
            },
            cancellationToken);

        // Publication has committed. Finish best-effort retirement deterministically instead of
        // surfacing cancellation as if the new generation had not become active.
        CleanupOutcome cleanup = CleanupAfterPublish(snapshot.WorkspaceId);
        return Report(
            manifest,
            staging.CollectionName,
            staged: true,
            problems: cleanup.Problems,
            published: true,
            databaseGenerationRevision: generation.Revision,
            removedGenerationRevisions: cleanup.RemovedGenerationRevisions,
            deferredGenerationRevisions: cleanup.DeferredGenerationRevisions,
            additionalLimitations: cleanup.Limitations);
    }

    internal DatabaseGenerationQueryLease AcquireActiveGeneration(string workspaceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        return _database.Generations.AcquireActive(workspaceId);
    }

    internal DatabaseGenerationCleanupResult CleanupRetired(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (CleanupRetiredTestHook is not null)
        {
            return CleanupRetiredTestHook(workspaceId, cancellationToken);
        }

        Dictionary<long, string> generationIds = _database.Generations.List(workspaceId)
            .ToDictionary(generation => generation.Revision, generation => generation.GenerationId);
        DatabaseGenerationCleanupResult cleanup = _database.Generations.CleanupRetired(
            workspaceId,
            cancellationToken);
        foreach (long revision in cleanup.RemovedRevisions)
        {
            if (generationIds.TryGetValue(revision, out string? indexRevision))
            {
                _control.Delete(StagingKey(workspaceId, indexRevision));
            }
        }

        if (cleanup.RemovedRevisions.Count != 0)
        {
            _control.CreateSnapshot();
        }

        return cleanup;
    }

    internal Func<string, CancellationToken, DatabaseGenerationCleanupResult>? CleanupRetiredTestHook { get; set; }
#endif

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
        IReadOnlyList<string> problems,
        bool published = false,
        long? databaseGenerationRevision = null,
        IReadOnlyList<long>? removedGenerationRevisions = null,
        IReadOnlyList<long>? deferredGenerationRevisions = null,
        bool reusedActiveGeneration = false,
        IReadOnlyList<string>? additionalLimitations = null) => new()
        {
            Manifest = manifest,
            CollectionName = collectionName,
            Staged = staged,
            Published = published,
            ReusedActiveGeneration = reusedActiveGeneration,
            DatabaseGenerationRevision = databaseGenerationRevision,
            BlockingGap = published ? null : "CG-005",
            RemovedGenerationRevisions = removedGenerationRevisions ?? [],
            DeferredGenerationRevisions = deferredGenerationRevisions ?? [],
            Limitations = (_backgroundMaintenanceEnabled
                    ? Array.Empty<string>()
                    : _aotMaintenanceLimitations)
                .Concat(additionalLimitations ?? [])
                .Order(StringComparer.Ordinal)
                .ToArray(),
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

#if COUPLET_SONNETDB_SOURCE_GENERATIONS
    private IndexStageReport? TryReuseActiveGeneration(
        WorkspaceIndexSnapshot snapshot,
        IncrementalIndexPlan plan,
        long expectedDatabaseGenerationRevision)
    {
        if (expectedDatabaseGenerationRevision == 0
            || plan.RebuildRequired
            || plan.Changes.Any(change => change.Kind != IndexFileChangeKind.Unchanged))
        {
            return null;
        }

        using DatabaseGenerationQueryLease lease = _database.Generations.AcquireActive(snapshot.WorkspaceId);
        if (lease.Generation.Revision != expectedDatabaseGenerationRevision
            || !string.Equals(lease.Generation.GenerationId, plan.PreviousIndexRevision, StringComparison.Ordinal))
        {
            throw new DatabaseGenerationException(
                DatabaseGenerationErrorCodes.RevisionConflict,
                "Active generation changed before the no-op index run completed.");
        }

        DatabaseGenerationResource planningResource = lease.GetRequiredResource(
            _planningRole,
            DatabaseGenerationResourceKind.KvKeyspace);
        KvKeyspace planningKeyspace = _database.Keyspaces.Open(planningResource.Name);
        byte[]? snapshotBytes = planningKeyspace.Get(_planningSnapshotKey);
        byte[]? manifestBytes = planningKeyspace.Get(_publishedManifestKey);
        if (snapshotBytes is null || manifestBytes is null)
        {
            throw new InvalidDataException("active_index_planning_metadata_missing");
        }

        IndexPlanningSnapshot activeSnapshot;
        GenerationManifest activeManifest;
        try
        {
            activeSnapshot = CoupletJsonSerializer.DeserializeIndexPlanningSnapshot(
                Encoding.UTF8.GetString(snapshotBytes));
            activeManifest = CoupletJsonSerializer.DeserializeGenerationManifest(
                Encoding.UTF8.GetString(manifestBytes));
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new InvalidDataException("active_index_planning_metadata_invalid", exception);
        }

        if (!string.Equals(activeSnapshot.SourceRevision, snapshot.SourceRevision, StringComparison.Ordinal)
            || !activeSnapshot.ProducerVersions.SequenceEqual(snapshot.ProducerVersions, StringComparer.Ordinal)
            || activeManifest.State != GenerationState.Published
            || !string.Equals(activeManifest.IndexRevision, lease.Generation.GenerationId, StringComparison.Ordinal))
        {
            return null;
        }

        DatabaseGenerationResource documents = lease.GetRequiredResource(
            _documentsRole,
            DatabaseGenerationResourceKind.DocumentCollection);
        CleanupOutcome cleanup = CleanupAfterPublish(snapshot.WorkspaceId);
        return Report(
            activeManifest,
            documents.Name,
            staged: true,
            problems: cleanup.Problems,
            published: true,
            databaseGenerationRevision: lease.Generation.Revision,
            removedGenerationRevisions: cleanup.RemovedGenerationRevisions,
            deferredGenerationRevisions: cleanup.DeferredGenerationRevisions,
            reusedActiveGeneration: true,
            additionalLimitations: cleanup.Limitations);
    }

    private CleanupOutcome CleanupAfterPublish(string workspaceId)
    {
        try
        {
            DatabaseGenerationCleanupResult cleanup = CleanupRetired(
                workspaceId,
                CancellationToken.None);
            return new CleanupOutcome(
                cleanup.RemovedRevisions,
                cleanup.DeferredRevisions,
                [],
                _sourceGenerationLimitations);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or DatabaseGenerationException)
        {
            return new CleanupOutcome(
                [],
                [],
                ["retired_generation_cleanup_failed"],
                _cleanupFailureLimitations);
        }
    }

    private static string PlanningKeyspaceName(string workspaceId, string indexRevision)
    {
        string value = "planning\0" + workspaceId + "\0" + indexRevision;
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return "cplk_" + digest[..32];
    }

    private static GenerationManifest PublishedManifest(GenerationManifest manifest) => new()
    {
        SchemaVersion = manifest.SchemaVersion,
        WorkspaceId = manifest.WorkspaceId,
        SourceRevision = manifest.SourceRevision,
        IndexRevision = manifest.IndexRevision,
        PreviousIndexRevision = manifest.PreviousIndexRevision,
        CodeGraphSchemaVersion = manifest.CodeGraphSchemaVersion,
        ProducerVersions = manifest.ProducerVersions,
        Counts = manifest.Counts,
        Checksum = manifest.Checksum,
        State = GenerationState.Published,
        CreatedAtUtc = manifest.CreatedAtUtc,
    };
#endif
}

internal sealed record StagingQueryProbeResult(
    string AccessPath,
    long? Candidates,
    long? Examined,
    IReadOnlyList<IndexStorageDocument> Documents);

#if COUPLET_SONNETDB_SOURCE_GENERATIONS
internal sealed record ActiveIndexPlanningSnapshot(
    long DatabaseGenerationRevision,
    IndexPlanningSnapshot PlanningSnapshot);

internal sealed record CleanupOutcome(
    IReadOnlyList<long> RemovedGenerationRevisions,
    IReadOnlyList<long> DeferredGenerationRevisions,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Limitations);
#endif
