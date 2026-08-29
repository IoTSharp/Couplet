#if COUPLET_SONNETDB_SOURCE_GENERATIONS
using System.Text;
using System.Text.Json;
using Couplet.Application.Indexing;
using Couplet.Application.Workspaces;
using Couplet.Core.Capabilities;
using Couplet.Core.Indexing;
using Couplet.Infrastructure.SonnetDb;
using SonnetDB.Generations;

namespace Couplet.Tests;

public sealed class C1GenerationPublishingTests
{
    [Fact]
    public async Task AcquireWriterFence_WhileHeld_WaitsSupportsCancellationAndRecovers()
    {
        string database = TemporaryDirectory();
        try
        {
            using var first = await IndexWriterFence.AcquireAsync(
                database,
                "workspace-fence-test",
                CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            Task<IndexWriterFence> blocked = IndexWriterFence.AcquireAsync(
                database,
                "workspace-fence-test",
                cancellation.Token);

            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.False(blocked.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await blocked);

            first.Dispose();
            using var recovered = await IndexWriterFence.AcquireAsync(
                database,
                "workspace-fence-test",
                CancellationToken.None);
        }
        finally
        {
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task IndexStage_AcrossReopenAndUnchangedRerun_PublishesThenReusesActiveGeneration()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "Sample.cs"),
                "public class Sample { public int Value() => 1; }");

            JsonElement first = await RunIndexStageAsync(workspace, database);
            string firstIndexRevision = first.GetProperty("manifest").GetProperty("index_revision").GetString()!;
            string firstCollection = first.GetProperty("collection_name").GetString()!;
            Assert.True(first.GetProperty("published").GetBoolean());
            Assert.False(first.GetProperty("reused_active_generation").GetBoolean());
            Assert.Equal(1, first.GetProperty("database_generation_revision").GetInt64());
            Assert.Equal("Published", first.GetProperty("manifest").GetProperty("state").GetString());

            JsonElement unchanged = await RunIndexStageAsync(workspace, database);

            Assert.True(unchanged.GetProperty("published").GetBoolean());
            Assert.True(unchanged.GetProperty("reused_active_generation").GetBoolean());
            Assert.Equal(1, unchanged.GetProperty("database_generation_revision").GetInt64());
            Assert.Equal(firstIndexRevision, unchanged.GetProperty("manifest").GetProperty("index_revision").GetString());
            Assert.Equal(firstCollection, unchanged.GetProperty("collection_name").GetString());
            Assert.Empty(unchanged.GetProperty("removed_generation_revisions").EnumerateArray());

            using var reopened = new SonnetDbIndexGenerationStore(database);
            ActiveIndexPlanningSnapshot active = Assert.IsType<ActiveIndexPlanningSnapshot>(
                reopened.ReadActivePlanningSnapshot(
                    unchanged.GetProperty("manifest").GetProperty("workspace_id").GetString()!));
            Assert.Equal(1, active.DatabaseGenerationRevision);
            Assert.Equal(firstIndexRevision, active.PlanningSnapshot.IndexRevision);
        }
        finally
        {
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task StageAndPublish_WithRetiredLease_DefersCleanupAndBindsCursorToRevision()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            string sourcePath = Path.Combine(workspaceRoot, "Sample.cs");
            await File.WriteAllTextAsync(sourcePath, "public class Sample { public int Value() => 1; }");
            DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                workspaceRoot,
                WorkspaceDiscoveryService.DefaultPolicy);
            WorkspaceIndexSnapshot firstSnapshot = await IndexSnapshotBuilder.BuildAsync(discovered, null);
            IncrementalIndexPlan firstPlan = IncrementalIndexPlanner.Plan(null, firstSnapshot);

            using var store = new SonnetDbIndexGenerationStore(database);
            IndexStageReport first = store.StageAndPublish(firstSnapshot, firstPlan, 0);
            Assert.True(first.Published);
            Assert.Equal(1, first.DatabaseGenerationRevision);

            using DatabaseGenerationQueryLease oldLease = store.AcquireActiveGeneration(firstSnapshot.WorkspaceId);
            string cursor = oldLease.CreateCursor("code-search:v1", Encoding.UTF8.GetBytes("after:sample"));
            string oldSymbolId = firstSnapshot.Files.SelectMany(file => file.Symbols).First().Id;

