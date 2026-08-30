using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Couplet.Application.Indexing;
using Couplet.Application.Workspaces;
using Couplet.Core.Capabilities;
using Couplet.Core.Indexing;
using Couplet.Core.Languages;
using Couplet.Infrastructure.SonnetDb;

namespace Couplet.Tests;

public sealed class C1WorkspaceWatcherFreshnessTests
{
    [Fact]
    public async Task WatchAsync_WithRenameAndDelete_CoalescesOldAndNewPathsThenReportsDelete()
    {
        string root = TemporaryDirectory();
        try
        {
            string original = Path.Combine(root, "original");
            string renamed = Path.Combine(root, "renamed");
            await File.WriteAllTextAsync(original, "public class Original { }");
            using var monitor = new WorkspaceChangeMonitor(root);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using IAsyncEnumerator<Couplet.Core.Workspaces.WorkspaceChangeBatch> batches = monitor
                .WatchAsync(TimeSpan.FromMilliseconds(100), cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);

            File.Move(original, renamed);
            Assert.True(await batches.MoveNextAsync());
            Assert.Equal(["original", "renamed"], batches.Current.Paths);
            Assert.False(batches.Current.RequiresFullRescan);

            File.Delete(renamed);
            Assert.True(await batches.MoveNextAsync());
            Assert.Equal(["renamed"], batches.Current.Paths);
            Assert.False(batches.Current.RequiresFullRescan);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task WatchAsync_WhenBoundedQueueOverflows_RequiresExplicitFullRescan()
    {
        string root = TemporaryDirectory();
        try
        {
            using var monitor = new WorkspaceChangeMonitor(root, queueCapacity: 1);
            for (int index = 0; index < 64; index++)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(root, $"change-{index:D2}.cs"),
                    $"public class Change{index:D2} {{ }}");
            }

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => monitor.FullRescanPending, cancellation.Token);
            await using IAsyncEnumerator<Couplet.Core.Workspaces.WorkspaceChangeBatch> batches = monitor
                .WatchAsync(TimeSpan.FromMilliseconds(50), cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);

            Assert.True(await batches.MoveNextAsync());
            Assert.True(batches.Current.RequiresFullRescan);
            Assert.Equal("watcher_overflow_or_error", batches.Current.Reason);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

#if COUPLET_SONNETDB_SOURCE_GENERATIONS
    [Fact]
    public async Task RunAsync_WithWatchedRenameAndDelete_PublishesFreshRevisionsAndStopsOnCancellation()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        CancellationTokenSource? cancellation = null;
        Task<int>? running = null;
        try
        {
            string original = Path.Combine(workspace, "Original.cs");
            string renamed = Path.Combine(workspace, "Renamed.cs");
            await File.WriteAllTextAsync(original, "public class Original { }");
            var output = new LineCaptureTextWriter();
            using var error = new StringWriter();
            cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            running = CoupletRuntime.RunAsync(
                ComponentKind.Daemon,
                [
                    "run",
                    "--workspace", workspace,
                    "--database", database,
                    "--watch-debounce", "00:00:00.1000000",
                    "--retired-generation-retention", "00:01:00",
                ],
                output,
                error,
                cancellation.Token);

            JsonElement initial = await output.ReadJsonAsync(cancellation.Token);
            string workspaceId = initial.GetProperty("manifest").GetProperty("workspace_id").GetString()!;
            string initialRevision = initial.GetProperty("manifest").GetProperty("index_revision").GetString()!;
            Assert.True(initial.GetProperty("published").GetBoolean());
            Assert.False(initial.GetProperty("reused_active_generation").GetBoolean());
            Assert.Equal(1, initial.GetProperty("database_generation_revision").GetInt64());

            File.Move(original, renamed);
            JsonElement afterRename = await output.ReadJsonAsync(cancellation.Token);
            string renameRevision = afterRename.GetProperty("manifest").GetProperty("index_revision").GetString()!;
            Assert.True(afterRename.GetProperty("published").GetBoolean());
            Assert.False(afterRename.GetProperty("reused_active_generation").GetBoolean());
            Assert.NotEqual(initialRevision, renameRevision);
            Assert.Equal(2, afterRename.GetProperty("database_generation_revision").GetInt64());

            File.Delete(renamed);
            JsonElement afterDelete = await output.ReadJsonAsync(cancellation.Token);
            string deleteRevision = afterDelete.GetProperty("manifest").GetProperty("index_revision").GetString()!;
            Assert.True(afterDelete.GetProperty("published").GetBoolean());
            Assert.False(afterDelete.GetProperty("reused_active_generation").GetBoolean());
            Assert.NotEqual(renameRevision, deleteRevision);
            Assert.Equal(3, afterDelete.GetProperty("database_generation_revision").GetInt64());
            Assert.Equal(0, afterDelete.GetProperty("manifest").GetProperty("counts").GetProperty("files").GetInt32());

            cancellation.Cancel();
            Assert.Equal(0, await running);
            running = null;
            Assert.Equal(string.Empty, error.ToString());

            using IndexWriterFence recoveredWriter = await IndexWriterFence.AcquireAsync(
                database,
                workspaceId,
                CancellationToken.None);

            using var reopened = new SonnetDbIndexGenerationStore(database, TimeSpan.FromMinutes(1));
            ActiveIndexPlanningSnapshot active = Assert.IsType<ActiveIndexPlanningSnapshot>(
                reopened.ReadActivePlanningSnapshot(workspaceId));
            Assert.Equal(3, active.DatabaseGenerationRevision);
            Assert.Equal(deleteRevision, active.PlanningSnapshot.IndexRevision);
            Assert.Empty(active.PlanningSnapshot.Files);
        }
        finally
        {
            await StopDaemonAsync(cancellation, running);
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task RunAsync_WithUnchangedWorkspace_DoesNotPublishDuringPeriodicReconciliation()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        CancellationTokenSource? cancellation = null;
        Task<int>? running = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "Stable.cs"), "public class Stable { }");
            var output = new LineCaptureTextWriter();
            using var error = new StringWriter();
            int discoveries = 0;
            var reconciled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            running = CoupletRuntime.RunIndexWatchDaemonForTestingAsync(
                [
                    "run",
                    "--workspace", workspace,
                    "--database", database,
                    "--watch-reconciliation-interval", "00:00:00.2500000",
                ],
                output,
                error,
                IndexSnapshotBuilder.BuildAsync,
                cancellation.Token,
                _ =>
                {
                    if (Interlocked.Increment(ref discoveries) >= 2)
                    {
                        reconciled.TrySetResult();
                    }
                });

