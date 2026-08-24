using Couplet.Application.Capabilities;
using Couplet.Application.Hosting;
using Couplet.Core.Capabilities;

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
        var probe = new SonnetDbCapabilityProbe();
        var reportService = new CapabilityReportService(probe);
        var runner = new ComponentRunner(reportService);
        return runner.RunAsync(component, arguments, output, error, cancellationToken);
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
}
