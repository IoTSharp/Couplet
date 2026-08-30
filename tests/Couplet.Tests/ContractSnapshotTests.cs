using System.Security.Cryptography;
using System.Text.Json;

namespace Couplet.Tests;

public sealed class ContractSnapshotTests
{
    [Fact]
    public void C0Handshake_CommittedInputsAndPackageLock_MatchFrozenHashes()
    {
        string root = FindRepositoryRoot();
        using JsonDocument handshake = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "contracts", "c0-handshake.v1.json")));
        JsonElement fixtures = handshake.RootElement.GetProperty("fixtures");

        Assert.Equal(fixtures.GetProperty("manifest_sha256").GetString(), Hash(Path.Combine(root, "fixtures", "c0", "manifest.v1.json")));
        Assert.Equal(fixtures.GetProperty("golden_answers_sha256").GetString(), Hash(Path.Combine(root, "fixtures", "c0", "golden-answers.v1.json")));
        Assert.Equal(fixtures.GetProperty("agent_eval_manifest_sha256").GetString(), Hash(Path.Combine(root, "fixtures", "c0", "agent-eval-manifest.v1.json")));

        using JsonDocument packageLock = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "src",
            "Couplet.Infrastructure.SonnetDb",
            "packages.lock.json")));
        JsonElement package = packageLock.RootElement
            .GetProperty("dependencies")
            .GetProperty("net10.0")
            .GetProperty("SonnetDB.Core");
        Assert.Equal(handshake.RootElement.GetProperty("sonnetdb").GetProperty("version").GetString(), package.GetProperty("resolved").GetString());
        Assert.Equal(handshake.RootElement.GetProperty("sonnetdb").GetProperty("package_content_hash").GetString(), package.GetProperty("contentHash").GetString());
    }

    [Theory]
    [InlineData("contracts/code-graph/v1/schema.json", "couplet.code_graph.v1")]
    [InlineData("contracts/indexing/v1/schema.json", "couplet.indexing.v1")]
    [InlineData("contracts/indexing/v2/stage-report.schema.json", "couplet.index_stage.v2")]
    [InlineData("contracts/security/v1/policy.schema.json", null)]
    [InlineData("contracts/mcp/v1/schema-catalog.json", "couplet.mcp.v1")]
    public void JsonSchema_CommittedSnapshot_ParsesWithExpectedVersion(
        string relativePath,
        string? expectedVersion)
    {
        string root = FindRepositoryRoot();
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, relativePath)));

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString());
        if (expectedVersion is not null)
        {
            Assert.Equal(expectedVersion, schema.RootElement.GetProperty("schema_version").GetString());
        }
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Couplet.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
