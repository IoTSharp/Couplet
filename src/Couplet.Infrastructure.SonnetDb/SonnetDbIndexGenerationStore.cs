using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Couplet.Application.Indexing;
using Couplet.Application.Serialization;
using Couplet.Core.Graph;
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
    private const string _databaseRootLockFileName = ".couplet-store.lock";
    private const string _databaseRootOwnershipError =
        "The Couplet database root cannot be exclusively owned by this store.";
    private const string _documentsRole = "code_documents";
    private const string _fullTextRole = "code_search";
    private const string _planningRole = "index_planning";
    private const string _planningSnapshotKey = "planning_snapshot";
    private const string _publishedManifestKey = "generation_manifest";
    private const string _queryCursorSigningKeyKey = "query_cursor_signing_key:v1";
    private const string _queryCursorRecordPrefix = "query_cursor:v1:";
    private const int _queryCursorEnvelopeVersion = 1;
    private const int _queryCursorRecordVersion = 1;
    private const int _queryCursorSigningKeyLength = 32;
    private const int _queryCursorSignatureLength = 32;
    private const int _queryCursorHashLength = 32;
    private const int _maximumRetainedQueryLeases = 128;
    private static readonly byte[] _queryCursorSignatureDomain =
        Encoding.ASCII.GetBytes("Couplet.CodeSearchCursor.v1\0");
    private static readonly UTF8Encoding _queryCursorUtf8 = new(false, true);
    private static readonly TimeSpan _defaultQueryCursorLeaseRetention = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _maximumQueryLeaseTimerDueTime =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1d);
    private static readonly Action<IndexQueryCursorRecoveryFaultPoint> _noQueryCursorRecoveryFault =
        static _ => { };
    private static readonly string[] _cleanupFailureLimitations =
        ["CPL-015:retired_generation_cleanup_retry_required"];
#endif
    private readonly Tsdb _database;
    private readonly KvKeyspace _control;
    private readonly bool _backgroundMaintenanceEnabled;
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
    private FileStream? _databaseRootLease;
    private readonly object _retainedQueryLeaseSync = new();
    private readonly Dictionary<Guid, RetainedIndexQueryCursor> _retainedQueryLeases = [];
    private readonly ITimer _retainedQueryLeaseTimer;
    private readonly byte[] _queryCursorSigningKey;
    private readonly TimeSpan _queryCursorLeaseRetention;
    private readonly TimeSpan _retiredGenerationRetention;
    private readonly TimeProvider _timeProvider;
    private bool _queryCursorRegistryFaulted;
    private int _queryLeaseSlotCount;
#endif
    private bool _disposed;

    /// <summary>
    /// 打开一个以数据库目录为持久化边界的 SonnetDB store。
    /// </summary>
    /// <param name="databaseRoot">显式数据库目录。</param>
    public SonnetDbIndexGenerationStore(string databaseRoot)
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
        : this(databaseRoot, TimeSpan.Zero, null)
#endif
    {
#if !COUPLET_SONNETDB_SOURCE_GENERATIONS
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
#endif
    }

