using Couplet.Core.Capabilities;
using Couplet.Infrastructure.SonnetDb;

namespace Couplet.McpServer;

internal static class Program
{
    private static Task<int> Main(string[] args) =>
        CoupletRuntime.RunConsoleAsync(ComponentKind.McpServer, args);

    internal static Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) =>
        CoupletRuntime.RunAsync(ComponentKind.McpServer, arguments, output, error, cancellationToken);

    internal static Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) =>
        CoupletRuntime.RunAsync(ComponentKind.McpServer, arguments, input, output, error, cancellationToken);
}
