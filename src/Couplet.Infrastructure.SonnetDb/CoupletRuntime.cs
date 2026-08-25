using Couplet.Application.Capabilities;
using Couplet.Application.Hosting;
using Couplet.Application.Indexing;
using Couplet.Application.Serialization;
using Couplet.Application.Workspaces;
using Couplet.Core.Capabilities;
using Couplet.Core.Evaluation;
using Couplet.Core.Indexing;
using Couplet.Core.Workspaces;

namespace Couplet.Infrastructure.SonnetDb;

/// <summary>
/// 组合 Couplet application 层与 SonnetDB Core 适配器。
/// </summary>
public static class CoupletRuntime
{
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

            WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(
                workspace,
                previousIndexRevision: null,
                cancellationToken).ConfigureAwait(false);
            IncrementalIndexPlan plan = IncrementalIndexPlanner.Plan(previous: null, snapshot);
            using var store = new SonnetDbIndexGenerationStore(databasePath);
            IndexStageReport report = store.Stage(snapshot, plan, cancellationToken);
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
    }

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