#if COUPLET_SONNETDB_SOURCE_GENERATIONS
    /// <summary>
    /// 打开 source lane SonnetDB store，并配置 retired generation 的清理资格时长。
    /// </summary>
    /// <param name="databaseRoot">显式数据库目录。</param>
    /// <param name="retiredGenerationRetention">retired generation 从发布时间起算、达到后具备清理资格的时长；零值保持立即清理。</param>
    /// <param name="timeProvider">用于计算 cleanup cutoff 的时钟；未提供时使用系统时钟。</param>
    public SonnetDbIndexGenerationStore(
        string databaseRoot,
        TimeSpan retiredGenerationRetention,
        TimeProvider? timeProvider = null)
        : this(
            databaseRoot,
            retiredGenerationRetention,
            timeProvider,
            _defaultQueryCursorLeaseRetention)
    {
    }

    /// <summary>
    /// 打开 source lane SonnetDB store，并分别配置 retired generation 与分页 cursor lease 保留时长。
    /// </summary>
    /// <param name="databaseRoot">显式数据库目录。</param>
    /// <param name="retiredGenerationRetention">retired generation 从发布时间起算、达到后具备清理资格的时长；零值保持立即清理。</param>
    /// <param name="timeProvider">用于计算 cleanup cutoff 与 cursor lease 到期时间的时钟。</param>
    /// <param name="queryCursorLeaseRetention">分页 cursor 跨请求保留 generation lease 的绝对时长；零值禁用续页保留。</param>
    public SonnetDbIndexGenerationStore(
        string databaseRoot,
        TimeSpan retiredGenerationRetention,
        TimeProvider? timeProvider,
        TimeSpan queryCursorLeaseRetention)
        : this(
            databaseRoot,
            retiredGenerationRetention,
            timeProvider,
            queryCursorLeaseRetention,
            queryCursorRecoveryFaultTestHook: null)
    {
    }

    internal SonnetDbIndexGenerationStore(
        string databaseRoot,
        TimeSpan retiredGenerationRetention,
        TimeProvider? timeProvider,
        TimeSpan queryCursorLeaseRetention,
        Action<IndexQueryCursorRecoveryFaultPoint>? queryCursorRecoveryFaultTestHook)
    {
        if (retiredGenerationRetention < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retiredGenerationRetention),
                retiredGenerationRetention,
                "Retired generation retention cannot be negative.");
        }

        if (queryCursorLeaseRetention < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(queryCursorLeaseRetention),
                queryCursorLeaseRetention,
                "Query cursor lease retention cannot be negative.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRoot);
        string root = Path.GetFullPath(databaseRoot);
        Directory.CreateDirectory(root);
        FileStream databaseRootLease = AcquireDatabaseRootLease(root);
        Tsdb? database = null;
        _retiredGenerationRetention = retiredGenerationRetention;
        _queryCursorLeaseRetention = queryCursorLeaseRetention;
        _timeProvider = timeProvider ?? TimeProvider.System;
        QueryCursorRecoveryFaultTestHook = queryCursorRecoveryFaultTestHook
            ?? _noQueryCursorRecoveryFault;
        _backgroundMaintenanceEnabled = true;
        try
        {
            database = Tsdb.Open(new TsdbOptions { RootDirectory = root });
            _database = database;
            _control = _database.Keyspaces.Open(_controlKeyspaceName);
            _queryCursorSigningKey = LoadOrCreateQueryCursorSigningKey();
            _retainedQueryLeaseTimer = _timeProvider.CreateTimer(
                static state => ((SonnetDbIndexGenerationStore)state!).RunRetainedQueryLeaseTimerCallback(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            RestoreRetainedQueryLeases();
            _databaseRootLease = databaseRootLease;
        }
        catch
        {
            try
            {
                _retainedQueryLeaseTimer?.Dispose();
                foreach (RetainedIndexQueryCursor retained in _retainedQueryLeases.Values)
                {
                    retained.Lease.Dispose();
                }

                _retainedQueryLeases.Clear();
            }
            finally
            {
                try
                {
                    database?.Dispose();
                }
                finally
                {
                    databaseRootLease.Dispose();
                }
            }

            throw;
        }
    }
#endif

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
                new DocumentPathIndexDefinition("by_language", "$.language"),
                new DocumentPathIndexDefinition("by_entity_kind", "$.entity_kind", IsSparse: true),
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

        PublishFaultTestHook?.Invoke(IndexGenerationPublishFaultPoint.BeforeCommit);
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
        PublishFaultTestHook?.Invoke(IndexGenerationPublishFaultPoint.AfterCommit);

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
            retentionDeferredGenerationRevisions: cleanup.RetentionDeferredGenerationRevisions,
            additionalLimitations: cleanup.Limitations);
    }

    internal DatabaseGenerationQueryLease AcquireActiveGeneration(string workspaceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        return _database.Generations.AcquireActive(workspaceId);
    }

    internal ActiveIndexQueryLease AcquireActiveIndexQuery(string workspaceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        DatabaseGenerationQueryLease lease = _database.Generations.AcquireActive(workspaceId);
        return CreateIndexQueryLease(workspaceId, lease);
    }

    private ActiveIndexQueryLease AcquireIndexQueryRevision(string workspaceId, long revision)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        DatabaseGenerationQueryLease lease = _database.Generations.Acquire(workspaceId, revision);
        return CreateIndexQueryLease(workspaceId, lease);
    }

    private ActiveIndexQueryLease CreateIndexQueryLease(
        string workspaceId,
        DatabaseGenerationQueryLease lease)
    {
        try
        {
            DatabaseGenerationResource planningResource;
            DatabaseGenerationResource documentsResource;
            DatabaseGenerationResource fullTextResource;
            try
            {
                planningResource = lease.GetRequiredResource(
                    _planningRole,
                    DatabaseGenerationResourceKind.KvKeyspace);
                documentsResource = lease.GetRequiredResource(
                    _documentsRole,
                    DatabaseGenerationResourceKind.DocumentCollection);
                fullTextResource = lease.GetRequiredResource(
                    _fullTextRole,
                    DatabaseGenerationResourceKind.DocumentFullTextIndex);
            }
            catch (InvalidOperationException exception)
            {
                throw new IndexQueryGenerationValidationException(
                    "active_generation_resource_contract_invalid",
                    exception);
            }

            string indexRevision = lease.Generation.GenerationId;
            if (!string.Equals(lease.Generation.Stream, workspaceId, StringComparison.Ordinal)
                || !string.Equals(
                    planningResource.Name,
                    PlanningKeyspaceName(workspaceId, indexRevision),
                    StringComparison.Ordinal)
                || !string.Equals(
                    documentsResource.Name,
                    CollectionName(workspaceId, indexRevision),
                    StringComparison.Ordinal)
                || !string.Equals(fullTextResource.Name, _fullTextIndexName, StringComparison.Ordinal)
                || !string.Equals(fullTextResource.ParentName, documentsResource.Name, StringComparison.Ordinal))
            {
                throw new IndexQueryGenerationValidationException(
                    "active_generation_resource_identity_invalid");
            }

            KvKeyspace planningKeyspace = _database.Keyspaces.Open(planningResource.Name);
            byte[]? planningBytes = planningKeyspace.Get(_planningSnapshotKey);
            byte[]? manifestBytes = planningKeyspace.Get(_publishedManifestKey);
            if (planningBytes is null || manifestBytes is null)
            {
                throw new IndexQueryGenerationValidationException(
                    "active_generation_metadata_missing");
            }

            IndexPlanningSnapshot planning;
            GenerationManifest manifest;
            try
            {
                planning = CoupletJsonSerializer.DeserializeIndexPlanningSnapshot(
                    Encoding.UTF8.GetString(planningBytes));
                manifest = CoupletJsonSerializer.DeserializeGenerationManifest(
                    Encoding.UTF8.GetString(manifestBytes));
            }
            catch (System.Text.Json.JsonException exception)
            {
                throw new IndexQueryGenerationValidationException(
                    "active_generation_metadata_invalid",
                    exception);
            }

            if (GenerationContractValidator.Validate(manifest).Count != 0
                || manifest.State != GenerationState.Published
                || planning.SchemaVersion != Couplet.Core.Contracts.ContractVersions.IndexPlanningSnapshot
                || !string.Equals(manifest.WorkspaceId, workspaceId, StringComparison.Ordinal)
                || !string.Equals(planning.WorkspaceId, workspaceId, StringComparison.Ordinal)
                || !string.Equals(manifest.IndexRevision, indexRevision, StringComparison.Ordinal)
                || !string.Equals(planning.IndexRevision, indexRevision, StringComparison.Ordinal)
                || !string.Equals(manifest.SourceRevision, planning.SourceRevision, StringComparison.Ordinal)
                || !manifest.ProducerVersions.SequenceEqual(planning.ProducerVersions, StringComparer.Ordinal)
                || manifest.Counts.Files != planning.Files.Count
                || manifest.Checksum.Length != 64
                || manifest.Checksum.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                || planning.Files.GroupBy(file => file.Path, StringComparer.Ordinal).Any(group => group.Count() != 1))
            {
                throw new IndexQueryGenerationValidationException(
                    "active_generation_metadata_inconsistent");
            }

            DocumentCollectionSchema? schema = _database.Documents.Catalog.TryGet(documentsResource.Name);
            if (!HasRequiredQuerySchema(schema, fullTextResource.Name))
            {
                throw new IndexQueryGenerationValidationException(
                    "active_generation_document_schema_invalid");
            }

            return new ActiveIndexQueryLease(
                lease,
                planning,
                manifest,
                documentsResource.Name,
                fullTextResource.Name);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal IndexQueryRequestLease AcquireIndexQuery(
        string workspaceId,
        string? cursor,
        string? queryFingerprint,
        bool reserveQueryLeaseSlot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (queryFingerprint is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queryFingerprint);
        }

        if (cursor is not null && reserveQueryLeaseSlot)
        {
            throw new ArgumentException(
                "A continuation cursor transfers its existing query lease slot.",
                nameof(reserveQueryLeaseSlot));
        }

        if (cursor is null)
        {
            ReleaseExpiredRetainedQueryLeases();
            bool ownsQueryLeaseSlot = reserveQueryLeaseSlot;
            if (ownsQueryLeaseSlot && !TryReserveIndexQueryLeaseSlot())
            {
                throw new IndexQueryCursorLeaseException(
                    IndexQueryCursorLeaseFailure.CapacityExceeded);
            }

            try
            {
                return new IndexQueryRequestLease(
                    this,
                    AcquireActiveIndexQuery(workspaceId),
                    QueryCursorLeaseExpirationUtc(),
                    cursorRecognized: false,
                    ownsQueryLeaseSlot,
                    Guid.NewGuid(),
                    claimedRecordVersion: 0,
                    innerCursor: null);
            }
            catch
            {
                if (ownsQueryLeaseSlot)
                {
                    ReleaseIndexQueryLeaseSlot();
                }

                throw;
            }
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryFingerprint);
        if (!TryDecodeQueryCursor(cursor, out DurableQueryCursorEnvelope? envelope)
            || envelope is null)
        {
            throw new IndexQueryCursorLeaseException(IndexQueryCursorLeaseFailure.Mismatch);
        }

        byte[] cursorHash = SHA256.HashData(Encoding.UTF8.GetBytes(cursor));
        List<RetainedIndexQueryCursor> expired = [];
        RetainedIndexQueryCursor? retained = null;
        RetainedIndexQueryCursor? failedTransition = null;
        Exception? transitionFailure = null;
        long claimedRecordVersion = 0;
        bool requestedCursorExpired = false;
        bool requestedCursorMismatch = false;
        try
        {
            lock (_retainedQueryLeaseSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ThrowIfQueryCursorRegistryUnavailableUnsafe();
                DateTimeOffset nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
                expired = RemoveExpiredRetainedQueryLeasesUnsafe(
                    nowUtc,
                    envelope.ChainId,
                    out requestedCursorExpired);
                requestedCursorExpired |= envelope.ExpiresAtUtc <= nowUtc;

                RetainedIndexQueryCursor? transitionCandidate = null;
                DurableQueryCursorRecord? transitionRecord = null;
                try
                {
                    if (!requestedCursorExpired
                        && _retainedQueryLeases.TryGetValue(
                            envelope.ChainId,
                            out RetainedIndexQueryCursor? candidate))
                    {
                        if (!string.Equals(candidate.WorkspaceId, workspaceId, StringComparison.Ordinal)
                            || !string.Equals(
                                candidate.QueryFingerprint,
                                queryFingerprint,
                                StringComparison.Ordinal)
                            || candidate.GenerationRevision != envelope.GenerationRevision
                            || candidate.ExpiresAtUtc != envelope.ExpiresAtUtc
                            || !CryptographicOperations.FixedTimeEquals(candidate.CursorHash, cursorHash))
                        {
                            requestedCursorMismatch = true;
                        }
                        else
                        {
                            transitionCandidate = candidate;
                            transitionRecord = candidate.Record with
                            {
                                State = DurableQueryCursorState.Claimed,
                            };
                            QueryCursorTransitionFaultTestHook?.Invoke(
                                IndexQueryCursorTransitionFaultPoint.BeforeClaimCas);
                            KvCasResult claim = _control.CompareAndSet(
                                QueryCursorRecordKey(envelope.ChainId),
                                candidate.RegistryVersion,
                                EncodeQueryCursorRecord(transitionRecord));
                            if (!claim.Succeeded)
                            {
                                requestedCursorMismatch = true;
                                transitionCandidate = null;
                                transitionRecord = null;
                            }
                            else
                            {
                                _retainedQueryLeases.Remove(envelope.ChainId);
                                retained = candidate;
                                claimedRecordVersion = claim.NewVersion!.Value;
                                QueryCursorTransitionFaultTestHook?.Invoke(
                                    IndexQueryCursorTransitionFaultPoint.AfterClaimCas);
                                _control.CreateSnapshot();
                            }
                        }
                    }

                    ScheduleRetainedQueryLeaseTimerUnsafe(nowUtc);
                }
                catch (Exception exception)
                {
                    transitionFailure = exception;
                    _queryCursorRegistryFaulted = true;
                    if (transitionCandidate is not null)
                    {
                        _retainedQueryLeases.Remove(envelope.ChainId);
                        retained = null;
                        failedTransition = transitionCandidate;
                        if (transitionRecord is not null)
                        {
                            _ = TryFailClosedObservedQueryCursorRecord(
                                envelope.ChainId,
                                transitionRecord,
                                allowCursorHashMismatch: false);
                        }
                    }
                }
            }
        }
        finally
        {
            DisposeRetainedQueryLeasesAndReleaseSlots(expired, deleteDurableRecords: true);
            if (failedTransition is not null)
            {
                try
                {
                    failedTransition.Lease.Dispose();
                }
                finally
                {
                    ReleaseIndexQueryLeaseSlot();
                }
            }
        }

        if (transitionFailure is not null)
        {
            throw new IndexQueryCursorLeaseException(
                IndexQueryCursorLeaseFailure.RegistryUnavailable,
                transitionFailure);
        }

        if (IsQueryCursorRegistryFaulted())
        {
            if (retained is not null)
            {
                try
                {
                    ReleaseClaimedIndexQueryCursor(retained.ChainId, claimedRecordVersion);
                }
                finally
                {
                    try
                    {
                        retained.Lease.Dispose();
                    }
                    finally
                    {
                        ReleaseIndexQueryLeaseSlot();
                    }
                }
            }

            throw new IndexQueryCursorLeaseException(
                IndexQueryCursorLeaseFailure.RegistryUnavailable);
        }

        if (requestedCursorExpired)
        {
            throw new IndexQueryCursorLeaseException(IndexQueryCursorLeaseFailure.Expired);
        }

        if (requestedCursorMismatch)
        {
            throw new IndexQueryCursorLeaseException(IndexQueryCursorLeaseFailure.Mismatch);
        }

        if (retained is not null)
        {
            return new IndexQueryRequestLease(
                this,
                retained.Lease,
                retained.ExpiresAtUtc,
                cursorRecognized: true,
                ownsQueryLeaseSlot: true,
                retained.ChainId,
                claimedRecordVersion,
                envelope.InnerCursor);
        }

        try
        {
            using DatabaseGenerationQueryLease _ = _database.Generations.Acquire(
                workspaceId,
                envelope.GenerationRevision);
        }
        catch (DatabaseGenerationException exception)
            when (exception.Code == DatabaseGenerationErrorCodes.RevisionUnavailable)
        {
            throw new IndexQueryCursorLeaseException(IndexQueryCursorLeaseFailure.Stale);
        }

        throw new IndexQueryCursorLeaseException(IndexQueryCursorLeaseFailure.Mismatch);
    }

    internal IndexQueryCursorRetentionResult RetainIndexQueryCursor(
        string cursor,
        string queryFingerprint,
        ActiveIndexQueryLease lease,
        DateTimeOffset expiresAtUtc,
        Guid chainId,
        long claimedRecordVersion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryFingerprint);
        ArgumentNullException.ThrowIfNull(lease);
        if (!TryDecodeQueryCursor(cursor, out DurableQueryCursorEnvelope? envelope)
            || envelope is null
            || envelope.ChainId != chainId
            || envelope.GenerationRevision != lease.DatabaseGenerationRevision
            || envelope.ExpiresAtUtc != expiresAtUtc)
        {
            throw new InvalidDataException("query_cursor_envelope_inconsistent");
        }

        List<RetainedIndexQueryCursor> expired = [];
        Exception? transitionFailure = null;
        DurableQueryCursorRecord? retainedRecordForAbort = null;
        IndexQueryCursorRetentionResult result = IndexQueryCursorRetentionResult.Conflict;
        try
        {
            lock (_retainedQueryLeaseSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ThrowIfQueryCursorRegistryUnavailableUnsafe();
                DateTimeOffset nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
                expired = RemoveExpiredRetainedQueryLeasesUnsafe(
                    nowUtc,
                    requestedChainId: null,
                    out _);

                DurableQueryCursorRecord? transitionRecord = null;
                bool retainedInMemory = false;
                try
                {
                    if (expiresAtUtc <= nowUtc)
                    {
                        result = IndexQueryCursorRetentionResult.Expired;
                    }
                    else if (_retainedQueryLeases.ContainsKey(chainId))
                    {
                        result = IndexQueryCursorRetentionResult.Conflict;
                    }
                    else
                    {
                        byte[] cursorHash = SHA256.HashData(Encoding.UTF8.GetBytes(cursor));
                        transitionRecord = new DurableQueryCursorRecord(
                            DurableQueryCursorState.Available,
                            lease.Manifest.WorkspaceId,
                            queryFingerprint,
                            lease.DatabaseGenerationRevision,
                            expiresAtUtc,
                            cursorHash);
                        QueryCursorTransitionFaultTestHook?.Invoke(
                            IndexQueryCursorTransitionFaultPoint.BeforeRetainCas);
                        KvCasResult retained = _control.CompareAndSet(
                            QueryCursorRecordKey(chainId),
                            claimedRecordVersion,
                            EncodeQueryCursorRecord(transitionRecord));
                        if (!retained.Succeeded)
                        {
                            result = IndexQueryCursorRetentionResult.Conflict;
                            transitionRecord = null;
                        }
                        else
                        {
                            QueryCursorTransitionFaultTestHook?.Invoke(
                                IndexQueryCursorTransitionFaultPoint.AfterRetainCas);
                            _control.CreateSnapshot();
                            _retainedQueryLeases.Add(
                                chainId,
                                new RetainedIndexQueryCursor(
                                    chainId,
                                    lease.Manifest.WorkspaceId,
                                    queryFingerprint,
                                    lease.DatabaseGenerationRevision,
                                    expiresAtUtc,
                                    cursorHash,
                                    retained.NewVersion!.Value,
                                    transitionRecord,
                                    lease));
                            retainedInMemory = true;
                            retainedRecordForAbort = transitionRecord;
                            result = IndexQueryCursorRetentionResult.Retained;
                        }
                    }

                    ScheduleRetainedQueryLeaseTimerUnsafe(nowUtc);
                }
                catch (Exception exception)
                {
                    transitionFailure = exception;
                    _queryCursorRegistryFaulted = true;
                    if (retainedInMemory)
                    {
                        _retainedQueryLeases.Remove(chainId);
                    }

                    if (transitionRecord is not null)
                    {
                        _ = TryFailClosedObservedQueryCursorRecord(
                            chainId,
                            transitionRecord,
                            allowCursorHashMismatch: claimedRecordVersion > 0);
                    }
                }
            }
        }
        finally
        {
            DisposeRetainedQueryLeasesAndReleaseSlots(expired, deleteDurableRecords: true);
        }

        if (transitionFailure is not null)
        {
            throw new IndexQueryCursorLeaseException(
                IndexQueryCursorLeaseFailure.RegistryUnavailable,
                transitionFailure);
        }

        if (IsQueryCursorRegistryFaulted())
        {
            if (retainedRecordForAbort is not null)
            {
                lock (_retainedQueryLeaseSync)
                {
                    _retainedQueryLeases.Remove(chainId);
                    _ = TryFailClosedObservedQueryCursorRecord(
                        chainId,
                        retainedRecordForAbort,
                        allowCursorHashMismatch: false);
                }
            }

            throw new IndexQueryCursorLeaseException(
                IndexQueryCursorLeaseFailure.RegistryUnavailable);
        }

        return result;
    }

    internal int RetainedIndexQueryLeaseCountForTest
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_retainedQueryLeaseSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _queryLeaseSlotCount;
            }
        }
    }

    internal static int MaximumRetainedIndexQueryLeasesForTest => _maximumRetainedQueryLeases;

    internal ActiveIndexSearchResult QueryActiveCodeSearch(
        ActiveIndexQueryLease lease,
        string mode,
        string query,
        long offset,
        int pageSize,
        string? path,
        string? language,
        CodeEntityKind? kind,
        long maxPostingVisits,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPostingVisits);
        cancellationToken.ThrowIfCancellationRequested();

        int firstResult = checked((int)offset);
        int topK = checked(firstResult + pageSize);

        DocumentCollectionSchema schema = _database.Documents.Catalog.TryGet(lease.DocumentCollectionName)
            ?? throw new InvalidDataException("active_generation_document_collection_missing");
        DocumentCollectionStore collection = _database.Documents.Open(lease.DocumentCollectionName);
        if (string.Equals(mode, "exact", StringComparison.Ordinal))
        {
            if (firstResult != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(offset),
                    offset,
                    "Exact lookup does not support a non-zero continuation offset.");
            }

            DocumentPathIndex index = schema.TryGetIndex("by_stable_id")
                ?? throw new InvalidDataException("active_generation_exact_index_missing");
            IReadOnlyList<DocumentRow> rows = collection.GetByIndex(index, query, Math.Min(topK, 2));
            cancellationToken.ThrowIfCancellationRequested();
            ActiveIndexSearchHit[] hits = rows
                .Select(row => new ActiveIndexSearchHit(
                    CoupletJsonSerializer.DeserializeIndexStorageDocument(row.Json),
                    1))
                .ToArray();
            return new ActiveIndexSearchResult(
                "document_path_index:by_stable_id",
                rows.Count,
                rows.Count,
                hits);
        }

        if (!string.Equals(mode, "fulltext", StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported C1 query mode.");
        }

        DocumentFullTextIndex fullTextIndex = schema.TryGetFullTextIndex(lease.FullTextIndexName)
            ?? throw new InvalidDataException("active_generation_fulltext_index_missing");
        bool hasFilters = path is not null || language is not null || kind is not null;
        IReadOnlyList<DocumentFullTextSearchHit> fullTextHits;
        string accessPath;
        long candidates;
        long examined;
        if (hasFilters)
        {
            HashSet<string> allowedDocumentIds = BuildFullTextAllowedDocumentIds(
                lease,
                schema,
                collection,
                path,
                language,
                kind,
                maxPostingVisits,
                cancellationToken,
                out string filterAccessPath,
                out long filterVisits);
            if (allowedDocumentIds.Count == 0)
            {
                fullTextHits = [];
                candidates = 0;
                examined = filterVisits;
            }
            else
            {
                long remainingPostingVisits = maxPostingVisits - filterVisits;
                if (remainingPostingVisits <= 0)
                {
                    throw new ActiveIndexFullTextPostingBudgetExceededException(
                        checked(filterVisits + 1),
                        maxPostingVisits);
                }

                DocumentFullTextFilteredSearchResult filtered = collection.SearchFullTextFiltered(
                    fullTextIndex,
                    "$.search_text",
                    query,
                    topK,
                    allowedDocumentIds,
                    remainingPostingVisits,
                    cancellationToken);
                if (filtered.PostingBudgetExceeded)
                {
                    throw new ActiveIndexFullTextPostingBudgetExceededException(
                        checked(filterVisits + filtered.PostingVisits),
                        maxPostingVisits);
                }

                fullTextHits = filtered.Hits;
                candidates = filtered.FilterCandidateCount;
                examined = checked(filterVisits + filtered.PostingVisits);
            }
            accessPath = "document_fulltext_filtered:code_search:" + filterAccessPath;
        }
        else
        {
            fullTextHits = collection.SearchFullText(
                fullTextIndex,
                "$.search_text",
                query,
                topK);
            accessPath = "document_fulltext:code_search";
            candidates = fullTextHits.Count;
            examined = Math.Max(0, fullTextHits.Count - firstResult);
        }

        int pageCount = Math.Max(0, fullTextHits.Count - firstResult);
        var hydrated = new List<ActiveIndexSearchHit>(pageCount);
        for (int index = firstResult; index < fullTextHits.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentFullTextSearchHit hit = fullTextHits[index];
            DocumentRow? row = collection.Get(hit.DocumentId);
            if (row is null)
            {
                throw new InvalidDataException("active_generation_fulltext_document_missing");
            }

            hydrated.Add(new ActiveIndexSearchHit(
                CoupletJsonSerializer.DeserializeIndexStorageDocument(row.Json),
                hit.Score));
        }

        return new ActiveIndexSearchResult(
            accessPath,
            candidates,
            examined,
            hydrated);
    }

    private static HashSet<string> BuildFullTextAllowedDocumentIds(
        ActiveIndexQueryLease lease,
        DocumentCollectionSchema schema,
        DocumentCollectionStore collection,
        string? path,
        string? language,
        CodeEntityKind? kind,
        long maxCandidates,
        CancellationToken cancellationToken,
        out string accessPath,
        out long filterVisits)
    {
        var budget = new ActiveIndexFilterVisitBudget(maxCandidates);
        var candidateSets = new List<(string AccessPath, HashSet<string> Ids)>();
        if (path is not null)
        {
            DocumentPathIndex pathIndex = schema.TryGetIndex("by_path")
                ?? throw new InvalidDataException("active_generation_path_filter_index_missing");
            string pattern = path.Replace('\\', '/');
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (IndexPlanningFile file in lease.PlanningSnapshot.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.VisitPlanningFile();
                if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                    pattern,
                    file.Path,
                    ignoreCase: false))
                {
                    int count = collection.CountByIndex(pathIndex, [file.Path]);
                    budget.VisitFilterCandidates(count);
                    foreach (DocumentRow row in collection.GetByIndex(pathIndex, file.Path, count))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ids.Add(row.Id);
                    }
                }
            }

            candidateSets.Add((
                "planning_snapshot_path_glob+document_path_index:" + pathIndex.Name,
                ids));
        }

        if (language is not null)
        {
            DocumentPathIndex languageIndex = schema.TryGetIndex("by_language")
                ?? throw new InvalidDataException("active_generation_language_filter_index_missing");
            HashSet<string> ids = ReadBoundedFilterCandidates(
                collection,
                languageIndex,
                language.ToLowerInvariant(),
                budget,
                cancellationToken);
            candidateSets.Add(("document_path_index:" + languageIndex.Name, ids));
        }

        if (kind is not null)
        {
            DocumentPathIndex kindIndex = schema.TryGetIndex("by_entity_kind")
                ?? throw new InvalidDataException("active_generation_kind_filter_index_missing");
            HashSet<string> ids = ReadBoundedFilterCandidates(
                collection,
                kindIndex,
                kind.Value.ToString(),
                budget,
                cancellationToken);
            candidateSets.Add(("document_path_index:" + kindIndex.Name, ids));
        }

        if (candidateSets.Count == 0)
        {
            throw new InvalidOperationException("A filtered full-text plan requires at least one filter index.");
        }

        (string AccessPath, HashSet<string> Ids) first = candidateSets
            .OrderBy(candidate => candidate.Ids.Count)
            .ThenBy(candidate => candidate.AccessPath, StringComparer.Ordinal)
            .First();
        var allowed = new HashSet<string>(first.Ids, StringComparer.Ordinal);
        foreach ((string _, HashSet<string> ids) in candidateSets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(ids, first.Ids))
            {
                allowed.IntersectWith(ids);
            }
        }

        accessPath = string.Join(
            "+",
            candidateSets.Select(candidate => candidate.AccessPath));
        filterVisits = budget.Visits;
        return allowed;
    }

    private static HashSet<string> ReadBoundedFilterCandidates(
        DocumentCollectionStore collection,
        DocumentPathIndex index,
        object value,
        ActiveIndexFilterVisitBudget budget,
        CancellationToken cancellationToken)
    {
        int count = collection.CountByIndex(index, [value]);
        budget.VisitFilterCandidates(count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (DocumentRow row in collection.GetByIndex(index, value, count))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ids.Add(row.Id);
        }

        return ids;
    }

    internal ActiveIndexSymbolQueryResult QueryActiveSymbol(
        ActiveIndexQueryLease lease,
        string? symbolId,
        string? qualifiedIdentity,
        string? language,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lease);
        bool hasSymbolId = !string.IsNullOrWhiteSpace(symbolId);
        bool hasQualifiedIdentity = !string.IsNullOrWhiteSpace(qualifiedIdentity);
        if (hasSymbolId == hasQualifiedIdentity)
        {
            throw new ArgumentException("Exactly one symbol identity must be provided.", nameof(symbolId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        DocumentCollectionSchema schema = _database.Documents.Catalog.TryGet(lease.DocumentCollectionName)
            ?? throw new InvalidDataException("active_generation_document_collection_missing");
        DocumentCollectionStore collection = _database.Documents.Open(lease.DocumentCollectionName);

        string indexName;
        string lookup;
        string accessPath;
        if (hasSymbolId)
        {
            indexName = "by_stable_id";
            lookup = symbolId!;
            accessPath = "document_path_index:by_stable_id";
        }
        else if (!string.IsNullOrWhiteSpace(language))
        {
            indexName = "by_stable_id";
            lookup = StableId.CreateSymbol(lease.Manifest.WorkspaceId, language, qualifiedIdentity!);
            accessPath = "document_path_index:by_stable_id:qualified_identity_language";
        }
        else
        {
            indexName = "by_qualified_identity";
            lookup = qualifiedIdentity!;
            accessPath = "document_path_index:by_qualified_identity";
        }

        DocumentPathIndex index = schema.TryGetIndex(indexName)
            ?? throw new InvalidDataException("active_generation_symbol_index_missing");
        IReadOnlyList<DocumentRow> rows = collection.GetByIndex(index, lookup, limit: 2);
        cancellationToken.ThrowIfCancellationRequested();
        IndexStorageDocument[] documents = rows
            .Select(row => CoupletJsonSerializer.DeserializeIndexStorageDocument(row.Json))
            .ToArray();
        return new ActiveIndexSymbolQueryResult(
            accessPath,
            rows.Count,
            rows.Count,
            documents);
    }

    internal DocumentCollectionStore GetActiveDocumentCollectionForTest(string workspaceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        using DatabaseGenerationQueryLease lease = _database.Generations.AcquireActive(workspaceId);
        DatabaseGenerationResource resource = lease.GetRequiredResource(
            _documentsRole,
            DatabaseGenerationResourceKind.DocumentCollection);
        return _database.Documents.Open(resource.Name);
    }

    internal bool DropActiveDocumentIndexForTest(string workspaceId, string indexName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        using DatabaseGenerationQueryLease lease = _database.Generations.AcquireActive(workspaceId);
        DatabaseGenerationResource resource = lease.GetRequiredResource(
            _documentsRole,
            DatabaseGenerationResourceKind.DocumentCollection);
        return _database.Documents.DropIndex(resource.Name, indexName);
    }

    internal DatabaseGenerationCleanupResult CleanupRetired(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        cancellationToken.ThrowIfCancellationRequested();
        ReleaseExpiredRetainedQueryLeases();
        if (CleanupRetiredTestHook is not null)
        {
            return CleanupRetiredTestHook(workspaceId, cancellationToken);
        }

        Dictionary<long, string> generationIds = _database.Generations.List(workspaceId)
            .ToDictionary(generation => generation.Revision, generation => generation.GenerationId);
        DatabaseGenerationCleanupResult cleanup = _retiredGenerationRetention == TimeSpan.Zero
            ? _database.Generations.CleanupRetired(workspaceId, cancellationToken)
            : _database.Generations.CleanupRetired(
                workspaceId,
                new DatabaseGenerationCleanupOptions(RetentionCutoffUtc()),
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

    internal Action<IndexGenerationPublishFaultPoint>? PublishFaultTestHook { get; set; }

    internal Action<IndexQueryCursorTransitionFaultPoint>? QueryCursorTransitionFaultTestHook { get; set; }

    internal Action<IndexQueryCursorRecoveryFaultPoint> QueryCursorRecoveryFaultTestHook { get; set; }

    internal void AddUnavailableRetainedQueryCursorRecordForTest(
        string workspaceId,
        long generationRevision,
        DateTimeOffset expiresAtUtc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationRevision);
        var record = new DurableQueryCursorRecord(
            DurableQueryCursorState.Available,
            workspaceId,
            "test-query-fingerprint",
            generationRevision,
            expiresAtUtc.ToUniversalTime(),
            new byte[_queryCursorHashLength]);
        KvCasResult result = _control.CompareAndSet(
            QueryCursorRecordKey(Guid.NewGuid()),
            expectedVersion: 0,
            EncodeQueryCursorRecord(record));
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Unable to create the unavailable cursor test record.");
        }

        _control.CreateSnapshot();
    }

    internal void AddSemanticallyInvalidRetainedQueryCursorRecordForTest(
        long generationRevision,
        DateTimeOffset expiresAtUtc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationRevision);
        var record = new DurableQueryCursorRecord(
            DurableQueryCursorState.Available,
            " ",
            "test-query-fingerprint",
            generationRevision,
            expiresAtUtc.ToUniversalTime(),
            new byte[_queryCursorHashLength]);
        KvCasResult result = _control.CompareAndSet(
            QueryCursorRecordKey(Guid.NewGuid()),
            expectedVersion: 0,
            EncodeQueryCursorRecord(record));
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Unable to create the invalid cursor test record.");
        }

        _control.CreateSnapshot();
    }

    internal void RemoveGenerationManifestForTest(string workspaceId, long generationRevision)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationRevision);
        using DatabaseGenerationQueryLease lease = _database.Generations.Acquire(
            workspaceId,
            generationRevision);
        DatabaseGenerationResource planningResource = lease.GetRequiredResource(
            _planningRole,
            DatabaseGenerationResourceKind.KvKeyspace);
        KvKeyspace planningKeyspace = _database.Keyspaces.Open(planningResource.Name);
        if (!planningKeyspace.Delete(_publishedManifestKey))
        {
            throw new InvalidOperationException("Unable to remove the generation manifest for the test.");
        }

        planningKeyspace.CreateSnapshot();
    }

    internal IReadOnlyList<long> ListGenerationRevisionsForTest(string workspaceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        return _database.Generations.List(workspaceId)
            .Select(generation => generation.Revision)
            .ToArray();
    }
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
            if (!HasRequiredPathIndexes(schema))
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

