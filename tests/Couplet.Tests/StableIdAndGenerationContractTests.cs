using Couplet.Core.Contracts;
using Couplet.Core.Graph;
using Couplet.Core.Indexing;

namespace Couplet.Tests;

public sealed class StableIdAndGenerationContractTests
{
    [Fact]
    public void CreateStableIds_FrozenInputs_ReturnExpectedSnapshots()
    {
        string workspace = StableId.CreateWorkspace("https://example.com/org/repo.git", "primary");
        string file = StableId.CreateFile(workspace, "src/Program.cs");
        string symbol = StableId.CreateSymbol(workspace, "csharp", "Example.Program.Main(System.String[])");

        Assert.Equal("cpl_workspace_3b401e06fb17bb81a17ff5097586c6baca701ada", workspace);
        Assert.Equal("cpl_file_050c02e23eaf9a7dedc98a1ad0b9e9d8597e1c4f", file);
        Assert.Equal("cpl_symbol_ca8bd95cb1002d9280cebb4f3a4a1ca0abac77d5", symbol);
    }

    [Fact]
    public void CreateFile_PathSeparatorNormalization_ReturnsSameId()
    {
        string workspace = StableId.CreateWorkspace("repo", "primary");

        Assert.Equal(
            StableId.CreateFile(workspace, "src/Example.cs"),
            StableId.CreateFile(workspace, "src\\Example.cs"));
        Assert.NotEqual(
            StableId.CreateFile(workspace, "src/Before.cs"),
            StableId.CreateFile(workspace, "src/After.cs"));
    }

    [Fact]
    public void CreateSymbol_SourceMoveWithSameSemanticIdentity_PreservesId()
    {
        string workspace = StableId.CreateWorkspace("repo", "primary");
        const string semanticIdentity = "Example.Service.Run(System.Int32)";

        string beforeMove = StableId.CreateSymbol(workspace, "csharp", semanticIdentity);
        string afterMove = StableId.CreateSymbol(workspace, "CSHARP", semanticIdentity);

        Assert.Equal(beforeMove, afterMove);
    }

    [Fact]
    public void CreateFile_ParentTraversal_ThrowsArgumentException()
    {
        string workspace = StableId.CreateWorkspace("repo", "primary");

        Assert.Throws<ArgumentException>(() => StableId.CreateFile(workspace, "../secret.txt"));
    }

    [Fact]
    public void Validate_PublishedGenerationWithNonNegativeCounts_ReturnsNoProblems()
    {
        GenerationManifest manifest = CreateManifest();

        IReadOnlyList<string> problems = GenerationContractValidator.Validate(manifest);

        Assert.Empty(problems);
    }

    [Fact]
    public void ValidateDeletion_ActiveLeaseOrActiveGeneration_ReturnsStableProblems()
    {
        var deletion = new GenerationDeletion
        {
            WorkspaceId = "workspace",
            IndexRevision = "index-2",
            SupersededBy = "index-3",
            Reason = "superseded",
            RequiredLeaseCount = 1,
        };

        IReadOnlyList<string> problems = GenerationContractValidator.ValidateDeletion(deletion, "index-2");

        Assert.Equal(
            ["active_generation_cannot_be_deleted", "generation_query_leases_active", "generation_superseding_revision_mismatch"],
            problems);
    }

    private static GenerationManifest CreateManifest() => new()
    {
        WorkspaceId = "workspace",
        SourceRevision = "source-1",
        IndexRevision = "index-2",
        PreviousIndexRevision = "index-1",
        CodeGraphSchemaVersion = ContractVersions.CodeGraph,
        ProducerVersions = ["csharp-adapter/1"],
        Counts = new GenerationCounts
        {
            Files = 1,
            Symbols = 2,
            Chunks = 2,
            FullTextDocuments = 2,
            Vectors = 0,
            GraphNodes = 3,
            GraphEdges = 2,
        },
        Checksum = new string('a', 64),
        State = GenerationState.Published,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };
}
