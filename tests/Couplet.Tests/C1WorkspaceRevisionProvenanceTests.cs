using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Couplet.Application.Indexing;
using Couplet.Application.Workspaces;
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
using Couplet.Core.Capabilities;
#endif
using Couplet.Core.Graph;
using Couplet.Core.Indexing;
using Couplet.Core.Languages;
using Couplet.Core.Workspaces;
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
using Couplet.Infrastructure.SonnetDb;
#endif

namespace Couplet.Tests;

public sealed class C1WorkspaceRevisionProvenanceTests
{
    [Fact]
    public async Task DiscoverAsync_WithCleanCommittedInputs_PreservesStableHeadRevision()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");

            DiscoveredWorkspace first = await DiscoverAsync(root);
            DiscoveredWorkspace second = await DiscoverAsync(root);
            WorkspaceIndexSnapshot firstSnapshot = await IndexSnapshotBuilder.BuildAsync(first, null);
            WorkspaceIndexSnapshot secondSnapshot = await IndexSnapshotBuilder.BuildAsync(second, null);

            Assert.False(first.Result.IsDirty);
            Assert.NotNull(first.Result.HeadRevision);
            Assert.Equal(first.Result.HeadRevision, first.Result.SourceRevision);
            Assert.Equal(
                StableId.CreateWorkspace(first.Result.RepositoryIdentity, first.Result.WorktreeIdentity),
                first.Result.WorkspaceId);
            Assert.Equal(first.Result.SourceRevision, second.Result.SourceRevision);
            Assert.Equal(firstSnapshot.IndexRevision, secondSnapshot.IndexRevision);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task DiscoverAsync_WithOrdinaryDirtyContent_UsesStableContentProvenance()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");
            DiscoveredWorkspace clean = await DiscoverAsync(root);
            await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "public class Changed { }");

            DiscoveredWorkspace first = await DiscoverAsync(root);
            DiscoveredWorkspace second = await DiscoverAsync(root);

            Assert.True(first.Result.IsDirty);
            Assert.StartsWith(clean.Result.HeadRevision + "+dirty.", first.Result.SourceRevision, StringComparison.Ordinal);
            Assert.Equal(first.Result.SourceRevision, second.Result.SourceRevision);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task DiscoverAsync_WithAssumeUnchangedContentChange_DetectsContentBoundRevision()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");
            DiscoveredWorkspace clean = await DiscoverAsync(root);
            await RunGitAsync(root, "update-index", "--assume-unchanged", "Sample.cs");
            await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "public class Changed { }");

            Assert.Equal(string.Empty, await RunGitCaptureAsync(root, "status", "--porcelain=v1", "--", "."));
            DiscoveredWorkspace first = await DiscoverAsync(root);
            DiscoveredWorkspace second = await DiscoverAsync(root);

            Assert.True(first.Result.IsDirty);
            Assert.StartsWith(clean.Result.HeadRevision + "+dirty.", first.Result.SourceRevision, StringComparison.Ordinal);
            Assert.NotEqual(clean.Result.SourceRevision, first.Result.SourceRevision);
            Assert.Equal(first.Result.SourceRevision, second.Result.SourceRevision);
            Assert.Equal(
                first.Result.Files.Single(file => file.Path == "Sample.cs").ContentHash,
                second.Result.Files.Single(file => file.Path == "Sample.cs").ContentHash);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task DiscoverAsync_WithAssumeUnchangedTextReplacedByBinary_DoesNotReportCleanHead()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");
            await RunGitAsync(root, "update-index", "--assume-unchanged", "Sample.cs");
            await File.WriteAllBytesAsync(Path.Combine(root, "Sample.cs"), [0, 1, 2, 3]);

            Assert.Equal(string.Empty, await RunGitCaptureAsync(root, "status", "--porcelain=v1", "--", "."));
            DiscoveredWorkspace discovered = await DiscoverAsync(root);

            WorkspaceFileDescriptor file = Assert.Single(discovered.Result.Files);
            Assert.Equal(WorkspaceFileDisposition.Binary, file.Disposition);
            Assert.True(discovered.Result.IsDirty);
            Assert.NotEqual(discovered.Result.HeadRevision, discovered.Result.SourceRevision);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task DiscoverAsync_WithSizeChangingBinarySmudgeFilter_PreservesCleanHeadRevision()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");
            await File.WriteAllTextAsync(Path.Combine(root, "Asset.dat"), "pointer\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".gitattributes"), "Asset.dat filter=binary-smudge\n");
            await RunGitAsync(root, "add", "Asset.dat", ".gitattributes");
            await RunGitAsync(root, "commit", "-m", "add filtered asset pointer");
            string pointerObjectId = await RunGitCaptureAsync(root, "rev-parse", "HEAD:Asset.dat");

            string payloadPath = Path.Combine(root, "Payload.bin");
            byte[] payload = [0, 1, 2, 3, 4, 5];
            await File.WriteAllBytesAsync(payloadPath, payload);
            string binaryObjectId = await RunGitCaptureAsync(root, "hash-object", "-w", "Payload.bin");
            File.Delete(payloadPath);
            await RunGitAsync(
                root,
                "config",
                "filter.binary-smudge.clean",
                $"git cat-file blob {pointerObjectId}");
            await RunGitAsync(
                root,
                "config",
                "filter.binary-smudge.smudge",
                $"git cat-file blob {binaryObjectId}");
            await RunGitAsync(root, "config", "filter.binary-smudge.required", "true");
            await File.WriteAllBytesAsync(Path.Combine(root, "Asset.dat"), payload);
            await RunGitAsync(root, "add", "Asset.dat");

            Assert.Equal(string.Empty, await RunGitCaptureAsync(root, "status", "--porcelain=v1", "--", "."));
            DiscoveredWorkspace discovered = await DiscoverAsync(root);

            WorkspaceFileDescriptor asset = Assert.Single(
                discovered.Result.Files,
                file => file.Path == "Asset.dat");
            Assert.Equal(WorkspaceFileDisposition.Binary, asset.Disposition);
            Assert.False(discovered.Result.IsDirty);
            Assert.Equal(discovered.Result.HeadRevision, discovered.Result.SourceRevision);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task DiscoverAsync_WithUtf8RuneCrossingProbeBoundary_PreservesTextClassification()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, new string('a', 8191) + "\u00e9");

            DiscoveredWorkspace discovered = await DiscoverAsync(root);

            WorkspaceFileDescriptor file = Assert.Single(discovered.Result.Files);
            Assert.Equal(WorkspaceFileDisposition.Included, file.Disposition);
            Assert.False(discovered.Result.IsDirty);
            Assert.Equal(discovered.Result.HeadRevision, discovered.Result.SourceRevision);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task BuildAsync_AfterFreshDiscoveryWithSameOrdinal_DoesNotReuseChangedContentRevision()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");
            DiscoveredWorkspace initial = await DiscoverAsync(root);
            WorkspaceIndexSnapshot initialSnapshot = await IndexSnapshotBuilder.BuildAsync(initial, null);

            await RunGitAsync(root, "update-index", "--assume-unchanged", "Sample.cs");
            await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "public class Changed { }");
            DiscoveredWorkspace changed = await DiscoverAsync(root);
            WorkspaceIndexSnapshot changedSnapshot = await IndexSnapshotBuilder.BuildAsync(changed, null);

            DiscoveredWorkspace reopened = await DiscoverAsync(root);
            WorkspaceIndexSnapshot reopenedSnapshot = await IndexSnapshotBuilder.BuildAsync(reopened, null);

            Assert.StartsWith("cpl_idx_0000000000000001_", initialSnapshot.IndexRevision, StringComparison.Ordinal);
            Assert.StartsWith("cpl_idx_0000000000000001_", changedSnapshot.IndexRevision, StringComparison.Ordinal);
            Assert.NotEqual(initialSnapshot.SourceRevision, changedSnapshot.SourceRevision);
            Assert.NotEqual(initialSnapshot.IndexRevision, changedSnapshot.IndexRevision);
            Assert.Equal(changedSnapshot.SourceRevision, reopenedSnapshot.SourceRevision);
            Assert.Equal(changedSnapshot.IndexRevision, reopenedSnapshot.IndexRevision);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task DiscoverAsync_WithCommittedTrackedSymlink_PreservesCleanHeadRevision()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");
            CreateSymbolicLinkOrSkip(Path.Combine(root, "Alias With Space.cs"), "Sample.cs");

            await RunGitAsync(root, "config", "core.symlinks", "true");
            await RunGitAsync(root, "add", "Alias With Space.cs");
            await RunGitAsync(root, "commit", "-m", "add tracked symlink");

            DiscoveredWorkspace discovered = await DiscoverAsync(root);

            Assert.False(discovered.Result.IsDirty);
            Assert.Equal(discovered.Result.HeadRevision, discovered.Result.SourceRevision);
            Assert.True(discovered.Result.Files.Single(file => file.Path == "Alias With Space.cs").IsSymlink);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task DiscoverAsync_WithTrackedSymlinkThroughLinkedDirectory_PreservesCleanHeadRevision()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");
            Directory.CreateDirectory(Path.Combine(root, "real"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "real", "Target.cs"),
                "public class Target { }");
            CreateDirectorySymbolicLinkOrSkip(Path.Combine(root, "linkdir"), "real");
            CreateSymbolicLinkOrSkip(Path.Combine(root, "Alias.cs"), "linkdir/Target.cs");
            await RunGitAsync(root, "config", "core.symlinks", "true");
            await RunGitAsync(root, "add", "real/Target.cs", "linkdir", "Alias.cs");
            await RunGitAsync(root, "commit", "-m", "add linked directory chain");

            Assert.Equal(string.Empty, await RunGitCaptureAsync(root, "status", "--porcelain=v1", "--", "."));
            DiscoveredWorkspace discovered = await DiscoverAsync(root);

            Assert.False(discovered.Result.IsDirty);
            Assert.Equal(discovered.Result.HeadRevision, discovered.Result.SourceRevision);
            Assert.True(discovered.Result.Files.Single(file => file.Path == "Alias.cs").IsSymlink);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task DiscoverAsync_WithStandaloneTrackedDirectorySymlink_PreservesCleanHeadRevision()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");
            Directory.CreateDirectory(Path.Combine(root, "real"));
            CreateDirectorySymbolicLinkOrSkip(Path.Combine(root, "linkdir"), "real");
            await RunGitAsync(root, "config", "core.symlinks", "true");
            await RunGitAsync(root, "add", "linkdir");
            await RunGitAsync(root, "commit", "-m", "add standalone directory link");

            DiscoveredWorkspace discovered = await DiscoverAsync(root);

            Assert.False(discovered.Result.IsDirty);
            Assert.Equal(discovered.Result.HeadRevision, discovered.Result.SourceRevision);
            WorkspaceFileDescriptor link = Assert.Single(
                discovered.Result.Files,
                file => file.Path == "linkdir");
            Assert.Equal(WorkspaceFileDisposition.Ignored, link.Disposition);
            Assert.Equal("symlink_directory", link.Reason);
            Assert.True(link.IsSymlink);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task DiscoverAsync_WithRegularFilenameContainingSpace_PreservesCleanHeadRevision()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");
            await File.WriteAllTextAsync(
                Path.Combine(root, "Regular File.cs"),
                "public class Spaced { }");
            await RunGitAsync(root, "add", "Regular File.cs");
            await RunGitAsync(root, "commit", "-m", "add spaced path");

            DiscoveredWorkspace discovered = await DiscoverAsync(root);

            Assert.False(discovered.Result.IsDirty);
            Assert.Equal(discovered.Result.HeadRevision, discovered.Result.SourceRevision);
            Assert.Contains(discovered.Result.Files, file => file.Path == "Regular File.cs");
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task DiscoverAsync_WithLeadingQuoteFilename_PreservesCleanHeadRevision()
    {
        Assert.False(WorkspaceDiscoveryService.CanUseGitStandardInputPath("\"Quoted.cs"));
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");
            const string filename = "\"Quoted.cs";
            await File.WriteAllTextAsync(Path.Combine(root, filename), "public class Quoted { }");
            await RunGitAsync(root, "add", filename);
            await RunGitAsync(root, "commit", "-m", "add quoted path");

            DiscoveredWorkspace discovered = await DiscoverAsync(root);

            Assert.False(discovered.Result.IsDirty);
            Assert.Equal(discovered.Result.HeadRevision, discovered.Result.SourceRevision);
            Assert.Contains(discovered.Result.Files, file => file.Path == filename);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task DiscoverAsync_WithFailingRequiredCleanFilter_DoesNotReportCleanHead()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");
            await File.WriteAllTextAsync(Path.Combine(root, ".gitattributes"), "*.cs filter=unavailable\n");
            await RunGitAsync(root, "add", ".gitattributes");
            await RunGitAsync(root, "commit", "-m", "require external clean filter");
            await RunGitAsync(root, "config", "filter.unavailable.clean", "couplet-filter-that-does-not-exist");
            await RunGitAsync(root, "config", "filter.unavailable.required", "true");

            DiscoveredWorkspace discovered = await DiscoverAsync(root);

            Assert.True(discovered.Result.IsDirty);
            Assert.NotEqual(discovered.Result.HeadRevision, discovered.Result.SourceRevision);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task ReadNullTerminatedAsciiAsync_WithOversizedHeader_ThrowsInvalidDataException()
    {
        byte[] payload = Encoding.ASCII.GetBytes(new string('x', 1025));
        await using var stream = new MemoryStream(payload);
        var reader = new WorkspaceDiscoveryService.GitBatchStreamReader(stream);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadNullTerminatedAsciiAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BuildAsync_WithCleanCrLfFilter_BindsRawSnapshotWithoutDirtyingHead()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".gitattributes"), "*.cs text\n");
            await RunGitAsync(root, "add", ".gitattributes");
            await RunGitAsync(root, "commit", "-m", "normalize C sharp text");
            DiscoveredWorkspace lineFeed = await DiscoverAsync(root);
            WorkspaceIndexSnapshot lineFeedSnapshot = await IndexSnapshotBuilder.BuildAsync(lineFeed, null);

            await RunGitAsync(root, "config", "core.autocrlf", "true");
            await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "public class Initial { }\r\n");
            await RunGitAsync(root, "add", "Sample.cs");

            Assert.Equal(string.Empty, await RunGitCaptureAsync(root, "status", "--porcelain=v1", "--", "."));
            DiscoveredWorkspace crlf = await DiscoverAsync(root);
            WorkspaceIndexSnapshot crlfSnapshot = await IndexSnapshotBuilder.BuildAsync(crlf, null);

            Assert.False(crlf.Result.IsDirty);
            Assert.Equal(lineFeed.Result.HeadRevision, crlf.Result.SourceRevision);
            Assert.NotEqual(
                lineFeed.Result.Files.Single(file => file.Path == "Sample.cs").ContentHash,
                crlf.Result.Files.Single(file => file.Path == "Sample.cs").ContentHash);
            Assert.NotEqual(lineFeedSnapshot.IndexRevision, crlfSnapshot.IndexRevision);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task BuildAsync_WithTrackedSymlinkOutsideWorkspace_ReportsFailureAndDirtyRevision()
    {
        string parent = TemporaryDirectory();
        string root = Path.Combine(parent, "workspace");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(parent, "outside.cs"), "public class Outside { }");
            await InitializeRepositoryAsync(root, "public class Initial { }");
            CreateSymbolicLinkOrSkip(Path.Combine(root, "Outside.cs"), "../outside.cs");
            await RunGitAsync(root, "config", "core.symlinks", "true");
            await RunGitAsync(root, "add", "Outside.cs");
            await RunGitAsync(root, "commit", "-m", "add outside symlink");

            DiscoveredWorkspace discovered = await DiscoverAsync(root);
            WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(discovered, null);

            WorkspaceFileDescriptor descriptor = Assert.Single(
                discovered.Result.Files,
                file => file.Path == "Outside.cs");
            Assert.Equal(WorkspaceFileDisposition.SymlinkOutside, descriptor.Disposition);
            Assert.True(discovered.Result.IsDirty);
            Assert.NotEqual(discovered.Result.HeadRevision, discovered.Result.SourceRevision);
            FileIndexFailure failure = Assert.Single(snapshot.Failures);
            Assert.Equal("Outside.cs", failure.Path);
            Assert.Equal("symlink_outside_workspace", failure.Code);
        }
        finally
        {
            DeleteTemporaryDirectory(parent);
        }
    }

    [Fact]
    public async Task BuildAsync_WithTrackedFileBehindOutsideDirectorySymlink_RejectsBeforeReadingTarget()
    {
        string parent = TemporaryDirectory();
        string root = Path.Combine(parent, "workspace");
        string outside = Path.Combine(parent, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");
            Directory.CreateDirectory(Path.Combine(root, "dir"));
            await File.WriteAllTextAsync(Path.Combine(root, "dir", "File.cs"), "public class Tracked { }");
            await RunGitAsync(root, "add", "dir/File.cs");
            await RunGitAsync(root, "commit", "-m", "add tracked directory file");
            File.Delete(Path.Combine(root, "dir", "File.cs"));
            Directory.Delete(Path.Combine(root, "dir"));
            await File.WriteAllTextAsync(Path.Combine(outside, "File.cs"), "public class ExternalSecret { }");
            CreateDirectorySymbolicLinkOrSkip(Path.Combine(root, "dir"), "../outside");

            DiscoveredWorkspace discovered = await DiscoverAsync(root);
            WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(discovered, null);

            WorkspaceFileDescriptor escaped = Assert.Single(
                discovered.Result.Files,
                file => file.Disposition == WorkspaceFileDisposition.SymlinkOutside);
            Assert.Equal(WorkspaceFileDisposition.SymlinkOutside, escaped.Disposition);
            Assert.True(escaped.IsSymlink);
            FileIndexFailure failure = Assert.Single(snapshot.Failures);
            Assert.Equal(escaped.Path, failure.Path);
            Assert.Equal("symlink_outside_workspace", failure.Code);
            Assert.DoesNotContain(
                snapshot.Files,
                file => file.Path == "dir/File.cs" || file.Chunks.Any(chunk => chunk.Content.Contains("ExternalSecret")));
        }
        finally
        {
            DeleteTemporaryDirectory(parent);
        }
    }

