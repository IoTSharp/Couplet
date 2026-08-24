using Couplet.Core.Capabilities;
using Couplet.Infrastructure.SonnetDb;

namespace Couplet.Cli;

internal static class Program
{
    private static Task<int> Main(string[] args) =>
        CoupletRuntime.RunConsoleAsync(ComponentKind.Cli, args);

    internal static Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) =>
        CoupletRuntime.RunAsync(ComponentKind.Cli, arguments, output, error, cancellationToken);
}
