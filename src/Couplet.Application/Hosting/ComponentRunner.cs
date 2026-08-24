using Couplet.Application.Capabilities;
using Couplet.Application.Serialization;
using Couplet.Core.Capabilities;

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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        string command = arguments.Count == 0 ? "capabilities" : arguments[0].Trim().ToLowerInvariant();

        return command switch
        {
            "version" or "--version" => await WriteVersionAsync(component, output).ConfigureAwait(false),
            "capabilities" or "--capabilities" => await WriteCapabilitiesAsync(component, output).ConfigureAwait(false),
            "run" when component == ComponentKind.Daemon =>
                await RunDaemonAsync(output, cancellationToken).ConfigureAwait(false),
            "serve" when component == ComponentKind.McpServer =>
                await WriteUnavailableAsync(component, error, "cpl_006_not_implemented").ConfigureAwait(false),
            "run" or "serve" =>
                await WriteUnavailableAsync(component, error, "component_command_not_implemented").ConfigureAwait(false),
            _ => await WriteInvalidCommandAsync(component, error).ConfigureAwait(false),
        };
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
