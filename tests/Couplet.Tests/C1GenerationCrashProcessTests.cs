#if COUPLET_SONNETDB_SOURCE_GENERATIONS
using System.Diagnostics;
using System.Text.Json;
using Couplet.Application.Indexing;
using Couplet.Application.Workspaces;
using Couplet.Core.Indexing;
using Couplet.Infrastructure.SonnetDb;
using SonnetDB.Generations;

namespace Couplet.Tests;

public sealed class C1GenerationCrashProcessTests
{
    private const string ProcessCrashTestHooksEnvironmentVariable =
        "COUPLET_ENABLE_PROCESS_CRASH_TEST_HOOKS";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task IndexStage_KillBeforePublishCommit_RestartPublishesCompleteNewRevision()
    {
        await VerifyKilledPublishAsync("before-commit", expectNewRevision: false);
    }

    [Fact]
    public async Task IndexStage_KillAfterPublishCommit_RestartReusesCompleteNewRevision()
    {
        await VerifyKilledPublishAsync("after-commit", expectNewRevision: true);
    }

    [Fact]
    public async Task IndexStage_ProcessCrashPauseWithoutExplicitOptIn_FailsClosed()
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "Sample.cs"),
                "public sealed class Sample { public int Token() => 1; }");
            using Process process = StartIndexStageProcess(
                workspace,
                database,
                pausePoint: "before-commit",
                enableProcessCrashTestHooks: false);
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(ProcessTimeout);
                Assert.Equal(64, process.ExitCode);
                Assert.Equal(string.Empty, await outputTask);
                using JsonDocument document = JsonDocument.Parse(await errorTask);
                JsonElement error = document.RootElement;
                Assert.Equal("couplet.index_stage.error.v1", error.GetProperty("schema_version").GetString());
                Assert.Equal("invalid_request", error.GetProperty("code").GetString());
                Assert.Equal("process_crash_test_hooks_disabled", error.GetProperty("reason").GetString());
            }
            finally
            {
                await TerminateIfRunningAsync(process);
            }
        }
        finally
        {
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    private static async Task VerifyKilledPublishAsync(string pausePoint, bool expectNewRevision)
    {
        string workspace = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            string sourcePath = Path.Combine(workspace, "Sample.cs");
            await File.WriteAllTextAsync(
                sourcePath,
                "public sealed class Sample { public int OriginalToken() => 1; }");
            DiscoveredWorkspace firstDiscovered = await WorkspaceDiscoveryService.DiscoverAsync(
                workspace,
                WorkspaceDiscoveryService.DefaultPolicy);
            WorkspaceIndexSnapshot firstSnapshot = await IndexSnapshotBuilder.BuildAsync(
                firstDiscovered,
                previousIndexRevision: null);
            string originalSymbolId = SymbolId(firstSnapshot, "OriginalToken");

            JsonElement firstReport = await RunIndexStageProcessAsync(workspace, database);
            Assert.Equal(
                firstSnapshot.IndexRevision,
                firstReport.GetProperty("manifest").GetProperty("index_revision").GetString());
            Assert.Equal(1, firstReport.GetProperty("database_generation_revision").GetInt64());

            await File.WriteAllTextAsync(
                sourcePath,
                "public sealed class Sample { public int UpdatedToken() => 2; }");
            DiscoveredWorkspace secondDiscovered = await WorkspaceDiscoveryService.DiscoverAsync(
                workspace,
                WorkspaceDiscoveryService.DefaultPolicy);
            WorkspaceIndexSnapshot secondSnapshot = await IndexSnapshotBuilder.BuildAsync(
                secondDiscovered,
                firstSnapshot.IndexRevision);
            string updatedSymbolId = SymbolId(secondSnapshot, "UpdatedToken");
            Assert.NotEqual(firstSnapshot.IndexRevision, secondSnapshot.IndexRevision);

            await RunIndexStageUntilPauseAndKillAsync(workspace, database, pausePoint);

            WorkspaceIndexSnapshot expectedSnapshot = expectNewRevision
                ? secondSnapshot
                : firstSnapshot;
            string expectedSymbolId = expectNewRevision ? updatedSymbolId : originalSymbolId;
            string unexpectedSymbolId = expectNewRevision ? originalSymbolId : updatedSymbolId;
            string expectedTerm = expectNewRevision ? "UpdatedToken" : "OriginalToken";
            string unexpectedTerm = expectNewRevision ? "OriginalToken" : "UpdatedToken";
            long expectedDatabaseRevision = expectNewRevision ? 2 : 1;

            using (var reopened = new SonnetDbIndexGenerationStore(database))
            {
                AssertVisibleGeneration(
                    reopened,
                    expectedSnapshot,
                    expectedDatabaseRevision,
                    expectedSymbolId,
                    unexpectedSymbolId,
                    expectedTerm,
                    unexpectedTerm);
                Assert.Equal(
                    expectNewRevision ? [1L, 2L] : [1L],
                    reopened.ListGenerationRevisionsForTest(expectedSnapshot.WorkspaceId));

                if (!expectNewRevision)
                {
                    AssertStoredGenerationComplete(reopened, secondSnapshot);
                }
            }

            JsonElement recoveryReport = await RunIndexStageProcessAsync(workspace, database);
            Assert.True(recoveryReport.GetProperty("staged").GetBoolean());
            Assert.True(recoveryReport.GetProperty("published").GetBoolean());
            Assert.Equal(
                expectNewRevision,
                recoveryReport.GetProperty("reused_active_generation").GetBoolean());
            Assert.Equal(
                secondSnapshot.IndexRevision,
                recoveryReport.GetProperty("manifest").GetProperty("index_revision").GetString());
            Assert.Equal(
                secondSnapshot.SourceRevision,
                recoveryReport.GetProperty("manifest").GetProperty("source_revision").GetString());
            Assert.Equal(2, recoveryReport.GetProperty("database_generation_revision").GetInt64());
            Assert.Equal(
                [1L],
                recoveryReport.GetProperty("removed_generation_revisions")
                    .EnumerateArray()
                    .Select(item => item.GetInt64())
                    .ToArray());
            Assert.Empty(recoveryReport.GetProperty("deferred_generation_revisions").EnumerateArray());
            Assert.Empty(
                recoveryReport.GetProperty("retention_deferred_generation_revisions").EnumerateArray());
            Assert.Empty(recoveryReport.GetProperty("problems").EnumerateArray());

            using var recovered = new SonnetDbIndexGenerationStore(database);
            AssertVisibleGeneration(
                recovered,
                secondSnapshot,
                expectedDatabaseRevision: 2,
                expectedSymbolId: updatedSymbolId,
                unexpectedSymbolId: originalSymbolId,
                expectedTerm: "UpdatedToken",
                unexpectedTerm: "OriginalToken");
            Assert.Equal(
                [2L],
                recovered.ListGenerationRevisionsForTest(secondSnapshot.WorkspaceId));
        }
        finally
        {
            DeleteTemporaryDirectory(workspace);
            DeleteTemporaryDirectory(database);
        }
    }

    private static void AssertVisibleGeneration(
        SonnetDbIndexGenerationStore store,
        WorkspaceIndexSnapshot expectedSnapshot,
        long expectedDatabaseRevision,
        string expectedSymbolId,
        string unexpectedSymbolId,
        string expectedTerm,
        string unexpectedTerm)
    {
        ActiveIndexPlanningSnapshot active = Assert.IsType<ActiveIndexPlanningSnapshot>(
            store.ReadActivePlanningSnapshot(expectedSnapshot.WorkspaceId));
        Assert.Equal(expectedDatabaseRevision, active.DatabaseGenerationRevision);
        Assert.Equal(expectedSnapshot.IndexRevision, active.PlanningSnapshot.IndexRevision);
        Assert.Equal(expectedSnapshot.SourceRevision, active.PlanningSnapshot.SourceRevision);

        AssertStoredGenerationComplete(store, expectedSnapshot);

        IReadOnlyList<IndexStorageDocument> expectedDocuments =
            IndexStorageMapper.CreateDocuments(expectedSnapshot);
        var expectedById = expectedDocuments.ToDictionary(
            document => document.StableId,
            StringComparer.Ordinal);
        foreach (IndexStorageDocument expectedDocument in expectedDocuments)
        {
            IndexStorageDocument actualDocument = Assert.Single(
                store.ProbeExact(
                    expectedSnapshot.WorkspaceId,
                    expectedSnapshot.IndexRevision,
                    expectedDocument.StableId).Documents);
            AssertStoredDocumentMatches(expectedDocument, actualDocument);
        }

        using (ActiveIndexQueryLease queryLease = store.AcquireActiveIndexQuery(
                   expectedSnapshot.WorkspaceId))
        {
            Assert.Equal(expectedDatabaseRevision, queryLease.DatabaseGenerationRevision);
            Assert.Equal(expectedSnapshot.IndexRevision, queryLease.PlanningSnapshot.IndexRevision);
            Assert.Equal(expectedSnapshot.IndexRevision, queryLease.Manifest.IndexRevision);
            Assert.Equal(expectedSnapshot.SourceRevision, queryLease.Manifest.SourceRevision);
        }

        using (DatabaseGenerationQueryLease generationLease = store.AcquireActiveGeneration(
                   expectedSnapshot.WorkspaceId))
        {
            Assert.Equal(expectedDatabaseRevision, generationLease.Generation.Revision);
            Assert.Equal(expectedSnapshot.IndexRevision, generationLease.Generation.GenerationId);
            Assert.Equal(3, generationLease.Generation.Resources.Count);
            DatabaseGenerationResource documents = Assert.Single(
                generationLease.Generation.Resources,
                resource => resource.Kind == DatabaseGenerationResourceKind.DocumentCollection);
            DatabaseGenerationResource fullText = Assert.Single(
                generationLease.Generation.Resources,
                resource => resource.Kind == DatabaseGenerationResourceKind.DocumentFullTextIndex);
            Assert.Single(
                generationLease.Generation.Resources,
                resource => resource.Kind == DatabaseGenerationResourceKind.KvKeyspace);
            Assert.Equal(documents.Name, fullText.ParentName);
        }

        IndexStorageDocument exact = Assert.Single(
            store.ProbeExact(
                expectedSnapshot.WorkspaceId,
                expectedSnapshot.IndexRevision,
                expectedSymbolId).Documents);
        Assert.Equal(expectedSnapshot.SourceRevision, exact.SourceRevision);
        Assert.Equal(expectedSnapshot.IndexRevision, exact.IndexRevision);
        Assert.Empty(store.ProbeExact(
            expectedSnapshot.WorkspaceId,
            expectedSnapshot.IndexRevision,
            unexpectedSymbolId).Documents);

        StagingQueryProbeResult expectedFullText = store.ProbeFullText(
            expectedSnapshot.WorkspaceId,
            expectedSnapshot.IndexRevision,
            expectedTerm,
            20);
        string[] expectedFullTextIds = expectedDocuments
            .Where(document => document.SearchText.Contains(
                expectedTerm,
                StringComparison.Ordinal))
            .Select(document => document.StableId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(expectedFullTextIds);
        Assert.Equal(
            expectedFullTextIds,
            expectedFullText.Documents
                .Select(document => document.StableId)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Contains(
            expectedFullText.Documents,
            document => document.StableId == expectedSymbolId);
        Assert.All(
            expectedFullText.Documents,
            document =>
            {
                Assert.True(expectedById.TryGetValue(
                    document.StableId,
                    out IndexStorageDocument? expectedDocument));
                AssertStoredDocumentMatches(expectedDocument, document);
                Assert.Equal(expectedSnapshot.SourceRevision, document.SourceRevision);
                Assert.Equal(expectedSnapshot.IndexRevision, document.IndexRevision);
            });
        Assert.Empty(store.ProbeFullText(
            expectedSnapshot.WorkspaceId,
            expectedSnapshot.IndexRevision,
            unexpectedTerm,
            20).Documents);
    }

    private static void AssertStoredDocumentMatches(
        IndexStorageDocument expected,
        IndexStorageDocument actual)
    {
        Assert.Equal(expected.RecordType, actual.RecordType);
        Assert.Equal(expected.StableId, actual.StableId);
        Assert.Equal(expected.WorkspaceId, actual.WorkspaceId);
        Assert.Equal(expected.SourceRevision, actual.SourceRevision);
        Assert.Equal(expected.IndexRevision, actual.IndexRevision);
        Assert.Equal(expected.Path, actual.Path);
        Assert.Equal(expected.ContentHash, actual.ContentHash);
        Assert.Equal(expected.SearchText, actual.SearchText);
    }

    private static void AssertStoredGenerationComplete(
        SonnetDbIndexGenerationStore store,
        WorkspaceIndexSnapshot expectedSnapshot)
    {
        IReadOnlyList<IndexStorageDocument> expectedDocuments =
            IndexStorageMapper.CreateDocuments(expectedSnapshot);
        StagingGenerationInspection inspection = store.InspectStaging(
            expectedSnapshot.WorkspaceId,
            expectedSnapshot.IndexRevision);
        Assert.True(inspection.Complete, string.Join(',', inspection.Problems));
        Assert.Empty(inspection.Problems);

        GenerationManifest manifest = Assert.IsType<GenerationManifest>(inspection.Manifest);
        Assert.Equal(expectedSnapshot.WorkspaceId, manifest.WorkspaceId);
        Assert.Equal(expectedSnapshot.IndexRevision, manifest.IndexRevision);
        Assert.Equal(expectedSnapshot.SourceRevision, manifest.SourceRevision);
        Assert.Equal(
            expectedDocuments.Count(document => document.RecordType == IndexStorageRecordType.File),
            manifest.Counts.Files);
        Assert.Equal(
            expectedDocuments.Count(document => document.RecordType == IndexStorageRecordType.Symbol),
            manifest.Counts.Symbols);
        Assert.Equal(
            expectedDocuments.Count(document => document.RecordType == IndexStorageRecordType.Chunk),
            manifest.Counts.Chunks);
        Assert.Equal(expectedDocuments.Count, manifest.Counts.FullTextDocuments);
    }

    private static async Task<JsonElement> RunIndexStageProcessAsync(string workspace, string database)
    {
        using Process process = StartIndexStageProcess(workspace, database, pausePoint: null);
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(ProcessTimeout);
            string output = await outputTask;
            string error = await errorTask;
            Assert.True(process.ExitCode == 0, $"CLI exited with {process.ExitCode}: {error}");
            Assert.Equal(string.Empty, error);
            using JsonDocument document = JsonDocument.Parse(output);
            return document.RootElement.Clone();
        }
        finally
        {
            await TerminateIfRunningAsync(process);
        }
    }

    private static async Task RunIndexStageUntilPauseAndKillAsync(
        string workspace,
        string database,
        string pausePoint)
    {
        using Process process = StartIndexStageProcess(workspace, database, pausePoint);
        Task<string?> readyTask = process.StandardOutput.ReadLineAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            string? ready = await readyTask.WaitAsync(ProcessTimeout);
            Assert.Equal($"couplet.internal-test.publish-paused:{pausePoint}", ready);
            Assert.False(process.HasExited);

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(ProcessTimeout);
            Assert.NotEqual(0, process.ExitCode);
            Assert.Equal(string.Empty, await errorTask);
        }
        finally
        {
            await TerminateIfRunningAsync(process);
        }
    }

    private static Process StartIndexStageProcess(
        string workspace,
        string database,
        string? pausePoint,
        bool enableProcessCrashTestHooks = true)
    {
        string cliAssembly = Path.Combine(AppContext.BaseDirectory, "Couplet.Cli.dll");
        Assert.True(File.Exists(cliAssembly), $"Couplet CLI assembly was not found: {cliAssembly}");
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(cliAssembly);
        startInfo.ArgumentList.Add("index-stage");
        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(workspace);
        startInfo.ArgumentList.Add("--database");
        startInfo.ArgumentList.Add(database);
        if (pausePoint is not null)
        {
            startInfo.ArgumentList.Add("--internal-test-publish-pause");
            startInfo.ArgumentList.Add(pausePoint);
            if (enableProcessCrashTestHooks)
            {
                startInfo.Environment[ProcessCrashTestHooksEnvironmentVariable] = "1";
            }
            else
            {
                _ = startInfo.Environment.Remove(ProcessCrashTestHooksEnvironmentVariable);
            }
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Couplet CLI process.");
    }

    private static async Task TerminateIfRunningAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            return;
        }

        await process.WaitForExitAsync().WaitAsync(ProcessTimeout);
    }

    private static string SymbolId(WorkspaceIndexSnapshot snapshot, string displayName) =>
        Assert.Single(
            snapshot.Files.SelectMany(file => file.Symbols),
            symbol => string.Equals(symbol.DisplayName, displayName, StringComparison.Ordinal)).Id;

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "couplet-generation-crash-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(
                     path,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(entry, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
#endif
