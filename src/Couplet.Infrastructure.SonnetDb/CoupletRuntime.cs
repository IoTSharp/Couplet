using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Couplet.Application.Capabilities;
using Couplet.Application.Hosting;
using Couplet.Application.Indexing;
using Couplet.Application.Mcp;
using Couplet.Application.Serialization;
using Couplet.Application.Workspaces;
using Couplet.Core.Capabilities;
using Couplet.Core.Evaluation;
using Couplet.Core.Indexing;
using Couplet.Core.Mcp;
using Couplet.Core.Workspaces;
using Microsoft.Win32.SafeHandles;
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
using SonnetDB.Generations;
#endif

namespace Couplet.Infrastructure.SonnetDb;

/// <summary>
/// 组合 Couplet application 层与 SonnetDB Core 适配器。
/// </summary>
public static class CoupletRuntime
{
    private static readonly TimeSpan DefaultWatchDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumWatchDebounce = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultWatchReconciliationInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinimumWatchReconciliationInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumWatchReconciliationInterval = TimeSpan.FromMinutes(10);
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
    private static readonly TimeSpan SnapshotRetryDelay = TimeSpan.FromMilliseconds(100);
    private const int SnapshotBuildAttempts = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint VolumeNameGuid = 0x1;
    private const string ProcessCrashTestHooksEnvironmentVariable =
        "COUPLET_ENABLE_PROCESS_CRASH_TEST_HOOKS";
    private const string ProcessCrashTestPublishPauseOption =
        "--internal-test-publish-pause";
#endif

    /// <summary>
    /// 通过指定输入输出运行一个 Couplet 组件。
    /// </summary>
    /// <param name="component">组件类型。</param>
    /// <param name="arguments">命令参数。</param>
    /// <param name="output">标准输出。</param>
    /// <param name="error">标准错误。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>进程退出码。</returns>
    public static Task<int> RunAsync(
        ComponentKind component,
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (ShouldRunIndexWatch(component, arguments))
        {
            return RunIndexWatchDaemonAsync(arguments, output, error, cancellationToken);
        }

        if (component == ComponentKind.Cli && arguments.Count > 0)
        {
            string command = arguments[0].Trim().ToLowerInvariant();
            if (command is "workspace-scan" or "index-stage" or "c1-capacity")
            {
                return RunIndexCommandAsync(command, arguments, output, error, cancellationToken);
            }
        }
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
        else if (component == ComponentKind.McpServer
            && arguments.Count > 0
            && string.Equals(arguments[0], "serve", StringComparison.OrdinalIgnoreCase)
            && Option(arguments, "--database") is not null)
        {
            return RunMcpServerAsync(
                arguments,
                TextReader.Null,
                output,
                error,
                cancellationToken);
        }
#endif

        var probe = new SonnetDbCapabilityProbe();
        var reportService = new CapabilityReportService(probe);
        var runner = new ComponentRunner(reportService);
        return runner.RunAsync(component, arguments, output, error, cancellationToken);
    }

    /// <summary>
    /// 通过指定标准输入输出运行一个 Couplet 组件。
    /// </summary>
    /// <param name="component">组件类型。</param>
    /// <param name="arguments">命令参数。</param>
    /// <param name="input">标准输入。</param>
    /// <param name="output">标准输出。</param>
    /// <param name="error">标准错误。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>进程退出码。</returns>
    public static Task<int> RunAsync(
        ComponentKind component,
        IReadOnlyList<string> arguments,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (ShouldRunIndexWatch(component, arguments))
        {
            return RunIndexWatchDaemonAsync(arguments, output, error, cancellationToken);
        }

        if (component == ComponentKind.Cli && arguments.Count > 0)
        {
            string command = arguments[0].Trim().ToLowerInvariant();
            if (command is "workspace-scan" or "index-stage" or "c1-capacity")
            {
                return RunIndexCommandAsync(command, arguments, output, error, cancellationToken);
            }
        }
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
        else if (component == ComponentKind.McpServer
            && arguments.Count > 0
            && string.Equals(arguments[0], "serve", StringComparison.OrdinalIgnoreCase)
            && Option(arguments, "--database") is not null)
        {
            return RunMcpServerAsync(arguments, input, output, error, cancellationToken);
        }
#endif

        var probe = new SonnetDbCapabilityProbe();
        var reportService = new CapabilityReportService(probe);
        var runner = new ComponentRunner(reportService);
        return runner.RunAsync(component, arguments, input, output, error, cancellationToken);
    }

    /// <summary>
    /// 使用控制台输入输出和 Ctrl+C 生命周期运行一个 Couplet 组件。
    /// </summary>
    /// <param name="component">组件类型。</param>
    /// <param name="arguments">命令参数。</param>
    /// <returns>进程退出码。</returns>
    public static async Task<int> RunConsoleAsync(ComponentKind component, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        EventHandler processExitHandler = (_, _) => cancellation.Cancel();

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        try
        {
            return await RunAsync(
                component,
                arguments,
                Console.In,
                Console.Out,
                Console.Error,
                cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
        }
    }

    private static bool ShouldRunIndexWatch(ComponentKind component, IReadOnlyList<string> arguments) =>
        component == ComponentKind.Daemon
        && arguments.Count > 1
        && string.Equals(arguments[0].Trim(), "run", StringComparison.OrdinalIgnoreCase);

#if COUPLET_SONNETDB_SOURCE_GENERATIONS
    private static Task<int> RunIndexWatchDaemonAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) => RunIndexWatchDaemonCoreAsync(
            arguments,
            output,
            error,
            IndexSnapshotBuilder.BuildAsync,
            discoveryObserver: null,
            cancellationToken);

