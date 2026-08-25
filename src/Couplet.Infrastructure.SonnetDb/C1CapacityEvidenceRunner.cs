using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Couplet.Application.Evaluation;
using Couplet.Application.Indexing;
using Couplet.Application.Workspaces;
using Couplet.Core.Evaluation;
using Couplet.Core.Indexing;
using Couplet.Core.Languages;
using Couplet.Core.Workspaces;

namespace Couplet.Infrastructure.SonnetDb;

/// <summary>
/// 对固定 C1 Medium/Large 语料执行真实 SonnetDB staging 容量取证。
/// </summary>
public static class C1CapacityEvidenceRunner
{
    private const int _incrementalFileCount = 100;

    /// <summary>
    /// 生成语料并执行首次、100 文件增量、查询探针和重开验证。
    /// </summary>
    /// <param name="scale">固定语料档位。</param>
    /// <param name="generatorVersion">语料生成器版本。</param>
    /// <param name="manifestJson">固定语料 manifest JSON。</param>
    /// <param name="workspaceRoot">必须为空的语料输出目录。</param>
    /// <param name="databaseRoot">必须为空的 SonnetDB 目录。</param>
    /// <param name="commit">Couplet commit 或 working_tree。</param>
    /// <param name="querySamples">exact 与 FullText warm 样本数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>明确保持 C1 gate 失败的 staging characterization。</returns>
    public static async Task<C1CapacityEvidenceReport> RunAsync(
        CorpusScaleDefinition scale,
        string generatorVersion,
        string manifestJson,
        string workspaceRoot,
        string databaseRoot,
        string commit,
        int querySamples,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scale);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatorVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(querySamples, 3);

        string workspacePath = Path.GetFullPath(workspaceRoot);
        string databasePath = Path.GetFullPath(databaseRoot);
        EnsureEmpty(databasePath, "C1 capacity database directory must be empty.");

        var metrics = new List<C1CapacityMetric>();
        var problems = new SortedSet<string>(StringComparer.Ordinal)
        {
            "CG-005:generation_publish_query_lease_cursor_cleanup_unavailable",
            "CG-006:native_aot_background_maintenance_unavailable",
            "active_generation_not_published",
            "cold_initial_sample_count_insufficient",
            "fulltext_candidate_examined_diagnostics_unavailable",
            "incremental_sample_count_insufficient",
            "process_io_counters_not_collected",
            "public_mcp_query_metrics_unavailable",
            "reopen_sample_count_insufficient",
        };

        Observation<C1CorpusGenerationReport>? generationObservation = await ObserveAsync(
            () => C1CapacityCorpusGenerator.GenerateAsync(
                scale,
                generatorVersion,
                workspacePath,
                cancellationToken),
            databasePath).ConfigureAwait(false);
        C1CorpusGenerationReport corpus = generationObservation.Value;
        metrics.Add(CreateMetric(
            "corpus_generate",
            "cold",
            [generationObservation],
            "deterministic_c1_capacity_generator",
            corpus.LinesOfCode));
        generationObservation = null;

        Observation<bool> gitInitializationObservation = await ObserveAsync(
            () => InitializeGitWorkspaceAsync(workspacePath, cancellationToken),
            databasePath).ConfigureAwait(false);
        metrics.Add(CreateMetric(
            "corpus_git_initialize",
            "cold",
            [gitInitializationObservation],
            "git_unborn_workspace_boundary",
            throughputItems: null));

        WorkspaceDiscoveryPolicy policy = WorkspaceDiscoveryService.DefaultPolicy;
        Observation<DiscoveredWorkspace>? initialDiscoveryObservation = await ObserveAsync(
            () => WorkspaceDiscoveryService.DiscoverAsync(workspacePath, policy, cancellationToken),
            databasePath).ConfigureAwait(false);
        DiscoveredWorkspace initialWorkspace = initialDiscoveryObservation.Value;
        metrics.Add(CreateMetric(
            "initial_discovery",
            "cold",
            [initialDiscoveryObservation],
            "filesystem_gitignore_sha256",
            initialWorkspace.Result.Files.Count));
        double initialDiscoveryMilliseconds = initialDiscoveryObservation.ElapsedMilliseconds;
        long initialDiscoveryAllocatedBytes = initialDiscoveryObservation.AllocatedBytes;
        long initialDiscoveryPeakWorkingSetBytes = initialDiscoveryObservation.PeakWorkingSetBytes;

