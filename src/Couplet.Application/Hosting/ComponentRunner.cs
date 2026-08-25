using Couplet.Application.Capabilities;
using Couplet.Application.Evaluation;
using Couplet.Application.Mcp;
using Couplet.Application.Serialization;
using Couplet.Core.Capabilities;
using Couplet.Core.Evaluation;
using Couplet.Core.Mcp;

namespace Couplet.Application.Hosting;

/// <summary>
/// 执行 CPL-007 最小命令面和可取消生命周期。
/// </summary>
public sealed class ComponentRunner
{
    private const int _successExitCode = 0;
    private const int _capabilityUnavailableExitCode = 2;
    private const int _invalidCommandExitCode = 64;

    private readonly CapabilityReportService _capabilityReportService;

    /// <summary>
    /// 初始化组件运行器。
    /// </summary>
    /// <param name="capabilityReportService">能力报告服务。</param>
    public ComponentRunner(CapabilityReportService capabilityReportService)
    {
        ArgumentNullException.ThrowIfNull(capabilityReportService);
        _capabilityReportService = capabilityReportService;
    }

    /// <summary>
    /// 执行组件命令。
    /// </summary>
    /// <param name="component">组件类型。</param>
    /// <param name="arguments">命令参数。</param>
    /// <param name="output">标准输出。</param>
    /// <param name="error">标准错误。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>进程退出码。</returns>
    public async Task<int> RunAsync(
        ComponentKind component,
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) =>
        await RunAsync(component, arguments, TextReader.Null, output, error, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 执行带标准输入的组件命令。
    /// </summary>
    /// <param name="component">组件类型。</param>
    /// <param name="arguments">命令参数。</param>
    /// <param name="input">标准输入。</param>
    /// <param name="output">标准输出。</param>
    /// <param name="error">标准错误。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>进程退出码。</returns>
    public async Task<int> RunAsync(
        ComponentKind component,
        IReadOnlyList<string> arguments,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        string command = arguments.Count == 0 ? "capabilities" : arguments[0].Trim().ToLowerInvariant();

        return command switch
        {
            "version" or "--version" => await WriteVersionAsync(component, output).ConfigureAwait(false),
            "capabilities" or "--capabilities" => await WriteCapabilitiesAsync(component, output).ConfigureAwait(false),
            "c0-evidence" when component == ComponentKind.Cli =>
                await RunC0EvidenceAsync(arguments, output, error).ConfigureAwait(false),
            "fixture-generate" when component == ComponentKind.Cli =>
                await RunFixtureGeneratorAsync(arguments, output, error, cancellationToken).ConfigureAwait(false),
            "run" when component == ComponentKind.Daemon =>
                await RunDaemonAsync(output, cancellationToken).ConfigureAwait(false),
            "serve" when component == ComponentKind.McpServer =>
                await RunMcpServerAsync(arguments, input, output, error, cancellationToken).ConfigureAwait(false),
            "run" or "serve" =>
                await WriteUnavailableAsync(component, error, "component_command_not_implemented").ConfigureAwait(false),
            _ => await WriteInvalidCommandAsync(component, error).ConfigureAwait(false),
        };
    }

    private static async Task<int> RunC0EvidenceAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error)
    {
        string root = Option(arguments, "--repository") ?? Environment.CurrentDirectory;
        string fixture = Option(arguments, "--fixture-manifest")
            ?? Path.Combine(root, "fixtures", "c0", "manifest.v1.json");
        string golden = Option(arguments, "--golden-answers")
            ?? Path.Combine(root, "fixtures", "c0", "golden-answers.v1.json");
        string eval = Option(arguments, "--agent-eval-manifest")
            ?? Path.Combine(root, "fixtures", "c0", "agent-eval-manifest.v1.json");
        string commit = Option(arguments, "--commit")
            ?? Environment.GetEnvironmentVariable("GITHUB_SHA")
            ?? "working_tree";

        try
        {
            C0EvidenceReport report = C0EvidenceRunner.Run(fixture, golden, eval, commit);
            await output.WriteLineAsync(CoupletJsonSerializer.Serialize(report)).ConfigureAwait(false);
            return report.ContractsPassed && report.AgentEvalRunnerReady ? _successExitCode : 1;
        }
        catch (IOException)
        {
            await error.WriteLineAsync(CoupletJsonSerializer.Serialize(new ErrorReport
            {
                SchemaVersion = "couplet.c0_evidence.error.v1",
                Code = "invalid_input",
                Component = "cli",
                Reason = "evidence_input_unreadable",
            })).ConfigureAwait(false);
            return _invalidCommandExitCode;
        }
        catch (System.Text.Json.JsonException)
        {
            await error.WriteLineAsync(CoupletJsonSerializer.Serialize(new ErrorReport
            {
                SchemaVersion = "couplet.c0_evidence.error.v1",
                Code = "invalid_input",
                Component = "cli",
                Reason = "evidence_input_schema_invalid",
            })).ConfigureAwait(false);
            return _invalidCommandExitCode;
        }
    }

    private static async Task<int> RunFixtureGeneratorAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string root = Option(arguments, "--repository") ?? Environment.CurrentDirectory;
        string manifestPath = Option(arguments, "--fixture-manifest")
            ?? Path.Combine(root, "fixtures", "c0", "manifest.v1.json");
        string? scaleId = Option(arguments, "--scale");
        string? destination = Option(arguments, "--output");
        if (scaleId is null || destination is null)
        {
            return await WriteInvalidCommandAsync(ComponentKind.Cli, error).ConfigureAwait(false);
        }

        try
        {
            string json = File.ReadAllText(manifestPath);
            FixtureManifest manifest = CoupletJsonSerializer.Deserialize(json, CoupletJsonContext.Default.FixtureManifest);
            CorpusScaleDefinition? scale = manifest.Scales.SingleOrDefault(
                value => string.Equals(value.Id, scaleId, StringComparison.Ordinal));
            if (scale is null)
            {
                return await WriteInvalidCommandAsync(ComponentKind.Cli, error).ConfigureAwait(false);
            }

            FixtureGenerationReport report = await DeterministicFixtureGenerator.GenerateAsync(
                scale,
                destination,
                cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(CoupletJsonSerializer.Serialize(report)).ConfigureAwait(false);
            return _successExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await WriteUnavailableAsync(ComponentKind.Cli, error, "fixture_generation_cancelled").ConfigureAwait(false);
        }
        catch (IOException)
        {
            return await WriteUnavailableAsync(ComponentKind.Cli, error, "fixture_output_unavailable").ConfigureAwait(false);
        }
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

    private static async Task<int> RunMcpServerAsync(
        IReadOnlyList<string> arguments,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        int workspaceOption = -1;
        for (int index = 1; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], "--workspace", StringComparison.Ordinal))
            {
                workspaceOption = index;
                break;
            }
        }

        if (workspaceOption < 0 || workspaceOption + 1 >= arguments.Count)
        {
            await error.WriteLineAsync(CoupletJsonSerializer.Serialize(new McpError
            {
                Code = McpErrorCodes.InvalidRequest,
                Reason = "explicit_workspace_required",
                Retryable = false,
                CorrelationId = "startup",
            })).ConfigureAwait(false);
            return _invalidCommandExitCode;
        }

        WorkspaceBinding binding;
        try
        {
            binding = McpWorkspaceBinder.Bind(arguments[workspaceOption + 1]);
        }
        catch (DirectoryNotFoundException)
        {
            await error.WriteLineAsync(CoupletJsonSerializer.Serialize(new McpError
            {
                Code = McpErrorCodes.WorkspaceNotFound,
                Reason = "configured_workspace_not_found",
                Retryable = false,
                CorrelationId = "startup",
            })).ConfigureAwait(false);
            return _capabilityUnavailableExitCode;
        }

        var host = new McpProtocolHost(binding);
        await host.RunAsync(input, output, cancellationToken).ConfigureAwait(false);
        return _successExitCode;
    }

    private static async Task<int> WriteVersionAsync(ComponentKind component, TextWriter output)
    {
        await output.WriteLineAsync($"Couplet {ProductVersion.Current} ({ComponentNames.Get(component)})")
            .ConfigureAwait(false);
        return _successExitCode;
    }

    private async Task<int> WriteCapabilitiesAsync(ComponentKind component, TextWriter output)
    {
        CapabilityReport report = _capabilityReportService.Create(component);
        await output.WriteLineAsync(CoupletJsonSerializer.Serialize(report)).ConfigureAwait(false);
        return _successExitCode;
    }

    private static async Task<int> RunDaemonAsync(TextWriter output, CancellationToken cancellationToken)
    {
        await WriteLifecycleAsync(output, "started").ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A requested cancellation is the daemon's normal shutdown path.
        }

        await WriteLifecycleAsync(output, "stopped").ConfigureAwait(false);
        return _successExitCode;
    }

    private static async Task WriteLifecycleAsync(TextWriter output, string lifecycleEvent)
    {
        var report = new LifecycleReport
        {
            SchemaVersion = "cpl-007.lifecycle.v1",
            Component = "daemon",
            Event = lifecycleEvent,
        };

        await output.WriteLineAsync(CoupletJsonSerializer.Serialize(report)).ConfigureAwait(false);
    }

    private static async Task<int> WriteUnavailableAsync(
        ComponentKind component,
        TextWriter error,
        string reason)
    {
        var report = new ErrorReport
        {
            SchemaVersion = "cpl-007.error.v1",
            Code = "capability_unavailable",
            Component = ComponentNames.Get(component),
            Reason = reason,
        };

        await error.WriteLineAsync(CoupletJsonSerializer.Serialize(report)).ConfigureAwait(false);
        return _capabilityUnavailableExitCode;
    }

    private static async Task<int> WriteInvalidCommandAsync(ComponentKind component, TextWriter error)
    {
        var report = new ErrorReport
        {
            SchemaVersion = "cpl-007.error.v1",
            Code = "invalid_command",
            Component = ComponentNames.Get(component),
            Reason = "supported_commands_are_version_and_capabilities",
        };

        await error.WriteLineAsync(CoupletJsonSerializer.Serialize(report)).ConfigureAwait(false);
        return _invalidCommandExitCode;
    }
}