#if COUPLET_SONNETDB_SOURCE_GENERATIONS
        List<RetainedIndexQueryCursor> retained;
        lock (_retainedQueryLeaseSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            retained = _retainedQueryLeases.Values.ToList();
            _retainedQueryLeases.Clear();
        }
        try
        {
            _retainedQueryLeaseTimer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            DisposeRetainedQueryLeasesAndReleaseSlots(retained, deleteDurableRecords: false);
        }
        finally
        {
            try
            {
                _database.Dispose();
            }
            finally
            {
                Interlocked.Exchange(ref _databaseRootLease, null)?.Dispose();
            }
        }
#else
        _disposed = true;
        _database.Dispose();
#endif
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
        IReadOnlyList<long>? retentionDeferredGenerationRevisions = null,
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
            RetentionDeferredGenerationRevisions = retentionDeferredGenerationRevisions ?? [],
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

    private static bool HasRequiredQuerySchema(
        DocumentCollectionSchema? schema,
        string fullTextIndexName) =>
        HasRequiredPathIndexes(schema)
        && schema!.TryGetFullTextIndex(fullTextIndexName) is not null;

    private static bool HasRequiredPathIndexes(DocumentCollectionSchema? schema) =>
        schema?.TryGetIndex("by_stable_id") is not null
        && schema.TryGetIndex("by_record_type") is not null
        && schema.TryGetIndex("by_path") is not null
        && schema.TryGetIndex("by_language") is not null
        && schema.TryGetIndex("by_entity_kind") is not null
        && schema.TryGetIndex("by_qualified_identity") is not null;

    private static string CollectionName(string workspaceId, string indexRevision)
    {
        string value = workspaceId + "\0" + indexRevision;
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return "cplg_" + digest[..32];
    }