            await File.WriteAllTextAsync(sourcePath, "public class Sample { public int Value() => 2; }");
            discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                workspaceRoot,
                WorkspaceDiscoveryService.DefaultPolicy);
            ActiveIndexPlanningSnapshot previous = Assert.IsType<ActiveIndexPlanningSnapshot>(
                store.ReadActivePlanningSnapshot(firstSnapshot.WorkspaceId));
            WorkspaceIndexSnapshot secondSnapshot = await IndexSnapshotBuilder.BuildAsync(
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
            Assert.Equal(2, second.DatabaseGenerationRevision);
            Assert.Empty(second.RemovedGenerationRevisions);
            Assert.Equal([1L], second.DeferredGenerationRevisions);
            Assert.Equal(
                "document_path_index:by_stable_id",
                store.ProbeExact(firstSnapshot.WorkspaceId, firstSnapshot.IndexRevision, oldSymbolId).AccessPath);
            Assert.Equal(
                "after:sample",
                Encoding.UTF8.GetString(oldLease.ReadCursor(cursor, "code-search:v1")));

            using (DatabaseGenerationQueryLease activeLease = store.AcquireActiveGeneration(firstSnapshot.WorkspaceId))
            {
                DatabaseGenerationException stale = Assert.Throws<DatabaseGenerationException>(() =>
                    activeLease.ReadCursor(cursor, "code-search:v1"));
                Assert.Equal(DatabaseGenerationErrorCodes.CursorStale, stale.Code);
            }

            oldLease.Dispose();
            DatabaseGenerationCleanupResult cleanup = store.CleanupRetired(firstSnapshot.WorkspaceId);
            Assert.Equal([1L], cleanup.RemovedRevisions);
            Assert.Empty(cleanup.DeferredRevisions);
            Assert.Equal(
                "unavailable",
                store.ProbeExact(firstSnapshot.WorkspaceId, firstSnapshot.IndexRevision, oldSymbolId).AccessPath);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task StageAndPublish_PreCancelledNoOp_DoesNotCleanupEligibleRetiredGeneration()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            string sourcePath = Path.Combine(workspaceRoot, "Sample.cs");
            await File.WriteAllTextAsync(sourcePath, "public class Sample { public int Value() => 1; }");
            DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                workspaceRoot,
                WorkspaceDiscoveryService.DefaultPolicy);
            WorkspaceIndexSnapshot firstSnapshot = await IndexSnapshotBuilder.BuildAsync(discovered, null);
            IncrementalIndexPlan firstPlan = IncrementalIndexPlanner.Plan(null, firstSnapshot);

            using var store = new SonnetDbIndexGenerationStore(database);
            IndexStageReport first = store.StageAndPublish(firstSnapshot, firstPlan, 0);
            using DatabaseGenerationQueryLease oldLease = store.AcquireActiveGeneration(firstSnapshot.WorkspaceId);

            await File.WriteAllTextAsync(sourcePath, "public class Sample { public int Value() => 2; }");
            discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                workspaceRoot,
                WorkspaceDiscoveryService.DefaultPolicy);
            ActiveIndexPlanningSnapshot firstActive = Assert.IsType<ActiveIndexPlanningSnapshot>(
                store.ReadActivePlanningSnapshot(firstSnapshot.WorkspaceId));
            WorkspaceIndexSnapshot secondSnapshot = await IndexSnapshotBuilder.BuildAsync(
                discovered,
                firstActive.PlanningSnapshot.IndexRevision);
            IncrementalIndexPlan secondPlan = IncrementalIndexPlanner.PlanFromPublished(
                firstActive.PlanningSnapshot,
                secondSnapshot);
            IndexStageReport second = store.StageAndPublish(
                secondSnapshot,
                secondPlan,
                first.DatabaseGenerationRevision!.Value);
            Assert.Equal([1L], second.DeferredGenerationRevisions);

            oldLease.Dispose();
            ActiveIndexPlanningSnapshot secondActive = Assert.IsType<ActiveIndexPlanningSnapshot>(
                store.ReadActivePlanningSnapshot(firstSnapshot.WorkspaceId));
            discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                workspaceRoot,
                WorkspaceDiscoveryService.DefaultPolicy);
            WorkspaceIndexSnapshot unchangedSnapshot = await IndexSnapshotBuilder.BuildAsync(
                discovered,
                secondActive.PlanningSnapshot.IndexRevision);
            IncrementalIndexPlan noOpPlan = IncrementalIndexPlanner.PlanFromPublished(
                secondActive.PlanningSnapshot,
                unchangedSnapshot);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => store.StageAndPublish(
                unchangedSnapshot,
                noOpPlan,
                secondActive.DatabaseGenerationRevision,
                cancellation.Token));

            string oldSymbolId = firstSnapshot.Files.SelectMany(file => file.Symbols).First().Id;
            Assert.Equal(
                "document_path_index:by_stable_id",
                store.ProbeExact(firstSnapshot.WorkspaceId, firstSnapshot.IndexRevision, oldSymbolId).AccessPath);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task StageAndPublish_WhenCleanupFails_ReportsCommittedPublicationAndRetryLimitation()
    {
        string workspaceRoot = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspaceRoot, "Sample.cs"),
                "public class Sample { public int Value() => 1; }");
            DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                workspaceRoot,
                WorkspaceDiscoveryService.DefaultPolicy);
            WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(discovered, null);
            IncrementalIndexPlan plan = IncrementalIndexPlanner.Plan(null, snapshot);

            using var store = new SonnetDbIndexGenerationStore(database)
            {
                CleanupRetiredTestHook = static (_, _) => throw new IOException("cleanup fault injection"),
            };
            IndexStageReport report = store.StageAndPublish(snapshot, plan, 0);

            Assert.True(report.Staged);
            Assert.True(report.Published);
            Assert.Equal(1, report.DatabaseGenerationRevision);
            Assert.Null(report.BlockingGap);
            Assert.Contains("retired_generation_cleanup_failed", report.Problems);
            Assert.Contains("CPL-015:retired_generation_cleanup_retry_required", report.Limitations);
            ActiveIndexPlanningSnapshot active = Assert.IsType<ActiveIndexPlanningSnapshot>(
                store.ReadActivePlanningSnapshot(snapshot.WorkspaceId));
            Assert.Equal(report.DatabaseGenerationRevision, active.DatabaseGenerationRevision);
            Assert.Equal(snapshot.IndexRevision, active.PlanningSnapshot.IndexRevision);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
            DeleteTemporaryDirectory(database);
        }
    }

    private static async Task<JsonElement> RunIndexStageAsync(string workspace, string database)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await CoupletRuntime.RunAsync(
            ComponentKind.Cli,
            ["index-stage", "--workspace", workspace, "--database", database],
            output,
            error,
            CancellationToken.None);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        return document.RootElement.Clone();
    }

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "couplet-generation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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
}
#endif
