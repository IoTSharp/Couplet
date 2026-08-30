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
        if (request is not WorkspaceStatusRequest status)
        {
            return Error(
                McpErrorCodes.CapabilityUnavailable,
                "generation_publish_blocked",
                true,
                binding,
                correlationId,
                "workspace.index",
                "CG-005");
        }

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
                status.RevisionSelector,
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