        Observation<WorkspaceIndexSnapshot>? initialSnapshotObservation = await ObserveAsync(
            () => IndexSnapshotBuilder.BuildAsync(initialWorkspace, null, cancellationToken),
            databasePath).ConfigureAwait(false);
        WorkspaceIndexSnapshot? initialSnapshot = initialSnapshotObservation.Value;
        metrics.Add(CreateMetric(
            "initial_snapshot",
            "cold",
            [initialSnapshotObservation],
            "csharp_typescript_lexical_partial",
            initialSnapshot.Files.Count));
        initialDiscoveryObservation = null;

        IncrementalIndexPlan initialPlan = IncrementalIndexPlanner.Plan(null, initialSnapshot);
        GenerationCounts initialCounts;
        WorkspaceIndexSnapshot planningSnapshot;
        string exactStableId;
        Observation<IndexStageReport> initialStageObservation;
        Observation<StagingQueryProbeResult>[] exactObservations;
        Observation<StagingQueryProbeResult>[] fullTextObservations;
        string exactAccessPath;
        long? exactCandidates;
        long? exactExamined;
        long exactReturned;
        string fullTextAccessPath;
        long fullTextReturned;

        using (var store = new SonnetDbIndexGenerationStore(databasePath))
        {
            initialStageObservation = Observe(
                () => store.Stage(initialSnapshot, initialPlan, cancellationToken),
                databasePath);
            IndexStageReport initialStage = initialStageObservation.Value;
            initialCounts = initialStage.Manifest.Counts;
            problems.UnionWith(initialStage.Problems.Select(problem => "initial_stage:" + problem));
            metrics.Add(CreateMetric(
                "initial_stage",
                "cold",
                [initialStageObservation],
                "sonnetdb_document_fulltext_staging",
                initialCounts.FullTextDocuments));

            exactStableId = initialSnapshot.Files
                .SelectMany(file => file.Symbols)
                .Select(symbol => symbol.Id)
                .FirstOrDefault() ?? "cpl_symbol_capacity_missing";
            if (exactStableId == "cpl_symbol_capacity_missing")
            {
                problems.Add("initial_symbols_missing");
            }
            exactObservations = Enumerable.Range(0, querySamples)
                .Select(_ => Observe(
                    () => store.ProbeExact(initialSnapshot.WorkspaceId, initialSnapshot.IndexRevision, exactStableId),
                    databasePath))
                .ToArray();
            StagingQueryProbeResult exact = exactObservations[^1].Value;
            exactAccessPath = exact.AccessPath;
            exactCandidates = exact.Candidates;
            exactExamined = exact.Examined;
            exactReturned = exact.Documents.Count;
            metrics.Add(CreateMetric(
                "staging_exact",
                "warm",
                exactObservations,
                exactAccessPath,
                throughputItems: null,
                exactCandidates,
                exactExamined,
                exactReturned));

            fullTextObservations = Enumerable.Range(0, querySamples)
                .Select(_ => Observe(
                    () => store.ProbeFullText(initialSnapshot.WorkspaceId, initialSnapshot.IndexRevision, "Method0001", 20),
                    databasePath))
                .ToArray();
            StagingQueryProbeResult fullText = fullTextObservations[^1].Value;
            fullTextAccessPath = fullText.AccessPath;
            fullTextReturned = fullText.Documents.Count;
            metrics.Add(CreateMetric(
                "staging_fulltext_top20",
                "warm",
                fullTextObservations,
                fullTextAccessPath,
                throughputItems: null,
                fullText.Candidates,
                fullText.Examined,
                fullTextReturned));

            planningSnapshot = CreatePlanningSnapshot(initialSnapshot);
        }