    internal static Task<int> RunIndexWatchDaemonForTestingAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<DiscoveredWorkspace, string?, CancellationToken, Task<WorkspaceIndexSnapshot>> snapshotBuilder,
        CancellationToken cancellationToken,
        Action<DiscoveredWorkspace>? discoveryObserver = null)
    {
        ArgumentNullException.ThrowIfNull(snapshotBuilder);
        return RunIndexWatchDaemonCoreAsync(
            arguments,
            output,
            error,
            snapshotBuilder,
            discoveryObserver,
            cancellationToken);
    }

    private static async Task<int> RunIndexWatchDaemonCoreAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<DiscoveredWorkspace, string?, CancellationToken, Task<WorkspaceIndexSnapshot>> snapshotBuilder,
        Action<DiscoveredWorkspace>? discoveryObserver,
        CancellationToken cancellationToken)
    {
        if (!TryParseIndexWatchOptions(arguments, out IndexWatchOptions? options, out string? invalidReason))
        {
            return await WriteIndexWatchErrorAsync(error, invalidReason!)
                .ConfigureAwait(false);
        }

        IndexWatchOptions watchOptions = options!;
        try
        {
            string workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(watchOptions.WorkspacePath));
            string databaseRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(watchOptions.DatabasePath));
            string physicalWorkspaceRoot = ResolvePhysicalDirectory(workspaceRoot);
            string physicalDatabaseCandidate = ResolvePhysicalDirectoryCandidate(databaseRoot);
            if (IsWithinDirectory(physicalWorkspaceRoot, physicalDatabaseCandidate))
            {
                return await WriteIndexWatchErrorAsync(
                    error,
                    "watched_database_must_be_outside_workspace").ConfigureAwait(false);
            }

            Directory.CreateDirectory(physicalDatabaseCandidate);
            string physicalDatabaseRoot = ResolvePhysicalDirectory(physicalDatabaseCandidate);
            if (IsWithinDirectory(physicalWorkspaceRoot, physicalDatabaseRoot))
            {
                return await WriteIndexWatchErrorAsync(
                    error,
                    "watched_database_must_be_outside_workspace").ConfigureAwait(false);
            }

            WorkspaceDiscoveryPolicy policy = CreateWatchPolicy(watchOptions);
            using var monitor = new WorkspaceChangeMonitor(workspaceRoot);
            DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                workspaceRoot,
                policy,
                cancellationToken).ConfigureAwait(false);
            string workspaceId = discovered.Result.WorkspaceId;
            using var store = new SonnetDbIndexGenerationStore(
                physicalDatabaseRoot,
                watchOptions.RetiredGenerationRetention);

            IndexWatchPublishResult initial = await PublishCurrentWorkspaceAsync(
                    workspaceRoot,
                    physicalWorkspaceRoot,
                    physicalDatabaseRoot,
                    workspaceId,
                    policy,
                    store,
                    snapshotBuilder,
                    discoveryObserver,
                    previousState: null,
                    force: true,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Initial watcher reconciliation must publish a status.");
            if (!initial.Report.Staged)
            {
                return 1;
            }

            int? outputFailure = await WriteIndexWatchReportAsync(
                initial.Report,
                output,
                error,
                cancellationToken).ConfigureAwait(false);
            if (outputFailure is { } initialOutputExitCode)
            {
                return initialOutputExitCode;
            }

            IndexWatchState state = initial.State;
            using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken watchToken = watchCancellation.Token;
            await using IAsyncEnumerator<WorkspaceChangeBatch> batches = monitor
                .WatchAsync(watchOptions.WatchDebounce, watchToken)
                .GetAsyncEnumerator(watchToken);
            using var reconciliationTimer = new PeriodicTimer(watchOptions.WatchReconciliationInterval);
            Task<bool> nextChange = batches.MoveNextAsync().AsTask();
            Task<bool> nextReconciliation = reconciliationTimer
                .WaitForNextTickAsync(watchToken)
                .AsTask();
            try
            {
                while (true)
                {
                    Task completed = await Task.WhenAny(nextChange, nextReconciliation).ConfigureAwait(false);
                    if (completed == nextChange)
                    {
                        if (!await nextChange.ConfigureAwait(false))
                        {
                            return 0;
                        }

                        nextChange = batches.MoveNextAsync().AsTask();
                    }
                    else
                    {
                        if (!await nextReconciliation.ConfigureAwait(false))
                        {
                            return 0;
                        }

                        nextReconciliation = reconciliationTimer
                            .WaitForNextTickAsync(watchToken)
                            .AsTask();
                    }

                    IndexWatchPublishResult? current = await PublishCurrentWorkspaceAsync(
                        workspaceRoot,
                        physicalWorkspaceRoot,
                        physicalDatabaseRoot,
                        workspaceId,
                        policy,
                        store,
                        snapshotBuilder,
                        discoveryObserver,
                        state,
                        force: false,
                        cancellationToken).ConfigureAwait(false);
                    if (current is null)
                    {
                        continue;
                    }

                    state = current.State;
                    if (!current.Report.Staged)
                    {
                        return 1;
                    }

                    outputFailure = await WriteIndexWatchReportAsync(
                        current.Report,
                        output,
                        error,
                        cancellationToken).ConfigureAwait(false);
                    if (outputFailure is { } outputExitCode)
                    {
                        return outputExitCode;
                    }
                }
            }
            finally
            {
                watchCancellation.Cancel();
                await ObserveWatchWaitCompletionAsync(nextChange, watchToken).ConfigureAwait(false);
                await ObserveWatchWaitCompletionAsync(nextReconciliation, watchToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (DirectoryNotFoundException)
        {
            return await WriteIndexWatchErrorAsync(error, "workspace_not_found").ConfigureAwait(false);
        }
        catch (IOException)
        {
            return await WriteIndexWatchErrorAsync(error, "index_io_failed").ConfigureAwait(false);
        }
        catch (DatabaseGenerationException exception)
        {
            return await WriteIndexWatchErrorAsync(error, exception.Code).ConfigureAwait(false);
        }
        catch (IndexWatchException exception)
        {
            return await WriteIndexWatchErrorAsync(
                error,
                exception.Reason,
                exception.Code,
                exception.ExitCode).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return await WriteIndexWatchErrorAsync(error, "invalid_watch_path").ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            return await WriteIndexWatchErrorAsync(error, "watch_path_access_denied").ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 5)
        {
            return await WriteIndexWatchErrorAsync(error, "watch_path_access_denied").ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return await WriteIndexWatchErrorAsync(error, "index_io_failed").ConfigureAwait(false);
        }
    }

    private static async Task<IndexWatchPublishResult?> PublishCurrentWorkspaceAsync(
        string workspaceRoot,
        string physicalWorkspaceRoot,
        string physicalDatabaseRoot,
        string workspaceId,
        WorkspaceDiscoveryPolicy policy,
        SonnetDbIndexGenerationStore store,
        Func<DiscoveredWorkspace, string?, CancellationToken, Task<WorkspaceIndexSnapshot>> snapshotBuilder,
        Action<DiscoveredWorkspace>? discoveryObserver,
        IndexWatchState? previousState,
        bool force,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= SnapshotBuildAttempts; attempt++)
        {
            bool retrySnapshot;
            using (IndexWriterFence writerFence = await IndexWriterFence.AcquireAsync(
                       physicalDatabaseRoot,
                       workspaceId,
                       cancellationToken).ConfigureAwait(false))
            {
                string currentPhysicalWorkspaceRoot = ResolvePhysicalDirectory(workspaceRoot);
                if (!PathEquals(physicalWorkspaceRoot, currentPhysicalWorkspaceRoot)
                    || IsWithinDirectory(currentPhysicalWorkspaceRoot, physicalDatabaseRoot))
                {
                    throw new IndexWatchException(
                        "workspace_physical_identity_changed_while_watching",
                        "workspace_changed",
                        1);
                }

                DiscoveredWorkspace workspace = await WorkspaceDiscoveryService.DiscoverAsync(
                    workspaceRoot,
                    policy,
                    cancellationToken).ConfigureAwait(false);
                discoveryObserver?.Invoke(workspace);
                if (!string.Equals(workspaceId, workspace.Result.WorkspaceId, StringComparison.Ordinal))
                {
                    throw new IndexWatchException(
                        "workspace_identity_changed_while_watching",
                        "workspace_changed",
                        1);
                }

                ActiveIndexPlanningSnapshot? active = store.ReadActivePlanningSnapshot(workspaceId);
                string fingerprint = CreateWorkspaceFingerprint(workspace.Result);
                if (!force
                    && previousState is { } expected
                    && string.Equals(expected.WorkspaceFingerprint, fingerprint, StringComparison.Ordinal)
                    && expected.DatabaseGenerationRevision == active?.DatabaseGenerationRevision
                    && string.Equals(
                        expected.IndexRevision,
                        active?.PlanningSnapshot.IndexRevision,
                        StringComparison.Ordinal))
                {
                    return null;
                }

                WorkspaceIndexSnapshot snapshot = await snapshotBuilder(
                    workspace,
                    active?.PlanningSnapshot.IndexRevision,
                    cancellationToken).ConfigureAwait(false);
                string snapshotPhysicalWorkspaceRoot = ResolvePhysicalDirectory(workspaceRoot);
                if (!PathEquals(physicalWorkspaceRoot, snapshotPhysicalWorkspaceRoot)
                    || IsWithinDirectory(snapshotPhysicalWorkspaceRoot, physicalDatabaseRoot))
                {
                    throw new IndexWatchException(
                        "workspace_physical_identity_changed_during_snapshot",
                        "workspace_changed",
                        1);
                }

                retrySnapshot = snapshot.Failures.Count > 0;
                if (!retrySnapshot)
                {
                    IncrementalIndexPlan plan = IncrementalIndexPlanner.PlanFromPublished(
                        active?.PlanningSnapshot,
                        snapshot);
                    IndexStageReport report = store.StageAndPublish(
                        snapshot,
                        plan,
                        active?.DatabaseGenerationRevision ?? 0,
                        cancellationToken);
                    return new IndexWatchPublishResult(
                        report,
                        new IndexWatchState(
                            fingerprint,
                            report.DatabaseGenerationRevision,
                            report.Manifest.IndexRevision));
                }
            }

            if (attempt == SnapshotBuildAttempts)
            {
                throw new IndexWatchException(
                    "workspace_snapshot_incomplete",
                    "indexing_failed",
                    1);
            }

            await Task.Delay(SnapshotRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Snapshot retry loop exited unexpectedly.");
    }
#else
    private static Task<int> RunIndexWatchDaemonAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!TryParseIndexWatchOptions(arguments, out _, out string? invalidReason))
        {
            return WriteIndexWatchErrorAsync(error, invalidReason!);
        }

        _ = output;
        _ = cancellationToken;
        return WriteIndexWatchErrorAsync(
            error,
            "generation_publish_unavailable",
            "capability_unavailable",
            2);
    }
#endif

    private static async Task<int> RunIndexCommandAsync(
        string command,
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string? workspacePath = Option(arguments, "--workspace");
        if (workspacePath is null)
        {
            return await WriteIndexErrorAsync(error, "explicit_workspace_required").ConfigureAwait(false);
        }

        try
        {
            if (command == "c1-capacity")
            {
                return await RunC1CapacityAsync(
                    arguments,
                    workspacePath,
                    output,
                    error,
                    cancellationToken).ConfigureAwait(false);
            }

            WorkspaceDiscoveryPolicy policy = CreatePolicy(arguments);
            DiscoveredWorkspace workspace = await WorkspaceDiscoveryService.DiscoverAsync(
                workspacePath,
                policy,
                cancellationToken).ConfigureAwait(false);
            if (command == "workspace-scan")
            {
                await output.WriteLineAsync(CoupletJsonSerializer.Serialize(workspace.Result)).ConfigureAwait(false);
                return 0;
            }

            string? databasePath = Option(arguments, "--database");
            if (databasePath is null)
            {
                return await WriteIndexErrorAsync(error, "explicit_database_required").ConfigureAwait(false);
            }

#if COUPLET_SONNETDB_SOURCE_GENERATIONS
            if (!TryGetRetiredGenerationRetention(arguments, out TimeSpan retiredGenerationRetention))
            {
                return await WriteIndexErrorAsync(
                    error,
                    "invalid_retired_generation_retention").ConfigureAwait(false);
            }

            if (!TryGetProcessCrashTestPublishPause(
                    arguments,
                    out IndexGenerationPublishFaultPoint? processCrashTestPublishPause))
            {
                return await WriteIndexErrorAsync(
                    error,
                    "invalid_process_crash_test_publish_pause").ConfigureAwait(false);
            }

            if (processCrashTestPublishPause is not null
                && !string.Equals(
                    Environment.GetEnvironmentVariable(ProcessCrashTestHooksEnvironmentVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                return await WriteIndexErrorAsync(
                    error,
                    "process_crash_test_hooks_disabled").ConfigureAwait(false);
            }
#endif

            using IndexWriterFence writerFence = await IndexWriterFence.AcquireAsync(
                databasePath,
                workspace.Result.WorkspaceId,
                cancellationToken).ConfigureAwait(false);
            string lockedWorkspaceId = workspace.Result.WorkspaceId;
            workspace = await WorkspaceDiscoveryService.DiscoverAsync(
                workspacePath,
                policy,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    lockedWorkspaceId,
                    workspace.Result.WorkspaceId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("workspace_identity_changed_while_waiting_for_writer");
            }

#if COUPLET_SONNETDB_SOURCE_GENERATIONS
            using var store = new SonnetDbIndexGenerationStore(
                databasePath,
                retiredGenerationRetention);
            ConfigureProcessCrashTestPublishPause(
                store,
                processCrashTestPublishPause,
                output,
                cancellationToken);
            ActiveIndexPlanningSnapshot? active = store.ReadActivePlanningSnapshot(
                workspace.Result.WorkspaceId);
            WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(
                workspace,
                active?.PlanningSnapshot.IndexRevision,
                cancellationToken).ConfigureAwait(false);
            if (snapshot.Failures.Count != 0)
            {
                return await WriteIndexErrorAsync(
                    error,
                    "workspace_snapshot_incomplete",
                    "indexing_failed",
                    1).ConfigureAwait(false);
            }

            IncrementalIndexPlan plan = IncrementalIndexPlanner.PlanFromPublished(
                active?.PlanningSnapshot,
                snapshot);
            IndexStageReport report = store.StageAndPublish(
                snapshot,
                plan,
                active?.DatabaseGenerationRevision ?? 0,
                cancellationToken);
#else
            using var store = new SonnetDbIndexGenerationStore(databasePath);
            WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(
                workspace,
                previousIndexRevision: null,
                cancellationToken).ConfigureAwait(false);
            if (snapshot.Failures.Count != 0)
            {
                return await WriteIndexErrorAsync(
                    error,
                    "workspace_snapshot_incomplete",
                    "indexing_failed",
                    1).ConfigureAwait(false);
            }

            IncrementalIndexPlan plan = IncrementalIndexPlanner.Plan(previous: null, snapshot);
            IndexStageReport report = store.Stage(snapshot, plan, cancellationToken);
#endif
            await output.WriteLineAsync(CoupletJsonSerializer.Serialize(report)).ConfigureAwait(false);
            return report.Staged ? 0 : 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await WriteIndexErrorAsync(error, "index_operation_cancelled").ConfigureAwait(false);
        }
        catch (DirectoryNotFoundException)
        {
            return await WriteIndexErrorAsync(error, "workspace_not_found").ConfigureAwait(false);
        }
        catch (IOException)
        {
            return await WriteIndexErrorAsync(error, "index_io_failed").ConfigureAwait(false);
        }
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
        catch (DatabaseGenerationException exception)
        {
            return await WriteIndexErrorAsync(error, exception.Code).ConfigureAwait(false);
        }
#endif
    }

#if COUPLET_SONNETDB_SOURCE_GENERATIONS
    private static async Task<int> RunMcpServerAsync(
        IReadOnlyList<string> arguments,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string? workspacePath = Option(arguments, "--workspace");
        string? databasePath = Option(arguments, "--database");
        if (workspacePath is null || databasePath is null)
        {
            return await WriteMcpStartupErrorAsync(
                error,
                McpErrorCodes.InvalidRequest,
                workspacePath is null ? "explicit_workspace_required" : "explicit_database_required",
                retryable: false).ConfigureAwait(false);
        }

        try
        {
            DiscoveredWorkspace workspace = await WorkspaceDiscoveryService.DiscoverAsync(
                workspacePath,
                CreatePolicy(arguments),
                cancellationToken).ConfigureAwait(false);
            using var store = new SonnetDbIndexGenerationStore(databasePath);

            string? activeIndexRevision = null;
            WorkspaceDatabaseState databaseState = WorkspaceDatabaseState.Empty;
            try
            {
                using DatabaseGenerationQueryLease lease = store.AcquireActiveGeneration(
                    workspace.Result.WorkspaceId);
                activeIndexRevision = lease.Generation.GenerationId;
                databaseState = WorkspaceDatabaseState.Current;
            }
            catch (DatabaseGenerationException exception)
                when (exception.Code == DatabaseGenerationErrorCodes.NoActiveGeneration)
            {
                // Empty is a valid server state; workspace_status reports index_not_ready.
            }

            var binding = new Couplet.Core.Mcp.WorkspaceBinding
            {
                WorkspaceId = workspace.Result.WorkspaceId,
                RepositoryIdentity = workspace.Result.RepositoryIdentity,
                SourceRevision = workspace.Result.SourceRevision,
                IndexRevision = activeIndexRevision,
            };
            long databaseBytesAtStartup = SonnetDbMcpToolExecutor.SampleDatabaseBytes(
                databasePath,
                cancellationToken);
            var executor = new SonnetDbMcpToolExecutor(store, databaseBytesAtStartup);
            var host = new McpProtocolHost(
                binding,
                executor,
                databaseState,
                McpWorkspaceBinder.CreateC1Capabilities());
            await host.RunAsync(input, output, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await WriteMcpStartupErrorAsync(
                error,
                McpErrorCodes.Cancelled,
                "mcp_startup_cancelled",
                retryable: false).ConfigureAwait(false);
        }
        catch (DirectoryNotFoundException)
        {
            return await WriteMcpStartupErrorAsync(
                error,
                McpErrorCodes.WorkspaceNotFound,
                "configured_workspace_not_found",
                retryable: false).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or OverflowException
            or DatabaseGenerationException)
        {
            return await WriteMcpStartupErrorAsync(
                error,
                McpErrorCodes.IndexCorrupt,
                "database_open_or_generation_read_failed",
                retryable: true).ConfigureAwait(false);
        }
    }

    private static async Task<int> WriteMcpStartupErrorAsync(
        TextWriter error,
        string code,
        string reason,
        bool retryable)
    {
        await error.WriteLineAsync(CoupletJsonSerializer.Serialize(new Couplet.Core.Mcp.McpError
        {
            Code = code,
            Reason = reason,
            Retryable = retryable,
            CorrelationId = "startup",
        })).ConfigureAwait(false);
        return code == McpErrorCodes.InvalidRequest ? 64 : 2;
    }
#endif

    private static async Task<int> RunC1CapacityAsync(
        IReadOnlyList<string> arguments,
        string workspacePath,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string? databasePath = Option(arguments, "--database");
        string? scaleId = Option(arguments, "--scale");
        if (databasePath is null || scaleId is null)
        {
            return await WriteIndexErrorAsync(error, "capacity_database_and_scale_required").ConfigureAwait(false);
        }

        string repository = Option(arguments, "--repository") ?? Environment.CurrentDirectory;
        string manifestPath = Option(arguments, "--fixture-manifest")
            ?? Path.Combine(repository, "fixtures", "c1", "capacity-manifest.v1.json");
        string manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        FixtureManifest manifest = CoupletJsonSerializer.DeserializeFixtureManifest(manifestJson);
        CorpusScaleDefinition? scale = manifest.Scales.SingleOrDefault(
            candidate => string.Equals(candidate.Id, scaleId, StringComparison.Ordinal));
        if (scale is null)
        {
            return await WriteIndexErrorAsync(error, "capacity_scale_unknown").ConfigureAwait(false);
        }

        int querySamples = 30;
        string? samplesValue = Option(arguments, "--query-samples");
        if (samplesValue is not null
            && (!int.TryParse(samplesValue, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out querySamples)
                || querySamples < 3))
        {
            return await WriteIndexErrorAsync(error, "capacity_query_samples_invalid").ConfigureAwait(false);
        }

        string commit = Option(arguments, "--commit")
            ?? Environment.GetEnvironmentVariable("GITHUB_SHA")
            ?? "working_tree";
        C1CapacityEvidenceReport report = await C1CapacityEvidenceRunner.RunAsync(
            scale,
            manifest.GeneratorVersion,
            manifestJson,
            workspacePath,
            databasePath,
            commit,
            querySamples,
            cancellationToken).ConfigureAwait(false);
        string reportJson = CoupletJsonSerializer.Serialize(report);
        string? reportPath = Option(arguments, "--report");
        if (reportPath is not null)
        {
            await File.WriteAllTextAsync(
                Path.GetFullPath(reportPath),
                reportJson + Environment.NewLine,
                new System.Text.UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
        }

        await output.WriteLineAsync(reportJson).ConfigureAwait(false);
        return 0;
    }

    private static WorkspaceDiscoveryPolicy CreatePolicy(IReadOnlyList<string> arguments)
    {
        WorkspaceDiscoveryPolicy defaults = WorkspaceDiscoveryService.DefaultPolicy;
        return new WorkspaceDiscoveryPolicy
        {
            IgnorePatterns = defaults.IgnorePatterns.Concat(OptionValues(arguments, "--ignore")).ToArray(),
            DenyPatterns = defaults.DenyPatterns.Concat(OptionValues(arguments, "--deny")).ToArray(),
            GeneratedPatterns = defaults.GeneratedPatterns,
            MaxSemanticFileBytes = defaults.MaxSemanticFileBytes,
        };
    }

#if COUPLET_SONNETDB_SOURCE_GENERATIONS
    private static WorkspaceDiscoveryPolicy CreateWatchPolicy(IndexWatchOptions options)
    {
        WorkspaceDiscoveryPolicy defaults = WorkspaceDiscoveryService.DefaultPolicy;
        return new WorkspaceDiscoveryPolicy
        {
            IgnorePatterns = defaults.IgnorePatterns.Concat(options.IgnorePatterns).ToArray(),
            DenyPatterns = defaults.DenyPatterns.Concat(options.DenyPatterns).ToArray(),
            GeneratedPatterns = defaults.GeneratedPatterns,
            MaxSemanticFileBytes = defaults.MaxSemanticFileBytes,
        };
    }
#endif

    private static string? Option(IReadOnlyList<string> arguments, string name)
    {
        for (int index = 1; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static List<string> OptionValues(IReadOnlyList<string> arguments, string name)
    {
        var values = new List<string>();
        for (int index = 1; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.Ordinal))
            {
                values.Add(arguments[index + 1]);
                index++;
            }
        }

        return values;
    }

    private static bool TryParseIndexWatchOptions(
        IReadOnlyList<string> arguments,
        out IndexWatchOptions? options,
        out string? invalidReason)
    {
        string? workspacePath = null;
        string? databasePath = null;
        TimeSpan debounce = DefaultWatchDebounce;
        TimeSpan reconciliationInterval = DefaultWatchReconciliationInterval;
        TimeSpan retention = TimeSpan.Zero;
        var ignorePatterns = new List<string>();
        var denyPatterns = new List<string>();
        var singletons = new HashSet<string>(StringComparer.Ordinal);
        options = null;
        invalidReason = null;

        for (int index = 1; index < arguments.Count; index++)
        {
            string name = arguments[index];
            bool repeatable = name is "--ignore" or "--deny";
            if (name is not ("--workspace"
                or "--database"
                or "--watch-debounce"
                or "--watch-reconciliation-interval"
                or "--retired-generation-retention"
                or "--ignore"
                or "--deny"))
            {
                invalidReason = name.StartsWith("--", StringComparison.Ordinal)
                    ? "unknown_watch_option"
                    : "unknown_watch_argument";
                return false;
            }

            bool duplicate = !repeatable && !singletons.Add(name);
            if (duplicate)
            {
                invalidReason = "duplicate_watch_option";
                return false;
            }

            if (index == arguments.Count - 1
                || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                invalidReason = "missing_watch_option_value";
                return false;
            }

            string value = arguments[++index];
            if (string.IsNullOrWhiteSpace(value))
            {
                invalidReason = "missing_watch_option_value";
                return false;
            }

            switch (name)
            {
                case "--workspace":
                    workspacePath = value;
                    break;
                case "--database":
                    databasePath = value;
                    break;
                case "--watch-debounce":
                    if (!TimeSpan.TryParseExact(
                            value,
                            "c",
                            System.Globalization.CultureInfo.InvariantCulture,
                            out debounce)
                        || debounce <= TimeSpan.Zero
                        || debounce > MaximumWatchDebounce)
                    {
                        invalidReason = "invalid_watch_debounce";
                        return false;
                    }

                    break;
                case "--watch-reconciliation-interval":
                    if (!TimeSpan.TryParseExact(
                            value,
                            "c",
                            System.Globalization.CultureInfo.InvariantCulture,
                            out reconciliationInterval)
                        || reconciliationInterval < MinimumWatchReconciliationInterval
                        || reconciliationInterval > MaximumWatchReconciliationInterval)
                    {
                        invalidReason = "invalid_watch_reconciliation_interval";
                        return false;
                    }

                    break;
                case "--retired-generation-retention":
                    if (!TimeSpan.TryParseExact(
                            value,
                            "c",
                            System.Globalization.CultureInfo.InvariantCulture,
                            out retention)
                        || retention < TimeSpan.Zero)
                    {
                        invalidReason = "invalid_retired_generation_retention";
                        return false;
                    }

                    break;
                case "--ignore":
                    ignorePatterns.Add(value);
                    break;
                case "--deny":
                    denyPatterns.Add(value);
                    break;
            }
        }

        if (workspacePath is null || databasePath is null)
        {
            invalidReason = workspacePath is null
                ? "explicit_workspace_required"
                : "explicit_database_required";
            return false;
        }

        options = new IndexWatchOptions(
            workspacePath,
            databasePath,
            debounce,
            reconciliationInterval,
            retention,
            ignorePatterns,
            denyPatterns);
        return true;
    }

#if COUPLET_SONNETDB_SOURCE_GENERATIONS
    private static void ConfigureProcessCrashTestPublishPause(
        SonnetDbIndexGenerationStore store,
        IndexGenerationPublishFaultPoint? pausePoint,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (pausePoint is not { } expectedPoint)
        {
            return;
        }

        store.PublishFaultTestHook = observedPoint =>
        {
            if (observedPoint != expectedPoint)
            {
                return;
            }

            string pointName = observedPoint == IndexGenerationPublishFaultPoint.BeforeCommit
                ? "before-commit"
                : "after-commit";
            output.WriteLine($"couplet.internal-test.publish-paused:{pointName}");
            output.Flush();
            using var processCrashWait = new ManualResetEventSlim(initialState: false);
            processCrashWait.Wait(cancellationToken);
        };
    }

    private static bool TryGetProcessCrashTestPublishPause(
        IReadOnlyList<string> arguments,
        out IndexGenerationPublishFaultPoint? pausePoint)
    {
        pausePoint = null;
        bool found = false;
        for (int index = 1; index < arguments.Count; index++)
        {
            if (!string.Equals(
                    arguments[index],
                    ProcessCrashTestPublishPauseOption,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (found || index == arguments.Count - 1)
            {
                return false;
            }

            found = true;
            pausePoint = arguments[++index] switch
            {
                "before-commit" => IndexGenerationPublishFaultPoint.BeforeCommit,
                "after-commit" => IndexGenerationPublishFaultPoint.AfterCommit,
                _ => null,
            };
            if (pausePoint is null)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetRetiredGenerationRetention(
        IReadOnlyList<string> arguments,
        out TimeSpan retention)
    {
        retention = TimeSpan.Zero;
        bool found = false;
        for (int index = 1; index < arguments.Count; index++)
        {
            if (!string.Equals(
                    arguments[index],
                    "--retired-generation-retention",
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (found || index == arguments.Count - 1)
            {
                return false;
            }

            found = true;
            if (!TimeSpan.TryParseExact(
                    arguments[++index],
                    "c",
                    System.Globalization.CultureInfo.InvariantCulture,
                    out retention)
                || retention < TimeSpan.Zero)
            {
                return false;
            }
        }

        return true;
    }
#endif

#if COUPLET_SONNETDB_SOURCE_GENERATIONS
    private static string CreateWorkspaceFingerprint(WorkspaceDiscoveryResult workspace)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            AppendFingerprintValue(hash, buffer, "couplet.workspace-watch.v2");
            AppendFingerprintValue(hash, buffer, workspace.WorkspaceId);
            AppendFingerprintValue(hash, buffer, workspace.RepositoryIdentity);
            AppendFingerprintValue(hash, buffer, workspace.WorktreeIdentity);
            AppendFingerprintValue(hash, buffer, workspace.SourceRevision);
            AppendFingerprintValue(hash, buffer, workspace.Branch);
            AppendFingerprintValue(hash, buffer, workspace.HeadRevision);
            Span<byte> metadata = stackalloc byte[14];
            foreach (WorkspaceFileDescriptor file in workspace.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                AppendFingerprintValue(hash, buffer, file.Path);
                AppendFingerprintValue(hash, buffer, file.ContentHash);
                AppendFingerprintValue(hash, buffer, file.Reason);
                AppendFingerprintValue(hash, buffer, file.Language);
                BinaryPrimitives.WriteInt64LittleEndian(metadata, file.Length);
                BinaryPrimitives.WriteInt32LittleEndian(metadata[sizeof(long)..], (int)file.Disposition);
                metadata[12] = file.TextOnly ? (byte)1 : (byte)0;
                metadata[13] = file.IsSymlink ? (byte)1 : (byte)0;
                hash.AppendData(metadata);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void AppendFingerprintValue(IncrementalHash hash, byte[] buffer, string? value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        if (value is null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(length, -1);
            hash.AppendData(length);
            return;
        }

        int byteCount = Encoding.UTF8.GetByteCount(value);
        BinaryPrimitives.WriteInt32LittleEndian(length, byteCount);
        hash.AppendData(length);
        if (byteCount == 0)
        {
            return;
        }

        Encoder encoder = Encoding.UTF8.GetEncoder();
        ReadOnlySpan<char> remaining = value;
        bool completed = false;
        while (!completed)
        {
            encoder.Convert(
                remaining,
                buffer,
                flush: true,
                out int charsUsed,
                out int bytesUsed,
                out completed);
            hash.AppendData(buffer.AsSpan(0, bytesUsed));
            remaining = remaining[charsUsed..];
        }
    }

    private static string ResolvePhysicalDirectoryCandidate(string path)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var missingSegments = new Stack<string>();
        string existingAncestor = fullPath;
        while (!Directory.Exists(existingAncestor))
        {
            string segment = Path.GetFileName(existingAncestor);
            DirectoryInfo? parent = Directory.GetParent(existingAncestor);
            if (parent is null || string.IsNullOrEmpty(segment))
            {
                throw new DirectoryNotFoundException("No existing ancestor was found for the configured directory.");
            }

            missingSegments.Push(segment);
            existingAncestor = parent.FullName;
        }

        string physicalPath = ResolvePhysicalDirectory(existingAncestor);
        while (missingSegments.TryPop(out string? segment))
        {
            physicalPath = Path.Combine(physicalPath, segment);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(physicalPath));
    }

    private static string ResolvePhysicalDirectory(string path)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("The configured directory was not found.");
        }

        string root = Path.GetPathRoot(fullPath)
            ?? throw new IOException("The configured directory has no filesystem root.");
        string current = root;
        string relative = Path.GetRelativePath(root, fullPath);
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(current, segment));
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException("A configured directory component was not found.");
            }

            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || directory.LinkTarget is not null)
            {
                FileSystemInfo target = directory.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new IOException("A configured directory link could not be resolved.");
                current = Path.GetFullPath(target.FullName);
            }
            else
            {
                current = directory.FullName;
            }
        }

        string resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        return OperatingSystem.IsWindows()
            ? GetFinalWindowsDirectoryPath(resolved)
            : resolved;
    }

    private static string GetFinalWindowsDirectoryPath(string path)
    {
        using SafeFileHandle handle = CreateFileW(
            path,
            desiredAccess: 0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        string? finalPath = TryGetFinalPathName(handle, VolumeNameGuid)
            ?? TryGetFinalPathName(handle, flags: 0);
        if (finalPath is null)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        return Path.TrimEndingDirectorySeparator(finalPath);
    }

    private static string? TryGetFinalPathName(SafeFileHandle handle, uint flags)
    {
        int capacity = 512;
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(checked(capacity * sizeof(char)));
            try
            {
                uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)capacity, flags);
                if (length == 0)
                {
                    return null;
                }

                if (length < capacity)
                {
                    return Marshal.PtrToStringUni(buffer, checked((int)length));
                }

                capacity = checked((int)length + 1);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        IntPtr filePath,
        uint filePathLength,
        uint flags);

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsWithinDirectory(string rootPath, string candidate)
    {
        string relative = Path.GetRelativePath(rootPath, candidate);
        return relative == "."
            || (!Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relative.StartsWith("../", StringComparison.Ordinal));
    }

    private static async Task<int?> WriteIndexWatchReportAsync(
        IndexStageReport report,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string json = CoupletJsonSerializer.Serialize(report);
        try
        {
            await output.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or ObjectDisposedException
            or InvalidOperationException)
        {
            string reason = report.Published && !report.ReusedActiveGeneration
                ? "index_committed_status_output_failed"
                : "index_status_output_failed";
            return await WriteIndexWatchErrorAsync(
                error,
                reason,
                "output_failed",
                74).ConfigureAwait(false);
        }
    }

    private static async Task ObserveWatchWaitCompletionAsync(Task<bool> wait, CancellationToken cancellationToken)
    {
        try
        {
            _ = await wait.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private readonly record struct IndexWatchState(
        string WorkspaceFingerprint,
        long? DatabaseGenerationRevision,
        string IndexRevision);

    private sealed record IndexWatchPublishResult(
        IndexStageReport Report,
        IndexWatchState State);

    private sealed class IndexWatchException : Exception
    {
        internal IndexWatchException(string reason, string code, int exitCode)
            : base(reason)
        {
            Reason = reason;
            Code = code;
            ExitCode = exitCode;
        }

        internal string Reason { get; }

        internal string Code { get; }

        internal int ExitCode { get; }
    }
#endif

    private sealed record IndexWatchOptions(
        string WorkspacePath,
        string DatabasePath,
        TimeSpan WatchDebounce,
        TimeSpan WatchReconciliationInterval,
        TimeSpan RetiredGenerationRetention,
        IReadOnlyList<string> IgnorePatterns,
        IReadOnlyList<string> DenyPatterns);

    private static async Task<int> WriteIndexWatchErrorAsync(
        TextWriter error,
        string reason,
        string code = "invalid_request",
        int exitCode = 64)
    {
        await error.WriteLineAsync(CoupletJsonSerializer.Serialize(new ErrorReport
        {
            SchemaVersion = "couplet.index_watch.error.v1",
            Code = code,
            Component = "daemon",
            Reason = reason,
        })).ConfigureAwait(false);
        return exitCode;
    }

    private static async Task<int> WriteIndexErrorAsync(
        TextWriter error,
        string reason,
        string code = "invalid_request",
        int exitCode = 64)
    {
        await error.WriteLineAsync(CoupletJsonSerializer.Serialize(new ErrorReport
        {
            SchemaVersion = "couplet.index_stage.error.v1",
            Code = code,
            Component = "cli",
            Reason = reason,
        })).ConfigureAwait(false);
        return exitCode;
    }
}