#if COUPLET_SONNETDB_SOURCE_GENERATIONS
    [Fact]
    public async Task IndexStage_WithSnapshotFailure_DoesNotCreateActiveOrStagingGeneration()
    {
        string parent = TemporaryDirectory();
        string workspace = Path.Combine(parent, "workspace");
        string database = Path.Combine(parent, "database");
        Directory.CreateDirectory(workspace);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(parent, "outside.cs"), "public class Outside { }");
            await InitializeRepositoryAsync(workspace, "public class Initial { }");
            CreateSymbolicLinkOrSkip(Path.Combine(workspace, "Outside.cs"), "../outside.cs");
            await RunGitAsync(workspace, "config", "core.symlinks", "true");
            await RunGitAsync(workspace, "add", "Outside.cs");
            await RunGitAsync(workspace, "commit", "-m", "add outside symlink");

            DiscoveredWorkspace discovered = await DiscoverAsync(workspace);
            WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(discovered, null);
            Assert.NotEmpty(snapshot.Failures);

            using var output = new StringWriter();
            using var error = new StringWriter();
            int exitCode = await CoupletRuntime.RunAsync(
                ComponentKind.Cli,
                ["index-stage", "--workspace", workspace, "--database", database],
                output,
                error,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            using JsonDocument document = JsonDocument.Parse(error.ToString());
            Assert.Equal("indexing_failed", document.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                "workspace_snapshot_incomplete",
                document.RootElement.GetProperty("reason").GetString());

            using var store = new SonnetDbIndexGenerationStore(database);
            Assert.Null(store.ReadActivePlanningSnapshot(snapshot.WorkspaceId));
            Assert.Empty(store.ListGenerationRevisionsForTest(snapshot.WorkspaceId));
            Assert.Null(store.ReadStagingManifest(snapshot.WorkspaceId, snapshot.IndexRevision));
        }
        finally
        {
            DeleteTemporaryDirectory(parent);
        }
    }
