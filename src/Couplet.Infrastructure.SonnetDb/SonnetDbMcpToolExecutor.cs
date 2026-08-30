#if COUPLET_SONNETDB_SOURCE_GENERATIONS
using System.Diagnostics;
using System.Text;
using Couplet.Application.Mcp;
using Couplet.Application.Serialization;
using Couplet.Core.Indexing;
using Couplet.Core.Mcp;
using SonnetDB.Generations;

namespace Couplet.Infrastructure.SonnetDb;

internal sealed class SonnetDbMcpToolExecutor : IMcpToolExecutor
{
    private static readonly IReadOnlyList<string> _blockingGaps = ["CG-005"];
    private readonly long _databaseBytesAtStartup;
    private readonly SonnetDbIndexGenerationStore _store;

    internal Action? BeforeResponseSerializationTestHook { get; set; }

    internal SonnetDbMcpToolExecutor(
        SonnetDbIndexGenerationStore store,
        long databaseBytesAtStartup)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegative(databaseBytesAtStartup);
        _store = store;
        _databaseBytesAtStartup = databaseBytesAtStartup;
    }

    public McpDispatchResult Execute(
        McpToolRequest request,
        WorkspaceBinding binding,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using ActiveIndexQueryLease lease = _store.AcquireActiveIndexQuery(binding.WorkspaceId);
            GenerationManifest manifest = lease.Manifest;
            if (lease.PlanningSnapshot.RepositoryIdentity is { } repositoryIdentity
                && !string.Equals(repositoryIdentity, binding.RepositoryIdentity, StringComparison.Ordinal))
            {
                throw new InvalidDataException("active_generation_repository_identity_invalid");
            }

            WorkspaceBinding activeBinding = ActiveBinding(binding, manifest);
            McpError? revisionError = ValidateRevisionSelector(
                request.RevisionSelector,
                activeBinding,
                correlationId);
            if (revisionError is not null)
            {
                return McpDispatchResult.FromError(revisionError);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Error(
                    McpErrorCodes.Cancelled,
                    "client_cancelled",
                    false,
                    activeBinding,
                    correlationId);
            }

            return request switch
            {
                WorkspaceStatusRequest status => ExecuteWorkspaceStatus(
                    status,
                    binding,
                    activeBinding,
                    manifest,
                    stopwatch,
                    correlationId,
                    cancellationToken),
                CodeSearchRequest search when search.Mode is "exact" or "fulltext" => ExecuteCodeSearch(
                    search,
                    binding,
                    activeBinding,
                    lease,
                    manifest,
                    stopwatch,
                    correlationId,
                    cancellationToken),
                _ => Error(
                    McpErrorCodes.CapabilityUnavailable,
                    "active_query_tool_not_connected",
                    true,
                    activeBinding,
                    correlationId,
                    "workspace.index",
                    "CG-005"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error(
                McpErrorCodes.Cancelled,
                "client_cancelled",
                false,
                binding,
                correlationId);
        }
        catch (DatabaseGenerationException exception)
            when (exception.Code == DatabaseGenerationErrorCodes.NoActiveGeneration)
        {
            return Error(
                McpErrorCodes.IndexNotReady,
                "active_generation_not_published",
                true,
                WithoutIndex(binding),
                correlationId,
                "workspace.index");
        }
        catch (Exception exception) when (exception is InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or DatabaseGenerationException)
        {
            return Error(
                McpErrorCodes.IndexCorrupt,
                "active_generation_validation_failed",
                false,
                binding,
                correlationId,
                "workspace.index");
        }
    }

    private McpDispatchResult ExecuteWorkspaceStatus(
        WorkspaceStatusRequest status,
        WorkspaceBinding binding,
        WorkspaceBinding activeBinding,
        GenerationManifest manifest,
        Stopwatch stopwatch,
        string correlationId,
        CancellationToken cancellationToken)
    {
        bool sourceCurrent = string.Equals(
            binding.SourceRevision,
            manifest.SourceRevision,
            StringComparison.Ordinal);
        WorkspaceStatusItem item = new()
        {
            Files = manifest.Counts.Files,
            Symbols = manifest.Counts.Symbols,
            Relations = manifest.Counts.GraphEdges,
            Chunks = manifest.Counts.Chunks,
            ParserVersions = manifest.ProducerVersions,
            EmbeddingModel = null,
            DatabaseBytes = _databaseBytesAtStartup,
            BlockingGaps = _blockingGaps,
            RebuildRequired = !sourceCurrent,
        };

        BeforeResponseSerializationTestHook?.Invoke();
        int consumedBytes = 0;
        int consumedTokens = 0;
        double reportedElapsedMilliseconds = Math.Round(
            stopwatch.Elapsed.TotalMilliseconds,
            3,
            MidpointRounding.AwayFromZero);
        McpToolResponse<WorkspaceStatusItem> response = CreateResponse(
            binding,
            manifest,
            item,
            sourceCurrent,
            reportedElapsedMilliseconds,
            consumedTokens,
            consumedBytes);
        int finalBytes = 0;
        int finalTokens = 0;
        string responseJson = string.Empty;
        for (int iteration = 0; iteration < 8; iteration++)
        {
            double serializationStartedAt = stopwatch.Elapsed.TotalMilliseconds;
            response = CreateResponse(
                binding,
                manifest,
                item,
                sourceCurrent,
                reportedElapsedMilliseconds,
                consumedTokens,
                consumedBytes);
            responseJson = CoupletJsonSerializer.Serialize(response);
            finalBytes = Encoding.UTF8.GetByteCount(responseJson);
            finalTokens = (finalBytes + 3) / 4;
            double serializationCompletedAt = stopwatch.Elapsed.TotalMilliseconds;
            bool payloadStable = finalBytes == consumedBytes && finalTokens == consumedTokens;
            bool elapsedClose = Math.Abs(serializationCompletedAt - reportedElapsedMilliseconds) <= 0.25;
            if (payloadStable && elapsedClose)
            {
                break;
            }

            consumedBytes = finalBytes;
            consumedTokens = finalTokens;
            double serializationDuration = serializationCompletedAt - serializationStartedAt;
            reportedElapsedMilliseconds = Math.Round(
                serializationCompletedAt + serializationDuration,
                3,
                MidpointRounding.AwayFromZero);
        }

        stopwatch.Stop();
        if (cancellationToken.IsCancellationRequested)
        {
            return Error(
                McpErrorCodes.Cancelled,
                "client_cancelled",
                false,
                activeBinding,
                correlationId);
        }

        if (stopwatch.Elapsed.TotalMilliseconds >= status.Budget.DeadlineMs)
        {
            return Error(
                McpErrorCodes.DeadlineExceeded,
                "request_deadline_reached",
                true,
                activeBinding,
                correlationId);
        }

        if (finalBytes > status.Budget.MaxBytes || finalTokens > status.Budget.MaxTokens)
        {
            return Error(
                McpErrorCodes.BudgetExhausted,
                "no_budget_for_reliable_item",
                true,
                activeBinding,
                correlationId);
        }

        return McpDispatchResult.FromWorkspaceStatus(response, responseJson);
    }

    private McpDispatchResult ExecuteCodeSearch(
        CodeSearchRequest search,
        WorkspaceBinding binding,
        WorkspaceBinding activeBinding,
        ActiveIndexQueryLease lease,
        GenerationManifest manifest,
        Stopwatch stopwatch,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (search.Cursor is not null)
        {
            return Error(
                McpErrorCodes.CapabilityUnavailable,
                "query_cursor_not_connected",
                true,
                activeBinding,
                correlationId,
                "workspace.index",
                "CG-005");
        }

        bool hasFilters = search.Path is not null
            || search.Language is not null
            || search.Kind is not null;
        if (search.Mode == "fulltext" && hasFilters)
        {
            return Error(
                McpErrorCodes.CapabilityUnavailable,
                "fulltext_filter_plan_not_connected",
                true,
                activeBinding,
                correlationId,
                "workspace.index",
                "CG-005");
        }

        int topK = checked(search.Budget.MaxItems + 1);
        ActiveIndexSearchResult query = _store.QueryActiveCodeSearch(
            lease,
            search.Mode,
            search.Query,
            topK,
            cancellationToken);
        IReadOnlyList<ActiveIndexSearchHit> filtered = search.Mode == "exact" && hasFilters
            ? query.Hits.Where(hit => MatchesFilters(hit.Document, search)).ToArray()
            : query.Hits;
        foreach (ActiveIndexSearchHit hit in filtered)
        {
            ValidateDocumentIdentity(hit.Document, manifest);
        }

        bool sourceCurrent = string.Equals(
            binding.SourceRevision,
            manifest.SourceRevision,
            StringComparison.Ordinal);
        int selectedCount = Math.Min(filtered.Count, search.Budget.MaxItems);
        bool truncated = filtered.Count > selectedCount;
        string? truncationReason = truncated ? "max_items" : null;
        BeforeResponseSerializationTestHook?.Invoke();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ActiveIndexSearchHit> selected = filtered.Take(selectedCount).ToArray();
            (IReadOnlyList<CodeSearchItem> items, IReadOnlyList<Evidence> evidence) = MapSearchResults(selected, search.Mode);
            int consumedBytes = 0;
            int consumedTokens = 0;
            int finalBytes = 0;
            int finalTokens = 0;
            double reportedElapsedMilliseconds = Math.Round(
                stopwatch.Elapsed.TotalMilliseconds,
                3,
                MidpointRounding.AwayFromZero);
            string responseJson = string.Empty;
            McpToolResponse<CodeSearchItem> response = CreateCodeSearchResponse(
                binding,
                manifest,
                items,
                evidence,
                query,
                sourceCurrent,
                truncated,
                truncationReason,
                reportedElapsedMilliseconds,
                consumedTokens,
                consumedBytes);
            for (int iteration = 0; iteration < 8; iteration++)
            {
                double serializationStartedAt = stopwatch.Elapsed.TotalMilliseconds;
                response = CreateCodeSearchResponse(
                    binding,
                    manifest,
                    items,
                    evidence,
                    query,
                    sourceCurrent,
                    truncated,
                    truncationReason,
                    reportedElapsedMilliseconds,
                    consumedTokens,
                    consumedBytes);
                responseJson = CoupletJsonSerializer.Serialize(response);
                finalBytes = Encoding.UTF8.GetByteCount(responseJson);
                finalTokens = (finalBytes + 3) / 4;
                double serializationCompletedAt = stopwatch.Elapsed.TotalMilliseconds;
                bool payloadStable = finalBytes == consumedBytes && finalTokens == consumedTokens;
                bool elapsedClose = Math.Abs(serializationCompletedAt - reportedElapsedMilliseconds) <= 0.25;
                if (payloadStable && elapsedClose)
                {
                    break;
                }

                consumedBytes = finalBytes;
                consumedTokens = finalTokens;
                double serializationDuration = serializationCompletedAt - serializationStartedAt;
                reportedElapsedMilliseconds = Math.Round(
                    serializationCompletedAt + serializationDuration,
                    3,
                    MidpointRounding.AwayFromZero);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed.TotalMilliseconds >= search.Budget.DeadlineMs)
            {
                return Error(
                    McpErrorCodes.DeadlineExceeded,
                    "request_deadline_reached",
                    true,
                    activeBinding,
                    correlationId);
            }

            if (finalBytes <= search.Budget.MaxBytes && finalTokens <= search.Budget.MaxTokens)
            {
                stopwatch.Stop();
                return McpDispatchResult.FromCodeSearch(response, responseJson);
            }

            if (selectedCount == 0)
            {
                return Error(
                    McpErrorCodes.BudgetExhausted,
                    "no_budget_for_reliable_item",
                    true,
                    activeBinding,
                    correlationId);
            }

            selectedCount--;
            truncated = true;
            truncationReason = finalBytes > search.Budget.MaxBytes ? "max_bytes" : "max_tokens";
        }
    }

    private static bool MatchesFilters(
        IndexStorageDocument document,
        CodeSearchRequest search)
    {
        if (search.Language is not null
            && !string.Equals(document.Language, search.Language, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (search.Kind is not null && document.EntityKind != search.Kind)
        {
            return false;
        }

        if (search.Path is null)
        {
            return true;
        }

        string pattern = search.Path.Replace('\\', '/');
        return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
            pattern,
            document.Path,
            ignoreCase: false);
    }

    private static void ValidateDocumentIdentity(
        IndexStorageDocument document,
        GenerationManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(document.StableId)
            || !string.Equals(document.WorkspaceId, manifest.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(document.SourceRevision, manifest.SourceRevision, StringComparison.Ordinal)
            || !string.Equals(document.IndexRevision, manifest.IndexRevision, StringComparison.Ordinal)
            || document.Span is { } span
                && !string.Equals(span.Path, document.Path, StringComparison.Ordinal))
        {
            throw new InvalidDataException("active_generation_query_document_identity_invalid");
        }
    }

    private static (IReadOnlyList<CodeSearchItem> Items, IReadOnlyList<Evidence> Evidence) MapSearchResults(
        IReadOnlyList<ActiveIndexSearchHit> hits,
        string mode)
    {
        var items = new List<CodeSearchItem>(hits.Count);
        var evidence = new List<Evidence>(hits.Count);
        foreach (ActiveIndexSearchHit hit in hits)
        {
            IndexStorageDocument document = hit.Document;
            if (!double.IsFinite(hit.Score) || hit.Score < 0)
            {
                throw new InvalidDataException("active_generation_query_score_invalid");
            }

            string evidenceId = "source:" + document.StableId;
            string kind = (document.EntityKind?.ToString() ?? document.RecordType.ToString())
                .ToLowerInvariant();
            items.Add(new CodeSearchItem
            {
                Id = document.StableId,
                Kind = kind,
                DisplayName = document.DisplayName
                    ?? document.QualifiedIdentity
                    ?? document.Path,
                Score = hit.Score,
                ScoreParts =
                [
                    new ScorePart
                    {
                        Name = mode,
                        Value = hit.Score,
                    },
                ],
                EvidenceIds = [evidenceId],
            });
            evidence.Add(new Evidence
            {
                Id = evidenceId,
                Kind = document.RecordType.ToString().ToLowerInvariant(),
                Span = document.Span,
                SymbolId = document.RecordType == IndexStorageRecordType.Symbol
                    ? document.StableId
                    : document.ContainerId,
                RelationId = null,
                SourceRevision = document.SourceRevision,
                IndexRevision = document.IndexRevision,
            });
        }

        return (items, evidence);
    }

    private static McpToolResponse<CodeSearchItem> CreateCodeSearchResponse(
        WorkspaceBinding binding,
        GenerationManifest manifest,
        IReadOnlyList<CodeSearchItem> items,
        IReadOnlyList<Evidence> evidence,
        ActiveIndexSearchResult query,
        bool sourceCurrent,
        bool truncated,
        string? truncationReason,
        double elapsedMilliseconds,
        int consumedTokens,
        int consumedBytes) => new()
        {
            WorkspaceId = manifest.WorkspaceId,
            SourceRevision = manifest.SourceRevision,
            IndexRevision = manifest.IndexRevision,
            Freshness = new Freshness
            {
                SourceState = sourceCurrent
                    ? SourceState(binding.SourceRevision)
                    : "unknown",
                IndexState = sourceCurrent ? "current" : "stale",
                Coverage = sourceCurrent ? 1 : 0,
                PendingFiles = 0,
                FailedFiles = 0,
                Reason = "source_revision_sampled_at_mcp_startup",
            },
            Capabilities = McpWorkspaceBinder.CreateC1Capabilities()
                .Where(capability => capability.Id is "exact" or "fulltext")
                .ToArray(),
            Items = items,
            Evidence = evidence,
            Diagnostics = new QueryDiagnostics
            {
                AccessPath = "generation_active_lease:" + query.AccessPath,
                Candidates = query.Candidates,
                Examined = query.Examined,
                Returned = items.Count,
                ExpandedEdges = 0,
                FrontierPeak = 0,
                FallbackReason = query.AccessPath.StartsWith(
                    "document_fulltext:",
                    StringComparison.Ordinal)
                    ? "sonnetdb_fulltext_internal_candidate_counts_unavailable"
                    : null,
                ElapsedMs = elapsedMilliseconds,
                ConsumedItems = items.Count,
                ConsumedTokens = consumedTokens,
                ConsumedBytes = consumedBytes,
            },
            Truncated = truncated,
            TruncationReason = truncationReason,
            NextCursor = null,
        };

    internal static long SampleDatabaseBytes(
        string databaseRoot,
        CancellationToken cancellationToken,
        Action<FileSystemInfo>? entryVisitedTestHook = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(Path.GetFullPath(databaseRoot)));
        long total = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo directory = pending.Pop();
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
            {
                entryVisitedTestHook?.Invoke(entry);
                cancellationToken.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
                else if (entry is FileInfo file)
                {
                    total = checked(total + file.Length);
                }
            }
        }

        return total;
    }

    private static McpToolResponse<WorkspaceStatusItem> CreateResponse(
        WorkspaceBinding binding,
        GenerationManifest manifest,
        WorkspaceStatusItem item,
        bool sourceCurrent,
        double elapsedMilliseconds,
        int consumedTokens,
        int consumedBytes) => new()
        {
            WorkspaceId = manifest.WorkspaceId,
            SourceRevision = manifest.SourceRevision,
            IndexRevision = manifest.IndexRevision,
            Freshness = new Freshness
            {
                SourceState = sourceCurrent
                    ? SourceState(binding.SourceRevision)
                    : "unknown",
                IndexState = sourceCurrent ? "current" : "stale",
                Coverage = sourceCurrent ? 1 : 0,
                PendingFiles = 0,
                FailedFiles = 0,
                Reason = "source_revision_sampled_at_mcp_startup",
            },
            Capabilities = McpWorkspaceBinder.CreateC1Capabilities(),
            Items = [item],
            Evidence = [],
            Diagnostics = new QueryDiagnostics
            {
                AccessPath = "generation_active_lease:index_planning",
                Candidates = 1,
                Examined = 1,
                Returned = 1,
                ExpandedEdges = 0,
                FrontierPeak = 0,
                FallbackReason = "database_bytes_and_source_revision_sampled_at_mcp_startup",
                ElapsedMs = elapsedMilliseconds,
                ConsumedItems = 1,
                ConsumedTokens = consumedTokens,
                ConsumedBytes = consumedBytes,
            },
            Truncated = false,
            TruncationReason = null,
            NextCursor = null,
        };

    private static McpError? ValidateRevisionSelector(
        RevisionSelector? selector,
        WorkspaceBinding activeBinding,
        string correlationId)
    {
        if (selector is null)
        {
            return null;
        }

        string? current = selector.Kind == "source"
            ? activeBinding.SourceRevision
            : activeBinding.IndexRevision;
        return string.Equals(selector.Value, current, StringComparison.Ordinal)
            ? null
            : new McpError
            {
                Code = McpErrorCodes.StaleRevision,
                Reason = "revision_not_available",
                Retryable = false,
                CurrentRevision = activeBinding.IndexRevision,
                CorrelationId = correlationId,
            };
    }

    private static string SourceState(string sourceRevision) =>
        sourceRevision.Contains("+dirty.", StringComparison.Ordinal) ? "dirty" : "clean";

    private static WorkspaceBinding ActiveBinding(
        WorkspaceBinding binding,
        GenerationManifest manifest) => new()
        {
            WorkspaceId = binding.WorkspaceId,
            RepositoryIdentity = binding.RepositoryIdentity,
            SourceRevision = manifest.SourceRevision,
            IndexRevision = manifest.IndexRevision,
        };

    private static WorkspaceBinding WithoutIndex(WorkspaceBinding binding) => new()
    {
        WorkspaceId = binding.WorkspaceId,
        RepositoryIdentity = binding.RepositoryIdentity,
        SourceRevision = binding.SourceRevision,
        IndexRevision = null,
    };

    private static McpDispatchResult Error(
        string code,
        string reason,
        bool retryable,
        WorkspaceBinding binding,
        string correlationId,
        string? capability = null,
        string? gapId = null) => McpDispatchResult.FromError(new McpError
        {
            Code = code,
            Reason = reason,
            Retryable = retryable,
            Capability = capability,
            GapId = gapId,
            CurrentRevision = binding.IndexRevision,
            CorrelationId = correlationId,
        });
}
#endif
