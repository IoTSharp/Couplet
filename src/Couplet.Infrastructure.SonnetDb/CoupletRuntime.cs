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
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
using SonnetDB.Generations;
#endif

namespace Couplet.Infrastructure.SonnetDb;

/// <summary>
/// 组合 Couplet application 层与 SonnetDB Core 适配器。
/// </summary>
public static class CoupletRuntime
{
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
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

    private static async Task<int> WriteIndexErrorAsync(TextWriter error, string reason)
    {
        await error.WriteLineAsync(CoupletJsonSerializer.Serialize(new ErrorReport
        {
            SchemaVersion = "couplet.index_stage.error.v1",
            Code = "invalid_request",
            Component = "cli",
            Reason = reason,
        })).ConfigureAwait(false);
        return 64;
    }
}