            JsonElement initial = await output.ReadJsonAsync(cancellation.Token);
            Assert.Equal(1, initial.GetProperty("database_generation_revision").GetInt64());
            await reconciled.Task.WaitAsync(cancellation.Token);
            Assert.True(Volatile.Read(ref discoveries) >= 2);
            Assert.False(await output.HasLineWithinAsync(TimeSpan.FromMilliseconds(500)));

            cancellation.Cancel();
            Assert.Equal(0, await running);
            running = null;
            Assert.Equal(string.Empty, error.ToString());

            string workspaceId = initial.GetProperty("manifest").GetProperty("workspace_id").GetString()!;
            using var reopened = new SonnetDbIndexGenerationStore(database, TimeSpan.Zero);
            ActiveIndexPlanningSnapshot active = Assert.IsType<ActiveIndexPlanningSnapshot>(
                reopened.ReadActivePlanningSnapshot(workspaceId));
            Assert.Equal(1, active.DatabaseGenerationRevision);
        }
        finally
        {
            await StopDaemonAsync(cancellation, running);
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task RunAsync_WithLinkedWorktreeSubdirectory_ReconcilesExternalGitHeadChange()
    {
        string root = TemporaryDirectory();
        string repository = Path.Combine(root, "repository");
        string worktree = Path.Combine(root, "linked-worktree");
        string database = Path.Combine(root, "database");
        CancellationTokenSource? cancellation = null;
        Task<int>? running = null;
        try
        {
            Directory.CreateDirectory(repository);
            Directory.CreateDirectory(Path.Combine(repository, "src"));
            await File.WriteAllTextAsync(Path.Combine(repository, "src", "Stable.cs"), "public class Stable { }");
            await File.WriteAllTextAsync(Path.Combine(repository, "outside.txt"), "initial");
            await RunGitAsync(repository, "init", "--initial-branch=main");
            await RunGitAsync(repository, "config", "user.email", "couplet@example.invalid");
            await RunGitAsync(repository, "config", "user.name", "Couplet Tests");
            await RunGitAsync(repository, "add", ".");
            await RunGitAsync(repository, "commit", "-m", "initial");
            await RunGitAsync(repository, "worktree", "add", "-b", "watcher-linked", worktree, "HEAD");

            string workspace = Path.Combine(worktree, "src");
            var output = new LineCaptureTextWriter();
            using var error = new StringWriter();
            cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            running = CoupletRuntime.RunAsync(
                ComponentKind.Daemon,
                [
                    "run",
                    "--workspace", workspace,
                    "--database", database,
                    "--watch-reconciliation-interval", "00:00:00.2500000",
                ],
                output,
                error,
                cancellation.Token);

            JsonElement initial = await output.ReadJsonAsync(cancellation.Token);
            string initialHead = initial.GetProperty("manifest").GetProperty("source_revision").GetString()!;
            await File.WriteAllTextAsync(Path.Combine(worktree, "outside.txt"), "next");
            await RunGitAsync(worktree, "add", "outside.txt");
            await RunGitAsync(worktree, "commit", "-m", "external metadata change");
            string expectedHead = await RunGitCaptureAsync(worktree, "rev-parse", "HEAD");
            Assert.NotEqual(initialHead, expectedHead);

            JsonElement reconciled = await output.ReadJsonUntilAsync(
                element => string.Equals(
                    element.GetProperty("manifest").GetProperty("source_revision").GetString(),
                    expectedHead,
                    StringComparison.Ordinal),
                cancellation.Token);
            Assert.Equal(2, reconciled.GetProperty("database_generation_revision").GetInt64());
            Assert.Equal(
                initial.GetProperty("manifest").GetProperty("counts").GetProperty("files").GetInt32(),
                reconciled.GetProperty("manifest").GetProperty("counts").GetProperty("files").GetInt32());

            cancellation.Cancel();
            Assert.Equal(0, await running);
            running = null;
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            await StopDaemonAsync(cancellation, running);
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task RunAsync_WithAssumeUnchangedTrackedEdit_PublishesChangedContentAndRevisions()
    {
        string root = TemporaryDirectory();
        string workspace = Path.Combine(root, "workspace");
        string database = Path.Combine(root, "database");
        CancellationTokenSource? cancellation = null;
        Task<int>? running = null;
        try
        {
            Directory.CreateDirectory(workspace);
            string source = Path.Combine(workspace, "Sample.cs");
            await File.WriteAllTextAsync(source, "public class Initial { }");
            await RunGitAsync(workspace, "init", "--initial-branch=main");
            await RunGitAsync(workspace, "config", "user.email", "couplet@example.invalid");
            await RunGitAsync(workspace, "config", "user.name", "Couplet Tests");
            await RunGitAsync(workspace, "add", "Sample.cs");
            await RunGitAsync(workspace, "commit", "-m", "initial");
            await RunGitAsync(workspace, "update-index", "--assume-unchanged", "Sample.cs");

            var output = new LineCaptureTextWriter();
            using var error = new StringWriter();
            cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            running = CoupletRuntime.RunAsync(
                ComponentKind.Daemon,
                [
                    "run",
                    "--workspace", workspace,
                    "--database", database,
                    "--watch-reconciliation-interval", "00:00:00.2500000",
                ],
                output,
                error,
                cancellation.Token);

            JsonElement initial = await output.ReadJsonAsync(cancellation.Token);
            string workspaceId = initial.GetProperty("manifest").GetProperty("workspace_id").GetString()!;
            string sourceRevision = initial.GetProperty("manifest").GetProperty("source_revision").GetString()!;
            await File.WriteAllTextAsync(source, "public class Changed { }");
            Assert.Equal(string.Empty, await RunGitCaptureAsync(workspace, "status", "--porcelain=v1", "--", "."));

            JsonElement changed = await output.ReadJsonAsync(cancellation.Token);
            Assert.NotEqual(sourceRevision, changed.GetProperty("manifest").GetProperty("source_revision").GetString());
            Assert.Equal(2, changed.GetProperty("database_generation_revision").GetInt64());
            Assert.NotEqual(
                initial.GetProperty("manifest").GetProperty("index_revision").GetString(),
                changed.GetProperty("manifest").GetProperty("index_revision").GetString());

            cancellation.Cancel();
            Assert.Equal(0, await running);
            running = null;
            Assert.Equal(string.Empty, error.ToString());

            string expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(source))).ToLowerInvariant();
            using var reopened = new SonnetDbIndexGenerationStore(database, TimeSpan.Zero);
            ActiveIndexPlanningSnapshot active = Assert.IsType<ActiveIndexPlanningSnapshot>(
                reopened.ReadActivePlanningSnapshot(workspaceId));
            Assert.Equal(expectedHash, Assert.Single(active.PlanningSnapshot.Files).ContentHash);
        }
        finally
        {
            await StopDaemonAsync(cancellation, running);
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task RunAsync_WhenStatusOutputBlocks_CancellationReleasesWriterFence()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        CancellationTokenSource? cancellation = null;
        Task<int>? running = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "Sample.cs"), "public class Sample { }");
            DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                workspace,
                WorkspaceDiscoveryService.DefaultPolicy);
            var output = new BlockingTextWriter();
            using var error = new StringWriter();
            cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            running = CoupletRuntime.RunAsync(
                ComponentKind.Daemon,
                ["run", "--workspace", workspace, "--database", database],
                output,
                error,
                cancellation.Token);

            await output.WriteStarted.WaitAsync(cancellation.Token);
            using (var writerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            using (IndexWriterFence writerFence = await IndexWriterFence.AcquireAsync(
                       database,
                       discovered.Result.WorkspaceId,
                       writerCancellation.Token))
            {
            }

            cancellation.Cancel();
            Assert.Equal(0, await running);
            running = null;
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            await StopDaemonAsync(cancellation, running);
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task RunAsync_WhenCommittedStatusOutputFails_ReportsCommittedStateWithoutRollback()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "Sample.cs"), "public class Sample { }");
            DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                workspace,
                WorkspaceDiscoveryService.DefaultPolicy);
            using var error = new StringWriter();
            int exitCode = await CoupletRuntime.RunAsync(
                ComponentKind.Daemon,
                ["run", "--workspace", workspace, "--database", database],
                new FailingTextWriter(),
                error,
                CancellationToken.None);

            Assert.Equal(74, exitCode);
            using JsonDocument document = JsonDocument.Parse(error.ToString());
            Assert.Equal("output_failed", document.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                "index_committed_status_output_failed",
                document.RootElement.GetProperty("reason").GetString());

            using var reopened = new SonnetDbIndexGenerationStore(database, TimeSpan.Zero);
            ActiveIndexPlanningSnapshot active = Assert.IsType<ActiveIndexPlanningSnapshot>(
                reopened.ReadActivePlanningSnapshot(discovered.Result.WorkspaceId));
            Assert.Equal(1, active.DatabaseGenerationRevision);
        }
        finally
        {
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task RunAsync_WhenSnapshotFailuresPersist_RetriesThreeFreshDiscoveriesAndKeepsOldActive()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        CancellationTokenSource? initialCancellation = null;
        Task<int>? initialRunning = null;
        try
        {
            string source = Path.Combine(workspace, "Sample.cs");
            await File.WriteAllTextAsync(source, "public class Initial { }");
            var initialOutput = new LineCaptureTextWriter();
            using var initialError = new StringWriter();
            initialCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            initialRunning = CoupletRuntime.RunAsync(
                ComponentKind.Daemon,
                ["run", "--workspace", workspace, "--database", database],
                initialOutput,
                initialError,
                initialCancellation.Token);
            JsonElement initial = await initialOutput.ReadJsonAsync(initialCancellation.Token);
            string workspaceId = initial.GetProperty("manifest").GetProperty("workspace_id").GetString()!;
            string initialSourceRevision = initial.GetProperty("manifest").GetProperty("source_revision").GetString()!;
            initialCancellation.Cancel();
            Assert.Equal(0, await initialRunning);
            initialRunning = null;

            await File.WriteAllTextAsync(source, "public class Changed { }");
            int attempts = 0;
            var observedHashes = new List<string>();
            using var output = new StringWriter();
            using var error = new StringWriter();
            int exitCode = await CoupletRuntime.RunIndexWatchDaemonForTestingAsync(
                ["run", "--workspace", workspace, "--database", database],
                output,
                error,
                async (discovered, previousIndexRevision, cancellationToken) =>
                {
                    int attempt = Interlocked.Increment(ref attempts);
                    observedHashes.Add(Assert.Single(discovered.Result.Files).ContentHash!);
                    if (attempt == 1)
                    {
                        await File.WriteAllTextAsync(
                            source,
                            "public class ChangedAgain { }",
                            cancellationToken);
                    }

                    WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(
                        discovered,
                        previousIndexRevision,
                        cancellationToken);
                    return WithFailure(snapshot);
                },
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal(3, attempts);
            Assert.Equal(3, observedHashes.Count);
            Assert.NotEqual(observedHashes[0], observedHashes[1]);
            Assert.Equal(observedHashes[1], observedHashes[2]);
            Assert.Equal(string.Empty, output.ToString());
            using JsonDocument document = JsonDocument.Parse(error.ToString());
            Assert.Equal("indexing_failed", document.RootElement.GetProperty("code").GetString());
            Assert.Equal("workspace_snapshot_incomplete", document.RootElement.GetProperty("reason").GetString());

            using var reopened = new SonnetDbIndexGenerationStore(database, TimeSpan.Zero);
            ActiveIndexPlanningSnapshot active = Assert.IsType<ActiveIndexPlanningSnapshot>(
                reopened.ReadActivePlanningSnapshot(workspaceId));
            Assert.Equal(1, active.DatabaseGenerationRevision);
            Assert.Equal(initialSourceRevision, active.PlanningSnapshot.SourceRevision);
        }
        finally
        {
            await StopDaemonAsync(initialCancellation, initialRunning);
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task RunAsync_WithMalformedWatchArguments_RejectsUnknownDuplicateMissingAndPositionalValues()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            (string[] Arguments, string Reason)[] cases =
            [
                (["run", "--workspace", workspace, "--database", database, "--unknown", "value"], "unknown_watch_option"),
                (["run", "--workspace", workspace, "--workspace", workspace, "--database", database], "duplicate_watch_option"),
                (["run", "--workspace", workspace, "--database", database, "--watch-debounce"], "missing_watch_option_value"),
                (["run", "--workspace", workspace, "--database", database, "unexpected"], "unknown_watch_argument"),
                (["run", "--workspce", workspace], "unknown_watch_option"),
                ([" run ", "--workspce", workspace], "unknown_watch_option"),
            ];

            foreach ((string[] arguments, string reason) in cases)
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                int exitCode = await CoupletRuntime.RunAsync(
                    ComponentKind.Daemon,
                    arguments,
                    output,
                    error,
                    CancellationToken.None);

                Assert.Equal(64, exitCode);
                Assert.Equal(string.Empty, output.ToString());
                using JsonDocument document = JsonDocument.Parse(error.ToString());
                Assert.Equal(reason, document.RootElement.GetProperty("reason").GetString());
            }
        }
        finally
        {
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task RunAsync_WithDatabaseSymlinkResolvingInsideWorkspace_RejectsPhysicalContainment()
    {
        string root = TemporaryDirectory();
        string workspace = Path.Combine(root, "workspace");
        string alias = Path.Combine(root, "workspace-alias");
        Directory.CreateDirectory(workspace);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(alias, workspace);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                                                or IOException
                                                or PlatformNotSupportedException)
            {
                throw Xunit.Sdk.SkipException.ForSkip(
                    $"Directory symbolic links are unavailable in this test environment: {exception.Message}");
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            int exitCode = await CoupletRuntime.RunAsync(
                ComponentKind.Daemon,
                ["run", "--workspace", workspace, "--database", Path.Combine(alias, "database")],
                output,
                error,
                CancellationToken.None);

            Assert.Equal(64, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            using JsonDocument document = JsonDocument.Parse(error.ToString());
            Assert.Equal(
                "watched_database_must_be_outside_workspace",
                document.RootElement.GetProperty("reason").GetString());
            Assert.False(Directory.Exists(Path.Combine(workspace, "database")));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task RunAsync_WithDatabaseInsideWorkspace_FailsBeforeStartingWatcher()
    {
        string workspace = TemporaryDirectory();
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            int exitCode = await CoupletRuntime.RunAsync(
                ComponentKind.Daemon,
                ["run", "--workspace", workspace, "--database", Path.Combine(workspace, ".couplet")],
                output,
                error,
                CancellationToken.None);

            Assert.Equal(64, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            using JsonDocument document = JsonDocument.Parse(error.ToString());
            Assert.Equal(
                "watched_database_must_be_outside_workspace",
                document.RootElement.GetProperty("reason").GetString());
            Assert.False(Directory.Exists(Path.Combine(workspace, ".couplet")));
        }
        finally
        {
            DeleteTemporaryDirectory(workspace);
        }
    }
#else
    [Fact]
    public async Task RunAsync_WithMalformedWatchConfigurationOnPackageLane_RejectsBeforeCapabilityCheck()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            (string[] Arguments, string Reason)[] cases =
            [
                (["run", "--workspce", workspace], "unknown_watch_option"),
                (["run", "--workspace", workspace, "--workspace", workspace, "--database", database], "duplicate_watch_option"),
                (["run", "--workspace", workspace, "--database"], "missing_watch_option_value"),
            ];
            foreach ((string[] arguments, string reason) in cases)
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                int exitCode = await CoupletRuntime.RunAsync(
                    ComponentKind.Daemon,
                    arguments,
                    output,
                    error,
                    CancellationToken.None);

                Assert.Equal(64, exitCode);
                Assert.Equal(string.Empty, output.ToString());
                using JsonDocument document = JsonDocument.Parse(error.ToString());
                Assert.Equal(reason, document.RootElement.GetProperty("reason").GetString());
            }
        }
        finally
        {
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task RunAsync_WithWatchConfigurationOnPackageLane_ReportsGenerationPublishUnavailable()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            int exitCode = await CoupletRuntime.RunAsync(
                ComponentKind.Daemon,
                ["run", "--workspace", workspace, "--database", database],
                output,
                error,
                CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            using JsonDocument document = JsonDocument.Parse(error.ToString());
            Assert.Equal("capability_unavailable", document.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                "generation_publish_unavailable",
                document.RootElement.GetProperty("reason").GetString());
        }
        finally
        {
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }
#endif

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "couplet-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                return;
            }

            try
            {
                DeleteDirectoryTree(path);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50));
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50));
            }
        }

        DeleteDirectoryTree(path);
    }

    private static void DeleteDirectoryTree(string path)
    {
        var root = new DirectoryInfo(path);
        if (!root.Exists)
        {
            return;
        }

        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            root.Delete();
            return;
        }

        foreach (FileInfo file in root.EnumerateFiles())
        {
            file.Attributes = FileAttributes.Normal;
            file.Delete();
        }

        foreach (DirectoryInfo directory in root.EnumerateDirectories())
        {
            DeleteDirectoryTree(directory.FullName);
        }

        root.Attributes = FileAttributes.Normal;
        root.Delete();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    private static WorkspaceIndexSnapshot WithFailure(WorkspaceIndexSnapshot snapshot) => new()
    {
        WorkspaceId = snapshot.WorkspaceId,
        RepositoryIdentity = snapshot.RepositoryIdentity,
        WorktreeIdentity = snapshot.WorktreeIdentity,
        Branch = snapshot.Branch,
        HeadRevision = snapshot.HeadRevision,
        SourceRevision = snapshot.SourceRevision,
        IndexRevision = snapshot.IndexRevision,
        PreviousIndexRevision = snapshot.PreviousIndexRevision,
        ProducerVersions = snapshot.ProducerVersions,
        Files = snapshot.Files,
        Failures =
        [
            new FileIndexFailure
            {
                Path = "Sample.cs",
                Code = "file_changed_during_snapshot",
                AdapterId = "couplet.csharp",
            },
        ],
    };

    private static async Task StopDaemonAsync(CancellationTokenSource? cancellation, Task<int>? running)
    {
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        if (running is not null)
        {
            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }
        }

        cancellation.Dispose();
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        (int exitCode, _, string standardError) = await RunGitProcessAsync(workingDirectory, arguments);
        Assert.True(exitCode == 0, $"git {string.Join(' ', arguments)} failed: {standardError}");
    }

    private static async Task<string> RunGitCaptureAsync(string workingDirectory, params string[] arguments)
    {
        (int exitCode, string standardOutput, string standardError) = await RunGitProcessAsync(
            workingDirectory,
            arguments);
        Assert.True(exitCode == 0, $"git {string.Join(' ', arguments)} failed: {standardError}");
        return standardOutput.Trim();
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunGitProcessAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await standardOutput, await standardError);
    }

    private sealed class BlockingTextWriter : TextWriter
    {
        private readonly TaskCompletionSource _writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Encoding Encoding => Encoding.UTF8;

        internal Task WriteStarted => _writeStarted.Task;

        public override Task WriteLineAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            _ = buffer;
            _writeStarted.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FailingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteLineAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            _ = buffer;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException(new IOException("status output unavailable"));
        }
    }

    private sealed class LineCaptureTextWriter : TextWriter
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteLineAsync(string? value)
        {
            Capture(value ?? string.Empty);
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Capture(buffer.ToString());
            return Task.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        internal async Task<bool> HasLineWithinAsync(TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            try
            {
                _ = await _lines.Reader.ReadAsync(cancellation.Token);
                return true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return false;
            }
        }

        private void Capture(string value)
        {
            if (!_lines.Writer.TryWrite(value))
            {
                throw new InvalidOperationException("Could not capture watcher output.");
            }
        }

        internal async Task<JsonElement> ReadJsonAsync(CancellationToken cancellationToken)
        {
            string json = await _lines.Reader.ReadAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        internal async Task<JsonElement> ReadJsonUntilAsync(
            Func<JsonElement, bool> predicate,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                JsonElement current = await ReadJsonAsync(cancellationToken);
                if (predicate(current))
                {
                    return current;
                }
            }
        }
    }
}
