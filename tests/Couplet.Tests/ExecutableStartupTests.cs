using System.Text.Json;

namespace Couplet.Tests;

public sealed class ExecutableStartupTests
{
    [Fact]
    public async Task RunAsync_CliWithoutArguments_ReportsUnavailableCapabilities()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Couplet.Cli.Program.RunAsync(
            [],
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.Equal("cpl-007.capabilities.v1", root.GetProperty("schema_version").GetString());
        Assert.Equal("cli", root.GetProperty("component").GetString());
        Assert.Equal("capability_unavailable", root.GetProperty("overall_state").GetString());
        Assert.Equal("source_project_reference", root.GetProperty("sonnet_db_core").GetProperty("mode").GetString());
        Assert.True(root.GetProperty("sonnet_db_core").GetProperty("graph_api_present").GetBoolean());
    }

    [Fact]
    public async Task RunAsync_VersionCommand_ReportsProductAndComponentVersion()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Couplet.Cli.Program.RunAsync(
            ["version"],
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("Couplet 0.1.0 (cli)", output.ToString().Trim());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task RunAsync_DaemonCanceled_ReportsStartedAndStopped()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        int exitCode = await Couplet.Daemon.Program.RunAsync(
            ["run"],
            output,
            error,
            cancellation.Token);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());

        string[] events = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("event").GetString()!)
            .ToArray();

        Assert.Equal(["started", "stopped"], events);
    }

    [Fact]
    public async Task RunAsync_McpServeCommand_ReportsCapabilityUnavailable()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Couplet.McpServer.Program.RunAsync(
            ["serve"],
            output,
            error,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());

        using JsonDocument document = JsonDocument.Parse(error.ToString());
        JsonElement root = document.RootElement;
        Assert.Equal("capability_unavailable", root.GetProperty("code").GetString());
        Assert.Equal("mcp_server", root.GetProperty("component").GetString());
        Assert.Equal("cpl_006_not_implemented", root.GetProperty("reason").GetString());
    }
}
