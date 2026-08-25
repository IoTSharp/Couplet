using Couplet.Application.Evaluation;
using Couplet.Core.Evaluation;

namespace Couplet.Tests;

public sealed class EvaluationRunnerTests
{
    [Fact]
    public void Run_CommittedC0Manifests_ProducesPassingContractEvidence()
    {
        string root = FindRepositoryRoot();

        C0EvidenceReport report = C0EvidenceRunner.Run(
            Path.Combine(root, "fixtures", "c0", "manifest.v1.json"),
            Path.Combine(root, "fixtures", "c0", "golden-answers.v1.json"),
            Path.Combine(root, "fixtures", "c0", "agent-eval-manifest.v1.json"),
            "test-commit");

        Assert.True(report.ContractsPassed);
        Assert.True(report.AgentEvalRunnerReady);
        Assert.Equal("not_run", report.AgentEvalState);
        Assert.Empty(report.Problems);
        EvidenceMetric metric = Assert.Single(report.Metrics);
        Assert.Equal("sha256_length_prefixed_utf8", metric.AccessPath);
        Assert.Equal(1000, metric.Samples);
    }

    [Fact]
    public async Task GenerateAsync_MiniMultilanguageScale_WritesDeterministicFiles()
    {
        string output = Path.Combine(Path.GetTempPath(), $"couplet-fixture-test-{Guid.NewGuid():N}");
        var scale = new CorpusScaleDefinition
        {
            Id = "mini",
            TargetLinesOfCode = 20,
            MinimumSymbols = 1,
            MinimumRelations = 1,
            Seed = 7,
            Languages =
            [
                new LanguageShare { Family = "csharp", Language = "csharp", Share = 0.5, SemanticTier = "fixture_contract" },
                new LanguageShare { Family = "typescript_javascript", Language = "typescript", Share = 0.5, SemanticTier = "fixture_contract" },
            ],
        };

        try
        {
            FixtureGenerationReport report = await DeterministicFixtureGenerator.GenerateAsync(scale, output, CancellationToken.None);

            Assert.Equal(20, report.LinesOfCode);
            Assert.Equal(2, report.Files);
            Assert.True(report.Symbols > 0);
            Assert.True(File.Exists(Path.Combine(output, "csharp", "unit-000000.cs")));
            Assert.True(File.Exists(Path.Combine(output, "typescript", "unit-000000.ts")));
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
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
