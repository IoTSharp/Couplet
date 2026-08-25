using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Couplet.Application.Serialization;
using Couplet.Core.Evaluation;
using Couplet.Core.Graph;

namespace Couplet.Application.Evaluation;

/// <summary>
/// 生成 C0 合同、硬件和 runner evidence。
/// </summary>
public static class C0EvidenceRunner
{
    /// <summary>
    /// 读取固定 manifest 并生成 C0 evidence 报告。
    /// </summary>
    /// <param name="fixtureManifestPath">fixture manifest 路径。</param>
    /// <param name="goldenAnswersPath">golden answer 路径。</param>
    /// <param name="agentEvalManifestPath">Agent eval manifest 路径。</param>
    /// <param name="commit">Couplet commit；工作树运行可传 working_tree。</param>
    /// <returns>C0 evidence 报告。</returns>
    public static C0EvidenceReport Run(
        string fixtureManifestPath,
        string goldenAnswersPath,
        string agentEvalManifestPath,
        string commit)
    {
        string fixtureJson = File.ReadAllText(fixtureManifestPath);
        string goldenJson = File.ReadAllText(goldenAnswersPath);
        string evalJson = File.ReadAllText(agentEvalManifestPath);
        FixtureManifest manifest = CoupletJsonSerializer.Deserialize(fixtureJson, CoupletJsonContext.Default.FixtureManifest);
        GoldenAnswerSet golden = CoupletJsonSerializer.Deserialize(goldenJson, CoupletJsonContext.Default.GoldenAnswerSet);
        AgentEvalManifest agentEval = CoupletJsonSerializer.Deserialize(evalJson, CoupletJsonContext.Default.AgentEvalManifest);
        IReadOnlyList<string> problems = FixtureContractValidator.Validate(manifest, golden, agentEval);

        var notRun = new AgentEvalResultSet
        {
            State = "not_run",
            ManifestHash = Hash(evalJson),
            Observations = [],
        };
        AgentEvalValidationResult evalValidation = PairedAgentEvalRunner.Validate(agentEval, notRun);

        return new C0EvidenceReport
        {
            Commit = string.IsNullOrWhiteSpace(commit) ? "working_tree" : commit,
            FixtureManifestHash = Hash(fixtureJson),
            GoldenAnswersHash = Hash(goldenJson),
            AgentEvalManifestHash = Hash(evalJson),
            Hardware = CollectHardware(fixtureManifestPath),
            ContractsPassed = problems.Count == 0,
            AgentEvalRunnerReady = evalValidation.RunnerReady,
            AgentEvalState = "not_run",
            Problems = problems.Concat(evalValidation.Problems).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            Metrics = [BenchmarkStableIds()],
            GeneratedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private static EvidenceMetric BenchmarkStableIds()
    {
        const int samples = 1_000;
        var elapsed = new double[samples];
        string workspaceId = StableId.CreateWorkspace("https://example.invalid/couplet-fixture", "primary");
        for (int index = 0; index < samples; index++)
        {
            long started = Stopwatch.GetTimestamp();
            _ = StableId.CreateSymbol(workspaceId, "csharp", $"Fixture.Type{index}.Method(System.Int32)");
            elapsed[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(elapsed);
        return new EvidenceMetric
        {
            Name = "stable_id_create",
            Samples = samples,
            P50 = Percentile(elapsed, 0.50),
            P95 = Percentile(elapsed, 0.95),
            P99 = Percentile(elapsed, 0.99),
            Unit = "ms",
            AccessPath = "sha256_length_prefixed_utf8",
        };
    }

    private static HardwareFingerprint CollectHardware(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? Path.DirectorySeparatorChar.ToString();
        string fileSystem;
        try
        {
            fileSystem = new DriveInfo(root).DriveFormat;
        }
        catch (IOException)
        {
            fileSystem = "unknown";
        }

        string cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
            ?? RuntimeInformation.ProcessArchitecture.ToString();
        return new HardwareFingerprint
        {
            Cpu = cpu,
            LogicalCores = Environment.ProcessorCount,
            AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            OperatingSystem = RuntimeInformation.OSDescription,
            Runtime = RuntimeInformation.FrameworkDescription,
            FileSystem = fileSystem,
            Storage = Environment.GetEnvironmentVariable("COUPLET_STORAGE_MODEL") ?? "explicit_unknown",
        };
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static double Percentile(double[] sorted, double percentile)
    {
        int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
