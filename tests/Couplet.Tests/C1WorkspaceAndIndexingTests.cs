using System.Diagnostics;
using System.Text;
using Couplet.Application.Indexing;
using Couplet.Application.Languages;
using Couplet.Application.Workspaces;
using Couplet.Core.Graph;
using Couplet.Core.Indexing;
using Couplet.Core.Languages;
using Couplet.Core.Workspaces;
using Couplet.Infrastructure.SonnetDb;
using SonnetDB.Engine;

namespace Couplet.Tests;

public sealed class C1WorkspaceAndIndexingTests
{
    [Fact]
    public async Task DiscoverAsync_WithSecurityAndFilePolicies_ClassifiesWithoutReadingDeniedContent()
    {
        string root = TemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "source.cs"), "public class Source { }");
            await File.WriteAllTextAsync(Path.Combine(root, "secret.env"), "TOKEN=secret");
            await File.WriteAllTextAsync(Path.Combine(root, "generated.g.cs"), "public class Generated { }");
            await File.WriteAllTextAsync(Path.Combine(root, "ignored.tmp"), "ignore me");
            await File.WriteAllBytesAsync(Path.Combine(root, "binary.bin"), [0, 1, 2, 3]);
            await File.WriteAllTextAsync(Path.Combine(root, "large.txt"), new string('x', 64));
            Directory.CreateDirectory(Path.Combine(root, "src", "bin"));
            await File.WriteAllTextAsync(Path.Combine(root, "src", "bin", "output.cs"), "public class Output { }");

            var policy = new WorkspaceDiscoveryPolicy
            {
                IgnorePatterns = ["*.tmp"],
                DenyPatterns = ["*.env", "**/bin/**"],
                GeneratedPatterns = ["**/*.g.cs"],
                MaxSemanticFileBytes = 32,
            };

            DiscoveredWorkspace workspace = await WorkspaceDiscoveryService.DiscoverAsync(root, policy);
            Dictionary<string, WorkspaceFileDescriptor> files = workspace.Result.Files
                .ToDictionary(file => file.Path, StringComparer.Ordinal);

            Assert.Equal(WorkspaceFileDisposition.Included, files["source.cs"].Disposition);
            Assert.Equal(WorkspaceFileDisposition.Denied, files["secret.env"].Disposition);
            Assert.Null(files["secret.env"].ContentHash);
            Assert.Equal(WorkspaceFileDisposition.Generated, files["generated.g.cs"].Disposition);
            Assert.Equal(WorkspaceFileDisposition.Ignored, files["ignored.tmp"].Disposition);
            Assert.Equal(WorkspaceFileDisposition.Binary, files["binary.bin"].Disposition);
            Assert.True(files["large.txt"].TextOnly);
            Assert.Equal("large_file_text_only", files["large.txt"].Reason);
            Assert.Equal(WorkspaceFileDisposition.Denied, files["src/bin/output.cs"].Disposition);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task DiscoverAsync_WithGitCommitAndEdit_ReportsHeadBranchAndDirtyDigest()
    {
        string root = TemporaryDirectory();
        try
        {
            RunGit(root, "init", "--initial-branch=main");
            RunGit(root, "config", "user.email", "couplet@example.invalid");
            RunGit(root, "config", "user.name", "Couplet Tests");
            await File.WriteAllTextAsync(Path.Combine(root, "sample.cs"), "public class Sample { }");
            RunGit(root, "add", "sample.cs");
            RunGit(root, "commit", "-m", "initial");

            DiscoveredWorkspace clean = await WorkspaceDiscoveryService.DiscoverAsync(
                root,
                WorkspaceDiscoveryService.DefaultPolicy);
            Assert.False(clean.Result.IsDirty);
            Assert.Equal("main", clean.Result.Branch);
            Assert.NotNull(clean.Result.HeadRevision);
            Assert.Equal(clean.Result.HeadRevision, clean.Result.SourceRevision);

            await File.WriteAllTextAsync(Path.Combine(root, "sample.cs"), "public class Changed { }");
            DiscoveredWorkspace dirty = await WorkspaceDiscoveryService.DiscoverAsync(
                root,
                WorkspaceDiscoveryService.DefaultPolicy);
            Assert.True(dirty.Result.IsDirty);
            Assert.StartsWith(clean.Result.HeadRevision + "+dirty.", dirty.Result.SourceRevision, StringComparison.Ordinal);
            Assert.Equal(clean.Result.WorkspaceId, dirty.Result.WorkspaceId);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void IsWithinRoot_WithSiblingPrefixAndTraversal_RejectsEscapes()
    {
        string parent = TemporaryDirectory();
        string root = Path.Combine(parent, "workspace");
        string sibling = Path.Combine(parent, "workspace-other", "secret.cs");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        try
        {
            Assert.True(WorkspacePathGuard.IsWithinRoot(root, Path.Combine(root, "src", "file.cs")));
            Assert.False(WorkspacePathGuard.IsWithinRoot(root, sibling));
            Assert.False(WorkspacePathGuard.IsWithinRoot(root, Path.Combine(root, "..", "outside.cs")));
        }
        finally
        {
            DeleteTemporaryDirectory(parent);
        }
    }

    [Fact]
    public async Task BuildAsync_WhenFileChangesAfterDiscovery_ReportsStableFailure()
    {
        string root = TemporaryDirectory();
        try
        {
            string path = Path.Combine(root, "sample.cs");
            await File.WriteAllTextAsync(path, "public class Before { }");
            DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                root,
                WorkspaceDiscoveryService.DefaultPolicy);
            await File.WriteAllTextAsync(path, "public class After { }");

            WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(discovered, null);

            FileIndexFailure failure = Assert.Single(snapshot.Failures);
            Assert.Equal("sample.cs", failure.Path);
            Assert.Equal("file_changed_during_snapshot", failure.Code);
            Assert.Empty(snapshot.Files);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task DiscoverAsync_WithCancelledToken_StopsBeforeFileRead()
    {
        string root = TemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "sample.cs"), "public class Sample { }");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                WorkspaceDiscoveryService.DiscoverAsync(root, WorkspaceDiscoveryService.DefaultPolicy, cancellation.Token));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Theory]
    [InlineData("csharp", "namespace Demo; public class Greeter { public string Say(int count) { return count.ToString(); } }", "Demo.Greeter", "Demo.Greeter.Say(int count)")]
    [InlineData("typescript", "export class Greeter { say(name: string): string { return name; } } export function top(value: number) { return value; }", "Greeter", "Greeter.say(name:string)")]
    public void Parse_WithSupportedDeclaration_ReturnsPartialTierStableSymbolsAndUtf8Spans(
        string language,
        string content,
        string typeIdentity,
        string memberIdentity)
    {
        ILanguageAdapter adapter = Assert.IsAssignableFrom<ILanguageAdapter>(BuiltinLanguageAdapters.Find(language));
        IndexedFile file = adapter.Parse(new LanguageParseRequest
        {
            WorkspaceId = "cpl_workspace_test",
            SourceRevision = "source-1",
            IndexRevision = "index-1",
            Path = language == "csharp" ? "src/Greeter.cs" : "src/greeter.ts",
            ContentHash = Hash(content),
            Content = content,
        });

        Assert.Equal(SemanticTier.Partial, file.SemanticTier);
        IndexedSymbol type = Assert.Single(file.Symbols, symbol => symbol.QualifiedIdentity == typeIdentity);
        IndexedSymbol member = Assert.Single(file.Symbols, symbol => symbol.QualifiedIdentity == memberIdentity);
        Assert.Equal(ConfidenceKind.Inferred, type.Confidence.Kind);
        Assert.Equal(type.Id, member.ContainerId);
        Assert.Equal(type.DisplayName, Utf8Slice(content, type.Definition));
        Assert.Equal(member.DisplayName, Utf8Slice(content, member.Definition));
        Assert.Contains(file.Chunks, chunk => chunk.SymbolId == member.Id);
    }

    [Fact]
    public void Plan_WithRenameModificationAndDeletion_ReturnsDeterministicChanges()
    {
        WorkspaceIndexSnapshot previous = Snapshot(
            "index-1",
            [IndexedFileFor("a.cs", "hash-a"), IndexedFileFor("b.cs", "hash-b"), IndexedFileFor("deleted.cs", "hash-d")]);
        WorkspaceIndexSnapshot current = Snapshot(
            "index-2",
            [IndexedFileFor("renamed.cs", "hash-a"), IndexedFileFor("b.cs", "hash-c"), IndexedFileFor("new.cs", "hash-n")]);

        IncrementalIndexPlan plan = IncrementalIndexPlanner.Plan(previous, current);

        Assert.Collection(
            plan.Changes,
            change => Assert.Equal(IndexFileChangeKind.Modified, change.Kind),
            change => Assert.Equal(IndexFileChangeKind.Deleted, change.Kind),
            change => Assert.Equal(IndexFileChangeKind.Added, change.Kind),
            change =>
            {
                Assert.Equal(IndexFileChangeKind.Renamed, change.Kind);
                Assert.Equal("a.cs", change.PreviousPath);
                Assert.Equal("renamed.cs", change.Path);
            });
    }

    [Fact]
    public void Plan_WithProducerUpgrade_RequiresDeterministicRebuild()
    {
        WorkspaceIndexSnapshot previous = Snapshot("index-1", [IndexedFileFor("a.cs", "hash-a")]);
        WorkspaceIndexSnapshot current = new()
        {
            WorkspaceId = previous.WorkspaceId,
            SourceRevision = "source-2",
            IndexRevision = "index-2",
            PreviousIndexRevision = previous.IndexRevision,
            ProducerVersions = ["adapter@2"],
            Files = [IndexedFileFor("a.cs", "hash-a")],
            Failures = [],
        };

        IncrementalIndexPlan plan = IncrementalIndexPlanner.Plan(previous, current);

        Assert.True(plan.RebuildRequired);
        Assert.Equal("producer_version_changed", plan.RebuildReason);
        Assert.All(plan.Changes, change => Assert.Equal(IndexFileChangeKind.Added, change.Kind));
    }

    [Fact]
    public void Plan_WithPersistedLexicalProducerVersion100_ReturnsProducerVersionChangedContract()
    {
        string[] currentVersions = BuiltinLanguageAdapters.All
            .Select(adapter => adapter.Capability.AdapterId + "@" + adapter.Capability.AdapterVersion)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] previousVersions = currentVersions
            .Select(version => version.Replace("@1.1.0", "@1.0.0", StringComparison.Ordinal))
            .ToArray();
        WorkspaceIndexSnapshot previous = new()
        {
            WorkspaceId = "workspace",
            SourceRevision = "source-1",
            IndexRevision = "index-1",
            ProducerVersions = previousVersions,
            Files = [IndexedFileFor("a.cs", "hash-a")],
            Failures = [],
        };
        WorkspaceIndexSnapshot current = new()
        {
            WorkspaceId = previous.WorkspaceId,
            SourceRevision = "source-2",
            IndexRevision = "index-2",
            PreviousIndexRevision = previous.IndexRevision,
            ProducerVersions = currentVersions,
            Files = [IndexedFileFor("a.cs", "hash-a")],
            Failures = [],
        };

        IncrementalIndexPlan plan = IncrementalIndexPlanner.Plan(previous, current);

        Assert.All(currentVersions, version => Assert.EndsWith("@1.1.0", version, StringComparison.Ordinal));
        Assert.All(previousVersions, version => Assert.EndsWith("@1.0.0", version, StringComparison.Ordinal));
        Assert.True(plan.RebuildRequired);
        Assert.Equal("producer_version_changed", plan.RebuildReason);
        Assert.All(plan.Changes, change => Assert.Equal(IndexFileChangeKind.Added, change.Kind));
    }

    [Fact]
    public async Task Plan_AfterRealGitBranchSwitch_RebuildsAndKeepsStagingGenerationsIsolatedAcrossReopen()
    {
        string root = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            RunGit(root, "init", "--initial-branch=main");
            RunGit(root, "config", "user.email", "couplet@example.invalid");
            RunGit(root, "config", "user.name", "Couplet Tests");
            await File.WriteAllTextAsync(Path.Combine(root, "Shared.cs"), "public class Shared { public int Value() => 1; }");
            RunGit(root, "add", "Shared.cs");
            RunGit(root, "commit", "-m", "shared");
            RunGit(root, "branch", "feature");

            await File.WriteAllTextAsync(Path.Combine(root, "MainOnly.cs"), "public class MainOnly { }");
            RunGit(root, "add", "MainOnly.cs");
            RunGit(root, "commit", "-m", "main only");
            DiscoveredWorkspace mainWorkspace = await WorkspaceDiscoveryService.DiscoverAsync(
                root,
                WorkspaceDiscoveryService.DefaultPolicy);
            WorkspaceIndexSnapshot mainSnapshot = await IndexSnapshotBuilder.BuildAsync(mainWorkspace, null);

            RunGit(root, "checkout", "feature");
            await File.WriteAllTextAsync(Path.Combine(root, "Shared.cs"), "public class Shared { public int Value() => 2; }");
            await File.WriteAllTextAsync(Path.Combine(root, "FeatureOnly.cs"), "public class FeatureOnly { }");
            RunGit(root, "add", "Shared.cs", "FeatureOnly.cs");
            RunGit(root, "commit", "-m", "feature only");
            DiscoveredWorkspace featureWorkspace = await WorkspaceDiscoveryService.DiscoverAsync(
                root,
                WorkspaceDiscoveryService.DefaultPolicy);
            WorkspaceIndexSnapshot featureSnapshot = await IndexSnapshotBuilder.BuildAsync(
                featureWorkspace,
                mainSnapshot.IndexRevision);
            IncrementalIndexPlan switchPlan = IncrementalIndexPlanner.Plan(mainSnapshot, featureSnapshot);

            Assert.Equal("main", mainSnapshot.Branch);
            Assert.Equal("feature", featureSnapshot.Branch);
            Assert.NotEqual(mainSnapshot.HeadRevision, featureSnapshot.HeadRevision);
            Assert.True(switchPlan.RebuildRequired);
            Assert.Equal("git_branch_changed", switchPlan.RebuildReason);
            Assert.All(switchPlan.Changes, change => Assert.Equal(IndexFileChangeKind.Added, change.Kind));

            string mainOnlyId = Assert.Single(
                mainSnapshot.Files.SelectMany(file => file.Symbols),
                symbol => symbol.QualifiedIdentity == "MainOnly").Id;
            string featureOnlyId = Assert.Single(
                featureSnapshot.Files.SelectMany(file => file.Symbols),
                symbol => symbol.QualifiedIdentity == "FeatureOnly").Id;

            using (var store = new SonnetDbIndexGenerationStore(database))
            {
                IndexStageReport mainReport = store.Stage(
                    mainSnapshot,
                    IncrementalIndexPlanner.Plan(null, mainSnapshot));
                IndexStageReport featureReport = store.Stage(featureSnapshot, switchPlan);

                Assert.True(mainReport.Staged);
                Assert.True(featureReport.Staged);
                Assert.False(mainReport.Published);
                Assert.False(featureReport.Published);
                Assert.Single(store.ProbeExact(mainSnapshot.WorkspaceId, mainSnapshot.IndexRevision, mainOnlyId).Documents);
                Assert.Empty(store.ProbeExact(featureSnapshot.WorkspaceId, featureSnapshot.IndexRevision, mainOnlyId).Documents);
                Assert.Single(store.ProbeExact(featureSnapshot.WorkspaceId, featureSnapshot.IndexRevision, featureOnlyId).Documents);
            }

            using var reopened = new SonnetDbIndexGenerationStore(database);
            Assert.True(reopened.InspectStaging(mainSnapshot.WorkspaceId, mainSnapshot.IndexRevision).Complete);
            Assert.True(reopened.InspectStaging(featureSnapshot.WorkspaceId, featureSnapshot.IndexRevision).Complete);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public void CreateDocuments_WithRepeatedLogicalSymbol_CollapsesToDeterministicPrimaryDefinition()
    {
        const string firstContent = "namespace Demo; public partial class Shared { public void First() { } }";
        const string secondContent = "namespace Demo; public partial class Shared { public void Second() { } }";
        ILanguageAdapter adapter = Assert.IsAssignableFrom<ILanguageAdapter>(BuiltinLanguageAdapters.Find("csharp"));
        IndexedFile first = adapter.Parse(new LanguageParseRequest
        {
            WorkspaceId = "cpl_workspace_test",
            SourceRevision = "source-1",
            IndexRevision = "index-1",
            Path = "a.cs",
            ContentHash = Hash(firstContent),
            Content = firstContent,
        });
        IndexedFile second = adapter.Parse(new LanguageParseRequest
        {
            WorkspaceId = "cpl_workspace_test",
            SourceRevision = "source-1",
            IndexRevision = "index-1",
            Path = "b.cs",
            ContentHash = Hash(secondContent),
            Content = secondContent,
        });
        WorkspaceIndexSnapshot snapshot = Snapshot("index-1", [second, first]);

        IReadOnlyList<IndexStorageDocument> documents = IndexStorageMapper.CreateDocuments(snapshot);
        IndexStorageDocument shared = Assert.Single(documents, document =>
            document.RecordType == IndexStorageRecordType.Symbol
            && document.QualifiedIdentity == "Demo.Shared");
        GenerationManifest manifest = IndexStorageMapper.CreateManifest(snapshot, documents, DateTimeOffset.UnixEpoch);

        Assert.Equal("a.cs", shared.Path);
        Assert.Equal(documents.Count(document => document.RecordType == IndexStorageRecordType.Symbol), manifest.Counts.Symbols);
        Assert.Equal(documents.Count, manifest.Counts.FullTextDocuments);
    }

    [Fact]
    public async Task WatchAsync_WithFileWrite_ReturnsRelativeDebouncedBatch()
    {
        string root = TemporaryDirectory();
        try
        {
            using var monitor = new WorkspaceChangeMonitor(root);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using IAsyncEnumerator<WorkspaceChangeBatch> batches = monitor
                .WatchAsync(TimeSpan.FromMilliseconds(50), cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);

            await File.WriteAllTextAsync(Path.Combine(root, "changed.cs"), "public class Changed { }");
            Assert.True(await batches.MoveNextAsync());

            Assert.Contains("changed.cs", batches.Current.Paths);
            Assert.False(batches.Current.RequiresFullRescan);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Stage_WithEmptyWorkspace_PersistsVerifiedEmptyUnpublishedGeneration()
    {
        string root = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                root,
                WorkspaceDiscoveryService.DefaultPolicy);
            WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(discovered, null);
            IncrementalIndexPlan plan = IncrementalIndexPlanner.Plan(null, snapshot);

            using var store = new SonnetDbIndexGenerationStore(database);
            IndexStageReport report = store.Stage(snapshot, plan);

            Assert.True(report.Staged);
            Assert.False(report.Published);
            Assert.Equal("CG-005", report.BlockingGap);
            Assert.Empty(report.Limitations);
            Assert.Empty(report.Problems);
            Assert.Equal(0, report.Manifest.Counts.Files);
            Assert.Equal(0, report.Manifest.Counts.Symbols);
            Assert.Equal(0, report.Manifest.Counts.Chunks);
            Assert.Equal(0, report.Manifest.Counts.FullTextDocuments);
            Assert.NotNull(store.ReadStagingManifest(snapshot.WorkspaceId, snapshot.IndexRevision));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task Stage_WithCSharpAndTypeScript_PersistsVerifiedUnpublishedGenerationAcrossReopen()
    {
        string root = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "namespace Demo; public class Sample { public void Run() { } }");
            await File.WriteAllTextAsync(Path.Combine(root, "sample.ts"), "export function run(value: number) { return value; }");
            DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                root,
                WorkspaceDiscoveryService.DefaultPolicy);
            WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(discovered, null);
            IncrementalIndexPlan plan = IncrementalIndexPlanner.Plan(null, snapshot);

            IndexStageReport report;
            using (var store = new SonnetDbIndexGenerationStore(database))
            {
                report = store.Stage(snapshot, plan);
                Assert.NotNull(store.ReadStagingManifest(snapshot.WorkspaceId, snapshot.IndexRevision));
                string symbolId = snapshot.Files.SelectMany(file => file.Symbols).First().Id;
                StagingQueryProbeResult exact = store.ProbeExact(snapshot.WorkspaceId, snapshot.IndexRevision, symbolId);
                Assert.Equal("document_path_index:by_stable_id", exact.AccessPath);
                Assert.Single(exact.Documents);
                Assert.Equal(symbolId, exact.Documents[0].StableId);

                StagingQueryProbeResult fullText = store.ProbeFullText(
                    snapshot.WorkspaceId,
                    snapshot.IndexRevision,
                    "Sample",
                    20);
                Assert.Equal("document_fulltext:code_search", fullText.AccessPath);
                Assert.NotEmpty(fullText.Documents);

                IndexStageReport retried = store.Stage(snapshot, plan);
                Assert.True(retried.Staged);
                Assert.Equal(report.Manifest.Checksum, retried.Manifest.Checksum);
            }

            Assert.True(report.Staged);
            Assert.False(report.Published);
            Assert.Equal("CG-005", report.BlockingGap);
            Assert.Empty(report.Limitations);
            Assert.Empty(report.Problems);
            Assert.Equal(2, report.Manifest.Counts.Files);
            Assert.True(report.Manifest.Counts.Symbols >= 3);
            Assert.True(report.Manifest.Counts.FullTextDocuments >= report.Manifest.Counts.Symbols);

            using var reopened = new SonnetDbIndexGenerationStore(database);
            StagingGenerationInspection inspection = reopened.InspectStaging(
                snapshot.WorkspaceId,
                snapshot.IndexRevision);
            Assert.True(inspection.Complete);
            Assert.Empty(inspection.Problems);
            GenerationManifest manifest = Assert.IsType<GenerationManifest>(inspection.Manifest);
            Assert.Equal(snapshot.IndexRevision, manifest.IndexRevision);
            Assert.Equal(GenerationState.Staging, manifest.State);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
            DeleteTemporaryDirectory(database);
        }
    }

    [Fact]
    public async Task InspectStaging_AfterMissingOrCorruptCompletionMarker_RejectsGenerationAndAllowsDeterministicRestage()
    {
        string root = TemporaryDirectory();
        string database = TemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "public class Sample { public void Run() { } }");
            DiscoveredWorkspace discovered = await WorkspaceDiscoveryService.DiscoverAsync(
                root,
                WorkspaceDiscoveryService.DefaultPolicy);
            WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(discovered, null);
            IncrementalIndexPlan plan = IncrementalIndexPlanner.Plan(null, snapshot);

            using (var store = new SonnetDbIndexGenerationStore(database))
            {
                Assert.True(store.Stage(snapshot, plan).Staged);
            }

            string stagingKey = $"staging/{snapshot.WorkspaceId}/{snapshot.IndexRevision}";
            using (Tsdb raw = Tsdb.Open(new TsdbOptions { RootDirectory = database }))
            {
                Assert.True(raw.Keyspaces.Open("couplet_control").Delete(stagingKey));
                raw.Keyspaces.Open("couplet_control").CreateSnapshot();
            }

            using (var reopened = new SonnetDbIndexGenerationStore(database))
            {
                StagingGenerationInspection missing = reopened.InspectStaging(
                    snapshot.WorkspaceId,
                    snapshot.IndexRevision);
                Assert.False(missing.Complete);
                Assert.Contains("staging_manifest_missing", missing.Problems);
                Assert.Null(reopened.ReadStagingManifest(snapshot.WorkspaceId, snapshot.IndexRevision));
                Assert.True(reopened.Stage(snapshot, plan).Staged);
            }

            using (Tsdb raw = Tsdb.Open(new TsdbOptions { RootDirectory = database }))
            {
                raw.Keyspaces.Open("couplet_control").Put(stagingKey, Encoding.UTF8.GetBytes("{"));
                raw.Keyspaces.Open("couplet_control").CreateSnapshot();
            }

            using var corruptReopen = new SonnetDbIndexGenerationStore(database);
            StagingGenerationInspection corrupt = corruptReopen.InspectStaging(
                snapshot.WorkspaceId,
                snapshot.IndexRevision);
            Assert.False(corrupt.Complete);
            Assert.Contains("staging_manifest_invalid", corrupt.Problems);
            Assert.Null(corruptReopen.ReadStagingManifest(snapshot.WorkspaceId, snapshot.IndexRevision));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
            DeleteTemporaryDirectory(database);
        }
    }

    private static WorkspaceIndexSnapshot Snapshot(string indexRevision, IReadOnlyList<IndexedFile> files) => new()
    {
        WorkspaceId = "workspace",
        SourceRevision = "source-" + indexRevision,
        IndexRevision = indexRevision,
        ProducerVersions = ["adapter@1"],
        Files = files,
        Failures = [],
    };

    private static IndexedFile IndexedFileFor(string path, string hash) => new()
    {
        Id = "file-" + path,
        Path = path,
        ContentHash = hash,
        Length = 1,
        Language = "csharp",
        SemanticTier = SemanticTier.Partial,
        AdapterId = "adapter",
        AdapterVersion = "1",
        Symbols = [],
        Chunks = [],
    };

    private static string Utf8Slice(string content, SourceSpan span)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        return Encoding.UTF8.GetString(bytes.AsSpan(checked((int)span.StartByte), checked((int)(span.EndByte - span.StartByte))));
    }

    private static string Hash(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "couplet-c1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(entry, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }
}
