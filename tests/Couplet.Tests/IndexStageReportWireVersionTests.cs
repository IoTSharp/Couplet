using System.Text.Json;
using Couplet.Application.Serialization;
using Couplet.Core.Contracts;
using Couplet.Core.Indexing;

namespace Couplet.Tests;

public sealed class IndexStageReportWireVersionTests
{
    [Fact]
    public void Serialize_IndexStageReport_UsesLaneSpecificCompatibleVersion()
    {
        var report = new IndexStageReport
        {
            Manifest = new GenerationManifest
            {
                WorkspaceId = "cpl_workspace_wire",
                SourceRevision = "source",
                IndexRevision = "index",
                CodeGraphSchemaVersion = ContractVersions.CodeGraph,
                ProducerVersions = ["test:v1"],
                Counts = new GenerationCounts
                {
                    Files = 0,
                    Symbols = 0,
                    Chunks = 0,
                    FullTextDocuments = 0,
                    Vectors = 0,
                    GraphNodes = 0,
                    GraphEdges = 0,
                },
                Checksum = new string('0', 64),
                State = GenerationState.Staging,
                CreatedAtUtc = DateTimeOffset.UnixEpoch,
            },
            CollectionName = "cplg_00000000000000000000000000000000",
            Staged = true,
            Published = false,
            RetentionDeferredGenerationRevisions = [1L],
            Problems = [],
        };

        using JsonDocument json = JsonDocument.Parse(CoupletJsonSerializer.Serialize(report));
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
        Assert.Equal("couplet.index_stage.v2", json.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal(
            [1L],
            json.RootElement
                .GetProperty("retention_deferred_generation_revisions")
                .EnumerateArray()
                .Select(value => value.GetInt64())
                .ToArray());
        AssertMatchesTopLevelSchema(json.RootElement, "contracts/indexing/v2/stage-report.schema.json");
#else
        Assert.Equal("couplet.index_stage.v1", json.RootElement.GetProperty("schema_version").GetString());
        Assert.False(json.RootElement.TryGetProperty("retention_deferred_generation_revisions", out _));
        AssertMatchesTopLevelSchema(json.RootElement, "contracts/indexing/v1/schema.json", "stage_report");
#endif
    }

    [Fact]
    public void IndexStageSchemas_KeepRetentionFieldOutOfV1AndRequiredInV2()
    {
        string root = FindRepositoryRoot();
        using JsonDocument v1 = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "contracts",
            "indexing",
            "v1",
            "schema.json")));
        JsonElement v1Stage = v1.RootElement.GetProperty("$defs").GetProperty("stage_report");
        Assert.False(v1Stage.GetProperty("properties").TryGetProperty(
            "retention_deferred_generation_revisions",
            out _));
        Assert.DoesNotContain(
            "retention_deferred_generation_revisions",
            v1Stage.GetProperty("required").EnumerateArray().Select(value => value.GetString()));

        using JsonDocument v2 = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "contracts",
            "indexing",
            "v2",
            "stage-report.schema.json")));
        Assert.True(v2.RootElement.GetProperty("properties").TryGetProperty(
            "retention_deferred_generation_revisions",
            out _));
        Assert.Contains(
            "retention_deferred_generation_revisions",
            v2.RootElement.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
    }

    private static void AssertMatchesTopLevelSchema(
        JsonElement payload,
        string relativeSchemaPath,
        string? definition = null)
    {
        string root = FindRepositoryRoot();
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            relativeSchemaPath.Replace('/', Path.DirectorySeparatorChar))));
        JsonElement contract = definition is null
            ? schema.RootElement
            : schema.RootElement.GetProperty("$defs").GetProperty(definition);
        JsonElement properties = contract.GetProperty("properties");
        foreach (JsonProperty property in payload.EnumerateObject())
        {
            Assert.True(properties.TryGetProperty(property.Name, out _), property.Name);
        }

        foreach (JsonElement required in contract.GetProperty("required").EnumerateArray())
        {
            Assert.True(payload.TryGetProperty(required.GetString()!, out _), required.GetString());
        }
    }

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
