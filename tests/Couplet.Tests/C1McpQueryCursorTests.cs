#if COUPLET_SONNETDB_SOURCE_GENERATIONS
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Couplet.Application.Indexing;
using Couplet.Application.Mcp;
using Couplet.Application.Serialization;
using Couplet.Application.Workspaces;
using Couplet.Core.Graph;
using Couplet.Core.Indexing;
using Couplet.Core.Mcp;
using Couplet.Infrastructure.SonnetDb;
using SonnetDB.Documents;
using SonnetDB.Generations;

namespace Couplet.Tests;

public sealed class C1McpQueryCursorTests
{
    [Fact]
    public async Task CodeSearch_FullTextCursorOnSameActiveGeneration_ReturnsAllPagesWithoutScanOrDuplicates()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex published = await PublishAsync(workspaceRoot, database, 4);
            using var store = new SonnetDbIndexGenerationStore(database);
            DocumentCollectionStore collection = store.GetActiveDocumentCollectionForTest(
                published.Snapshot.WorkspaceId);
            long fullScansBefore = FullScanCount(collection);
            var executor = new SonnetDbMcpToolExecutor(store, 0);
            WorkspaceBinding binding = Binding(published.Snapshot);

            McpToolResponse<CodeSearchItem> complete = Success(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 100));
            Assert.False(complete.Truncated);
            Assert.Null(complete.NextCursor);
            Assert.True(complete.Items.Count > 2);

            var actualIds = new List<string>();
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            do
            {
                bool retainedCursorLease = cursor is not null;
                McpToolResponse<CodeSearchItem> page = Success(DispatchSearch(
                    executor,
                    binding,
                    "SharedToken",
                    maxItems: 2,
                    cursor));
                Assert.Equal(published.Snapshot.IndexRevision, page.IndexRevision);
                Assert.Equal(
                    (retainedCursorLease
                        ? "generation_retained_cursor_lease:"
                        : "generation_active_lease:") + "document_fulltext:code_search",
                    page.Diagnostics.AccessPath);
                Assert.InRange(page.Items.Count, 1, 2);
                actualIds.AddRange(page.Items.Select(item => item.Id));
                cursor = page.NextCursor;
                if (cursor is not null)
                {
                    Assert.True(page.Truncated);
                    Assert.Equal("max_items", page.TruncationReason);
                    Assert.True(cursors.Add(cursor));
                }
                else
                {
                    Assert.False(page.Truncated);
                }
            }
            while (cursor is not null);

            Assert.Equal(complete.Items.Select(item => item.Id), actualIds);
            Assert.Equal(actualIds.Count, actualIds.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(fullScansBefore, FullScanCount(collection));
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task CodeSearch_FullTextCursorTamperOrQueryShapeChange_FailsClosedWithoutScan()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex published = await PublishAsync(workspaceRoot, database, 3);
            using var store = new SonnetDbIndexGenerationStore(database);
            DocumentCollectionStore collection = store.GetActiveDocumentCollectionForTest(
                published.Snapshot.WorkspaceId);
            long fullScansBefore = FullScanCount(collection);
            var executor = new SonnetDbMcpToolExecutor(store, 0);
            WorkspaceBinding binding = Binding(published.Snapshot);
            string cursor = Assert.IsType<string>(Success(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 1)).NextCursor);
            string tampered = (cursor[0] == 'A' ? 'B' : 'A') + cursor[1..];

            McpError tamperedError = Assert.IsType<McpError>(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 1,
                tampered).Error);
            Assert.Equal(McpErrorCodes.InvalidRequest, tamperedError.Code);
            Assert.Equal("query_cursor_invalid", tamperedError.Reason);

            McpError shapeError = Assert.IsType<McpError>(DispatchSearch(
                executor,
                binding,
                "DifferentToken",
                maxItems: 1,
                cursor).Error);
            Assert.Equal(McpErrorCodes.InvalidRequest, shapeError.Code);
            Assert.Equal("query_cursor_invalid", shapeError.Reason);
            Assert.Equal(published.Snapshot.IndexRevision, shapeError.CurrentRevision);
            McpToolResponse<CodeSearchItem> validContinuation = Success(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 1,
                cursor));
            Assert.NotEmpty(validContinuation.Items);
            Assert.Equal(fullScansBefore, FullScanCount(collection));
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task CodeSearch_PublishDuringFirstPage_RetainsRetiredLeaseAcrossRequestsThenReleasesAfterLastPage()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex first = await PublishAsync(workspaceRoot, database, 3);
            await File.WriteAllTextAsync(
                Path.Combine(workspaceRoot, "Sample0.cs"),
                "public class Sample0 { public string Replacement() => \"replacement\"; }");
            DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                workspaceRoot,
                WorkspaceDiscoveryService.DefaultPolicy);
            WorkspaceIndexSnapshot secondSnapshot = await IndexSnapshotBuilder.BuildAsync(
                discovered,
                first.Snapshot.IndexRevision);

            using var store = new SonnetDbIndexGenerationStore(database);
            IndexPlanningSnapshot previous = Assert.IsType<ActiveIndexPlanningSnapshot>(
                store.ReadActivePlanningSnapshot(first.Snapshot.WorkspaceId)).PlanningSnapshot;
            IncrementalIndexPlan secondPlan = IncrementalIndexPlanner.PlanFromPublished(
                previous,
                secondSnapshot);
            DocumentCollectionStore oldCollection = store.GetActiveDocumentCollectionForTest(
                first.Snapshot.WorkspaceId);
            long oldFullScansBefore = FullScanCount(oldCollection);
            var executor = new SonnetDbMcpToolExecutor(store, 0);
            McpToolResponse<CodeSearchItem> complete = Success(DispatchSearch(
                executor,
                Binding(first.Snapshot),
                "SharedToken",
                maxItems: 100));
            string[] expectedIds = complete.Items.Select(item => item.Id).ToArray();
            IndexStageReport? secondReport = null;
            DatabaseGenerationCleanupResult? cleanupWhileLeased = null;
            executor.BeforeResponseSerializationTestHook = () =>
            {
                secondReport = store.StageAndPublish(
                    secondSnapshot,
                    secondPlan,
                    first.Report.DatabaseGenerationRevision!.Value);
                cleanupWhileLeased = store.CleanupRetired(first.Snapshot.WorkspaceId);
            };

            McpToolResponse<CodeSearchItem> firstPage = Success(DispatchSearch(
                executor,
                Binding(first.Snapshot),
                "SharedToken",
                maxItems: 1));

            Assert.Equal(first.Snapshot.IndexRevision, firstPage.IndexRevision);
            string retiredCursor = Assert.IsType<string>(firstPage.NextCursor);
            Assert.Equal([1L], Assert.IsType<IndexStageReport>(secondReport).DeferredGenerationRevisions);
            Assert.Equal(
                [1L],
                Assert.IsType<DatabaseGenerationCleanupResult>(cleanupWhileLeased).DeferredRevisions);
            Assert.Equal(oldFullScansBefore, FullScanCount(oldCollection));
            Assert.Equal(1, store.RetainedIndexQueryLeaseCountForTest);

            executor.BeforeResponseSerializationTestHook = null;
            DatabaseGenerationCleanupResult cleanupAfterFirstResponse = store.CleanupRetired(
                first.Snapshot.WorkspaceId);
            Assert.Empty(cleanupAfterFirstResponse.RemovedRevisions);
            Assert.Equal([1L], cleanupAfterFirstResponse.DeferredRevisions);

            var actualIds = new List<string>(firstPage.Items.Select(item => item.Id));
            string? cursor = retiredCursor;
            while (cursor is not null)
            {
                McpToolResponse<CodeSearchItem> page = Success(DispatchSearch(
                    executor,
                    Binding(secondSnapshot),
                    "SharedToken",
                    maxItems: 1,
                    cursor));
                Assert.Equal(first.Snapshot.IndexRevision, page.IndexRevision);
                Assert.Equal(
                    "generation_retained_cursor_lease:document_fulltext:code_search",
                    page.Diagnostics.AccessPath);
                actualIds.AddRange(page.Items.Select(item => item.Id));
                cursor = page.NextCursor;
            }

            Assert.Equal(expectedIds, actualIds);
            Assert.Equal(actualIds.Count, actualIds.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(0, store.RetainedIndexQueryLeaseCountForTest);
            Assert.Equal(oldFullScansBefore, FullScanCount(oldCollection));

            DatabaseGenerationCleanupResult cleanupAfterRequest = store.CleanupRetired(
                first.Snapshot.WorkspaceId);
            Assert.Equal([1L], cleanupAfterRequest.RemovedRevisions);
            Assert.Empty(cleanupAfterRequest.DeferredRevisions);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task CodeSearch_RetiredCursorLeaseExpiresAutomatically_ReleasesGenerationWithoutAnotherRequest()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        TimeSpan cursorLeaseRetention = TimeSpan.FromMinutes(1);
        try
        {
            PublishedIndex first = await PublishAsync(workspaceRoot, database, 3);
            await File.WriteAllTextAsync(
                Path.Combine(workspaceRoot, "Sample0.cs"),
                "public class Sample0 { public string Replacement() => \"replacement\"; }");
            DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                workspaceRoot,
                WorkspaceDiscoveryService.DefaultPolicy);
            WorkspaceIndexSnapshot secondSnapshot = await IndexSnapshotBuilder.BuildAsync(
                discovered,
                first.Snapshot.IndexRevision);

            using var store = new SonnetDbIndexGenerationStore(
                database,
                TimeSpan.Zero,
                timeProvider,
                cursorLeaseRetention);
            IndexPlanningSnapshot previous = Assert.IsType<ActiveIndexPlanningSnapshot>(
                store.ReadActivePlanningSnapshot(first.Snapshot.WorkspaceId)).PlanningSnapshot;
            IncrementalIndexPlan secondPlan = IncrementalIndexPlanner.PlanFromPublished(
                previous,
                secondSnapshot);
            DocumentCollectionStore oldCollection = store.GetActiveDocumentCollectionForTest(
                first.Snapshot.WorkspaceId);
            long oldFullScansBefore = FullScanCount(oldCollection);
            IndexStageReport? secondReport = null;
            var executor = new SonnetDbMcpToolExecutor(store, 0)
            {
                BeforeResponseSerializationTestHook = () =>
                {
                    secondReport = store.StageAndPublish(
                        secondSnapshot,
                        secondPlan,
                        first.Report.DatabaseGenerationRevision!.Value);
                },
            };

            McpToolResponse<CodeSearchItem> firstPage = Success(DispatchSearch(
                executor,
                Binding(first.Snapshot),
                "SharedToken",
                maxItems: 1));
            string cursor = Assert.IsType<string>(firstPage.NextCursor);
            Assert.Equal([1L], Assert.IsType<IndexStageReport>(secondReport).DeferredGenerationRevisions);
            Assert.Equal(1, store.RetainedIndexQueryLeaseCountForTest);

            executor.BeforeResponseSerializationTestHook = null;
            DocumentCollectionStore activeCollection = store.GetActiveDocumentCollectionForTest(
                first.Snapshot.WorkspaceId);
            long activeFullScansBefore = FullScanCount(activeCollection);
            TimeSpan halfRetention = TimeSpan.FromTicks(cursorLeaseRetention.Ticks / 2);
            timeProvider.Advance(halfRetention);
            McpToolResponse<CodeSearchItem> continued = Success(DispatchSearch(
                executor,
                Binding(secondSnapshot),
                "SharedToken",
                maxItems: 1,
                cursor));
            Assert.Equal(first.Snapshot.IndexRevision, continued.IndexRevision);
            cursor = Assert.IsType<string>(continued.NextCursor);
            Assert.Equal(1, store.RetainedIndexQueryLeaseCountForTest);

            timeProvider.Advance(halfRetention);
            Assert.Equal(0, store.RetainedIndexQueryLeaseCountForTest);
            McpError expired = Assert.IsType<McpError>(DispatchSearch(
                executor,
                Binding(secondSnapshot),
                "SharedToken",
                maxItems: 1,
                cursor).Error);

            Assert.Equal(McpErrorCodes.StaleRevision, expired.Code);
            Assert.Equal("query_cursor_stale", expired.Reason);
            Assert.Equal(secondSnapshot.IndexRevision, expired.CurrentRevision);
            Assert.Equal(oldFullScansBefore, FullScanCount(oldCollection));
            Assert.Equal(activeFullScansBefore, FullScanCount(activeCollection));

            DatabaseGenerationCleanupResult cleanup = store.CleanupRetired(
                first.Snapshot.WorkspaceId);
            Assert.Equal([1L], cleanup.RemovedRevisions);
            Assert.Empty(cleanup.DeferredRevisions);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task CodeSearch_CancelledContinuation_ReleasesLeaseAndRejectsReplayWithoutScan()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex published = await PublishAsync(workspaceRoot, database, 3);
            using var store = new SonnetDbIndexGenerationStore(database);
            DocumentCollectionStore collection = store.GetActiveDocumentCollectionForTest(
                published.Snapshot.WorkspaceId);
            long fullScansBefore = FullScanCount(collection);
            var executor = new SonnetDbMcpToolExecutor(store, 0);
            WorkspaceBinding binding = Binding(published.Snapshot);
            string cursor = Assert.IsType<string>(Success(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 1)).NextCursor);
            Assert.Equal(1, store.RetainedIndexQueryLeaseCountForTest);

            using var cancellation = new CancellationTokenSource();
            executor.BeforeResponseSerializationTestHook = cancellation.Cancel;
            McpDispatchResult cancelled = executor.Execute(
                new CodeSearchRequest
                {
                    Budget = new QueryBudget
                    {
                        MaxItems = 1,
                        MaxTokens = 65_536,
                        MaxBytes = 1_000_000,
                        DeadlineMs = 10_000,
                    },
                    Cursor = cursor,
                    Query = "SharedToken",
                    Mode = "fulltext",
                },
                binding,
                "query-cursor-cancel-test",
                cancellation.Token);

            McpError cancelledError = Assert.IsType<McpError>(cancelled.Error);
            Assert.Equal(McpErrorCodes.Cancelled, cancelledError.Code);
            Assert.Equal("client_cancelled", cancelledError.Reason);
            Assert.Equal(0, store.RetainedIndexQueryLeaseCountForTest);
            executor.BeforeResponseSerializationTestHook = null;

            McpError replay = Assert.IsType<McpError>(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 1,
                cursor).Error);
            Assert.Equal(McpErrorCodes.InvalidRequest, replay.Code);
            Assert.Equal("query_cursor_invalid", replay.Reason);
            Assert.Equal(fullScansBefore, FullScanCount(collection));
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task CodeSearch_RetiredContinuationFault_ReleasesLeaseAndAllowsCleanup()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex first = await PublishAsync(workspaceRoot, database, 3);
            using var store = new SonnetDbIndexGenerationStore(database);
            DocumentCollectionStore oldCollection = store.GetActiveDocumentCollectionForTest(
                first.Snapshot.WorkspaceId);
            long fullScansBefore = FullScanCount(oldCollection);
            string cursor = CreateSignedCursor(
                store,
                first.Snapshot.WorkspaceId,
                "SharedToken",
                offset: 0);
            WorkspaceIndexSnapshot secondSnapshot = await PublishReplacementAsync(
                workspaceRoot,
                store,
                first);
            Assert.Equal(1, store.RetainedIndexQueryLeaseCountForTest);

            var executor = new SonnetDbMcpToolExecutor(store, 0)
            {
                BeforeResponseSerializationTestHook = () =>
                    throw new IOException("controlled_query_response_fault"),
            };
            McpDispatchResult failed;
            try
            {
                failed = DispatchSearch(
                    executor,
                    Binding(secondSnapshot),
                    "SharedToken",
                    maxItems: 1,
                    cursor);
            }
            finally
            {
                executor.BeforeResponseSerializationTestHook = null;
            }

            McpError error = Assert.IsType<McpError>(failed.Error);
            Assert.Equal(McpErrorCodes.IndexCorrupt, error.Code);
            Assert.Equal("active_generation_validation_failed", error.Reason);
            Assert.Equal(0, store.RetainedIndexQueryLeaseCountForTest);
            Assert.Equal(fullScansBefore, FullScanCount(oldCollection));

            DatabaseGenerationCleanupResult cleanup = store.CleanupRetired(
                first.Snapshot.WorkspaceId);
            Assert.Equal([1L], cleanup.RemovedRevisions);
            Assert.Empty(cleanup.DeferredRevisions);

            McpError replay = Assert.IsType<McpError>(DispatchSearch(
                executor,
                Binding(secondSnapshot),
                "SharedToken",
                maxItems: 1,
                cursor).Error);
            Assert.Equal(McpErrorCodes.StaleRevision, replay.Code);
            Assert.Equal("query_cursor_stale", replay.Reason);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task Store_DisposeWithRetiredCursorLease_ReopenCanCleanupGeneration()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex first = await PublishAsync(workspaceRoot, database, 3);
            WorkspaceIndexSnapshot secondSnapshot;
            var store = new SonnetDbIndexGenerationStore(database);
            try
            {
                _ = CreateSignedCursor(
                    store,
                    first.Snapshot.WorkspaceId,
                    "SharedToken",
                    offset: 0);
                secondSnapshot = await PublishReplacementAsync(workspaceRoot, store, first);
                Assert.Equal(1, store.RetainedIndexQueryLeaseCountForTest);
                Assert.Equal(
                    [1L, 2L],
                    store.ListGenerationRevisionsForTest(first.Snapshot.WorkspaceId));
            }
            finally
            {
                store.Dispose();
            }

            using var reopened = new SonnetDbIndexGenerationStore(database);
            DatabaseGenerationCleanupResult cleanup = reopened.CleanupRetired(
                first.Snapshot.WorkspaceId);
            Assert.Equal([1L], cleanup.RemovedRevisions);
            Assert.Empty(cleanup.DeferredRevisions);
            Assert.Equal(
                [2L],
                reopened.ListGenerationRevisionsForTest(secondSnapshot.WorkspaceId));
            ActiveIndexPlanningSnapshot active = Assert.IsType<ActiveIndexPlanningSnapshot>(
                reopened.ReadActivePlanningSnapshot(secondSnapshot.WorkspaceId));
            Assert.Equal(secondSnapshot.IndexRevision, active.PlanningSnapshot.IndexRevision);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task CodeSearch_RetainedLeaseCapacity_BlocksNewFullTextButAllowsExactWithoutScan()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex published = await PublishAsync(workspaceRoot, database, 2);
            using var store = new SonnetDbIndexGenerationStore(database);
            DocumentCollectionStore collection = store.GetActiveDocumentCollectionForTest(
                published.Snapshot.WorkspaceId);
            long fullScansBefore = FullScanCount(collection);
            var retainedCursors = new List<string>();
            for (int index = 0;
                index < SonnetDbIndexGenerationStore.MaximumRetainedIndexQueryLeasesForTest;
                index++)
            {
                retainedCursors.Add(CreateSignedCursor(
                    store,
                    published.Snapshot.WorkspaceId,
                    "SharedToken",
                    index));
            }

            Assert.Equal(
                SonnetDbIndexGenerationStore.MaximumRetainedIndexQueryLeasesForTest,
                store.RetainedIndexQueryLeaseCountForTest);
            bool responseSerializationReached = false;
            var executor = new SonnetDbMcpToolExecutor(store, 0)
            {
                BeforeResponseSerializationTestHook = () => responseSerializationReached = true,
            };
            McpError capacity = Assert.IsType<McpError>(DispatchSearch(
                executor,
                Binding(published.Snapshot),
                "SharedToken",
                maxItems: 1).Error);

            Assert.Equal(McpErrorCodes.BudgetExhausted, capacity.Code);
            Assert.Equal("query_cursor_lease_capacity_exhausted", capacity.Reason);
            Assert.False(responseSerializationReached);
            Assert.Equal(
                SonnetDbIndexGenerationStore.MaximumRetainedIndexQueryLeasesForTest,
                store.RetainedIndexQueryLeaseCountForTest);
            Assert.Equal(fullScansBefore, FullScanCount(collection));

            string exactId = IndexStorageMapper.CreateDocuments(published.Snapshot)[0].StableId;
            McpToolResponse<CodeSearchItem> exact = Success(DispatchSearch(
                executor,
                Binding(published.Snapshot),
                exactId,
                maxItems: 1,
                mode: "exact"));

            Assert.Single(exact.Items);
            Assert.Equal(exactId, exact.Items[0].Id);
            Assert.Null(exact.NextCursor);
            Assert.True(responseSerializationReached);
            Assert.Equal(
                SonnetDbIndexGenerationStore.MaximumRetainedIndexQueryLeasesForTest,
                store.RetainedIndexQueryLeaseCountForTest);
            Assert.Equal(fullScansBefore, FullScanCount(collection));

            McpToolResponse<CodeSearchItem> finalPage = Success(DispatchSearch(
                executor,
                Binding(published.Snapshot),
                "SharedToken",
                maxItems: 100,
                retainedCursors[0]));
            Assert.Null(finalPage.NextCursor);
            Assert.False(finalPage.Truncated);
            Assert.Equal(
                SonnetDbIndexGenerationStore.MaximumRetainedIndexQueryLeasesForTest - 1,
                store.RetainedIndexQueryLeaseCountForTest);

            McpToolResponse<CodeSearchItem> replacementCursor = Success(DispatchSearch(
                executor,
                Binding(published.Snapshot),
                "SharedToken",
                maxItems: 1));
            Assert.NotNull(replacementCursor.NextCursor);
            Assert.Equal(
                SonnetDbIndexGenerationStore.MaximumRetainedIndexQueryLeasesForTest,
                store.RetainedIndexQueryLeaseCountForTest);
            Assert.Equal(fullScansBefore, FullScanCount(collection));
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public void Store_WithNegativeCursorLeaseRetention_FailsBeforeCreatingDatabase()
    {
        string database = Path.Combine(
            Path.GetTempPath(),
            "couplet-query-cursor-negative-" + Guid.NewGuid().ToString("N"));

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using var store = new SonnetDbIndexGenerationStore(
                database,
                TimeSpan.Zero,
                TimeProvider.System,
                TimeSpan.FromTicks(-1));
        });

        Assert.Equal("queryCursorLeaseRetention", exception.ParamName);
        Assert.False(Directory.Exists(database));
    }

    [Fact]
    public void Store_LegacyThreeParameterConstructor_RemainsCallableByClrSignature()
    {
        string database = TemporaryDirectory();
        try
        {
            System.Reflection.ConstructorInfo? legacyConstructor =
                typeof(SonnetDbIndexGenerationStore).GetConstructor(
                    [typeof(string), typeof(TimeSpan), typeof(TimeProvider)]);
            Assert.NotNull(legacyConstructor);
            System.Reflection.ParameterInfo timeProviderParameter = legacyConstructor.GetParameters()[2];
            Assert.True(timeProviderParameter.IsOptional);
            Assert.Null(timeProviderParameter.DefaultValue);
            Assert.NotNull(typeof(SonnetDbIndexGenerationStore).GetConstructor(
                [typeof(string), typeof(TimeSpan), typeof(TimeProvider), typeof(TimeSpan)]));

            using var store = Assert.IsType<SonnetDbIndexGenerationStore>(
                legacyConstructor.Invoke([database, TimeSpan.Zero, null]));
        }
        finally
        {
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task CodeSearch_SignedInvalidOrDeepOffset_FailsFastBeforeFullTextOrDocumentScan()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex published = await PublishAsync(workspaceRoot, database, 2);
            using var store = new SonnetDbIndexGenerationStore(database);
            DocumentCollectionStore collection = store.GetActiveDocumentCollectionForTest(
                published.Snapshot.WorkspaceId);
            long fullScansBefore = FullScanCount(collection);
            var executor = new SonnetDbMcpToolExecutor(store, 0);
            WorkspaceBinding binding = Binding(published.Snapshot);

            string negativeCursor = CreateSignedCursor(store, published.Snapshot.WorkspaceId, "SharedToken", -1);
            McpError negative = Assert.IsType<McpError>(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 1,
                negativeCursor).Error);
            Assert.Equal(McpErrorCodes.InvalidRequest, negative.Code);
            Assert.Equal("query_cursor_invalid", negative.Reason);

            string overflowCursor = CreateSignedCursor(
                store,
                published.Snapshot.WorkspaceId,
                "SharedToken",
                int.MaxValue);
            McpError overflow = Assert.IsType<McpError>(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 1,
                overflowCursor).Error);
            Assert.Equal(McpErrorCodes.InvalidRequest, overflow.Code);
            Assert.Equal("query_cursor_offset_out_of_range", overflow.Reason);

            string deepCursor = CreateSignedCursor(store, published.Snapshot.WorkspaceId, "SharedToken", 1_000_000);
            McpError deep = Assert.IsType<McpError>(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 1,
                deepCursor,
                maxTokens: 1000,
                maxBytes: 4096).Error);
            Assert.Equal(McpErrorCodes.BudgetExhausted, deep.Code);
            Assert.Equal("query_cursor_candidate_budget_exhausted", deep.Reason);

            McpError blank = Assert.IsType<McpError>(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 1,
                " ").Error);
            Assert.Equal(McpErrorCodes.InvalidRequest, blank.Code);
            Assert.Equal("query_cursor_invalid", blank.Reason);
            Assert.Equal(fullScansBefore, FullScanCount(collection));
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task CodeSearch_FullTextFilters_UseGenerationIndexesAndPageWithoutGapsOrDuplicates()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex published = await PublishFilteredFixtureAsync(workspaceRoot, database);
            using var store = new SonnetDbIndexGenerationStore(database);
            DocumentCollectionStore collection = store.GetActiveDocumentCollectionForTest(
                published.Snapshot.WorkspaceId);
            long fullScansBefore = FullScanCount(collection);
            var executor = new SonnetDbMcpToolExecutor(store, 0);
            WorkspaceBinding binding = Binding(published.Snapshot);
            McpToolResponse<CodeSearchItem> unfiltered = Success(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 100));
            Dictionary<string, IndexStorageDocument> documents = IndexStorageMapper.CreateDocuments(
                    published.Snapshot)
                .ToDictionary(document => document.StableId, StringComparer.Ordinal);
            string[] expected = unfiltered.Items
                .Where(item => documents[item.Id].Path.StartsWith("src/", StringComparison.Ordinal)
                    && documents[item.Id].Language == "csharp"
                    && documents[item.Id].EntityKind == CodeEntityKind.Chunk)
                .Select(item => item.Id)
                .ToArray();
            Assert.True(expected.Length > 1);

            var actual = new List<string>();
            string? cursor = null;
            do
            {
                bool retainedCursorLease = cursor is not null;
                McpToolResponse<CodeSearchItem> page = Success(DispatchSearch(
                    executor,
                    binding,
                    "SharedToken",
                    maxItems: 1,
                    cursor,
                    path: "src/*.cs",
                    language: "CSHARP",
                    kind: CodeEntityKind.Chunk));
                Assert.Equal(
                    (retainedCursorLease
                        ? "generation_retained_cursor_lease:"
                        : "generation_active_lease:")
                    + "document_fulltext_filtered:code_search:"
                    + "planning_snapshot_path_glob+document_path_index:by_path+"
                    + "document_path_index:by_language+"
                    + "document_path_index:by_entity_kind",
                    page.Diagnostics.AccessPath);
                Assert.Null(page.Diagnostics.FallbackReason);
                Assert.True(page.Diagnostics.Candidates >= page.Items.Count);
                Assert.True(page.Diagnostics.Examined > 0);
                actual.AddRange(page.Items.Select(item => item.Id));
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            Assert.Equal(expected, actual);
            Assert.Equal(actual.Count, actual.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(fullScansBefore, FullScanCount(collection));
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task CodeSearch_FullTextFilterCursor_BindsPathLanguageAndKind()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex published = await PublishFilteredFixtureAsync(workspaceRoot, database);
            using var store = new SonnetDbIndexGenerationStore(database);
            DocumentCollectionStore collection = store.GetActiveDocumentCollectionForTest(
                published.Snapshot.WorkspaceId);
            long fullScansBefore = FullScanCount(collection);
            var executor = new SonnetDbMcpToolExecutor(store, 0);
            WorkspaceBinding binding = Binding(published.Snapshot);
            string cursor = Assert.IsType<string>(Success(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 1,
                path: "src/*.cs",
                language: "csharp",
                kind: CodeEntityKind.Chunk)).NextCursor);

            McpDispatchResult changedPath = DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 1,
                cursor,
                path: "tests/*.cs",
                language: "csharp",
                kind: CodeEntityKind.Chunk);
            McpDispatchResult changedLanguage = DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 1,
                cursor,
                path: "src/*.cs",
                language: "typescript",
                kind: CodeEntityKind.Chunk);
            McpDispatchResult changedKind = DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 1,
                cursor,
                path: "src/*.cs",
                language: "csharp",
                kind: CodeEntityKind.Member);

            foreach (McpDispatchResult result in new[] { changedPath, changedLanguage, changedKind })
            {
                McpError error = Assert.IsType<McpError>(result.Error);
                Assert.Equal(McpErrorCodes.InvalidRequest, error.Code);
                Assert.Equal("query_cursor_invalid", error.Reason);
            }

            Assert.Equal(fullScansBefore, FullScanCount(collection));
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task CodeSearch_EachFullTextFilter_ReturnsTheCorrespondingIndexedSubset()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex published = await PublishFilteredFixtureAsync(workspaceRoot, database);
            using var store = new SonnetDbIndexGenerationStore(database);
            var executor = new SonnetDbMcpToolExecutor(store, 0);
            WorkspaceBinding binding = Binding(published.Snapshot);
            McpToolResponse<CodeSearchItem> unfiltered = Success(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 100));
            Dictionary<string, IndexStorageDocument> documents = IndexStorageMapper.CreateDocuments(
                    published.Snapshot)
                .ToDictionary(document => document.StableId, StringComparer.Ordinal);
            IndexStorageDocument chunk = documents.Values.First(
                document => document.EntityKind == CodeEntityKind.Chunk);
            CodeEntityKind chunkKind = Assert.IsType<CodeEntityKind>(chunk.EntityKind);
            using (JsonDocument serializedChunk = JsonDocument.Parse(CoupletJsonSerializer.Serialize(chunk)))
            {
                Assert.Equal(
                    chunkKind.ToString(),
                    serializedChunk.RootElement.GetProperty("entity_kind").GetString());
            }

            McpToolResponse<CodeSearchItem> byPath = Success(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 100,
                path: "web/*.ts"));
            McpToolResponse<CodeSearchItem> byLanguage = Success(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 100,
                language: "TYPESCRIPT"));
            McpToolResponse<CodeSearchItem> byKind = Success(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 100,
                kind: CodeEntityKind.Chunk));

            Assert.Equal(
                unfiltered.Items.Where(item => documents[item.Id].Path.StartsWith("web/", StringComparison.Ordinal))
                    .Select(item => item.Id),
                byPath.Items.Select(item => item.Id));
            Assert.Equal(
                unfiltered.Items.Where(item => documents[item.Id].Language == "typescript")
                    .Select(item => item.Id),
                byLanguage.Items.Select(item => item.Id));
            Assert.Equal(
                unfiltered.Items.Where(item => documents[item.Id].EntityKind == CodeEntityKind.Chunk)
                    .Select(item => item.Id),
                byKind.Items.Select(item => item.Id));
            Assert.EndsWith("document_path_index:by_path", byPath.Diagnostics.AccessPath, StringComparison.Ordinal);
            Assert.EndsWith("document_path_index:by_language", byLanguage.Diagnostics.AccessPath, StringComparison.Ordinal);
            Assert.EndsWith("document_path_index:by_entity_kind", byKind.Diagnostics.AccessPath, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task CodeSearch_FullTextFilterBudgetOrCancellation_FailsWithoutDocumentScan()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex published = await PublishFilteredFixtureAsync(workspaceRoot, database);
            using var store = new SonnetDbIndexGenerationStore(database);
            DocumentCollectionStore collection = store.GetActiveDocumentCollectionForTest(
                published.Snapshot.WorkspaceId);
            long fullScansBefore = FullScanCount(collection);
            var executor = new SonnetDbMcpToolExecutor(store, 0);
            WorkspaceBinding binding = Binding(published.Snapshot);

            McpError candidateBudget = Assert.IsType<McpError>(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 1,
                language: "csharp",
                maxTokens: 1,
                maxBytes: 4).Error);
            Assert.Equal(McpErrorCodes.BudgetExhausted, candidateBudget.Code);
            Assert.Equal("fulltext_filter_candidate_budget_exhausted", candidateBudget.Reason);

            McpError planningBudget = Assert.IsType<McpError>(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 1,
                path: "missing/*.cs",
                maxTokens: 1,
                maxBytes: 4).Error);
            Assert.Equal(McpErrorCodes.BudgetExhausted, planningBudget.Code);
            Assert.Equal("fulltext_path_planning_budget_exhausted", planningBudget.Reason);

            McpError postingBudget = Assert.IsType<McpError>(DispatchSearch(
                executor,
                binding,
                "SharedToken",
                maxItems: 1,
                path: "src/Sample0.cs",
                maxTokens: 3,
                maxBytes: 12).Error);
            Assert.Equal(McpErrorCodes.BudgetExhausted, postingBudget.Code);
            Assert.Equal("fulltext_posting_budget_exhausted", postingBudget.Reason);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            McpDispatchResult cancelled = executor.Execute(
                new CodeSearchRequest
                {
                    Budget = new QueryBudget
                    {
                        MaxItems = 1,
                        MaxTokens = 1000,
                        MaxBytes = 4096,
                        DeadlineMs = 10_000,
                    },
                    Query = "SharedToken",
                    Mode = "fulltext",
                    Path = "src/*.cs",
                },
                binding,
                "query-filter-cancel-test",
                cancellation.Token);
            McpError cancelledError = Assert.IsType<McpError>(cancelled.Error);
            Assert.Equal(McpErrorCodes.Cancelled, cancelledError.Code);
            Assert.Equal("client_cancelled", cancelledError.Reason);
            Assert.Equal(fullScansBefore, FullScanCount(collection));
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    private static McpDispatchResult DispatchSearch(
        SonnetDbMcpToolExecutor executor,
        WorkspaceBinding binding,
        string query,
        int maxItems,
        string? cursor = null,
        int maxTokens = 65_536,
        int maxBytes = 1_000_000,
        string? path = null,
        string? language = null,
        CodeEntityKind? kind = null,
        string mode = "fulltext") => executor.Execute(
            new CodeSearchRequest
            {
                Budget = new QueryBudget
                {
                    MaxItems = maxItems,
                    MaxTokens = maxTokens,
                    MaxBytes = maxBytes,
                    DeadlineMs = 10_000,
                },
                Cursor = cursor,
                Query = query,
                Mode = mode,
                Path = path,
                Language = language,
                Kind = kind,
            },
            binding,
            "query-cursor-test",
            CancellationToken.None);

    private static string CreateSignedCursor(
        SonnetDbIndexGenerationStore store,
        string workspaceId,
        string query,
        long offset)
    {
        string queryFingerprint = CodeSearchFingerprint(query);
        using IndexQueryRequestLease requestLease = store.AcquireIndexQuery(
            workspaceId,
            cursor: null,
            queryFingerprint,
            reserveQueryLeaseSlot: true);
        Span<byte> state = stackalloc byte[sizeof(long) + 16];
        BinaryPrimitives.WriteInt64LittleEndian(state, offset);
        _ = Guid.NewGuid().TryWriteBytes(state[sizeof(long)..]);
        string cursor = requestLease.Lease.CreateCursor(queryFingerprint, state);
        Assert.Equal(
            IndexQueryCursorRetentionResult.Retained,
            requestLease.TryRetain(cursor, queryFingerprint));
        return cursor;
    }

    private static string CodeSearchFingerprint(string query)
    {
        var canonical = new StringBuilder("couplet.code_search.cursor.v1");
        AppendPart(canonical, "fulltext");
        AppendPart(canonical, query);
        AppendPart(canonical, null);
        AppendPart(canonical, null);
        AppendPart(canonical, null);
        AppendPart(canonical, null);
        return "code-search:v1:" + CursorCodec.HashRequest(canonical.ToString());
    }

    private static void AppendPart(StringBuilder canonical, string? value)
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

    private static McpToolResponse<CodeSearchItem> Success(McpDispatchResult result)
    {
        Assert.Null(result.Error);
        return Assert.IsType<McpToolResponse<CodeSearchItem>>(result.CodeSearch);
    }

    private static async Task<PublishedIndex> PublishAsync(
        string workspaceRoot,
        string database,
        int fileCount)
    {
        for (int index = 0; index < fileCount; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspaceRoot, $"Sample{index}.cs"),
                $"public class Sample{index} {{ public string SharedToken{index}() => \"SharedToken\"; }}");
        }

        DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
            workspaceRoot,
            WorkspaceDiscoveryService.DefaultPolicy);
        WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(discovered, null);
        using var store = new SonnetDbIndexGenerationStore(database);
        IndexStageReport report = store.StageAndPublish(
            snapshot,
            IncrementalIndexPlanner.Plan(null, snapshot),
            0);
        Assert.True(report.Published);
        return new PublishedIndex(snapshot, report);
    }

    private static async Task<WorkspaceIndexSnapshot> PublishReplacementAsync(
        string workspaceRoot,
        SonnetDbIndexGenerationStore store,
        PublishedIndex first)
    {
        await File.WriteAllTextAsync(
            Path.Combine(workspaceRoot, "Sample0.cs"),
            "public class Sample0 { public string Replacement() => \"replacement\"; }");
        DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
            workspaceRoot,
            WorkspaceDiscoveryService.DefaultPolicy);
        WorkspaceIndexSnapshot secondSnapshot = await IndexSnapshotBuilder.BuildAsync(
            discovered,
            first.Snapshot.IndexRevision);
        IndexPlanningSnapshot previous = Assert.IsType<ActiveIndexPlanningSnapshot>(
            store.ReadActivePlanningSnapshot(first.Snapshot.WorkspaceId)).PlanningSnapshot;
        IncrementalIndexPlan plan = IncrementalIndexPlanner.PlanFromPublished(
            previous,
            secondSnapshot);
        IndexStageReport report = store.StageAndPublish(
            secondSnapshot,
            plan,
            first.Report.DatabaseGenerationRevision!.Value);
        Assert.True(report.Published);
        Assert.Equal([1L], report.DeferredGenerationRevisions);
        return secondSnapshot;
    }

    private static async Task<PublishedIndex> PublishFilteredFixtureAsync(
        string workspaceRoot,
        string database)
    {
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "src"));
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "web"));
        for (int index = 0; index < 3; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspaceRoot, "src", $"Sample{index}.cs"),
                $"public class Sample{index} {{ public string SharedToken() => \"SharedToken\"; }}");
        }

        for (int index = 0; index < 2; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspaceRoot, "web", $"sample{index}.ts"),
                $"export function SharedToken{index}() {{ return 'SharedToken'; }}");
        }

        DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
            workspaceRoot,
            WorkspaceDiscoveryService.DefaultPolicy);
        WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(discovered, null);
        using var store = new SonnetDbIndexGenerationStore(database);
        IndexStageReport report = store.StageAndPublish(
            snapshot,
            IncrementalIndexPlanner.Plan(null, snapshot),
            0);
        Assert.True(report.Published);
        return new PublishedIndex(snapshot, report);
    }

    private static WorkspaceBinding Binding(WorkspaceIndexSnapshot snapshot) => new()
    {
        WorkspaceId = snapshot.WorkspaceId,
        RepositoryIdentity = snapshot.RepositoryIdentity!,
        SourceRevision = snapshot.SourceRevision,
        IndexRevision = snapshot.IndexRevision,
    };

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "couplet-query-cursor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static long FullScanCount(DocumentCollectionStore collection)
    {
        System.Reflection.PropertyInfo property = typeof(DocumentCollectionStore).GetProperty(
                "FullScanCount",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SonnetDB full-scan counter is unavailable.");
        return Assert.IsType<long>(property.GetValue(collection));
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(entry, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private sealed record PublishedIndex(
        WorkspaceIndexSnapshot Snapshot,
        IndexStageReport Report);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _sync = new();
        private DateTimeOffset _utcNow = utcNow;
        private ManualTimer? _timer;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _utcNow;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            ValidateTimerInterval(dueTime, nameof(dueTime));
            ValidateTimerInterval(period, nameof(period));
            lock (_sync)
            {
                if (_timer is not null)
                {
                    throw new InvalidOperationException("The test provider supports one timer.");
                }

                var timer = new ManualTimer(this, callback, state);
                timer.ChangeUnsafe(_utcNow, dueTime, period);
                _timer = timer;
                return timer;
            }
        }

        internal void Advance(TimeSpan amount)
        {
            if (amount < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "Advance cannot be negative.");
            }

            lock (_sync)
            {
                _utcNow += amount;
            }

            while (true)
            {
                TimerCallback callback;
                object? state;
                lock (_sync)
                {
                    if (_timer is null
                        || !_timer.TryTakeDueUnsafe(_utcNow, out callback, out state))
                    {
                        return;
                    }
                }

                callback(state);
            }
        }

        private bool Change(
            ManualTimer timer,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ValidateTimerInterval(dueTime, nameof(dueTime));
            ValidateTimerInterval(period, nameof(period));
            lock (_sync)
            {
                if (!ReferenceEquals(_timer, timer) || timer.IsDisposedUnsafe)
                {
                    return false;
                }

                timer.ChangeUnsafe(_utcNow, dueTime, period);
                return true;
            }
        }

        private void Dispose(ManualTimer timer)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_timer, timer))
                {
                    timer.DisposeUnsafe();
                }
            }
        }

        private static void ValidateTimerInterval(TimeSpan value, string parameterName)
        {
            if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(parameterName, value, "Invalid timer interval.");
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private DateTimeOffset? _dueAtUtc;
            private TimeSpan _period;

            internal bool IsDisposedUnsafe { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                owner.Change(this, dueTime, period);

            public void Dispose() => owner.Dispose(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            internal void ChangeUnsafe(
                DateTimeOffset nowUtc,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _period = period;
                _dueAtUtc = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : nowUtc + dueTime;
            }

            internal bool TryTakeDueUnsafe(
                DateTimeOffset nowUtc,
                out TimerCallback dueCallback,
                out object? dueState)
            {
                dueCallback = callback;
                dueState = state;
                if (IsDisposedUnsafe || _dueAtUtc is null || _dueAtUtc > nowUtc)
                {
                    return false;
                }

                _dueAtUtc = _period <= TimeSpan.Zero || _period == Timeout.InfiniteTimeSpan
                    ? null
                    : nowUtc + _period;
                return true;
            }

            internal void DisposeUnsafe()
            {
                IsDisposedUnsafe = true;
                _dueAtUtc = null;
            }
        }
    }
}
#endif