#if COUPLET_SONNETDB_SOURCE_GENERATIONS
    private static FileStream AcquireDatabaseRootLease(string databaseRoot)
    {
        FileStream? lease = null;
        try
        {
            lease = new FileStream(
                Path.Combine(databaseRoot, _databaseRootLockFileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            if (!OperatingSystem.IsMacOS())
            {
                lease.Lock(0, 1);
            }

            return lease;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                lease?.Dispose();
            }
            catch (Exception disposeException) when (disposeException is IOException or UnauthorizedAccessException)
            {
            }

            throw new IOException(_databaseRootOwnershipError);
        }
    }

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
        DatabaseGenerationResource fullText = lease.GetRequiredResource(
            _fullTextRole,
            DatabaseGenerationResourceKind.DocumentFullTextIndex);
        DocumentCollectionSchema? schema = _database.Documents.Catalog.TryGet(documents.Name);
        if (!HasRequiredQuerySchema(schema, fullText.Name))
        {
            return null;
        }

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
            retentionDeferredGenerationRevisions: cleanup.RetentionDeferredGenerationRevisions,
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
                cleanup.RetentionDeferredRevisions,
                [],
                []);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or DatabaseGenerationException)
        {
            return new CleanupOutcome(
                [],
                [],
                [],
                ["retired_generation_cleanup_failed"],
                _cleanupFailureLimitations);
        }
    }

    private DateTimeOffset RetentionCutoffUtc()
    {
        DateTimeOffset nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        TimeSpan elapsedSinceMinimum = nowUtc - DateTimeOffset.MinValue;
        return _retiredGenerationRetention > elapsedSinceMinimum
            ? DateTimeOffset.MinValue
            : nowUtc - _retiredGenerationRetention;
    }

    private DateTimeOffset QueryCursorLeaseExpirationUtc()
    {
        DateTimeOffset nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        TimeSpan remaining = DateTimeOffset.MaxValue - nowUtc;
        return _queryCursorLeaseRetention > remaining
            ? DateTimeOffset.MaxValue
            : nowUtc + _queryCursorLeaseRetention;
    }

    internal string CreateIndexQueryCursor(
        ActiveIndexQueryLease lease,
        string queryFingerprint,
        ReadOnlySpan<byte> continuationState,
        Guid chainId,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryFingerprint);
        string innerCursor = lease.CreateCursor(queryFingerprint, continuationState);
        return EncodeQueryCursor(new DurableQueryCursorEnvelope(
            chainId,
            lease.DatabaseGenerationRevision,
            expiresAtUtc,
            innerCursor));
    }

    internal void ReleaseClaimedIndexQueryCursor(Guid chainId, long claimedRecordVersion)
    {
        if (claimedRecordVersion <= 0)
        {
            return;
        }

        Exception? transitionFailure = null;
        lock (_retainedQueryLeaseSync)
        {
            if (_disposed)
            {
                return;
            }

            DurableQueryCursorRecord? expectedRecord = null;
            try
            {
                string key = QueryCursorRecordKey(chainId);
                KvEntry? entry = _control.GetEntry(key);
                if (entry is null)
                {
                    return;
                }

                if (!TryDecodeQueryCursorRecord(
                        entry.Value.Span,
                        out DurableQueryCursorRecord? record)
                    || record is null)
                {
                    throw new InvalidDataException("query_cursor_release_record_invalid");
                }

                expectedRecord = record;
                if (entry.Version != claimedRecordVersion
                    || record.State != DurableQueryCursorState.Claimed)
                {
                    throw new InvalidDataException("query_cursor_release_state_conflict");
                }

                QueryCursorTransitionFaultTestHook?.Invoke(
                    IndexQueryCursorTransitionFaultPoint.BeforeReleaseCas);
                KvCasResult terminal = _control.CompareAndSet(
                    key,
                    claimedRecordVersion,
                    EncodeQueryCursorRecord(record));
                if (!terminal.Succeeded)
                {
                    throw new InvalidDataException("query_cursor_release_cas_conflict");
                }

                QueryCursorTransitionFaultTestHook?.Invoke(
                    IndexQueryCursorTransitionFaultPoint.AfterReleaseCas);
                QueryCursorTransitionFaultTestHook?.Invoke(
                    IndexQueryCursorTransitionFaultPoint.BeforeReleaseDelete);
                if (!_control.Delete(key))
                {
                    throw new InvalidDataException("query_cursor_release_delete_conflict");
                }

                QueryCursorTransitionFaultTestHook?.Invoke(
                    IndexQueryCursorTransitionFaultPoint.AfterReleaseDelete);
                QueryCursorTransitionFaultTestHook?.Invoke(
                    IndexQueryCursorTransitionFaultPoint.BeforeReleaseSnapshot);
                _control.CreateSnapshot();
                QueryCursorTransitionFaultTestHook?.Invoke(
                    IndexQueryCursorTransitionFaultPoint.AfterReleaseSnapshot);
            }
            catch (Exception exception)
            {
                _queryCursorRegistryFaulted = true;
                transitionFailure = exception;
                if (expectedRecord is not null)
                {
                    _ = TryFailClosedObservedQueryCursorRecord(
                        chainId,
                        expectedRecord,
                        allowCursorHashMismatch: false);
                }
            }
        }

        if (transitionFailure is not null)
        {
            throw new IndexQueryCursorLeaseException(
                IndexQueryCursorLeaseFailure.RegistryUnavailable,
                transitionFailure);
        }
    }

    private bool TryFailClosedQueryCursorRecord(
        Guid chainId,
        long expectedVersion,
        DurableQueryCursorRecord record)
    {
        try
        {
            string key = QueryCursorRecordKey(chainId);
            KvEntry? entry = _control.GetEntry(key);
            if (entry is null)
            {
                return true;
            }

            if (entry.Version != expectedVersion)
            {
                return false;
            }

            DurableQueryCursorRecord claimed = record with
            {
                State = DurableQueryCursorState.Claimed,
            };
            KvCasResult terminal = _control.CompareAndSet(
                key,
                expectedVersion,
                EncodeQueryCursorRecord(claimed));
            if (!terminal.Succeeded || !_control.Delete(key))
            {
                return false;
            }

            _control.CreateSnapshot();
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryFailClosedObservedQueryCursorRecord(
        Guid chainId,
        DurableQueryCursorRecord expectedRecord,
        bool allowCursorHashMismatch)
    {
        try
        {
            KvEntry? entry = _control.GetEntry(QueryCursorRecordKey(chainId));
            if (entry is null)
            {
                return true;
            }

            if (!TryDecodeQueryCursorRecord(
                    entry.Value.Span,
                    out DurableQueryCursorRecord? observedRecord)
                || observedRecord is null
                || !string.Equals(
                    observedRecord.WorkspaceId,
                    expectedRecord.WorkspaceId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    observedRecord.QueryFingerprint,
                    expectedRecord.QueryFingerprint,
                    StringComparison.Ordinal)
                || observedRecord.GenerationRevision != expectedRecord.GenerationRevision
                || observedRecord.ExpiresAtUtc != expectedRecord.ExpiresAtUtc
                || (!allowCursorHashMismatch
                    && !CryptographicOperations.FixedTimeEquals(
                        observedRecord.CursorHash,
                        expectedRecord.CursorHash)))
            {
                return false;
            }

            return TryFailClosedQueryCursorRecord(
                chainId,
                entry.Version,
                observedRecord);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            return false;
        }
    }

    private byte[] LoadOrCreateQueryCursorSigningKey()
    {
        KvEntry? existing = _control.GetEntry(_queryCursorSigningKeyKey);
        if (existing is not null)
        {
            if (existing.Value.Length != _queryCursorSigningKeyLength)
            {
                throw new InvalidDataException("query_cursor_signing_key_invalid");
            }

            return existing.Value.ToArray();
        }

        byte[] created = RandomNumberGenerator.GetBytes(_queryCursorSigningKeyLength);
        KvCasResult result = _control.CompareAndSet(
            _queryCursorSigningKeyKey,
            expectedVersion: 0,
            created);
        if (result.Succeeded)
        {
            _control.CreateSnapshot();
            return created;
        }

        existing = _control.GetEntry(_queryCursorSigningKeyKey);
        if (existing is null || existing.Value.Length != _queryCursorSigningKeyLength)
        {
            throw new InvalidDataException("query_cursor_signing_key_invalid");
        }

        return existing.Value.ToArray();
    }

    private void RestoreRetainedQueryLeases()
    {
        IReadOnlyList<KvEntry> entries;
        while (true)
        {
            entries = _control.ScanPrefix(
                _queryCursorRecordPrefix,
                _maximumRetainedQueryLeases + 1);
            bool removedGarbage = false;
            foreach (KvEntry entry in entries)
            {
                string key = Encoding.UTF8.GetString(entry.Key.Span);
                bool keyValid = TryParseQueryCursorRecordKey(key, out _);
                bool recordValid = TryDecodeQueryCursorRecord(
                    entry.Value.Span,
                    out DurableQueryCursorRecord? record);
                bool remove = !keyValid
                    || !recordValid
                    || record is null
                    || record.State != DurableQueryCursorState.Available
                    || record.ExpiresAtUtc <= _timeProvider.GetUtcNow().ToUniversalTime();
                if (!remove)
                {
                    try
                    {
                        using ActiveIndexQueryLease _ = AcquireIndexQueryRevision(
                            record!.WorkspaceId,
                            record.GenerationRevision);
                        remove = record.ExpiresAtUtc
                            <= _timeProvider.GetUtcNow().ToUniversalTime();
                    }
                    catch (DatabaseGenerationException exception)
                        when (exception.Code == DatabaseGenerationErrorCodes.RevisionUnavailable)
                    {
                        remove = true;
                    }
                    catch (IndexQueryGenerationValidationException)
                    {
                        remove = true;
                    }
                }

                if (remove)
                {
                    FenceAndRemoveObservedQueryCursorRecord(entry);
                    removedGarbage = true;
                }
            }

            if (removedGarbage)
            {
                _control.CreateSnapshot();
                continue;
            }

            if (entries.Count > _maximumRetainedQueryLeases)
            {
                throw new InvalidDataException("query_cursor_registry_capacity_exceeded");
            }

            break;
        }

        bool changed = false;
        foreach (KvEntry entry in entries)
        {
            string key = Encoding.UTF8.GetString(entry.Key.Span);
            if (!TryParseQueryCursorRecordKey(key, out Guid chainId)
                || !TryDecodeQueryCursorRecord(entry.Value.Span, out DurableQueryCursorRecord? record)
                || record is null
                || record.State != DurableQueryCursorState.Available
                || record.ExpiresAtUtc <= _timeProvider.GetUtcNow().ToUniversalTime())
            {
                FenceAndRemoveObservedQueryCursorRecord(entry);
                changed = true;
                continue;
            }

            ActiveIndexQueryLease lease;
            try
            {
                lease = AcquireIndexQueryRevision(record.WorkspaceId, record.GenerationRevision);
            }
            catch (DatabaseGenerationException exception)
                when (exception.Code == DatabaseGenerationErrorCodes.RevisionUnavailable)
            {
                FenceAndRemoveObservedQueryCursorRecord(entry);
                changed = true;
                continue;
            }
            catch (IndexQueryGenerationValidationException)
            {
                FenceAndRemoveObservedQueryCursorRecord(entry);
                changed = true;
                continue;
            }

            if (record.ExpiresAtUtc <= _timeProvider.GetUtcNow().ToUniversalTime())
            {
                lease.Dispose();
                FenceAndRemoveObservedQueryCursorRecord(entry);
                changed = true;
                continue;
            }

            if (!_retainedQueryLeases.TryAdd(
                chainId,
                new RetainedIndexQueryCursor(
                    chainId,
                    record.WorkspaceId,
                    record.QueryFingerprint,
                    record.GenerationRevision,
                    record.ExpiresAtUtc,
                    record.CursorHash,
                    entry.Version,
                    record,
                    lease)))
            {
                lease.Dispose();
                throw new InvalidDataException("query_cursor_registry_duplicate_chain");
            }

            _queryLeaseSlotCount = checked(_queryLeaseSlotCount + 1);
        }

        if (changed)
        {
            _control.CreateSnapshot();
        }

        ScheduleRetainedQueryLeaseTimerUnsafe(
            _timeProvider.GetUtcNow().ToUniversalTime());
    }

    private void FenceAndRemoveObservedQueryCursorRecord(KvEntry entry)
    {
        byte[] terminalValue = TryDecodeQueryCursorRecord(
                entry.Value.Span,
                out DurableQueryCursorRecord? record)
            && record is not null
            ? EncodeQueryCursorRecord(record with { State = DurableQueryCursorState.Claimed })
            : entry.Value.ToArray();
        KvCasResult tombstone = _control.CompareAndSet(
            entry.Key.Span,
            entry.Version,
            terminalValue);
        if (!tombstone.Succeeded)
        {
            throw new InvalidDataException("query_cursor_registry_cleanup_conflict");
        }

        _control.CreateSnapshot();
        QueryCursorRecoveryFaultTestHook(
            IndexQueryCursorRecoveryFaultPoint.AfterTerminalSnapshotBeforeDelete);
        if (!_control.Delete(entry.Key.Span))
        {
            throw new InvalidDataException("query_cursor_registry_cleanup_conflict");
        }

        QueryCursorRecoveryFaultTestHook(
            IndexQueryCursorRecoveryFaultPoint.AfterDeleteBeforeSnapshot);
        _control.CreateSnapshot();
    }

    private string EncodeQueryCursor(DurableQueryCursorEnvelope envelope)
    {
        using var payloadStream = new MemoryStream();
        using (var writer = new BinaryWriter(payloadStream, _queryCursorUtf8, leaveOpen: true))
        {
            writer.Write(_queryCursorEnvelopeVersion);
            writer.Write(envelope.ChainId.ToByteArray());
            writer.Write(envelope.GenerationRevision);
            writer.Write(envelope.ExpiresAtUtc.UtcTicks);
            WriteCursorString(writer, envelope.InnerCursor);
        }

        byte[] payload = payloadStream.ToArray();
        byte[] signatureInput = new byte[_queryCursorSignatureDomain.Length + payload.Length];
        _queryCursorSignatureDomain.CopyTo(signatureInput, 0);
        payload.CopyTo(signatureInput, _queryCursorSignatureDomain.Length);
        byte[] signature = HMACSHA256.HashData(_queryCursorSigningKey, signatureInput);
        byte[] signed = new byte[payload.Length + signature.Length];
        payload.CopyTo(signed, 0);
        signature.CopyTo(signed, payload.Length);
        return Base64UrlEncode(signed);
    }

    private bool TryDecodeQueryCursor(
        string cursor,
        out DurableQueryCursorEnvelope? envelope)
    {
        envelope = null;
        byte[] signed;
        try
        {
            signed = Base64UrlDecode(cursor);
        }
        catch (FormatException)
        {
            return false;
        }

        if (signed.Length <= _queryCursorSignatureLength)
        {
            return false;
        }

        ReadOnlySpan<byte> payload = signed.AsSpan(0, signed.Length - _queryCursorSignatureLength);
        ReadOnlySpan<byte> actualSignature = signed.AsSpan(payload.Length, _queryCursorSignatureLength);
        byte[] signatureInput = new byte[_queryCursorSignatureDomain.Length + payload.Length];
        _queryCursorSignatureDomain.CopyTo(signatureInput, 0);
        payload.CopyTo(signatureInput.AsSpan(_queryCursorSignatureDomain.Length));
        byte[] expectedSignature = HMACSHA256.HashData(_queryCursorSigningKey, signatureInput);
        if (!CryptographicOperations.FixedTimeEquals(actualSignature, expectedSignature))
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, _queryCursorUtf8, leaveOpen: false);
            if (reader.ReadInt32() != _queryCursorEnvelopeVersion)
            {
                return false;
            }

            byte[] chainBytes = reader.ReadBytes(16);
            if (chainBytes.Length != 16)
            {
                return false;
            }

            var chainId = new Guid(chainBytes);
            long revision = reader.ReadInt64();
            long expiresAtTicks = reader.ReadInt64();
            string innerCursor = ReadCursorString(reader);
            if (revision <= 0 || stream.Position != stream.Length)
            {
                return false;
            }

            envelope = new DurableQueryCursorEnvelope(
                chainId,
                revision,
                new DateTimeOffset(expiresAtTicks, TimeSpan.Zero),
                innerCursor);
            return true;
        }
        catch (Exception exception) when (exception is EndOfStreamException
            or IOException
            or DecoderFallbackException
            or InvalidDataException
            or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static byte[] EncodeQueryCursorRecord(DurableQueryCursorRecord record)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, _queryCursorUtf8, leaveOpen: true))
        {
            writer.Write(_queryCursorRecordVersion);
            writer.Write((byte)record.State);
            writer.Write(record.GenerationRevision);
            writer.Write(record.ExpiresAtUtc.UtcTicks);
            WriteCursorString(writer, record.WorkspaceId);
            WriteCursorString(writer, record.QueryFingerprint);
            writer.Write(record.CursorHash);
        }

        return stream.ToArray();
    }

    private static bool TryDecodeQueryCursorRecord(
        ReadOnlySpan<byte> payload,
        out DurableQueryCursorRecord? record)
    {
        record = null;
        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, _queryCursorUtf8, leaveOpen: false);
            if (reader.ReadInt32() != _queryCursorRecordVersion)
            {
                return false;
            }

            var state = (DurableQueryCursorState)reader.ReadByte();
            long revision = reader.ReadInt64();
            long expiresAtTicks = reader.ReadInt64();
            string workspaceId = ReadCursorString(reader);
            string queryFingerprint = ReadCursorString(reader);
            byte[] cursorHash = reader.ReadBytes(_queryCursorHashLength);
            if (!Enum.IsDefined(state)
                || revision <= 0
                || string.IsNullOrWhiteSpace(workspaceId)
                || string.IsNullOrWhiteSpace(queryFingerprint)
                || cursorHash.Length != _queryCursorHashLength
                || stream.Position != stream.Length)
            {
                return false;
            }

            record = new DurableQueryCursorRecord(
                state,
                workspaceId,
                queryFingerprint,
                revision,
                new DateTimeOffset(expiresAtTicks, TimeSpan.Zero),
                cursorHash);
            return true;
        }
        catch (Exception exception) when (exception is EndOfStreamException
            or IOException
            or DecoderFallbackException
            or InvalidDataException
            or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static void WriteCursorString(BinaryWriter writer, string value)
    {
        byte[] bytes = _queryCursorUtf8.GetBytes(value);
        if (bytes.Length > 4096)
        {
            throw new InvalidDataException("query_cursor_text_length_invalid");
        }

        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadCursorString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > 4096 || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("query_cursor_text_length_invalid");
        }

        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException();
        }

        return _queryCursorUtf8.GetString(bytes);
    }

    private static string QueryCursorRecordKey(Guid chainId) =>
        _queryCursorRecordPrefix + chainId.ToString("N");

    private static bool TryParseQueryCursorRecordKey(string key, out Guid chainId)
    {
        chainId = default;
        return key.StartsWith(_queryCursorRecordPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(key[_queryCursorRecordPrefix.Length..], "N", out chainId);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty,
        };
        return Convert.FromBase64String(padded);
    }

    private void ReleaseExpiredRetainedQueryLeases()
    {
        DateTimeOffset nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        List<RetainedIndexQueryCursor> expired;
        lock (_retainedQueryLeaseSync)
        {
            if (_disposed)
            {
                return;
            }

            expired = RemoveExpiredRetainedQueryLeasesUnsafe(
                nowUtc,
                requestedChainId: null,
                out _);
            ScheduleRetainedQueryLeaseTimerUnsafe(nowUtc);
        }

        DisposeRetainedQueryLeasesAndReleaseSlots(expired, deleteDurableRecords: true);
    }

    private void RunRetainedQueryLeaseTimerCallback()
    {
        try
        {
            QueryCursorTransitionFaultTestHook?.Invoke(
                IndexQueryCursorTransitionFaultPoint.BeforeExpirationTimerMaintenance);
            ReleaseExpiredRetainedQueryLeases();
        }
        catch (Exception)
        {
            MarkQueryCursorRegistryFaulted();
        }
    }

    internal bool TryReserveIndexQueryLeaseSlot()
    {
        lock (_retainedQueryLeaseSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfQueryCursorRegistryUnavailableUnsafe();
            if (_queryLeaseSlotCount >= _maximumRetainedQueryLeases)
            {
                return false;
            }

            _queryLeaseSlotCount = checked(_queryLeaseSlotCount + 1);
            return true;
        }
    }

    internal void ReleaseIndexQueryLeaseSlot()
    {
        lock (_retainedQueryLeaseSync)
        {
            if (_queryLeaseSlotCount <= 0)
            {
                if (_disposed)
                {
                    return;
                }

                throw new InvalidOperationException("Index query lease slot accounting underflow.");
            }

            _queryLeaseSlotCount--;
        }
    }

    private void ScheduleRetainedQueryLeaseTimerUnsafe(DateTimeOffset nowUtc)
    {
        if (_disposed)
        {
            return;
        }

        DateTimeOffset? earliestExpiration = _retainedQueryLeases.Count == 0
            ? null
            : _retainedQueryLeases.Values.Min(retained => retained.ExpiresAtUtc);
        if (earliestExpiration is null)
        {
            _ = _retainedQueryLeaseTimer.Change(
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            return;
        }

        TimeSpan dueTime = earliestExpiration <= nowUtc
            ? TimeSpan.Zero
            : earliestExpiration.Value - nowUtc;
        if (dueTime > _maximumQueryLeaseTimerDueTime)
        {
            dueTime = _maximumQueryLeaseTimerDueTime;
        }

        _ = _retainedQueryLeaseTimer.Change(dueTime, Timeout.InfiniteTimeSpan);
    }

    private List<RetainedIndexQueryCursor> RemoveExpiredRetainedQueryLeasesUnsafe(
        DateTimeOffset nowUtc,
        Guid? requestedChainId,
        out bool requestedChainExpired)
    {
        requestedChainExpired = false;
        var expired = new List<RetainedIndexQueryCursor>();
        foreach ((Guid chainId, RetainedIndexQueryCursor retained) in _retainedQueryLeases
            .Where(entry => entry.Value.ExpiresAtUtc <= nowUtc)
            .ToArray())
        {
            _retainedQueryLeases.Remove(chainId);
            expired.Add(retained);
            requestedChainExpired |= chainId == requestedChainId;
        }

        return expired;
    }

    private void DisposeRetainedQueryLeasesAndReleaseSlots(
        IEnumerable<RetainedIndexQueryCursor> retainedQueryLeases,
        bool deleteDurableRecords)
    {
        foreach (RetainedIndexQueryCursor retained in retainedQueryLeases)
        {
            try
            {
                if (deleteDurableRecords)
                {
                    _ = TryDeleteRetainedQueryCursorRecord(retained);
                }

                retained.Lease.Dispose();
            }
            finally
            {
                ReleaseIndexQueryLeaseSlot();
            }
        }
    }

    private bool TryDeleteRetainedQueryCursorRecord(RetainedIndexQueryCursor retained)
    {
        bool deleted;
        try
        {
            deleted = TryFailClosedQueryCursorRecord(
                retained.ChainId,
                retained.RegistryVersion,
                retained.Record);
            if (deleted)
            {
                QueryCursorTransitionFaultTestHook?.Invoke(
                    IndexQueryCursorTransitionFaultPoint.AfterExpirationDelete);
            }
        }
        catch (Exception)
        {
            deleted = false;
        }

        if (!deleted)
        {
            MarkQueryCursorRegistryFaulted();
        }

        return deleted;
    }

    private void MarkQueryCursorRegistryFaulted()
    {
        lock (_retainedQueryLeaseSync)
        {
            _queryCursorRegistryFaulted = true;
        }
    }

    private bool IsQueryCursorRegistryFaulted()
    {
        lock (_retainedQueryLeaseSync)
        {
            return _queryCursorRegistryFaulted;
        }
    }

    private void ThrowIfQueryCursorRegistryUnavailableUnsafe()
    {
        if (_queryCursorRegistryFaulted)
        {
            throw new IndexQueryCursorLeaseException(
                IndexQueryCursorLeaseFailure.RegistryUnavailable);
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

internal sealed class ActiveIndexQueryLease : IDisposable
{
    private readonly DatabaseGenerationQueryLease _lease;

    internal ActiveIndexQueryLease(
        DatabaseGenerationQueryLease lease,
        IndexPlanningSnapshot planningSnapshot,
        GenerationManifest manifest,
        string documentCollectionName,
        string fullTextIndexName)
    {
        _lease = lease;
        PlanningSnapshot = planningSnapshot;
        Manifest = manifest;
        DocumentCollectionName = documentCollectionName;
        FullTextIndexName = fullTextIndexName;
    }

    internal long DatabaseGenerationRevision => _lease.Generation.Revision;

    internal IndexPlanningSnapshot PlanningSnapshot { get; }

    internal GenerationManifest Manifest { get; }

    internal string DocumentCollectionName { get; }

    internal string FullTextIndexName { get; }

    internal string CreateCursor(string queryFingerprint, ReadOnlySpan<byte> continuationState) =>
        _lease.CreateCursor(queryFingerprint, continuationState);

    internal byte[] ReadCursor(string cursor, string queryFingerprint) =>
        _lease.ReadCursor(cursor, queryFingerprint);

    public void Dispose() => _lease.Dispose();
}

internal sealed class IndexQueryRequestLease : IDisposable
{
    private readonly SonnetDbIndexGenerationStore _owner;
    private readonly Guid _chainId;
    private readonly string? _innerCursor;
    private ActiveIndexQueryLease? _lease;
    private long _claimedRecordVersion;
    private int _ownsQueryLeaseSlot;

    internal IndexQueryRequestLease(
        SonnetDbIndexGenerationStore owner,
        ActiveIndexQueryLease lease,
        DateTimeOffset expiresAtUtc,
        bool cursorRecognized,
        bool ownsQueryLeaseSlot,
        Guid chainId,
        long claimedRecordVersion,
        string? innerCursor)
    {
        _owner = owner;
        _lease = lease;
        _chainId = chainId;
        _claimedRecordVersion = claimedRecordVersion;
        _innerCursor = innerCursor;
        ExpiresAtUtc = expiresAtUtc;
        CursorRecognized = cursorRecognized;
        _ownsQueryLeaseSlot = ownsQueryLeaseSlot ? 1 : 0;
    }

    internal ActiveIndexQueryLease Lease => _lease
        ?? throw new ObjectDisposedException(nameof(IndexQueryRequestLease));

    internal bool CursorRecognized { get; }

    internal DateTimeOffset ExpiresAtUtc { get; }

    internal byte[] ReadCursor(string cursor, string queryFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryFingerprint);
        if (!CursorRecognized || _innerCursor is null)
        {
            throw new InvalidOperationException("No continuation cursor was acquired.");
        }

        return Lease.ReadCursor(_innerCursor, queryFingerprint);
    }

    internal string CreateCursor(string queryFingerprint, ReadOnlySpan<byte> continuationState) =>
        _owner.CreateIndexQueryCursor(
            Lease,
            queryFingerprint,
            continuationState,
            _chainId,
            ExpiresAtUtc);

    internal IndexQueryCursorRetentionResult TryRetain(
        string cursor,
        string queryFingerprint)
    {
        ActiveIndexQueryLease lease = Lease;
        bool reservedQueryLeaseSlot = false;
        if (Volatile.Read(ref _ownsQueryLeaseSlot) == 0)
        {
            if (!_owner.TryReserveIndexQueryLeaseSlot())
            {
                return IndexQueryCursorRetentionResult.CapacityExceeded;
            }

            if (Interlocked.CompareExchange(ref _ownsQueryLeaseSlot, 1, 0) == 0)
            {
                reservedQueryLeaseSlot = true;
            }
            else
            {
                _owner.ReleaseIndexQueryLeaseSlot();
            }
        }

        try
        {
            IndexQueryCursorRetentionResult result = _owner.RetainIndexQueryCursor(
                cursor,
                queryFingerprint,
                lease,
                ExpiresAtUtc,
                _chainId,
                _claimedRecordVersion);
            if (result == IndexQueryCursorRetentionResult.Retained)
            {
                _lease = null;
                _claimedRecordVersion = 0;
                _ = Interlocked.Exchange(ref _ownsQueryLeaseSlot, 0);
            }
            else if (reservedQueryLeaseSlot
                && Interlocked.Exchange(ref _ownsQueryLeaseSlot, 0) != 0)
            {
                _owner.ReleaseIndexQueryLeaseSlot();
            }

            return result;
        }
        catch
        {
            if (reservedQueryLeaseSlot
                && Interlocked.Exchange(ref _ownsQueryLeaseSlot, 0) != 0)
            {
                _owner.ReleaseIndexQueryLeaseSlot();
            }

            throw;
        }
    }

    public void Dispose()
    {
        ActiveIndexQueryLease? lease = Interlocked.Exchange(ref _lease, null);
        try
        {
            long claimedRecordVersion = Interlocked.Exchange(ref _claimedRecordVersion, 0);
            try
            {
                _owner.ReleaseClaimedIndexQueryCursor(_chainId, claimedRecordVersion);
            }
            finally
            {
                lease?.Dispose();
            }
        }
        finally
        {
            if (Interlocked.Exchange(ref _ownsQueryLeaseSlot, 0) != 0)
            {
                _owner.ReleaseIndexQueryLeaseSlot();
            }
        }
    }
}

internal sealed record RetainedIndexQueryCursor(
    Guid ChainId,
    string WorkspaceId,
    string QueryFingerprint,
    long GenerationRevision,
    DateTimeOffset ExpiresAtUtc,
    byte[] CursorHash,
    long RegistryVersion,
    DurableQueryCursorRecord Record,
    ActiveIndexQueryLease Lease);

internal sealed record DurableQueryCursorEnvelope(
    Guid ChainId,
    long GenerationRevision,
    DateTimeOffset ExpiresAtUtc,
    string InnerCursor);

internal sealed record DurableQueryCursorRecord(
    DurableQueryCursorState State,
    string WorkspaceId,
    string QueryFingerprint,
    long GenerationRevision,
    DateTimeOffset ExpiresAtUtc,
    byte[] CursorHash);

internal enum DurableQueryCursorState : byte
{
    Available = 1,
    Claimed = 2,
}

internal enum IndexQueryCursorRetentionResult
{
    Retained,
    Expired,
    CapacityExceeded,
    Conflict,
}

internal enum IndexQueryCursorLeaseFailure
{
    Expired,
    Mismatch,
    Stale,
    CapacityExceeded,
    RegistryUnavailable,
}

internal sealed class IndexQueryCursorLeaseException : InvalidOperationException
{
    internal IndexQueryCursorLeaseException(IndexQueryCursorLeaseFailure failure)
        : base("The retained index query cursor lease is unavailable.")
    {
        Failure = failure;
    }

    internal IndexQueryCursorLeaseException(
        IndexQueryCursorLeaseFailure failure,
        Exception innerException)
        : base("The retained index query cursor lease is unavailable.", innerException)
    {
        Failure = failure;
    }

    internal IndexQueryCursorLeaseFailure Failure { get; }
}

internal sealed class IndexQueryGenerationValidationException : Exception
{
    internal IndexQueryGenerationValidationException(string message)
        : base(message)
    {
    }

    internal IndexQueryGenerationValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed record ActiveIndexSearchHit(
    IndexStorageDocument Document,
    double Score);

internal sealed record ActiveIndexSearchResult(
    string AccessPath,
    long Candidates,
    long Examined,
    IReadOnlyList<ActiveIndexSearchHit> Hits);

internal sealed class ActiveIndexFilterVisitBudget
{
    private readonly long _limit;

    internal ActiveIndexFilterVisitBudget(long limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _limit = limit;
    }

    internal long Visits { get; private set; }

    internal void VisitPlanningFile()
    {
        Visits++;
        if (Visits > _limit)
        {
            throw new ActiveIndexPlanningPathBudgetExceededException(Visits, _limit);
        }
    }

    internal void VisitFilterCandidates(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        long requested = checked(Visits + count);
        if (requested > _limit)
        {
            throw new ActiveIndexFilterCandidateBudgetExceededException(requested, _limit);
        }

        Visits = requested;
    }
}

internal sealed class ActiveIndexPlanningPathBudgetExceededException : Exception
{
    internal ActiveIndexPlanningPathBudgetExceededException(long visits, long limit)
        : base($"Planning path visits {visits} exceed budget {limit}.")
    {
    }
}

internal sealed class ActiveIndexFilterCandidateBudgetExceededException : Exception
{
    internal ActiveIndexFilterCandidateBudgetExceededException(long candidates, long limit)
        : base($"Full-text filter candidates {candidates} exceed budget {limit}.")
    {
    }
}

internal sealed class ActiveIndexFullTextPostingBudgetExceededException : Exception
{
    internal ActiveIndexFullTextPostingBudgetExceededException(long postingVisits, long limit)
        : base($"Full-text posting visits {postingVisits} exceed budget {limit}.")
    {
    }
}

internal sealed record ActiveIndexSymbolQueryResult(
    string AccessPath,
    long Candidates,
    long Examined,
    IReadOnlyList<IndexStorageDocument> Documents);

internal enum IndexGenerationPublishFaultPoint
{
    BeforeCommit,
    AfterCommit,
}

internal enum IndexQueryCursorTransitionFaultPoint
{
    BeforeClaimCas,
    AfterClaimCas,
    BeforeRetainCas,
    AfterRetainCas,
    BeforeReleaseCas,
    AfterReleaseCas,
    BeforeReleaseDelete,
    AfterReleaseDelete,
    BeforeReleaseSnapshot,
    AfterReleaseSnapshot,
    AfterExpirationDelete,
    BeforeExpirationTimerMaintenance,
}

internal enum IndexQueryCursorRecoveryFaultPoint
{
    AfterTerminalSnapshotBeforeDelete,
    AfterDeleteBeforeSnapshot,
}

internal sealed record CleanupOutcome(
    IReadOnlyList<long> RemovedGenerationRevisions,
    IReadOnlyList<long> DeferredGenerationRevisions,
    IReadOnlyList<long> RetentionDeferredGenerationRevisions,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Limitations);
#endif