#endif

    [Fact]
    public async Task BuildAsync_WithUnreadableDiscoveryDescriptor_ReportsStableFailure()
    {
        string root = TemporaryDirectory();
        try
        {
            var descriptor = new WorkspaceFileDescriptor
            {
                Path = "Unreadable.cs",
                Length = 0,
                Disposition = WorkspaceFileDisposition.Unreadable,
                Reason = "file_unreadable",
                Language = "csharp",
                TextOnly = true,
                IsSymlink = false,
            };
            var discovered = new DiscoveredWorkspace(
                new WorkspaceDiscoveryResult
                {
                    WorkspaceId = "workspace",
                    RepositoryIdentity = "repository",
                    WorktreeIdentity = "worktree",
                    HeadRevision = "head",
                    SourceRevision = "head+dirty.unreadable",
                    IsDirty = true,
                    Files = [descriptor],
                },
                root,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "inputs");

            WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(discovered, null);

            FileIndexFailure failure = Assert.Single(snapshot.Failures);
            Assert.Equal("Unreadable.cs", failure.Path);
            Assert.Equal("file_unreadable", failure.Code);
            Assert.Equal("couplet.lexical.csharp", failure.AdapterId);
            Assert.Empty(snapshot.Files);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task BuildAsync_WithSymlinkAliasToDeniedTarget_ExcludesAliasBeforeReadingTarget()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");
            await File.WriteAllTextAsync(Path.Combine(root, ".env"), "TOKEN=not-indexed");
            CreateSymbolicLinkOrSkip(Path.Combine(root, "Safe.cs"), ".env");
            await RunGitAsync(root, "config", "core.symlinks", "true");
            await RunGitAsync(root, "add", ".env", "Safe.cs");
            await RunGitAsync(root, "commit", "-m", "add denied symlink target");

            DiscoveredWorkspace discovered = await DiscoverAsync(root);
            WorkspaceFileDescriptor alias = discovered.Result.Files.Single(file => file.Path == "Safe.cs");
            WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(discovered, null);

            Assert.False(discovered.Result.IsDirty);
            Assert.Equal(discovered.Result.HeadRevision, discovered.Result.SourceRevision);
            Assert.Equal(WorkspaceFileDisposition.Denied, alias.Disposition);
            Assert.Equal("symlink_target_deny_pattern", alias.Reason);
            Assert.True(alias.IsSymlink);
            Assert.Null(alias.ContentHash);
            Assert.DoesNotContain(snapshot.Files, file => file.Path == "Safe.cs");
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task BuildAsync_WithSymlinkAliasToGitIgnoredTarget_ExcludesAliasBeforeReadingTarget()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");
            Directory.CreateDirectory(Path.Combine(root, "secrets"));
            await File.WriteAllTextAsync(Path.Combine(root, "secrets", "local.txt"), "not-indexed");
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "secrets/\n");
            CreateSymbolicLinkOrSkip(Path.Combine(root, "Safe.cs"), "secrets/local.txt");
            await RunGitAsync(root, "config", "core.symlinks", "true");
            await RunGitAsync(root, "add", ".gitignore", "Safe.cs");
            await RunGitAsync(root, "commit", "-m", "add ignored symlink target");

            DiscoveredWorkspace discovered = await DiscoverAsync(root);
            WorkspaceFileDescriptor alias = discovered.Result.Files.Single(file => file.Path == "Safe.cs");
            WorkspaceIndexSnapshot snapshot = await IndexSnapshotBuilder.BuildAsync(discovered, null);

            Assert.False(discovered.Result.IsDirty);
            Assert.Equal(WorkspaceFileDisposition.Ignored, alias.Disposition);
            Assert.Equal("symlink_target_git_ignored", alias.Reason);
            Assert.Null(alias.ContentHash);
            Assert.DoesNotContain(snapshot.Files, file => file.Path == "Safe.cs");
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task BuildAsync_WithIrrelevantPolicyChange_ReusesCleanHeadAndIndexRevision()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");
            DiscoveredWorkspace baseline = await DiscoverAsync(root);
            WorkspaceIndexSnapshot baselineSnapshot = await IndexSnapshotBuilder.BuildAsync(baseline, null);
            WorkspaceDiscoveryPolicy policy = Policy(ignorePatterns: ["*.not-present"]);

            DiscoveredWorkspace configured = await WorkspaceDiscoveryService.DiscoverAsync(root, policy);
            WorkspaceIndexSnapshot configuredSnapshot = await IndexSnapshotBuilder.BuildAsync(configured, null);

            Assert.False(configured.Result.IsDirty);
            Assert.Equal(configured.Result.HeadRevision, configured.Result.SourceRevision);
            Assert.Equal(baselineSnapshot.IndexRevision, configuredSnapshot.IndexRevision);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task BuildAsync_WithPolicyChangingIncludedSet_ChangesSameOrdinalIndexRevision()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");
            DiscoveredWorkspace baseline = await DiscoverAsync(root);
            WorkspaceIndexSnapshot baselineSnapshot = await IndexSnapshotBuilder.BuildAsync(baseline, null);
            WorkspaceDiscoveryPolicy policy = Policy(ignorePatterns: ["Sample.cs"]);

            DiscoveredWorkspace configured = await WorkspaceDiscoveryService.DiscoverAsync(root, policy);
            WorkspaceIndexSnapshot configuredSnapshot = await IndexSnapshotBuilder.BuildAsync(configured, null);

            Assert.False(configured.Result.IsDirty);
            Assert.Equal(baseline.Result.HeadRevision, configured.Result.SourceRevision);
            Assert.Empty(configuredSnapshot.Files);
            Assert.NotEqual(baselineSnapshot.IndexRevision, configuredSnapshot.IndexRevision);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task DiscoverAsync_WithRepositoryScopes_IsolatesSubtreesAndLinkedWorktree()
    {
        string parent = TemporaryDirectory();
        string primary = Path.Combine(parent, "primary");
        string linked = Path.Combine(parent, "linked");
        Directory.CreateDirectory(primary);
        try
        {
            await InitializeRepositoryAsync(primary, "public class Root { }");
            Directory.CreateDirectory(Path.Combine(primary, "src"));
            Directory.CreateDirectory(Path.Combine(primary, "tests"));
            await File.WriteAllTextAsync(Path.Combine(primary, "src", "Source.cs"), "public class Source { }");
            await File.WriteAllTextAsync(Path.Combine(primary, "tests", "SourceTests.cs"), "public class SourceTests { }");
            await RunGitAsync(primary, "add", "src", "tests");
            await RunGitAsync(primary, "commit", "-m", "add scopes");
            await RunGitAsync(primary, "remote", "add", "origin", "https://example.invalid/couplet.git");

            DiscoveredWorkspace root = await DiscoverAsync(primary);
            DiscoveredWorkspace source = await DiscoverAsync(Path.Combine(primary, "src"));
            DiscoveredWorkspace sourceRepeat = await DiscoverAsync(Path.Combine(primary, "src"));
            DiscoveredWorkspace tests = await DiscoverAsync(Path.Combine(primary, "tests"));

            Assert.Equal(
                StableId.CreateWorkspace(root.Result.RepositoryIdentity, root.Result.WorktreeIdentity),
                root.Result.WorkspaceId);
            Assert.NotEqual(root.Result.WorkspaceId, source.Result.WorkspaceId);
            Assert.NotEqual(source.Result.WorkspaceId, tests.Result.WorkspaceId);
            Assert.Equal(source.Result.WorkspaceId, sourceRepeat.Result.WorkspaceId);
            Assert.NotEqual(source.Result.WorktreeIdentity, tests.Result.WorktreeIdentity);
            Assert.Equal(
                StableId.CreateWorkspace(source.Result.RepositoryIdentity, source.Result.WorktreeIdentity),
                source.Result.WorkspaceId);
            Assert.Equal(
                StableId.CreateWorkspace(tests.Result.RepositoryIdentity, tests.Result.WorktreeIdentity),
                tests.Result.WorkspaceId);

            await RunGitAsync(primary, "worktree", "add", "--detach", linked, "HEAD");
            DiscoveredWorkspace linkedSource = await DiscoverAsync(Path.Combine(linked, "src"));

            Assert.Equal(source.Result.RepositoryIdentity, linkedSource.Result.RepositoryIdentity);
            Assert.NotEqual(source.Result.WorktreeIdentity, linkedSource.Result.WorktreeIdentity);
            Assert.NotEqual(source.Result.WorkspaceId, linkedSource.Result.WorkspaceId);
            Assert.Equal(
                StableId.CreateWorkspace(
                    linkedSource.Result.RepositoryIdentity,
                    linkedSource.Result.WorktreeIdentity),
                linkedSource.Result.WorkspaceId);
        }
        finally
        {
            DeleteTemporaryDirectory(parent);
        }
    }

    [Fact]
    public async Task DiscoverAsync_WithPathspecMetacharacterScope_PreservesCleanHeadRevision()
    {
        string root = TemporaryDirectory();
        try
        {
            await InitializeRepositoryAsync(root, "public class Initial { }");
            Directory.CreateDirectory(Path.Combine(root, "a[1]"));
            Directory.CreateDirectory(Path.Combine(root, "a1"));
            await File.WriteAllTextAsync(Path.Combine(root, "a[1]", "Literal.cs"), "public class Literal { }");
            await File.WriteAllTextAsync(Path.Combine(root, "a1", "Wildcard.cs"), "public class Wildcard { }");
            await RunGitAsync(root, "add", "a[1]", "a1");
            await RunGitAsync(root, "commit", "-m", "add literal pathspec scope");

            DiscoveredWorkspace discovered = await DiscoverAsync(Path.Combine(root, "a[1]"));

            Assert.False(discovered.Result.IsDirty);
            Assert.Equal(discovered.Result.HeadRevision, discovered.Result.SourceRevision);
            WorkspaceFileDescriptor file = Assert.Single(discovered.Result.Files);
            Assert.Equal("Literal.cs", file.Path);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static Task<DiscoveredWorkspace> DiscoverAsync(string root) =>
        WorkspaceDiscoveryService.DiscoverAsync(root, WorkspaceDiscoveryService.DefaultPolicy);

    private static WorkspaceDiscoveryPolicy Policy(IReadOnlyList<string> ignorePatterns) => new()
    {
        IgnorePatterns = ignorePatterns,
        DenyPatterns = WorkspaceDiscoveryService.DefaultPolicy.DenyPatterns,
        GeneratedPatterns = WorkspaceDiscoveryService.DefaultPolicy.GeneratedPatterns,
        MaxSemanticFileBytes = WorkspaceDiscoveryService.DefaultPolicy.MaxSemanticFileBytes,
    };

    private static void CreateSymbolicLinkOrSkip(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                            or IOException
                                            or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                $"File symbolic links are unavailable in this test environment: {exception.Message}");
        }
    }

    private static void CreateDirectorySymbolicLinkOrSkip(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                            or IOException
                                            or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                $"Directory symbolic links are unavailable in this test environment: {exception.Message}");
        }
    }

    private static async Task InitializeRepositoryAsync(string root, string content)
    {
        await RunGitAsync(root, "init", "--initial-branch=main");
        await RunGitAsync(root, "config", "user.email", "couplet@example.invalid");
        await RunGitAsync(root, "config", "user.name", "Couplet Tests");
        await RunGitAsync(root, "config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), content);
        await RunGitAsync(root, "add", "Sample.cs");
        await RunGitAsync(root, "commit", "-m", "initial");
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        string output = await RunGitCaptureAsync(workingDirectory, arguments);
        _ = output;
    }

    private static async Task<string> RunGitCaptureAsync(string workingDirectory, params string[] arguments)
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

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return (await output).Trim();
    }

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "couplet-revision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        DeleteDirectoryTree(new DirectoryInfo(path));
    }

    private static void DeleteDirectoryTree(DirectoryInfo root)
    {
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            root.Delete();
            return;
        }

        foreach (FileInfo file in root.EnumerateFiles())
        {
            file.Attributes = FileAttributes.Normal;
            file.Delete();
        }

        foreach (DirectoryInfo directory in root.EnumerateDirectories())
        {
            DeleteDirectoryTree(directory);
        }

        root.Attributes = FileAttributes.Normal;
        root.Delete();
    }
}
