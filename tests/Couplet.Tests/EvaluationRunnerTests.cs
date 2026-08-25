using Couplet.Application.Evaluation;
using Couplet.Core.Evaluation;
using Couplet.Infrastructure.SonnetDb;

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

    [Fact]
    public async Task GenerateAsync_C1CapacityScale_MeetsExactLineAndSymbolTargetsDeterministically()
    {
        string firstOutput = Path.Combine(Path.GetTempPath(), $"couplet-c1-capacity-test-{Guid.NewGuid():N}");
        string secondOutput = Path.Combine(Path.GetTempPath(), $"couplet-c1-capacity-test-{Guid.NewGuid():N}");
        var scale = new CorpusScaleDefinition
        {
            Id = "medium",
            TargetLinesOfCode = 2_000,
            MinimumSymbols = 200,
            MinimumRelations = 0,
            Seed = 11,
            Languages =
            [
                new LanguageShare { Family = "csharp", Language = "csharp", Share = 0.5, SemanticTier = "partial" },
                new LanguageShare { Family = "typescript_javascript", Language = "typescript", Share = 0.5, SemanticTier = "partial" },
            ],
        };

        try
        {
            C1CorpusGenerationReport first = await C1CapacityCorpusGenerator.GenerateAsync(
                scale,
                "test-v1",
                firstOutput,
                CancellationToken.None);
            C1CorpusGenerationReport second = await C1CapacityCorpusGenerator.GenerateAsync(
                scale,
                "test-v1",
                secondOutput,
                CancellationToken.None);

            Assert.Equal(2_000, first.LinesOfCode);
            Assert.Equal(200, first.DeclaredSymbols);
            Assert.Equal(2, first.Files);
            Assert.Equal(first.CorpusHash, second.CorpusHash);
        }
        finally
        {
            if (Directory.Exists(firstOutput))
            {
                Directory.Delete(firstOutput, recursive: true);
            }

            if (Directory.Exists(secondOutput))
            {
                Directory.Delete(secondOutput, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_C1SmallCapacityCharacterization_StagesQueriesAndReopensWithoutPublishing()
    {
        string workspace = Path.Combine(Path.GetTempPath(), $"couplet-c1-runner-workspace-{Guid.NewGuid():N}");
        string database = Path.Combine(Path.GetTempPath(), $"couplet-c1-runner-database-{Guid.NewGuid():N}");
        var scale = new CorpusScaleDefinition
        {
            Id = "small",
            TargetLinesOfCode = 100_000,
            MinimumSymbols = 10_000,
            MinimumRelations = 0,
            Seed = 13,
            Languages =
            [
                new LanguageShare { Family = "csharp", Language = "csharp", Share = 0.5, SemanticTier = "partial" },
                new LanguageShare { Family = "typescript_javascript", Language = "typescript", Share = 0.5, SemanticTier = "partial" },
            ],
        };

        try
        {
            C1CapacityEvidenceReport report = await C1CapacityEvidenceRunner.RunAsync(
                scale,
                "test-v1",
                "{\"id\":\"test\"}",
                workspace,
                database,
                "test-commit",
                3,
                CancellationToken.None);

            Assert.Equal(100, report.ModifiedFiles);
            Assert.True(report.InitialCounts.Symbols >= 10_000);
            Assert.Equal(report.InitialCounts.Symbols, report.IncrementalCounts.Symbols);
            Assert.False(report.Published);
            Assert.False(report.CorrectnessRecoveryPassed);
            Assert.False(report.PerformanceCapacityPassed);
            Assert.Contains(report.Metrics, metric => metric.Name == "staging_exact");
            Assert.Contains(report.Metrics, metric => metric.Name == "staging_fulltext_top20");
            Assert.Contains(report.Metrics, metric => metric.Name == "staging_reopen_validate");
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }

            if (Directory.Exists(database))
            {
                Directory.Delete(database, recursive: true);
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
