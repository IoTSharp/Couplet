#if COUPLET_SONNETDB_SOURCE_GENERATIONS
using System.Text.Json;
using Couplet.Application.Indexing;
using Couplet.Application.Serialization;
using Couplet.Application.Workspaces;
using Couplet.Core.Indexing;
using Couplet.Infrastructure.SonnetDb;
using SonnetDB.Generations;

namespace Couplet.Tests;

public sealed class C1GenerationRetentionTests
{
    [Fact]
    public async Task StageAndPublish_WithZeroRetention_ImmediatelyRemovesEligibleRetiredGeneration()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            using var store = new SonnetDbIndexGenerationStore(database);
            PublishedGeneration first = await PublishAsync(store, workspace, 1, null);
            PublishedGeneration second = await PublishAsync(store, workspace, 2, first);

            Assert.Equal([1L], second.Report.RemovedGenerationRevisions);
            Assert.Empty(second.Report.DeferredGenerationRevisions);
            Assert.Empty(second.Report.RetentionDeferredGenerationRevisions);
            Assert.Empty(second.Report.Limitations);
        }
        finally
        {
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task CleanupRetired_AfterRetentionExpires_RetriesCancellationAndFailureThenRemovesGeneration()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        var timeProvider = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        TimeSpan retention = TimeSpan.FromHours(1);
        try
        {
            using var store = new SonnetDbIndexGenerationStore(database, retention, timeProvider);
            PublishedGeneration first = await PublishAsync(store, workspace, 1, null);
            PublishedGeneration second = await PublishAsync(store, workspace, 2, first);

            Assert.Empty(second.Report.RemovedGenerationRevisions);
            Assert.Empty(second.Report.DeferredGenerationRevisions);
            Assert.Equal([1L], second.Report.RetentionDeferredGenerationRevisions);

            timeProvider.SetUtcNow(
                (first.PublishedAtUtc + retention).ToOffset(TimeSpan.FromHours(8)));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                store.CleanupRetired(first.Snapshot.WorkspaceId, cancellation.Token));

            store.CleanupRetiredTestHook = static (_, _) =>
                throw new IOException("retention cleanup fault injection");
            Assert.Throws<IOException>(() => store.CleanupRetired(first.Snapshot.WorkspaceId));
            store.CleanupRetiredTestHook = null;
            DatabaseGenerationCleanupResult cleanup = store.CleanupRetired(first.Snapshot.WorkspaceId);

            Assert.Equal([1L], cleanup.RemovedRevisions);
            Assert.Empty(cleanup.DeferredRevisions);
            Assert.Empty(cleanup.RetentionDeferredRevisions);
        }
        finally
        {
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task StageAndPublish_ContinuouslyWithZeroRetention_KeepsOnlyActiveGeneration()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            using var store = new SonnetDbIndexGenerationStore(database);
            PublishedGeneration? current = null;
            for (int revision = 1; revision <= 6; revision++)
            {
                current = await PublishAsync(store, workspace, revision, current);
                Assert.Equal(revision, current.Report.DatabaseGenerationRevision);
                Assert.Equal(
                    revision == 1 ? [] : [revision - 1L],
                    current.Report.RemovedGenerationRevisions);
            }

            PublishedGeneration activeGeneration = Assert.IsType<PublishedGeneration>(current);
            Assert.Equal([6L], store.ListGenerationRevisionsForTest(activeGeneration.Snapshot.WorkspaceId));
            using DatabaseGenerationQueryLease active = store.AcquireActiveGeneration(
                activeGeneration.Snapshot.WorkspaceId);
            Assert.Equal(6, active.Generation.Revision);
            Assert.Equal(activeGeneration.Snapshot.IndexRevision, active.Generation.GenerationId);
        }
        finally
        {
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task CleanupRetired_AfterReopenWithMixedAges_RemovesOnlyDueGeneration()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        var timeProvider = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        TimeSpan retention = TimeSpan.FromHours(1);
        try
        {
            PublishedGeneration first;
            PublishedGeneration second;
            PublishedGeneration third;
            using (var store = new SonnetDbIndexGenerationStore(database, retention, timeProvider))
            {
                first = await PublishAsync(store, workspace, 1, null);
                await WaitForSystemUtcAfterAsync(first.PublishedAtUtc);
                second = await PublishAsync(store, workspace, 2, first);
                third = await PublishAsync(store, workspace, 3, second);
                Assert.Equal([1L, 2L], third.Report.RetentionDeferredGenerationRevisions);
            }

            long midpointTicks = first.PublishedAtUtc.UtcTicks
                + ((second.PublishedAtUtc.UtcTicks - first.PublishedAtUtc.UtcTicks) / 2);
            DateTimeOffset cutoff = new(midpointTicks, TimeSpan.Zero);
            timeProvider.SetUtcNow(cutoff + retention);

            using var reopened = new SonnetDbIndexGenerationStore(database, retention, timeProvider);
            DatabaseGenerationCleanupResult cleanup = reopened.CleanupRetired(first.Snapshot.WorkspaceId);

            Assert.Equal([1L], cleanup.RemovedRevisions);
            Assert.Empty(cleanup.DeferredRevisions);
            Assert.Equal([2L], cleanup.RetentionDeferredRevisions);
            using DatabaseGenerationQueryLease active = reopened.AcquireActiveGeneration(first.Snapshot.WorkspaceId);
            Assert.Equal(3, active.Generation.Revision);
            Assert.Equal(third.Snapshot.IndexRevision, active.Generation.GenerationId);
        }
        finally
        {
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task CleanupRetired_WithLeaseOnDueGeneration_DefersUntilLeaseReleased()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        var timeProvider = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        TimeSpan retention = TimeSpan.FromMinutes(30);
        try
        {
            using var store = new SonnetDbIndexGenerationStore(database, retention, timeProvider);
            PublishedGeneration first = await PublishAsync(store, workspace, 1, null);
            using DatabaseGenerationQueryLease lease = store.AcquireActiveGeneration(first.Snapshot.WorkspaceId);
            PublishedGeneration second = await PublishAsync(store, workspace, 2, first);
            Assert.Equal([1L], second.Report.RetentionDeferredGenerationRevisions);

            timeProvider.SetUtcNow(first.PublishedAtUtc + retention);
            DatabaseGenerationCleanupResult leasedCleanup = store.CleanupRetired(first.Snapshot.WorkspaceId);
            Assert.Empty(leasedCleanup.RemovedRevisions);
            Assert.Equal([1L], leasedCleanup.DeferredRevisions);
            Assert.Empty(leasedCleanup.RetentionDeferredRevisions);

            lease.Dispose();
            DatabaseGenerationCleanupResult retry = store.CleanupRetired(first.Snapshot.WorkspaceId);
            Assert.Equal([1L], retry.RemovedRevisions);
            Assert.Empty(retry.DeferredRevisions);
            Assert.Empty(retry.RetentionDeferredRevisions);
        }
        finally
        {
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public void Constructor_WithNegativeRetention_ThrowsBeforeCreatingDatabaseDirectory()
    {
        string database = Path.Combine(
            Path.GetTempPath(),
            "couplet-retention-negative-" + Guid.NewGuid().ToString("N"));

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using var store = new SonnetDbIndexGenerationStore(
                database,
                TimeSpan.FromTicks(-1));
        });

        Assert.Equal("retiredGenerationRetention", exception.ParamName);
        Assert.False(Directory.Exists(database));
    }

    [Fact]
    public async Task CleanupRetired_WithMaximumRetention_ClampsCutoffAndRetainsCandidate()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        var timeProvider = new AdjustableTimeProvider(DateTimeOffset.MinValue);
        try
        {
            using var store = new SonnetDbIndexGenerationStore(
                database,
                TimeSpan.MaxValue,
                timeProvider);
            PublishedGeneration first = await PublishAsync(store, workspace, 1, null);
            PublishedGeneration second = await PublishAsync(store, workspace, 2, first);

            Assert.Empty(second.Report.RemovedGenerationRevisions);
            Assert.Empty(second.Report.DeferredGenerationRevisions);
            Assert.Equal([1L], second.Report.RetentionDeferredGenerationRevisions);

            DatabaseGenerationCleanupResult cleanup = store.CleanupRetired(first.Snapshot.WorkspaceId);
            Assert.Empty(cleanup.RemovedRevisions);
            Assert.Empty(cleanup.DeferredRevisions);
            Assert.Equal([1L], cleanup.RetentionDeferredRevisions);
        }
        finally
        {
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task Serialize_IndexStageReport_ContainsRetentionDeferredGenerationRevisions()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            using var store = new SonnetDbIndexGenerationStore(database);
            PublishedGeneration published = await PublishAsync(store, workspace, 1, null);

            using JsonDocument json = JsonDocument.Parse(CoupletJsonSerializer.Serialize(published.Report));
            JsonElement property = json.RootElement.GetProperty(
                "retention_deferred_generation_revisions");
            Assert.Equal(JsonValueKind.Array, property.ValueKind);
            Assert.Empty(property.EnumerateArray());
        }
        finally
        {
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task IndexStage_WithRuntimeRetentionOption_PreservesRetiredGeneration()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "Sample.cs"),
                "public class Sample { public int Version() => 1; }");
            using JsonDocument first = await RunIndexStageAsync(workspace, database);
            Assert.Equal(
                "couplet.index_stage.v2",
                first.RootElement.GetProperty("schema_version").GetString());

            await File.WriteAllTextAsync(
                Path.Combine(workspace, "Sample.cs"),
                "public class Sample { public int Version() => 2; }");
            using JsonDocument second = await RunIndexStageAsync(workspace, database);

            Assert.Empty(second.RootElement.GetProperty("removed_generation_revisions").EnumerateArray());
            Assert.Equal(
                [1L],
                second.RootElement
                    .GetProperty("retention_deferred_generation_revisions")
                    .EnumerateArray()
                    .Select(value => value.GetInt64())
                    .ToArray());
        }
        finally
        {
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task IndexStage_WithMissingRuntimeRetentionValue_RejectsBeforeCreatingDatabase()
    {
        string workspace = TemporaryDirectory();
        string database = Path.Combine(
            Path.GetTempPath(),
            "couplet-retention-missing-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "Sample.cs"),
                "public class Sample { }");
            using var output = new StringWriter();
            using var error = new StringWriter();

            int exitCode = await CoupletRuntime.RunAsync(
                Couplet.Core.Capabilities.ComponentKind.Cli,
                [
                    "index-stage",
                    "--workspace",
                    workspace,
                    "--database",
                    database,
                    "--retired-generation-retention",
                ],
                output,
                error,
                CancellationToken.None);

            Assert.Equal(64, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            using JsonDocument json = JsonDocument.Parse(error.ToString());
            Assert.Equal(
                "invalid_retired_generation_retention",
                json.RootElement.GetProperty("reason").GetString());
            Assert.False(Directory.Exists(database));
        }
        finally
        {
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    private static async Task<PublishedGeneration> PublishAsync(
        SonnetDbIndexGenerationStore store,
        string workspace,
        int version,
        PublishedGeneration? previous)
    {
        string sourcePath = Path.Combine(workspace, "Sample.cs");
        await File.WriteAllTextAsync(
            sourcePath,
            $"public class Sample {{ public int Version() => {version}; }}");
        DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
            workspace,
            WorkspaceDiscoveryService.DefaultPolicy);
        WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(
            discovered,
            previous?.Snapshot.IndexRevision);
        IncrementalIndexPlan plan = previous is null
            ? IncrementalIndexPlanner.Plan(null, snapshot)
            : IncrementalIndexPlanner.PlanFromPublished(previous.PlanningSnapshot, snapshot);
        IndexStageReport report = store.StageAndPublish(
            snapshot,
            plan,
            previous?.Report.DatabaseGenerationRevision ?? 0);
        ActiveIndexPlanningSnapshot active = Assert.IsType<ActiveIndexPlanningSnapshot>(
            store.ReadActivePlanningSnapshot(snapshot.WorkspaceId));
        using DatabaseGenerationQueryLease lease = store.AcquireActiveGeneration(snapshot.WorkspaceId);
        Assert.Equal(report.DatabaseGenerationRevision, lease.Generation.Revision);
        return new PublishedGeneration(
            snapshot,
            active.PlanningSnapshot,
            report,
            lease.Generation.PublishedAtUtc);
    }

    private static async Task WaitForSystemUtcAfterAsync(DateTimeOffset value)
    {
        for (int attempt = 0; attempt < 100 && DateTimeOffset.UtcNow <= value; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        Assert.True(DateTimeOffset.UtcNow > value);
    }

    private static async Task<JsonDocument> RunIndexStageAsync(string workspace, string database)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await CoupletRuntime.RunAsync(
            Couplet.Core.Capabilities.ComponentKind.Cli,
            [
                "index-stage",
                "--workspace",
                workspace,
                "--database",
                database,
                "--retired-generation-retention",
                "01:00:00",
            ],
            output,
            error,
            CancellationToken.None);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        return JsonDocument.Parse(output.ToString());
    }

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "couplet-retention-" + Guid.NewGuid().ToString("N"));
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

    private sealed record PublishedGeneration(
        WorkspaceIndexSnapshot Snapshot,
        IndexPlanningSnapshot PlanningSnapshot,
        IndexStageReport Report,
        DateTimeOffset PublishedAtUtc);

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
    }
}
#endif