        double initialTotalMilliseconds = initialDiscoveryMilliseconds
            + initialSnapshotObservation.ElapsedMilliseconds
            + initialStageObservation.ElapsedMilliseconds;
        metrics.Add(CreateCombinedMetric(
            "initial_index_total",
            "cold",
            initialTotalMilliseconds,
            initialDiscoveryAllocatedBytes + initialSnapshotObservation.AllocatedBytes + initialStageObservation.AllocatedBytes,
            Math.Max(initialDiscoveryPeakWorkingSetBytes, Math.Max(initialSnapshotObservation.PeakWorkingSetBytes, initialStageObservation.PeakWorkingSetBytes)),
            initialStageObservation.StorageGrowthBytes,
            "discover_parse_sonnetdb_staging",
            initialCounts.FullTextDocuments));
        initialSnapshotObservation = null;
        initialSnapshot = null;
        exactObservations = [];
        fullTextObservations = [];
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        string[] changedPaths = Directory.EnumerateFiles(workspacePath, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Take(_incrementalFileCount)
            .ToArray();
        foreach ((string path, int index) in changedPaths.Select((path, index) => (path, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await File.AppendAllTextAsync(
                path,
                $"// incremental mutation {index}{Environment.NewLine}",
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);
        }

        Observation<DiscoveredWorkspace>? incrementalDiscoveryObservation = await ObserveAsync(
            () => WorkspaceDiscoveryService.DiscoverAsync(workspacePath, policy, cancellationToken),
            databasePath).ConfigureAwait(false);
        DiscoveredWorkspace incrementalWorkspace = incrementalDiscoveryObservation.Value;
        metrics.Add(CreateMetric(
            "incremental_discovery",
            "warm",
            [incrementalDiscoveryObservation],
            "filesystem_gitignore_sha256",
            incrementalWorkspace.Result.Files.Count));
        double incrementalDiscoveryMilliseconds = incrementalDiscoveryObservation.ElapsedMilliseconds;
        long incrementalDiscoveryAllocatedBytes = incrementalDiscoveryObservation.AllocatedBytes;
        long incrementalDiscoveryPeakWorkingSetBytes = incrementalDiscoveryObservation.PeakWorkingSetBytes;

        Observation<WorkspaceIndexSnapshot>? incrementalSnapshotObservation = await ObserveAsync(
            () => IndexSnapshotBuilder.BuildAsync(
                incrementalWorkspace,
                planningSnapshot.IndexRevision,
                cancellationToken),
            databasePath).ConfigureAwait(false);
        WorkspaceIndexSnapshot incrementalSnapshot = incrementalSnapshotObservation.Value;
        metrics.Add(CreateMetric(
            "incremental_snapshot",
            "warm",
            [incrementalSnapshotObservation],
            "csharp_typescript_lexical_partial",
            incrementalSnapshot.Files.Count));
        incrementalDiscoveryObservation = null;

        IncrementalIndexPlan incrementalPlan = IncrementalIndexPlanner.Plan(planningSnapshot, incrementalSnapshot);
        int modifiedFiles = incrementalPlan.Changes.Count(change => change.Kind == IndexFileChangeKind.Modified);
        if (modifiedFiles != _incrementalFileCount)
        {
            problems.Add("incremental_modified_file_count_mismatch");
        }

        Observation<IndexStageReport> incrementalStageObservation;
        GenerationCounts incrementalCounts;
        using (var store = new SonnetDbIndexGenerationStore(databasePath))
        {
            incrementalStageObservation = Observe(
                () => store.Stage(incrementalSnapshot, incrementalPlan, cancellationToken),
                databasePath);
            IndexStageReport incrementalStage = incrementalStageObservation.Value;
            incrementalCounts = incrementalStage.Manifest.Counts;
            problems.UnionWith(incrementalStage.Problems.Select(problem => "incremental_stage:" + problem));
            metrics.Add(CreateMetric(
                "incremental_stage",
                "warm",
                [incrementalStageObservation],
                "sonnetdb_full_generation_staging",
                incrementalCounts.FullTextDocuments));
        }

        double incrementalTotalMilliseconds = incrementalDiscoveryMilliseconds
            + incrementalSnapshotObservation.ElapsedMilliseconds
            + incrementalStageObservation.ElapsedMilliseconds;
        metrics.Add(CreateCombinedMetric(
            "incremental_index_total",
            "warm",
            incrementalTotalMilliseconds,
            incrementalDiscoveryAllocatedBytes + incrementalSnapshotObservation.AllocatedBytes + incrementalStageObservation.AllocatedBytes,
            Math.Max(incrementalDiscoveryPeakWorkingSetBytes, Math.Max(incrementalSnapshotObservation.PeakWorkingSetBytes, incrementalStageObservation.PeakWorkingSetBytes)),
            incrementalStageObservation.StorageGrowthBytes,
            "rediscover_reparse_full_generation_staging",
            _incrementalFileCount));

        string incrementalWorkspaceId = incrementalSnapshot.WorkspaceId;
        string incrementalIndexRevision = incrementalSnapshot.IndexRevision;
        incrementalSnapshotObservation = null;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        Observation<StagingGenerationInspection> reopenObservation = Observe(
            () =>
            {
                using var reopened = new SonnetDbIndexGenerationStore(databasePath);
                return reopened.InspectStaging(incrementalWorkspaceId, incrementalIndexRevision);
            },
            databasePath);
        metrics.Add(CreateMetric(
            "staging_reopen_validate",
            "cold",
            [reopenObservation],
            "sonnetdb_checkpoint_reopen_consistency",
            incrementalCounts.FullTextDocuments));
        if (!reopenObservation.Value.Complete)
        {
            problems.UnionWith(reopenObservation.Value.Problems.Select(problem => "reopen:" + problem));
        }

        if (corpus.LinesOfCode < scale.TargetLinesOfCode)
        {
            problems.Add("corpus_lines_below_target");
        }

        if (incrementalCounts.Symbols < scale.MinimumSymbols)
        {
            problems.Add("indexed_symbols_below_target");
        }

        C1CapacityMetric exactMetric = metrics.Single(metric => metric.Name == "staging_exact");
        C1CapacityMetric fullTextMetric = metrics.Single(metric => metric.Name == "staging_fulltext_top20");
        double exactSlo = string.Equals(scale.Id, "large", StringComparison.Ordinal) ? 100 : 50;
        double fullTextSlo = string.Equals(scale.Id, "large", StringComparison.Ordinal) ? 500 : 200;
        double initialIndexSlo = string.Equals(scale.Id, "large", StringComparison.Ordinal) ? 45 * 60 * 1_000 : 5 * 60 * 1_000;
        double incrementalIndexSlo = string.Equals(scale.Id, "large", StringComparison.Ordinal) ? 10_000 : 3_000;
        if (initialTotalMilliseconds > initialIndexSlo)
        {
            problems.Add("initial_index_p95_exceeded");
        }

        if (incrementalTotalMilliseconds > incrementalIndexSlo)
        {
            problems.Add("incremental_index_p95_exceeded");
        }

        if (exactMetric.P95Milliseconds > exactSlo)
        {
            problems.Add("staging_exact_p95_exceeded");
        }

        if (fullTextMetric.P95Milliseconds > fullTextSlo)
        {
            problems.Add("staging_fulltext_p95_exceeded");
        }

        long rssLimit = string.Equals(scale.Id, "large", StringComparison.Ordinal)
            ? 12L * 1024 * 1024 * 1024
            : 4L * 1024 * 1024 * 1024;
        if (metrics.Max(metric => metric.PeakWorkingSetBytes) > rssLimit)
        {
            problems.Add("process_peak_working_set_exceeded");
        }

        C1CapacityEnvironment environment = CollectEnvironment(workspacePath);
        if (environment.PhysicalCores is null
            || environment.Storage == "explicit_unknown"
            || environment.PowerProfile == "explicit_unknown"
            || environment.BackgroundLoad == "explicit_unknown")
        {
            problems.Add("capacity_environment_incomplete");
        }

        return new C1CapacityEvidenceReport
        {
            Commit = string.IsNullOrWhiteSpace(commit) ? "working_tree" : commit,
            CorpusManifestHash = Hash(manifestJson),
            Corpus = corpus,
            Environment = environment,
            InitialCounts = initialCounts,
            IncrementalCounts = incrementalCounts,
            ModifiedFiles = modifiedFiles,
            DatabaseBytes = DirectorySize(databasePath),
            Published = false,
            CorrectnessRecoveryPassed = false,
            PerformanceCapacityPassed = false,
            Metrics = metrics,
            Problems = problems.ToArray(),
            GeneratedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private static WorkspaceIndexSnapshot CreatePlanningSnapshot(WorkspaceIndexSnapshot snapshot) => new()
    {
        WorkspaceId = snapshot.WorkspaceId,
        RepositoryIdentity = snapshot.RepositoryIdentity,
        WorktreeIdentity = snapshot.WorktreeIdentity,
        Branch = snapshot.Branch,
        HeadRevision = snapshot.HeadRevision,
        SourceRevision = snapshot.SourceRevision,
        IndexRevision = snapshot.IndexRevision,
        PreviousIndexRevision = snapshot.PreviousIndexRevision,
        ProducerVersions = snapshot.ProducerVersions,
        Files = snapshot.Files.Select(file => new IndexedFile
        {
            Id = file.Id,
            Path = file.Path,
            ContentHash = file.ContentHash,
            Length = file.Length,
            Language = file.Language,
            SemanticTier = file.SemanticTier,
            AdapterId = file.AdapterId,
            AdapterVersion = file.AdapterVersion,
            Symbols = [],
            Chunks = [],
        }).ToArray(),
        Failures = snapshot.Failures,
    };

    private static async Task<Observation<T>> ObserveAsync<T>(Func<Task<T>> action, string databasePath)
    {
        long storageBefore = DirectorySize(databasePath);
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        long started = Stopwatch.GetTimestamp();
        T value = await action().ConfigureAwait(false);
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return CompleteObservation(value, elapsed, allocatedBefore, storageBefore, databasePath);
    }

    private static async Task<bool> InitializeGitWorkspaceAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workspacePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("init");
        startInfo.ArgumentList.Add("--initial-branch=main");
        using Process process = Process.Start(startInfo)
            ?? throw new IOException("Could not start git for the C1 capacity workspace.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            throw new IOException("Could not initialize C1 capacity Git workspace: " + error.Trim());
        }

        return true;
    }

    private static Observation<T> Observe<T>(Func<T> action, string databasePath)
    {
        long storageBefore = DirectorySize(databasePath);
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        long started = Stopwatch.GetTimestamp();
        T value = action();
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return CompleteObservation(value, elapsed, allocatedBefore, storageBefore, databasePath);
    }

    private static Observation<T> CompleteObservation<T>(
        T value,
        double elapsedMilliseconds,
        long allocatedBefore,
        long storageBefore,
        string databasePath)
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return new Observation<T>(
            value,
            elapsedMilliseconds,
            Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore),
            process.PeakWorkingSet64,
            Math.Max(0, DirectorySize(databasePath) - storageBefore));
    }

    private static C1CapacityMetric CreateMetric<T>(
        string name,
        string temperature,
        IReadOnlyList<Observation<T>> observations,
        string accessPath,
        long? throughputItems,
        long? candidates = null,
        long? examined = null,
        long? returned = null)
    {
        double[] elapsed = observations.Select(observation => observation.ElapsedMilliseconds).Order().ToArray();
        double totalSeconds = elapsed.Sum() / 1_000;
        return new C1CapacityMetric
        {
            Name = name,
            Temperature = temperature,
            Samples = elapsed.Length,
            P50Milliseconds = Percentile(elapsed, 0.50),
            P95Milliseconds = Percentile(elapsed, 0.95),
            P99Milliseconds = Percentile(elapsed, 0.99),
            ThroughputPerSecond = throughputItems is null || totalSeconds <= 0
                ? null
                : throughputItems.Value / totalSeconds,
            AllocatedBytes = observations.Sum(observation => observation.AllocatedBytes),
            PeakWorkingSetBytes = observations.Max(observation => observation.PeakWorkingSetBytes),
            StorageGrowthBytes = observations.Sum(observation => observation.StorageGrowthBytes),
            AccessPath = accessPath,
            Candidates = candidates,
            Examined = examined,
            Returned = returned,
        };
    }

    private static C1CapacityMetric CreateCombinedMetric(
        string name,
        string temperature,
        double elapsedMilliseconds,
        long allocatedBytes,
        long peakWorkingSetBytes,
        long storageGrowthBytes,
        string accessPath,
        long throughputItems) => new()
        {
            Name = name,
            Temperature = temperature,
            Samples = 1,
            P50Milliseconds = elapsedMilliseconds,
            P95Milliseconds = elapsedMilliseconds,
            P99Milliseconds = elapsedMilliseconds,
            ThroughputPerSecond = elapsedMilliseconds <= 0 ? null : throughputItems / (elapsedMilliseconds / 1_000),
            AllocatedBytes = allocatedBytes,
            PeakWorkingSetBytes = peakWorkingSetBytes,
            StorageGrowthBytes = storageGrowthBytes,
            AccessPath = accessPath,
        };

    private static C1CapacityEnvironment CollectEnvironment(string workspacePath)
    {
        string root = Path.GetPathRoot(workspacePath) ?? Path.DirectorySeparatorChar.ToString();
        string fileSystem;
        try
        {
            fileSystem = new DriveInfo(root).DriveFormat;
        }
        catch (IOException)
        {
            fileSystem = "unknown";
        }

        int? physicalCores = int.TryParse(
            Environment.GetEnvironmentVariable("COUPLET_PHYSICAL_CORES"),
            out int parsedPhysicalCores)
            ? parsedPhysicalCores
            : null;
        return new C1CapacityEnvironment
        {
            Cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
                ?? RuntimeInformation.ProcessArchitecture.ToString(),
            PhysicalCores = physicalCores,
            LogicalCores = Environment.ProcessorCount,
            AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            OperatingSystem = RuntimeInformation.OSDescription,
            Runtime = RuntimeInformation.FrameworkDescription,
            ExecutionMode = RuntimeFeature.IsDynamicCodeSupported ? "jit" : "native_aot",
            FileSystem = fileSystem,
            Storage = Environment.GetEnvironmentVariable("COUPLET_STORAGE_MODEL") ?? "explicit_unknown",
            PowerProfile = Environment.GetEnvironmentVariable("COUPLET_POWER_PROFILE") ?? "explicit_unknown",
            BackgroundLoad = Environment.GetEnvironmentVariable("COUPLET_BACKGROUND_LOAD") ?? "explicit_unknown",
        };
    }

    private static void EnsureEmpty(string path, string message)
    {
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new IOException(message);
        }

        Directory.CreateDirectory(path);
    }

    private static long DirectorySize(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total = checked(total + new FileInfo(file).Length);
                }
                catch (FileNotFoundException)
                {
                    // Concurrent checkpoint replacement can remove a file between enumerate and stat.
                }
                catch (DirectoryNotFoundException)
                {
                    // Concurrent compaction can replace a directory between enumerate and stat.
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // The database can replace a checkpoint directory while it is being enumerated.
        }

        return total;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static double Percentile(double[] sorted, double percentile)
    {
        int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private sealed record Observation<T>(
        T Value,
        double ElapsedMilliseconds,
        long AllocatedBytes,
        long PeakWorkingSetBytes,
        long StorageGrowthBytes);
}
