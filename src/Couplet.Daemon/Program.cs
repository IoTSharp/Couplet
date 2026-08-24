using Couplet.Core.Capabilities;
using Couplet.Infrastructure.SonnetDb;

namespace Couplet.Daemon;

internal static class Program
{
    private static Task<int> Main(string[] args) =>
        CoupletRuntime.RunConsoleAsync(ComponentKind.Daemon, args);

    internal static Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) =>
        CoupletRuntime.RunAsync(ComponentKind.Daemon, arguments, output, error, cancellationToken);
}
