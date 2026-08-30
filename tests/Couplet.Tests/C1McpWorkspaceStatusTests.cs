#if COUPLET_SONNETDB_SOURCE_GENERATIONS
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Couplet.Application.Indexing;
using Couplet.Application.Mcp;
using Couplet.Application.Serialization;
using Couplet.Application.Workspaces;
using Couplet.Core.Capabilities;
using Couplet.Core.Indexing;
using Couplet.Core.Languages;
using Couplet.Core.Mcp;
using Couplet.Infrastructure.SonnetDb;
using SonnetDB.Engine;
using SonnetDB.Generations;
using SonnetDB.Documents;

namespace Couplet.Tests;

public sealed class C1McpWorkspaceStatusTests
{
    [Fact]
    public void WorkspaceStatus_EmptyDatabase_ReturnsIndexNotReadyForActiveQueries()
    {
        string database = TemporaryDirectory();
        try
        {
            using var store = new SonnetDbIndexGenerationStore(database);
            WorkspaceBinding binding = Binding("workspace-empty", "repository-empty", "source-empty", null);
            var executor = new SonnetDbMcpToolExecutor(store, 0);

            McpDispatchResult status = DispatchStatus(executor, binding);
            McpError error = Assert.IsType<McpError>(status.Error);
            Assert.Equal(McpErrorCodes.IndexNotReady, error.Code);
            Assert.Equal("active_generation_not_published", error.Reason);
            Assert.Null(error.CurrentRevision);

            using JsonDocument arguments = JsonDocument.Parse("""
                {"protocol_version":"1","budget":{"max_items":20,"max_tokens":1000,"max_bytes":4096,"deadline_ms":1000},"query":"Sample","mode":"fulltext"}
                """);
            McpDispatchResult search = McpToolContractDispatcher.Dispatch(
                McpToolNames.CodeSearch,
                arguments.RootElement,
                binding,
                "search-empty",
                executor,
                CancellationToken.None,
                McpWorkspaceBinder.CreateC1Capabilities());
            McpError searchError = Assert.IsType<McpError>(search.Error);
            Assert.Equal(McpErrorCodes.IndexNotReady, searchError.Code);
            Assert.Equal("active_generation_not_published", searchError.Reason);
        }
        finally
        {
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task WorkspaceStatus_PublishedGeneration_UsesLeaseWithoutDocumentScanAndSerializesTypedSuccess()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex published = await PublishFirstAsync(workspaceRoot, database);
            using var store = new SonnetDbIndexGenerationStore(database);
            DocumentCollectionStore collection = store.GetActiveDocumentCollectionForTest(
                published.Snapshot.WorkspaceId);
            long fullScansBefore = FullScanCount(collection);
            var executor = new SonnetDbMcpToolExecutor(
                store,
                SonnetDbMcpToolExecutor.SampleDatabaseBytes(database, CancellationToken.None));
            WorkspaceBinding binding = Binding(published.Snapshot);

            McpDispatchResult result = DispatchStatus(executor, binding);

            Assert.Null(result.Error);
            McpToolResponse<WorkspaceStatusItem> response = Assert.IsType<McpToolResponse<WorkspaceStatusItem>>(
                result.WorkspaceStatus);
            WorkspaceStatusItem item = Assert.Single(response.Items);
            Assert.Equal(published.Snapshot.SourceRevision, response.SourceRevision);
            Assert.Equal(published.Snapshot.IndexRevision, response.IndexRevision);
            Assert.Equal(published.Report.Manifest.Counts.Files, item.Files);
            Assert.Equal(published.Report.Manifest.Counts.Symbols, item.Symbols);
            Assert.Equal(published.Report.Manifest.Counts.Chunks, item.Chunks);
            Assert.Equal(["CG-005"], item.BlockingGaps);
            Assert.False(item.RebuildRequired);
            Assert.Equal("current", response.Freshness.IndexState);
            Assert.Equal("source_revision_sampled_at_mcp_startup", response.Freshness.Reason);
            Assert.Equal(
                "database_bytes_and_source_revision_sampled_at_mcp_startup",
                response.Diagnostics.FallbackReason);
            Assert.Equal(
                fullScansBefore,
                FullScanCount(collection));
            Assert.All(
                response.Capabilities.Where(capability => capability.Id is "exact" or "fulltext"),
                capability => Assert.Equal("preview", capability.Level));

            var host = new McpProtocolHost(
                binding,
                executor,
                WorkspaceDatabaseState.Current,
                McpWorkspaceBinder.CreateC1Capabilities());
            string json = host.ProcessLine(ToolCall(McpToolNames.WorkspaceStatus, StatusArguments()), CancellationToken.None)!;
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement toolResult = document.RootElement.GetProperty("result");
            Assert.False(toolResult.GetProperty("isError").GetBoolean());
            JsonElement structured = toolResult.GetProperty("structuredContent");
            Assert.Equal(published.Snapshot.IndexRevision, structured.GetProperty("index_revision").GetString());
            Assert.Equal(1, structured.GetProperty("items").GetArrayLength());
            string contentJson = toolResult.GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.Equal(structured.GetRawText(), contentJson);
            Assert.Equal(
                Encoding.UTF8.GetByteCount(contentJson),
                structured.GetProperty("diagnostics").GetProperty("consumed_bytes").GetInt32());
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task CodeSearch_ExactStableId_UsesActiveGenerationPathIndexWithoutDocumentScan()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex published = await PublishFirstAsync(workspaceRoot, database);
            IndexedSymbol symbol = Assert.Single(
                published.Snapshot.Files.Single().Symbols,
                candidate => candidate.DisplayName == "Original");
            using var store = new SonnetDbIndexGenerationStore(database);
            DocumentCollectionStore collection = store.GetActiveDocumentCollectionForTest(
                published.Snapshot.WorkspaceId);
            long fullScansBefore = FullScanCount(collection);
            var executor = new SonnetDbMcpToolExecutor(store, 0);
            using JsonDocument arguments = JsonDocument.Parse(
                "{\"protocol_version\":\"1\",\"budget\":{\"max_items\":20,\"max_tokens\":1000,\"max_bytes\":65536,\"deadline_ms\":10000},\"query\":\""
                + symbol.Id
                + "\",\"mode\":\"exact\"}");

            McpDispatchResult result = McpToolContractDispatcher.Dispatch(
                McpToolNames.CodeSearch,
                arguments.RootElement,
                Binding(published.Snapshot),
                "search-exact",
                executor,
                CancellationToken.None,
                McpWorkspaceBinder.CreateC1Capabilities());

            Assert.Null(result.Error);
            McpToolResponse<CodeSearchItem> response = Assert.IsType<McpToolResponse<CodeSearchItem>>(
                result.CodeSearch);
            CodeSearchItem item = Assert.Single(response.Items);
            Evidence evidence = Assert.Single(response.Evidence);
            Assert.Equal(symbol.Id, item.Id);
            Assert.Equal("member", item.Kind);
            Assert.Equal([evidence.Id], item.EvidenceIds);
            Assert.Equal(symbol.Definition.Path, evidence.Span!.Path);
            Assert.Equal(published.Snapshot.IndexRevision, evidence.IndexRevision);
            Assert.Equal(
                "generation_active_lease:document_path_index:by_stable_id",
                response.Diagnostics.AccessPath);
            Assert.Equal(1, response.Diagnostics.Candidates);
            Assert.Equal(1, response.Diagnostics.Examined);
            Assert.Equal(1, response.Diagnostics.Returned);
            Assert.False(response.Truncated);
            Assert.Null(response.NextCursor);
            Assert.Equal(fullScansBefore, FullScanCount(collection));
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task SymbolGet_StableOrQualifiedIdentity_UsesActiveIndexesAndEnforcesRequestGuards()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex published = await PublishFirstAsync(workspaceRoot, database);
            IndexedSymbol symbol = Assert.Single(
                published.Snapshot.Files.Single().Symbols,
                candidate => candidate.DisplayName == "Original");
            using var store = new SonnetDbIndexGenerationStore(database);
            DocumentCollectionStore collection = store.GetActiveDocumentCollectionForTest(
                published.Snapshot.WorkspaceId);
            long fullScansBefore = FullScanCount(collection);
            var executor = new SonnetDbMcpToolExecutor(store, 0);
            WorkspaceBinding binding = Binding(published.Snapshot);

            McpDispatchResult byId = DispatchSymbol(
                executor,
                binding,
                symbolId: symbol.Id);
            Assert.Null(byId.Error);
            McpToolResponse<SymbolDetailsItem> idResponse = Assert.IsType<McpToolResponse<SymbolDetailsItem>>(
                byId.SymbolDetails);
            SymbolDetailsItem item = Assert.Single(idResponse.Items);
            Evidence evidence = Assert.Single(idResponse.Evidence);
            Assert.Equal(symbol.Id, item.Id);
            Assert.Equal(symbol.QualifiedIdentity, item.QualifiedIdentity);
            Assert.Equal(symbol.Signature, item.Signature);
            Assert.Equal(symbol.Confidence.Kind, item.Confidence.Kind);
            Assert.Equal([evidence.Id], item.EvidenceIds);
            Assert.Equal(symbol.Definition.Path, evidence.Span!.Path);
            Assert.Equal(published.Snapshot.IndexRevision, evidence.IndexRevision);
            Assert.Equal(
                "generation_active_lease:document_path_index:by_stable_id",
                idResponse.Diagnostics.AccessPath);

            McpToolResponse<SymbolDetailsItem> qualifiedResponse = Assert.IsType<McpToolResponse<SymbolDetailsItem>>(
                DispatchSymbol(
                    executor,
                    binding,
                    qualifiedIdentity: symbol.QualifiedIdentity,
                    language: symbol.Language).SymbolDetails);
            Assert.Equal(symbol.Id, Assert.Single(qualifiedResponse.Items).Id);
            Assert.Equal(
                "generation_active_lease:document_path_index:by_stable_id:qualified_identity_language",
                qualifiedResponse.Diagnostics.AccessPath);

            McpToolResponse<SymbolDetailsItem> unqualifiedLanguageResponse = Assert.IsType<McpToolResponse<SymbolDetailsItem>>(
                DispatchSymbol(
                    executor,
                    binding,
                    qualifiedIdentity: symbol.QualifiedIdentity).SymbolDetails);
            Assert.Equal(symbol.Id, Assert.Single(unqualifiedLanguageResponse.Items).Id);
            Assert.Equal(
                "generation_active_lease:document_path_index:by_qualified_identity",
                unqualifiedLanguageResponse.Diagnostics.AccessPath);

            McpToolResponse<SymbolDetailsItem> languageMismatch = Assert.IsType<McpToolResponse<SymbolDetailsItem>>(
                DispatchSymbol(
                    executor,
                    binding,
                    symbolId: symbol.Id,
                    language: "typescript").SymbolDetails);
            Assert.Empty(languageMismatch.Items);
            Assert.Empty(languageMismatch.Evidence);

            McpError nonSymbol = Assert.IsType<McpError>(DispatchSymbol(
                executor,
                binding,
                symbolId: published.Snapshot.Files.Single().Id).Error);
            Assert.Equal(McpErrorCodes.InvalidRequest, nonSymbol.Code);
            Assert.Equal("symbol_id_not_symbol", nonSymbol.Reason);

            McpError stale = Assert.IsType<McpError>(DispatchSymbol(
                executor,
                binding,
                symbolId: symbol.Id,
                revisionKind: "index",
                revisionValue: "stale-index").Error);
            Assert.Equal(McpErrorCodes.StaleRevision, stale.Code);

            McpError budget = Assert.IsType<McpError>(DispatchSymbol(
                executor,
                binding,
                symbolId: symbol.Id,
                maxBytes: 1).Error);
            Assert.Equal(McpErrorCodes.BudgetExhausted, budget.Code);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            McpError cancelled = Assert.IsType<McpError>(DispatchSymbol(
                executor,
                binding,
                symbolId: symbol.Id,
                cancellationToken: cancellation.Token).Error);
            Assert.Equal(McpErrorCodes.Cancelled, cancelled.Code);

            executor.BeforeResponseSerializationTestHook = () => Thread.Sleep(TimeSpan.FromMilliseconds(20));
            McpError deadline = Assert.IsType<McpError>(DispatchSymbol(
                executor,
                binding,
                symbolId: symbol.Id,
                deadlineMilliseconds: 5).Error);
            Assert.Equal(McpErrorCodes.DeadlineExceeded, deadline.Code);
            Assert.Equal(fullScansBefore, FullScanCount(collection));
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task SymbolGet_QualifiedIdentityWithoutLanguage_WhenCrossLanguageAmbiguous_FailsClosed()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspaceRoot, "Shared.cs"),
                "public class Shared { }");
            await File.WriteAllTextAsync(
                Path.Combine(workspaceRoot, "Shared.ts"),
                "export class Shared { }");
            DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                workspaceRoot,
                WorkspaceDiscoveryService.DefaultPolicy);
            WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(discovered, null);
            IGrouping<string, IndexedSymbol> ambiguous = Assert.Single(
                snapshot.Files
                    .SelectMany(file => file.Symbols)
                    .GroupBy(symbol => symbol.QualifiedIdentity, StringComparer.Ordinal),
                group => group.Select(symbol => symbol.Language).Distinct(StringComparer.Ordinal).Count() > 1);
            using var store = new SonnetDbIndexGenerationStore(database);
            IndexStageReport report = store.StageAndPublish(
                snapshot,
                IncrementalIndexPlanner.Plan(null, snapshot),
                0);
            Assert.True(report.Published);
            DocumentCollectionStore collection = store.GetActiveDocumentCollectionForTest(snapshot.WorkspaceId);
            long fullScansBefore = FullScanCount(collection);
            var executor = new SonnetDbMcpToolExecutor(store, 0);

            McpError error = Assert.IsType<McpError>(DispatchSymbol(
                executor,
                Binding(snapshot),
                qualifiedIdentity: ambiguous.Key).Error);

            Assert.Equal(McpErrorCodes.InvalidRequest, error.Code);
            Assert.Equal("qualified_identity_ambiguous", error.Reason);
            Assert.Equal(snapshot.IndexRevision, error.CurrentRevision);
            Assert.Equal(fullScansBefore, FullScanCount(collection));
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public void SampleDatabaseBytes_PreCancelled_StopsBeforeScanning()
    {
        string database = TemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(database, "data.bin"), "payload");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            OperationCanceledException exception = Assert.Throws<OperationCanceledException>(() =>
                SonnetDbMcpToolExecutor.SampleDatabaseBytes(database, cancellation.Token));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }
        finally
        {
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public void SampleDatabaseBytes_CancelledDuringEntryScan_StopsImmediately()
    {
        string database = TemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(database, "first.bin"), "first");
            File.WriteAllText(Path.Combine(database, "second.bin"), "second");
            using var cancellation = new CancellationTokenSource();
            int visitedEntries = 0;

            OperationCanceledException exception = Assert.Throws<OperationCanceledException>(() =>
                SonnetDbMcpToolExecutor.SampleDatabaseBytes(
                    database,
                    cancellation.Token,
                    _ =>
                    {
                        visitedEntries++;
                        cancellation.Cancel();
                    }));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Equal(1, visitedEntries);
        }
        finally
        {
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task WorkspaceStatus_AfterNewPublish_RejectsOldSelectorAndReopenReadsOnlyNewRevision()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex first = await PublishFirstAsync(workspaceRoot, database);
            WorkspaceIndexSnapshot secondSnapshot;
            using (var store = new SonnetDbIndexGenerationStore(database))
            {
                ActiveIndexPlanningSnapshot previous = Assert.IsType<ActiveIndexPlanningSnapshot>(
                    store.ReadActivePlanningSnapshot(first.Snapshot.WorkspaceId));
                await File.WriteAllTextAsync(
                    Path.Combine(workspaceRoot, "Sample.cs"),
                    "public class Sample { public int Updated() => 2; }");
                DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                    workspaceRoot,
                    WorkspaceDiscoveryService.DefaultPolicy);
                secondSnapshot = await IndexSnapshotBuilder.BuildAsync(
                    discovered,
                    previous.PlanningSnapshot.IndexRevision);
                IncrementalIndexPlan secondPlan = IncrementalIndexPlanner.PlanFromPublished(
                    previous.PlanningSnapshot,
                    secondSnapshot);
                IndexStageReport second = store.StageAndPublish(
                    secondSnapshot,
                    secondPlan,
                    previous.DatabaseGenerationRevision);
                Assert.True(second.Published);

                var executor = new SonnetDbMcpToolExecutor(store, 0);
                McpDispatchResult stale = DispatchStatus(
                    executor,
                    Binding(first.Snapshot),
                    revisionKind: "index",
                    revisionValue: first.Snapshot.IndexRevision);
                McpError staleError = Assert.IsType<McpError>(stale.Error);
                Assert.Equal(McpErrorCodes.StaleRevision, staleError.Code);
                Assert.Equal(secondSnapshot.IndexRevision, staleError.CurrentRevision);

                McpToolResponse<WorkspaceStatusItem> latest = Assert.IsType<McpToolResponse<WorkspaceStatusItem>>(
                    DispatchStatus(executor, Binding(first.Snapshot)).WorkspaceStatus);
                Assert.Equal(secondSnapshot.IndexRevision, latest.IndexRevision);
                Assert.Equal("stale", latest.Freshness.IndexState);
                Assert.True(Assert.Single(latest.Items).RebuildRequired);
            }

            using (var reopened = new SonnetDbIndexGenerationStore(database))
            {
                var executor = new SonnetDbMcpToolExecutor(reopened, 0);
                McpToolResponse<WorkspaceStatusItem> response = Assert.IsType<McpToolResponse<WorkspaceStatusItem>>(
                    DispatchStatus(executor, Binding(secondSnapshot)).WorkspaceStatus);
                Assert.Equal(secondSnapshot.IndexRevision, response.IndexRevision);
                Assert.Equal("current", response.Freshness.IndexState);
                Assert.False(Assert.Single(response.Items).RebuildRequired);
            }
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task WorkspaceStatus_CorruptPublishedManifest_FailsClosedWithoutPayloadDetails()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex published = await PublishFirstAsync(workspaceRoot, database);
            string planningKeyspace;
            using (var store = new SonnetDbIndexGenerationStore(database))
            using (DatabaseGenerationQueryLease lease = store.AcquireActiveGeneration(
                       published.Snapshot.WorkspaceId))
            {
                planningKeyspace = lease.GetRequiredResource(
                    "index_planning",
                    DatabaseGenerationResourceKind.KvKeyspace).Name;
            }

            using (Tsdb databaseHandle = Tsdb.Open(new TsdbOptions { RootDirectory = database }))
            {
                var keyspace = databaseHandle.Keyspaces.Open(planningKeyspace);
                keyspace.Put("generation_manifest", Encoding.UTF8.GetBytes("{}"));
                keyspace.CreateSnapshot();
            }

            using var reopened = new SonnetDbIndexGenerationStore(database);
            var executor = new SonnetDbMcpToolExecutor(reopened, 0);
            McpDispatchResult result = DispatchStatus(executor, Binding(published.Snapshot));

            McpError error = Assert.IsType<McpError>(result.Error);
            Assert.Equal(McpErrorCodes.IndexCorrupt, error.Code);
            Assert.Equal("active_generation_validation_failed", error.Reason);
            Assert.DoesNotContain(
                database,
                CoupletJsonSerializer.Serialize(error),
                StringComparison.OrdinalIgnoreCase);
            Assert.Null(result.WorkspaceStatus);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task McpRuntime_WithExplicitDatabase_KeepsStoreAliveForCompleteHostSession()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex published = await PublishFirstAsync(workspaceRoot, database);
            IndexedSymbol publishedSymbol = Assert.Single(
                published.Snapshot.Files.Single().Symbols,
                candidate => candidate.DisplayName == "Original");
            string initialize = "{\"jsonrpc\":\"2.0\",\"id\":\"init\",\"method\":\"initialize\","
                + "\"params\":{\"protocolVersion\":\"2025-06-18\",\"clientInfo\":{\"name\":\"Codex\",\"version\":\"1\"},\"capabilities\":{}}}";
            string codeSearch = ToolCall(
                McpToolNames.CodeSearch,
                "{\"protocol_version\":\"1\",\"budget\":{\"max_items\":20,\"max_tokens\":1000,\"max_bytes\":4096,\"deadline_ms\":1000},\"query\":\"Sample\",\"mode\":\"fulltext\"}");
            string symbolGet = ToolCall(
                McpToolNames.SymbolGet,
                "{\"protocol_version\":\"1\",\"budget\":{\"max_items\":20,\"max_tokens\":1000,\"max_bytes\":4096,\"deadline_ms\":1000},\"symbol_id\":"
                + JsonString(publishedSymbol.Id)
                + "}");
            using var input = new StringReader(
                initialize
                + Environment.NewLine
                + ToolCall(McpToolNames.WorkspaceStatus, StatusArguments())
                + Environment.NewLine
                + codeSearch
                + Environment.NewLine
                + symbolGet
                + Environment.NewLine);
            using var output = new StringWriter();
            using var error = new StringWriter();

            int exitCode = await CoupletRuntime.RunAsync(
                ComponentKind.McpServer,
                ["serve", "--workspace", workspaceRoot, "--database", database],
                input,
                output,
                error,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            string[] responses = output.ToString().Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(4, responses.Length);
            using (JsonDocument initializeResponse = JsonDocument.Parse(responses[0]))
            {
                JsonElement couplet = initializeResponse.RootElement
                    .GetProperty("result")
                    .GetProperty("couplet");
                Assert.Equal(
                    published.Snapshot.WorkspaceId,
                    couplet.GetProperty("binding").GetProperty("workspace_id").GetString());
                Assert.All(
                    couplet.GetProperty("capabilities")
                        .EnumerateArray()
                        .Where(capability => capability.GetProperty("id").GetString() is "exact" or "fulltext"),
                    capability =>
                    {
                        Assert.Equal("preview", capability.GetProperty("level").GetString());
                        Assert.Equal(
                            "active_generation_query_connected",
                            capability.GetProperty("reason").GetString());
                    });
            }

            using (JsonDocument statusResponse = JsonDocument.Parse(responses[1]))
            {
                JsonElement result = statusResponse.RootElement.GetProperty("result");
                Assert.False(result.GetProperty("isError").GetBoolean());
                Assert.Equal(
                    published.Snapshot.IndexRevision,
                    result.GetProperty("structuredContent").GetProperty("index_revision").GetString());
            }

            using (JsonDocument searchResponse = JsonDocument.Parse(responses[2]))
            {
                JsonElement result = searchResponse.RootElement.GetProperty("result");
                Assert.False(result.GetProperty("isError").GetBoolean());
                Assert.Equal(
                    "generation_active_lease:document_fulltext:code_search",
                    result.GetProperty("structuredContent")
                        .GetProperty("diagnostics")
                        .GetProperty("access_path")
                        .GetString());
            }

            using (JsonDocument symbolResponse = JsonDocument.Parse(responses[3]))
            {
                JsonElement result = symbolResponse.RootElement.GetProperty("result");
                Assert.False(result.GetProperty("isError").GetBoolean());
                JsonElement structured = result.GetProperty("structuredContent");
                Assert.Equal(
                    publishedSymbol.Id,
                    structured.GetProperty("items")[0].GetProperty("id").GetString());
                Assert.Equal(
                    "generation_active_lease:document_path_index:by_stable_id",
                    structured.GetProperty("diagnostics").GetProperty("access_path").GetString());
            }

            using var reopened = new SonnetDbIndexGenerationStore(database);
            using DatabaseGenerationQueryLease lease = reopened.AcquireActiveGeneration(
                published.Snapshot.WorkspaceId);
            Assert.Equal(published.Snapshot.IndexRevision, lease.Generation.GenerationId);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task WorkspaceStatus_ResponseSerializationExceedsDeadline_ReturnsDeadlineError()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex published = await PublishFirstAsync(workspaceRoot, database);
            using var store = new SonnetDbIndexGenerationStore(database);
            var executor = new SonnetDbMcpToolExecutor(store, 0)
            {
                BeforeResponseSerializationTestHook = () => Thread.Sleep(TimeSpan.FromMilliseconds(20)),
            };

            McpDispatchResult result = DispatchStatus(
                executor,
                Binding(published.Snapshot),
                deadlineMilliseconds: 5);

            McpError error = Assert.IsType<McpError>(result.Error);
            Assert.Equal(McpErrorCodes.DeadlineExceeded, error.Code);
            Assert.Null(result.WorkspaceStatus);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task WorkspaceStatus_PublishDuringSerialization_DefersLeasedGenerationUntilDispatchReturns()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            PublishedIndex first = await PublishFirstAsync(workspaceRoot, database);
            await File.WriteAllTextAsync(
                Path.Combine(workspaceRoot, "Sample.cs"),
                "public class Sample { public int Updated() => 2; }");
            DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                workspaceRoot,
                WorkspaceDiscoveryService.DefaultPolicy);
            WorkspaceIndexSnapshot secondSnapshot = await IndexSnapshotBuilder.BuildAsync(
                discovered,
                first.Snapshot.IndexRevision);
            IndexPlanningSnapshot previousPlanning;
            using (var planningStore = new SonnetDbIndexGenerationStore(database))
            {
                previousPlanning = Assert.IsType<ActiveIndexPlanningSnapshot>(
                    planningStore.ReadActivePlanningSnapshot(first.Snapshot.WorkspaceId)).PlanningSnapshot;
            }

            IncrementalIndexPlan secondPlan = IncrementalIndexPlanner.PlanFromPublished(
                previousPlanning,
                secondSnapshot);
            using var store = new SonnetDbIndexGenerationStore(database);
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

            McpToolResponse<WorkspaceStatusItem> response = Assert.IsType<McpToolResponse<WorkspaceStatusItem>>(
                DispatchStatus(executor, Binding(first.Snapshot)).WorkspaceStatus);

            Assert.Equal(first.Snapshot.IndexRevision, response.IndexRevision);
            Assert.Equal(secondSnapshot.IndexRevision, Assert.IsType<IndexStageReport>(secondReport).Manifest.IndexRevision);
            Assert.Equal([1L], Assert.IsType<IndexStageReport>(secondReport).DeferredGenerationRevisions);
            Assert.Equal([1L], Assert.IsType<DatabaseGenerationCleanupResult>(cleanupWhileLeased).DeferredRevisions);

            DatabaseGenerationCleanupResult afterDispatch = store.CleanupRetired(
                first.Snapshot.WorkspaceId);
            Assert.Equal([1L], afterDispatch.RemovedRevisions);
            Assert.Empty(afterDispatch.DeferredRevisions);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task WorkspaceStatus_AfterRealBranchSwitchWithOldActive_ReportsStartupSnapshotAsStale()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            await RunGitAsync(workspaceRoot, "init", "-b", "main");
            await RunGitAsync(workspaceRoot, "config", "user.email", "couplet@example.invalid");
            await RunGitAsync(workspaceRoot, "config", "user.name", "Couplet Tests");
            await File.WriteAllTextAsync(
                Path.Combine(workspaceRoot, "Sample.cs"),
                "public class Sample { public int Main() => 1; }");
            await RunGitAsync(workspaceRoot, "add", "Sample.cs");
            await RunGitAsync(workspaceRoot, "commit", "-m", "main snapshot");

            DiscoveredWorkspace main = await WorkspaceDiscoveryService.DiscoverAsync(
                workspaceRoot,
                WorkspaceDiscoveryService.DefaultPolicy);
            WorkspaceIndexSnapshot mainSnapshot = await IndexSnapshotBuilder.BuildAsync(main, null);
            using (var publishStore = new SonnetDbIndexGenerationStore(database))
            {
                IndexStageReport report = publishStore.StageAndPublish(
                    mainSnapshot,
                    IncrementalIndexPlanner.Plan(null, mainSnapshot),
                    0);
                Assert.True(report.Published);
            }

            await RunGitAsync(workspaceRoot, "checkout", "-b", "feature");
            await File.WriteAllTextAsync(
                Path.Combine(workspaceRoot, "Sample.cs"),
                "public class Sample { public int Feature() => 2; }");
            await RunGitAsync(workspaceRoot, "add", "Sample.cs");
            await RunGitAsync(workspaceRoot, "commit", "-m", "feature snapshot");
            DiscoveredWorkspace feature = await WorkspaceDiscoveryService.DiscoverAsync(
                workspaceRoot,
                WorkspaceDiscoveryService.DefaultPolicy);
            Assert.Equal(main.Result.WorkspaceId, feature.Result.WorkspaceId);
            Assert.NotEqual(main.Result.SourceRevision, feature.Result.SourceRevision);

            using var store = new SonnetDbIndexGenerationStore(database);
            var executor = new SonnetDbMcpToolExecutor(store, 0);
            WorkspaceBinding startupBinding = Binding(
                feature.Result.WorkspaceId,
                feature.Result.RepositoryIdentity,
                feature.Result.SourceRevision,
                mainSnapshot.IndexRevision);
            McpToolResponse<WorkspaceStatusItem> response = Assert.IsType<McpToolResponse<WorkspaceStatusItem>>(
                DispatchStatus(executor, startupBinding).WorkspaceStatus);

            Assert.Equal(mainSnapshot.SourceRevision, response.SourceRevision);
            Assert.Equal(mainSnapshot.IndexRevision, response.IndexRevision);
            Assert.Equal("unknown", response.Freshness.SourceState);
            Assert.Equal("stale", response.Freshness.IndexState);
            Assert.Equal(0, response.Freshness.Coverage);
            Assert.Equal("source_revision_sampled_at_mcp_startup", response.Freshness.Reason);
            Assert.True(Assert.Single(response.Items).RebuildRequired);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    private static McpDispatchResult DispatchStatus(
        IMcpToolExecutor executor,
        WorkspaceBinding binding,
        string? revisionKind = null,
        string? revisionValue = null,
        int deadlineMilliseconds = 10_000)
    {
        string arguments = StatusArguments(revisionKind, revisionValue, deadlineMilliseconds);
        using JsonDocument document = JsonDocument.Parse(arguments);
        return McpToolContractDispatcher.Dispatch(
            McpToolNames.WorkspaceStatus,
            document.RootElement,
            binding,
            "workspace-status",
            executor,
            CancellationToken.None,
            McpWorkspaceBinder.CreateC1Capabilities());
    }

    private static McpDispatchResult DispatchSymbol(
        IMcpToolExecutor executor,
        WorkspaceBinding binding,
        string? symbolId = null,
        string? qualifiedIdentity = null,
        string? language = null,
        string? revisionKind = null,
        string? revisionValue = null,
        int maxBytes = 65_536,
        int deadlineMilliseconds = 10_000,
        CancellationToken cancellationToken = default)
    {
        string identity = symbolId is not null
            ? "\"symbol_id\":" + JsonString(symbolId)
            : "\"qualified_identity\":" + JsonString(qualifiedIdentity!);
        string languageProperty = language is null
            ? string.Empty
            : ",\"language\":" + JsonString(language);
        string selector = revisionKind is null
            ? string.Empty
            : ",\"revision_selector\":{\"kind\":"
                + JsonString(revisionKind)
                + ",\"value\":"
                + JsonString(revisionValue!)
                + "}";
        string arguments = "{\"protocol_version\":\"1\",\"budget\":{\"max_items\":1,\"max_tokens\":1000,\"max_bytes\":"
            + maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"deadline_ms\":"
            + deadlineMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "},"
            + identity
            + languageProperty
            + selector
            + "}";
        using JsonDocument document = JsonDocument.Parse(arguments);
        return McpToolContractDispatcher.Dispatch(
            McpToolNames.SymbolGet,
            document.RootElement,
            binding,
            "symbol-get",
            executor,
            cancellationToken,
            McpWorkspaceBinder.CreateC1Capabilities());
    }

    private static string JsonString(string value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStringValue(value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string StatusArguments(
        string? revisionKind = null,
        string? revisionValue = null,
        int deadlineMilliseconds = 10_000)
    {
        string selector = revisionKind is null
            ? string.Empty
            : $",\"revision_selector\":{{\"kind\":\"{revisionKind}\",\"value\":\"{revisionValue}\"}}";
        return "{\"protocol_version\":\"1\",\"budget\":{\"max_items\":20,\"max_tokens\":1000,\"max_bytes\":65536,\"deadline_ms\":"
            + deadlineMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "}"
            + selector
            + "}";
    }

    private static string ToolCall(string tool, string arguments) =>
        "{\"jsonrpc\":\"2.0\",\"id\":\"status\",\"method\":\"tools/call\",\"params\":{\"name\":\""
        + tool
        + "\",\"arguments\":"
        + arguments
        + "}}";

    private static async Task<PublishedIndex> PublishFirstAsync(string workspaceRoot, string database)
    {
        await File.WriteAllTextAsync(
            Path.Combine(workspaceRoot, "Sample.cs"),
            "public class Sample { public int Original() => 1; }");
        DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
            workspaceRoot,
            WorkspaceDiscoveryService.DefaultPolicy);
        WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(discovered, null);
        IncrementalIndexPlan plan = IncrementalIndexPlanner.Plan(null, snapshot);
        using var store = new SonnetDbIndexGenerationStore(database);
        IndexStageReport report = store.StageAndPublish(snapshot, plan, 0);
        Assert.True(report.Published);
        return new PublishedIndex(snapshot, report);
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git process could not start.");
        string standardOutput = await process.StandardOutput.ReadToEndAsync();
        string standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: {standardOutput}{standardError}");
    }

    private static WorkspaceBinding Binding(WorkspaceIndexSnapshot snapshot) => Binding(
        snapshot.WorkspaceId,
        snapshot.RepositoryIdentity!,
        snapshot.SourceRevision,
        snapshot.IndexRevision);

    private static WorkspaceBinding Binding(
        string workspaceId,
        string repositoryIdentity,
        string sourceRevision,
        string? indexRevision) => new()
        {
            WorkspaceId = workspaceId,
            RepositoryIdentity = repositoryIdentity,
            SourceRevision = sourceRevision,
            IndexRevision = indexRevision,
        };

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "couplet-mcp-status-" + Guid.NewGuid().ToString("N"));
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
