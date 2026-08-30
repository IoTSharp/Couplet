#if COUPLET_SONNETDB_SOURCE_GENERATIONS
using System.Buffers.Binary;
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
    private const int CursorStateLength = sizeof(long) + 16;
    private const int MaxCursorLength = 4096;
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
            CodeSearchRequest? cursorRequest = request as CodeSearchRequest;
            string? queryFingerprint = cursorRequest is null
                ? null
                : CreateCodeSearchFingerprint(cursorRequest);
            if (cursorRequest?.Cursor is { } suppliedCursor
                && (string.IsNullOrWhiteSpace(suppliedCursor)
                    || suppliedCursor.Length > MaxCursorLength))
            {
                return Error(
                    McpErrorCodes.InvalidRequest,
                    "query_cursor_invalid",
                    false,
                    binding,
                    correlationId);
            }

            bool reserveQueryLeaseSlot = cursorRequest is { Cursor: null, Mode: "fulltext" };
            IndexQueryRequestLease requestLease = _store.AcquireIndexQuery(
                binding.WorkspaceId,
                cursorRequest?.Cursor,
                queryFingerprint,
                reserveQueryLeaseSlot);
            McpDispatchResult result;
            try
            {
                result = ExecuteWithQueryLease(
                    request,
                    binding,
                    cursorRequest,
                    queryFingerprint,
                    requestLease,
                    stopwatch,
                    correlationId,
                    cancellationToken);
            }
            catch
            {
                DisposePreservingPrimaryFailure(requestLease);
                throw;
            }

            if (result.Error is not null)
            {
                DisposePreservingPrimaryFailure(requestLease);
                return result;
            }

            requestLease.Dispose();
            return result;
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
        catch (IndexQueryCursorLeaseException exception)
            when (exception.Failure == IndexQueryCursorLeaseFailure.Expired)
        {
            return Error(
                McpErrorCodes.StaleRevision,
                "query_cursor_expired",
                false,
                binding,
                correlationId);
        }
        catch (IndexQueryCursorLeaseException exception)
            when (exception.Failure == IndexQueryCursorLeaseFailure.Mismatch)
        {
            return Error(
                McpErrorCodes.InvalidRequest,
                "query_cursor_invalid",
                false,
                binding,
                correlationId);
        }
        catch (IndexQueryCursorLeaseException exception)
            when (exception.Failure == IndexQueryCursorLeaseFailure.Stale)
        {
            return Error(
                McpErrorCodes.StaleRevision,
                "query_cursor_stale",
                false,
                binding,
                correlationId);
        }
        catch (IndexQueryCursorLeaseException exception)
            when (exception.Failure == IndexQueryCursorLeaseFailure.CapacityExceeded)
        {
            return Error(
                McpErrorCodes.BudgetExhausted,
                "query_cursor_lease_capacity_exhausted",
                true,
                binding,
                correlationId);
        }
        catch (IndexQueryCursorLeaseException exception)
            when (exception.Failure == IndexQueryCursorLeaseFailure.RegistryUnavailable)
        {
            return Error(
                McpErrorCodes.IndexCorrupt,
                "query_cursor_registry_unavailable",
                false,
                binding,
                correlationId,
                "workspace.index");
        }
        catch (IndexQueryGenerationValidationException)
        {
            return Error(
                McpErrorCodes.IndexCorrupt,
                "active_generation_validation_failed",
                false,
                binding,
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

    private McpDispatchResult ExecuteWithQueryLease(
        McpToolRequest request,
        WorkspaceBinding binding,
        CodeSearchRequest? cursorRequest,
        string? queryFingerprint,
        IndexQueryRequestLease requestLease,
        Stopwatch stopwatch,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ActiveIndexQueryLease lease = requestLease.Lease;
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

        McpDispatchResult result = request switch
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
                requestLease,
                requestLease.CursorRecognized,
                manifest,
                stopwatch,
                correlationId,
                cancellationToken),
            SymbolGetRequest symbol => ExecuteSymbolGet(
                symbol,
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
        if (result.CodeSearch?.NextCursor is not string nextCursor)
        {
            return result;
        }

        return requestLease.TryRetain(nextCursor, queryFingerprint!) switch
        {
            IndexQueryCursorRetentionResult.Retained => result,
            IndexQueryCursorRetentionResult.Expired => Error(
                McpErrorCodes.StaleRevision,
                "query_cursor_expired",
                false,
                activeBinding,
                correlationId),
            IndexQueryCursorRetentionResult.CapacityExceeded => Error(
                McpErrorCodes.BudgetExhausted,
                "query_cursor_lease_capacity_exhausted",
                true,
                activeBinding,
                correlationId),
            IndexQueryCursorRetentionResult.Conflict => Error(
                McpErrorCodes.InvalidRequest,
                "query_cursor_invalid",
                false,
                activeBinding,
                correlationId),
            _ => throw new InvalidOperationException("Unknown cursor retention result."),
        };
    }

    private static void DisposePreservingPrimaryFailure(IndexQueryRequestLease requestLease)
    {
        try
        {
            requestLease.Dispose();
        }
        catch (IndexQueryCursorLeaseException exception)
            when (exception.Failure == IndexQueryCursorLeaseFailure.RegistryUnavailable)
        {
            // The primary request failure wins; the faulted registry remains observable by later requests.
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
        IndexQueryRequestLease requestLease,
        bool cursorRecognized,
        GenerationManifest manifest,
        Stopwatch stopwatch,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ActiveIndexQueryLease lease = requestLease.Lease;
        string queryFingerprint = CreateCodeSearchFingerprint(search);
        long offset = 0;
        if (search.Cursor is not null)
        {
            if (string.IsNullOrWhiteSpace(search.Cursor) || search.Cursor.Length > MaxCursorLength)
            {
                return Error(
                    McpErrorCodes.InvalidRequest,
                    "query_cursor_invalid",
                    false,
                    activeBinding,
                    correlationId);
            }

            byte[] cursorState;
            try
            {
                cursorState = requestLease.ReadCursor(search.Cursor, queryFingerprint);
            }
            catch (DatabaseGenerationException exception)
                when (exception.Code == DatabaseGenerationErrorCodes.CursorStale)
            {
                return Error(
                    McpErrorCodes.StaleRevision,
                    "query_cursor_stale",
                    false,
                    activeBinding,
                    correlationId);
            }
            catch (DatabaseGenerationException exception)
                when (exception.Code is DatabaseGenerationErrorCodes.CursorInvalid
                    or DatabaseGenerationErrorCodes.CursorMismatch)
            {
                return Error(
                    McpErrorCodes.InvalidRequest,
                    "query_cursor_invalid",
                    false,
                    activeBinding,
                    correlationId);
            }

            if (!cursorRecognized)
            {
                return Error(
                    McpErrorCodes.InvalidRequest,
                    "query_cursor_invalid",
                    false,
                    activeBinding,
                    correlationId);
            }

            if (cursorState.Length != CursorStateLength)
            {
                return Error(
                    McpErrorCodes.InvalidRequest,
                    "query_cursor_invalid",
                    false,
                    activeBinding,
                    correlationId);
            }

            offset = BinaryPrimitives.ReadInt64LittleEndian(cursorState);
            if (offset < 0)
            {
                return Error(
                    McpErrorCodes.InvalidRequest,
                    "query_cursor_invalid",
                    false,
                    activeBinding,
                    correlationId);
            }
        }

        bool hasFilters = search.Path is not null
            || search.Language is not null
            || search.Kind is not null;
        int pageSize;
        long requestedTopK;
        try
        {
            requestedTopK = checked(offset + search.Budget.MaxItems + 1L);
            _ = checked((int)requestedTopK);
            pageSize = checked(search.Budget.MaxItems + 1);
        }
        catch (OverflowException)
        {
            return Error(
                McpErrorCodes.InvalidRequest,
                "query_cursor_offset_out_of_range",
                false,
                activeBinding,
                correlationId);
        }

        long maxCandidateCount = Math.Min(
            search.Budget.MaxBytes,
            checked((long)search.Budget.MaxTokens * 4));
        if (requestedTopK > maxCandidateCount)
        {
            return Error(
                McpErrorCodes.BudgetExhausted,
                "query_cursor_candidate_budget_exhausted",
                true,
                activeBinding,
                correlationId);
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

        ActiveIndexSearchResult query;
        try
        {
            query = _store.QueryActiveCodeSearch(
                lease,
                search.Mode,
                search.Query,
                offset,
                pageSize,
                search.Path,
                search.Language,
                search.Kind,
                maxCandidateCount,
                cancellationToken);
        }
        catch (OverflowException)
        {
            return Error(
                McpErrorCodes.InvalidRequest,
                "query_cursor_offset_out_of_range",
                false,
                activeBinding,
                correlationId);
        }
        catch (ActiveIndexFilterCandidateBudgetExceededException)
        {
            return Error(
                McpErrorCodes.BudgetExhausted,
                "fulltext_filter_candidate_budget_exhausted",
                true,
                activeBinding,
                correlationId);
        }
        catch (ActiveIndexPlanningPathBudgetExceededException)
        {
            return Error(
                McpErrorCodes.BudgetExhausted,
                "fulltext_path_planning_budget_exhausted",
                true,
                activeBinding,
                correlationId);
        }
        catch (ActiveIndexFullTextPostingBudgetExceededException)
        {
            return Error(
                McpErrorCodes.BudgetExhausted,
                "fulltext_posting_budget_exhausted",
                true,
                activeBinding,
                correlationId);
        }
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
            string? nextCursor = truncated
                ? CreateCodeSearchCursor(requestLease, queryFingerprint, checked(offset + selectedCount))
                : null;
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
                cursorRecognized,
                sourceCurrent,
                truncated,
                truncationReason,
                nextCursor,
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
                    cursorRecognized,
                    sourceCurrent,
                    truncated,
                    truncationReason,
                    nextCursor,
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

            if (selectedCount == 0 && filtered.Count > 0)
            {
                return Error(
                    McpErrorCodes.BudgetExhausted,
                    "no_budget_for_reliable_item",
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

    private McpDispatchResult ExecuteSymbolGet(
        SymbolGetRequest request,
        WorkspaceBinding binding,
        WorkspaceBinding activeBinding,
        ActiveIndexQueryLease lease,
        GenerationManifest manifest,
        Stopwatch stopwatch,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (request.Cursor is not null)
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

        ActiveIndexSymbolQueryResult query = _store.QueryActiveSymbol(
            lease,
            request.SymbolId,
            request.QualifiedIdentity,
            request.Language,
            cancellationToken);
        if (stopwatch.Elapsed.TotalMilliseconds >= request.Budget.DeadlineMs)
        {
            return Error(
                McpErrorCodes.DeadlineExceeded,
                "request_deadline_reached",
                true,
                activeBinding,
                correlationId);
        }

        foreach (IndexStorageDocument document in query.Documents)
        {
            ValidateDocumentIdentity(document, manifest);
            if (document.RecordType != IndexStorageRecordType.Symbol)
            {
                if (request.SymbolId is not null)
                {
                    return Error(
                        McpErrorCodes.InvalidRequest,
                        "symbol_id_not_symbol",
                        false,
                        activeBinding,
                        correlationId);
                }

                throw new InvalidDataException("active_generation_symbol_index_contains_non_symbol");
            }

            ValidateSymbolDocument(document);
        }

        if (request.QualifiedIdentity is not null
            && request.Language is null
            && query.Documents.Count > 1)
        {
            return Error(
                McpErrorCodes.InvalidRequest,
                "qualified_identity_ambiguous",
                false,
                activeBinding,
                correlationId);
        }

        IndexStorageDocument? match = query.Documents.SingleOrDefault();
        if (match is not null
            && request.Language is not null
            && !string.Equals(match.Language, request.Language, StringComparison.OrdinalIgnoreCase))
        {
            match = null;
        }

        if (match is not null
            && request.QualifiedIdentity is not null
            && !string.Equals(
                match.QualifiedIdentity!.Normalize(),
                request.QualifiedIdentity.Normalize(),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("active_generation_symbol_identity_mismatch");
        }

        string evidenceId = match is null ? string.Empty : "source:" + match.StableId;
        IReadOnlyList<SymbolDetailsItem> items = match is null
            ? []
            :
            [
                new SymbolDetailsItem
                {
                    Id = match.StableId,
                    Kind = match.EntityKind!.Value,
                    QualifiedIdentity = match.QualifiedIdentity!,
                    Signature = match.Signature!,
                    ContainerId = match.ContainerId,
                    Language = match.Language,
                    Confidence = match.Confidence!,
                    EvidenceIds = [evidenceId],
                },
            ];
        IReadOnlyList<Evidence> evidence = match is null
            ? []
            :
            [
                new Evidence
                {
                    Id = evidenceId,
                    Kind = "symbol",
                    Span = match.Span,
                    SymbolId = match.StableId,
                    RelationId = null,
                    SourceRevision = match.SourceRevision,
                    IndexRevision = match.IndexRevision,
                },
            ];

        bool sourceCurrent = string.Equals(
            binding.SourceRevision,
            manifest.SourceRevision,
            StringComparison.Ordinal);
        BeforeResponseSerializationTestHook?.Invoke();
        int consumedBytes = 0;
        int consumedTokens = 0;
        int finalBytes = 0;
        int finalTokens = 0;
        double reportedElapsedMilliseconds = Math.Round(
            stopwatch.Elapsed.TotalMilliseconds,
            3,
            MidpointRounding.AwayFromZero);
        string responseJson = string.Empty;
        McpToolResponse<SymbolDetailsItem> response = CreateSymbolDetailsResponse(
            binding,
            manifest,
            items,
            evidence,
            query,
            sourceCurrent,
            reportedElapsedMilliseconds,
            consumedTokens,
            consumedBytes);
        for (int iteration = 0; iteration < 8; iteration++)
        {
            double serializationStartedAt = stopwatch.Elapsed.TotalMilliseconds;
            response = CreateSymbolDetailsResponse(
                binding,
                manifest,
                items,
                evidence,
                query,
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

        cancellationToken.ThrowIfCancellationRequested();
        if (stopwatch.Elapsed.TotalMilliseconds >= request.Budget.DeadlineMs)
        {
            return Error(
                McpErrorCodes.DeadlineExceeded,
                "request_deadline_reached",
                true,
                activeBinding,
                correlationId);
        }

        if (finalBytes > request.Budget.MaxBytes || finalTokens > request.Budget.MaxTokens)
        {
            return Error(
                McpErrorCodes.BudgetExhausted,
                "no_budget_for_reliable_item",
                true,
                activeBinding,
                correlationId);
        }

        stopwatch.Stop();
        return McpDispatchResult.FromSymbolDetails(response, responseJson);
    }

    private static void ValidateSymbolDocument(IndexStorageDocument document)
    {
        if (document.EntityKind is null
            || string.IsNullOrWhiteSpace(document.QualifiedIdentity)
            || string.IsNullOrWhiteSpace(document.Signature)
            || string.IsNullOrWhiteSpace(document.Language)
            || document.Confidence is null
            || document.Span is null)
        {
            throw new InvalidDataException("active_generation_symbol_document_invalid");
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
        bool retainedCursorLease,
        bool sourceCurrent,
        bool truncated,
        string? truncationReason,
        string? nextCursor,
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
                AccessPath = (retainedCursorLease
                    ? "generation_retained_cursor_lease:"
                    : "generation_active_lease:") + query.AccessPath,
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
            NextCursor = nextCursor,
        };

    private static string CreateCodeSearchFingerprint(CodeSearchRequest search)
    {
        var canonical = new StringBuilder("couplet.code_search.cursor.v1");
        AppendFingerprintPart(canonical, search.Mode);
        AppendFingerprintPart(canonical, search.Query);
        AppendFingerprintPart(canonical, search.Path);
        AppendFingerprintPart(canonical, search.Language);
        AppendFingerprintPart(canonical, search.Kind?.ToString());
        AppendFingerprintPart(canonical, search.ProviderId);
        return "code-search:v1:" + CursorCodec.HashRequest(canonical.ToString());
    }

    private static void AppendFingerprintPart(StringBuilder canonical, string? value)
    {
        canonical.Append('|');
        if (value is null)
        {
            canonical.Append("null");
            return;
        }

        canonical.Append(value.Length);
        canonical.Append(':');
        canonical.Append(value);
    }

    private static string CreateCodeSearchCursor(
        IndexQueryRequestLease requestLease,
        string queryFingerprint,
        long offset)
    {
        Span<byte> state = stackalloc byte[CursorStateLength];
        BinaryPrimitives.WriteInt64LittleEndian(state, offset);
        _ = Guid.NewGuid().TryWriteBytes(state[sizeof(long)..]);
        return requestLease.CreateCursor(queryFingerprint, state);
    }

    private static McpToolResponse<SymbolDetailsItem> CreateSymbolDetailsResponse(
        WorkspaceBinding binding,
        GenerationManifest manifest,
        IReadOnlyList<SymbolDetailsItem> items,
        IReadOnlyList<Evidence> evidence,
        ActiveIndexSymbolQueryResult query,
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
            Capabilities = McpWorkspaceBinder.CreateC1Capabilities()
                .Where(capability => capability.Id == "exact")
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
                FallbackReason = null,
                ElapsedMs = elapsedMilliseconds,
                ConsumedItems = items.Count,
                ConsumedTokens = consumedTokens,
                ConsumedBytes = consumedBytes,
            },
            Truncated = false,
            TruncationReason = null,
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
