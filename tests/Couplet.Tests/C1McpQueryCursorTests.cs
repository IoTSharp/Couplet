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
                McpToolResponse<CodeSearchItem> page = Success(DispatchSearch(
                    executor,
                    binding,
                    "SharedToken",
                    maxItems: 2,
                    cursor));
                Assert.Equal(published.Snapshot.IndexRevision, page.IndexRevision);
                Assert.Equal(
                    "generation_active_lease:document_fulltext:code_search",
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
            Assert.Equal(fullScansBefore, FullScanCount(collection));
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task CodeSearch_PublishDuringFirstPage_KeepsRequestLeaseThenRejectsRetiredCursorAsStale()
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
            IndexStageReport? secondReport = null;
            DatabaseGenerationCleanupResult? cleanupWhileLeased = null;
            var executor = new SonnetDbMcpToolExecutor(store, 0)
            {
                BeforeResponseSerializationTestHook = () =>
                {
                    secondReport = store.StageAndPublish(
                        secondSnapshot,
                        secondPlan,
                        first.Report.DatabaseGenerationRevision!.Value);
                    cleanupWhileLeased = store.CleanupRetired(first.Snapshot.WorkspaceId);
                },
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

            executor.BeforeResponseSerializationTestHook = null;
            DocumentCollectionStore activeCollection = store.GetActiveDocumentCollectionForTest(
                first.Snapshot.WorkspaceId);
            long activeFullScansBefore = FullScanCount(activeCollection);
            McpError stale = Assert.IsType<McpError>(DispatchSearch(
                executor,
                Binding(secondSnapshot),
                "SharedToken",
                maxItems: 1,
                retiredCursor).Error);
            Assert.Equal(McpErrorCodes.StaleRevision, stale.Code);
            Assert.Equal("query_cursor_stale", stale.Reason);
            Assert.Equal(secondSnapshot.IndexRevision, stale.CurrentRevision);
            Assert.Equal(activeFullScansBefore, FullScanCount(activeCollection));

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
                    "generation_active_lease:document_fulltext_filtered:code_search:"
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
        CodeEntityKind? kind = null) => executor.Execute(
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
                Mode = "fulltext",
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
        using ActiveIndexQueryLease lease = store.AcquireActiveIndexQuery(workspaceId);
        Span<byte> state = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(state, offset);
        return lease.CreateCursor(CodeSearchFingerprint(query), state);
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
}
#endif
