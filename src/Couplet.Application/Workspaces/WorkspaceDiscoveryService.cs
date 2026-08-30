using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Couplet.Core.Graph;
using Couplet.Core.Workspaces;

namespace Couplet.Application.Workspaces;

/// <summary>
/// 包含公开发现合同和仅供索引管线读取的本地文件路径。
/// </summary>
public sealed class DiscoveredWorkspace
{
    internal DiscoveredWorkspace(
        WorkspaceDiscoveryResult result,
        string rootPath,
        IReadOnlyDictionary<string, string> includedPaths,
        string inputsDigest)
    {
        Result = result;
        RootPath = rootPath;
        IncludedPaths = includedPaths;
        InputsDigest = inputsDigest;
    }

    /// <summary>获取不泄露绝对路径的发现结果。</summary>
    public WorkspaceDiscoveryResult Result { get; }

    internal string RootPath { get; }

    internal IReadOnlyDictionary<string, string> IncludedPaths { get; }

    internal string InputsDigest { get; }
}

/// <summary>
/// 按 Git、ignore/deny、symlink 和文件类型策略发现索引输入。
/// </summary>
public static class WorkspaceDiscoveryService
{
    private const int _binaryProbeBytes = 8192;

    /// <summary>
    /// 获取不允许索引凭证、构建产物和生成文件的默认策略。
    /// </summary>
    public static WorkspaceDiscoveryPolicy DefaultPolicy { get; } = new()
    {
        IgnorePatterns = [],
        DenyPatterns =
        [
            "**/.git/**", "**/.couplet/**", "**/bin/**", "**/obj/**", "**/node_modules/**",
            ".env", ".env.*", "*.pem", "*.key", "id_rsa*", "credentials*",
        ],
        GeneratedPatterns =
        [
            "**/*.g.cs", "**/*.generated.cs", "**/*.designer.cs", "**/*.min.js",
            "**/dist/**", "**/coverage/**",
        ],
        MaxSemanticFileBytes = 4 * 1024 * 1024,
    };

    /// <summary>
    /// 发现一个显式工作区目录。
    /// </summary>
    /// <param name="workspacePath">显式工作区目录。</param>
    /// <param name="policy">安全和文件策略。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>确定性发现结果。</returns>
    public static async Task<DiscoveredWorkspace> DiscoverAsync(
        string workspacePath,
        WorkspaceDiscoveryPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MaxSemanticFileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "MaxSemanticFileBytes must be positive.");
        }

        string rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException("The explicitly configured workspace was not found.");
        }

        GitWorkspaceInfo git = await GitWorkspaceInspector.InspectAsync(rootPath, cancellationToken)
            .ConfigureAwait(false);
        string worktreeIdentity = CreateScopedWorktreeIdentity(rootPath, git);
        string workspaceId = StableId.CreateWorkspace(git.RepositoryIdentity, worktreeIdentity);
        IReadOnlyList<string> candidates = git.GitFiles is not null
            ? NormalizeGitCandidates(rootPath, git.GitRoot!, git.GitFiles)
            : EnumerateFiles(rootPath);
        StringComparer fileSystemComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var candidatePaths = new Dictionary<string, string>(fileSystemComparer);
        foreach (string candidate in candidates.Order(StringComparer.Ordinal))
        {
            candidatePaths.TryAdd(candidate, candidate);
        }

        var descriptors = new List<WorkspaceFileDescriptor>(candidates.Count);
        var includedPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string relativePath in candidates.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!WorkspacePathGuard.IsWithinRoot(rootPath, fullPath))
            {
                descriptors.Add(Excluded(relativePath, WorkspaceFileDisposition.SymlinkOutside, "path_outside_workspace"));
                continue;
            }

            if (policy.DenyPatterns.Any(pattern => GlobMatcher.IsMatch(relativePath, pattern)))
            {
                descriptors.Add(Excluded(relativePath, WorkspaceFileDisposition.Denied, "deny_pattern"));
                continue;
            }

            if (policy.IgnorePatterns.Any(pattern => GlobMatcher.IsMatch(relativePath, pattern)))
            {
                descriptors.Add(Excluded(relativePath, WorkspaceFileDisposition.Ignored, "ignore_pattern"));
                continue;
            }

            if (policy.GeneratedPatterns.Any(pattern => GlobMatcher.IsMatch(relativePath, pattern)))
            {
                descriptors.Add(Excluded(relativePath, WorkspaceFileDisposition.Generated, "generated_file"));
                continue;
            }

            bool isSymlink = false;
            try
            {
                bool resolvedPath = TryResolveFileSystemLinkWithinRoot(
                    rootPath,
                    fullPath,
                    out string targetPath,
                    out isSymlink);
                if (!resolvedPath && isSymlink)
                {
                    descriptors.Add(Excluded(
                        relativePath,
                        WorkspaceFileDisposition.SymlinkOutside,
                        "symlink_outside_workspace",
                        isSymlink: true));
                    continue;
                }

                if (isSymlink)
                {
                    if (!resolvedPath)
                    {
                        descriptors.Add(Excluded(
                            relativePath,
                            WorkspaceFileDisposition.SymlinkOutside,
                            "symlink_outside_workspace",
                            isSymlink: true));
                        continue;
                    }

                    string targetRelativePath = Path.GetRelativePath(rootPath, targetPath)
                        .Replace('\\', '/');
                    if (policy.DenyPatterns.Any(pattern => GlobMatcher.IsMatch(targetRelativePath, pattern)))
                    {
                        descriptors.Add(Excluded(
                            relativePath,
                            WorkspaceFileDisposition.Denied,
                            "symlink_target_deny_pattern",
                            isSymlink: true));
                        continue;
                    }

                    if (policy.IgnorePatterns.Any(pattern => GlobMatcher.IsMatch(targetRelativePath, pattern)))
                    {
                        descriptors.Add(Excluded(
                            relativePath,
                            WorkspaceFileDisposition.Ignored,
                            "symlink_target_ignore_pattern",
                            isSymlink: true));
                        continue;
                    }

                    if (policy.GeneratedPatterns.Any(pattern => GlobMatcher.IsMatch(targetRelativePath, pattern)))
                    {
                        descriptors.Add(Excluded(
                            relativePath,
                            WorkspaceFileDisposition.Generated,
                            "symlink_target_generated_pattern",
                            isSymlink: true));
                        continue;
                    }

                    if (Directory.Exists(targetPath))
                    {
                        descriptors.Add(Excluded(
                            relativePath,
                            WorkspaceFileDisposition.Ignored,
                            "symlink_directory",
                            isSymlink: true));
                        continue;
                    }

                    if (!candidatePaths.ContainsKey(targetRelativePath))
                    {
                        descriptors.Add(Excluded(
                            relativePath,
                            WorkspaceFileDisposition.Ignored,
                            "symlink_target_git_ignored",
                            isSymlink: true));
                        continue;
                    }
                }

                string readPath = resolvedPath ? targetPath : fullPath;
                await using FileStream stream = new(
                    readPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                long observedLength = stream.Length;
                if (await IsBinaryFileAsync(stream, observedLength, cancellationToken).ConfigureAwait(false))
                {
                    descriptors.Add(new WorkspaceFileDescriptor
                    {
                        Path = relativePath,
                        Length = observedLength,
                        Disposition = WorkspaceFileDisposition.Binary,
                        Reason = "binary_content",
                        Language = LanguageFromPath(relativePath),
                        TextOnly = true,
                        IsSymlink = isSymlink,
                    });
                    continue;
                }

                stream.Position = 0;
                string contentHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                    .ToLowerInvariant();
                bool textOnly = observedLength > policy.MaxSemanticFileBytes;
                descriptors.Add(new WorkspaceFileDescriptor
                {
                    Path = relativePath,
                    Length = observedLength,
                    ContentHash = contentHash,
                    Disposition = WorkspaceFileDisposition.Included,
                    Reason = textOnly ? "large_file_text_only" : "included",
                    Language = LanguageFromPath(relativePath),
                    TextOnly = textOnly,
                    IsSymlink = isSymlink,
                });
                includedPaths.Add(relativePath, readPath);
            }
            catch (IOException)
            {
                descriptors.Add(Excluded(
                    relativePath,
                    WorkspaceFileDisposition.Unreadable,
                    "file_unreadable",
                    isSymlink));
            }
            catch (UnauthorizedAccessException)
            {
                descriptors.Add(Excluded(
                    relativePath,
                    WorkspaceFileDisposition.Unreadable,
                    "file_access_denied",
                    isSymlink));
            }
        }

        string inputsDigest = ComputeInputsDigest(descriptors);
        bool isDirty = git.IsDirty;
        if (!isDirty && git.HeadRevision is not null && git.GitRoot is not null)
        {
            isDirty |= !await SnapshotInputsMatchHeadAsync(
                    rootPath,
                    git.GitRoot,
                    git.HeadRevision,
                    descriptors,
                    includedPaths,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        string sourceRevision = git.HeadRevision is null
            ? "filesystem:" + inputsDigest
            : isDirty ? git.HeadRevision + "+dirty." + inputsDigest[..20] : git.HeadRevision;
        var result = new WorkspaceDiscoveryResult
        {
            WorkspaceId = workspaceId,
            RepositoryIdentity = git.RepositoryIdentity,
            WorktreeIdentity = worktreeIdentity,
            Branch = git.Branch,
            HeadRevision = git.HeadRevision,
            SourceRevision = sourceRevision,
            IsDirty = isDirty,
            Files = descriptors.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(),
        };
        return new DiscoveredWorkspace(result, rootPath, includedPaths, inputsDigest);
    }

    private static string CreateScopedWorktreeIdentity(string rootPath, GitWorkspaceInfo git)
    {
        if (git.GitRoot is null)
        {
            return git.WorktreeIdentity;
        }

        string scope = Path.GetRelativePath(git.GitRoot, rootPath).Replace('\\', '/').Trim('/').Normalize();
        if (scope.Length == 0 || scope == ".")
        {
            return git.WorktreeIdentity;
        }

        if (OperatingSystem.IsWindows())
        {
            scope = scope.ToLowerInvariant();
        }

        string scopeDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))).ToLowerInvariant();
        return git.WorktreeIdentity + ":scope:" + scopeDigest;
    }

    private static string[] NormalizeGitCandidates(
        string workspaceRoot,
        string gitRoot,
        IReadOnlyList<string> gitFiles)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string value in gitFiles)
        {
            string fullPathFromGit = Path.GetFullPath(Path.Combine(gitRoot, value.Replace('/', Path.DirectorySeparatorChar)));
            string fullPathFromWorkspace = Path.GetFullPath(Path.Combine(workspaceRoot, value.Replace('/', Path.DirectorySeparatorChar)));
            string fullPath = WorkspacePathGuard.IsWithinRoot(workspaceRoot, fullPathFromWorkspace)
                && PathExistsOrIsLink(fullPathFromWorkspace)
                    ? fullPathFromWorkspace
                    : fullPathFromGit;
            if (PathExistsOrIsLink(fullPath) && WorkspacePathGuard.IsWithinRoot(workspaceRoot, fullPath))
            {
                result.Add(Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/'));
            }
        }

        return result.ToArray();
    }

    private static bool PathExistsOrIsLink(string path) =>
        File.Exists(path)
        || (TryGetLinkTarget(path, out string? linkTarget) && linkTarget is not null);

    private static List<string> EnumerateFiles(string rootPath)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                result.Add(Path.GetRelativePath(rootPath, file).Replace('\\', '/'));
            }

            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                var info = new DirectoryInfo(child);
                if (info.LinkTarget is null)
                {
                    pending.Push(child);
                }
            }
        }

        return result;
    }

    private static WorkspaceFileDescriptor Excluded(
        string path,
        WorkspaceFileDisposition disposition,
        string reason,
        bool isSymlink = false) => new()
        {
            Path = path,
            Length = 0,
            Disposition = disposition,
            Reason = reason,
            Language = LanguageFromPath(path),
            TextOnly = true,
            IsSymlink = isSymlink,
        };

    private static async Task<bool> IsBinaryFileAsync(
        FileStream stream,
        long observedLength,
        CancellationToken cancellationToken)
    {
        int probeLimit = Math.Min(
            _binaryProbeBytes,
            checked((int)Math.Min(observedLength, int.MaxValue)));
        if (probeLimit == 0)
        {
            return false;
        }

        byte[] probe = ArrayPool<byte>.Shared.Rent(probeLimit);
        try
        {
            int probeLength = 0;
            while (probeLength < probeLimit)
            {
                int read = await stream.ReadAsync(
                        probe.AsMemory(probeLength, probeLimit - probeLength),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                probeLength += read;
            }

            return IsBinaryProbe(
                probe.AsSpan(0, probeLength),
                reachedEnd: probeLength == observedLength);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(probe, clearArray: true);
        }
    }

    private static bool IsBinaryProbe(ReadOnlySpan<byte> bytes, bool reachedEnd)
    {
        if (bytes.Contains((byte)0))
        {
            return true;
        }

        try
        {
            Decoder decoder = new UTF8Encoding(false, true).GetDecoder();
            _ = decoder.GetCharCount(bytes, flush: reachedEnd);
            return false;
        }
        catch (DecoderFallbackException)
        {
            return true;
        }
    }

    private static bool TryResolveFileSystemLinkWithinRoot(
        string workspaceRoot,
        string linkPath,
        out string targetPath,
        out bool encounteredLink)
    {
        var visited = new HashSet<string>(PathComparer);
        encounteredLink = false;
        string currentPath = linkPath;
        while (true)
        {
            if (!WorkspacePathGuard.IsWithinRoot(workspaceRoot, currentPath))
            {
                targetPath = string.Empty;
                return false;
            }

            string relativePath = Path.GetRelativePath(workspaceRoot, currentPath);
            string[] components = relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            string candidatePath = workspaceRoot;
            bool resolvedLink = false;
            for (int index = 0; index < components.Length; index++)
            {
                candidatePath = Path.Combine(candidatePath, components[index]);
                if (!TryGetLinkTarget(candidatePath, out string? linkTarget))
                {
                    targetPath = string.Empty;
                    return false;
                }

                if (linkTarget is null)
                {
                    continue;
                }

                encounteredLink = true;

                string normalizedCandidate = Path.GetFullPath(candidatePath);
                if (!visited.Add(normalizedCandidate))
                {
                    targetPath = string.Empty;
                    return false;
                }

                currentPath = ResolveLinkPath(normalizedCandidate, linkTarget, components, index);
                resolvedLink = true;
                break;
            }

            if (resolvedLink)
            {
                continue;
            }

            targetPath = currentPath;
            return File.Exists(currentPath) || Directory.Exists(currentPath);
        }
    }

    private static string LanguageFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".ts" or ".tsx" => "typescript",
        ".js" or ".jsx" or ".mjs" or ".cjs" => "javascript",
        ".json" => "json",
        ".md" => "markdown",
        _ => "text",
    };

    private static async Task<bool> SnapshotInputsMatchHeadAsync(
        string workspaceRoot,
        string gitRoot,
        string headRevision,
        IReadOnlyList<WorkspaceFileDescriptor> descriptors,
        Dictionary<string, string> includedPaths,
        CancellationToken cancellationToken)
    {
        string workspaceGitPath = Path.GetRelativePath(gitRoot, workspaceRoot).Replace('\\', '/');
        if (workspaceGitPath == ".."
            || workspaceGitPath.StartsWith("../", StringComparison.Ordinal))
        {
            return false;
        }

        GitCommandResult tree = await RunGitAsync(
                gitRoot,
                ["--literal-pathspecs", "ls-tree", "-r", "-z", "--full-tree", headRevision, "--", workspaceGitPath],
                cancellationToken)
            .ConfigureAwait(false);
        if (tree.ExitCode != 0 || !TryParseHeadBlobs(tree.Output, out Dictionary<string, HeadBlob>? headBlobs))
        {
            return false;
        }

        var observedPaths = new HashSet<string>(PathComparer);
        var contentInputs = new List<GitContentInput>();
        var binaryInputs = new List<GitContentInput>();
        var symlinkInputs = new List<ObservedInput>();
        foreach (WorkspaceFileDescriptor file in descriptors)
        {
            string fullPath = Path.GetFullPath(Path.Combine(
                workspaceRoot,
                file.Path.Replace('/', Path.DirectorySeparatorChar)));
            string gitPath = Path.GetRelativePath(gitRoot, fullPath).Replace('\\', '/');
            observedPaths.Add(gitPath);

            if (file.Disposition is WorkspaceFileDisposition.Unreadable
                or WorkspaceFileDisposition.SymlinkOutside)
            {
                if (headBlobs.ContainsKey(gitPath))
                {
                    return false;
                }

                continue;
            }

            if (file.Disposition == WorkspaceFileDisposition.Included)
            {
                if (!includedPaths.ContainsKey(file.Path)
                    || !headBlobs.TryGetValue(gitPath, out HeadBlob headBlob))
                {
                    return false;
                }

                if (file.IsSymlink)
                {
                    if (!TryResolveIncludedSymlink(
                            workspaceRoot,
                            gitRoot,
                            fullPath,
                            headBlobs,
                            observedPaths,
                            symlinkInputs,
                            out string? targetGitPath)
                        || !headBlobs.TryGetValue(targetGitPath, out HeadBlob targetBlob))
                    {
                        return false;
                    }

                    contentInputs.Add(new GitContentInput(
                        targetGitPath,
                        targetBlob.ObjectId));
                }
                else
                {
                    contentInputs.Add(new GitContentInput(
                        gitPath,
                        headBlob.ObjectId));
                }
            }
            else if (file.Disposition == WorkspaceFileDisposition.Binary)
            {
                if (!headBlobs.TryGetValue(gitPath, out HeadBlob headBlob))
                {
                    return false;
                }

                if (file.IsSymlink)
                {
                    if (!TryResolveIncludedSymlink(
                            workspaceRoot,
                            gitRoot,
                            fullPath,
                            headBlobs,
                            observedPaths,
                            symlinkInputs,
                            out string? targetGitPath)
                        || !headBlobs.TryGetValue(targetGitPath, out HeadBlob targetBlob))
                    {
                        return false;
                    }

                    binaryInputs.Add(new GitContentInput(targetGitPath, targetBlob.ObjectId));
                }
                else
                {
                    binaryInputs.Add(new GitContentInput(gitPath, headBlob.ObjectId));
                }
            }
            else if (file.IsSymlink
                     && !TryObserveDirectSymlink(gitPath, fullPath, headBlobs, symlinkInputs))
            {
                return false;
            }
        }

        if (!observedPaths.SetEquals(headBlobs.Keys))
        {
            return false;
        }

        return await HashGitPathsMatchHeadAsync(
                gitRoot,
                contentInputs,
                cancellationToken)
            .ConfigureAwait(false)
            && await HeadBlobsAreBinaryAsync(
                gitRoot,
                binaryInputs,
                cancellationToken)
            .ConfigureAwait(false)
            && await VerifyGitBatchAsync(
                gitRoot,
                symlinkInputs,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool TryObserveDirectSymlink(
        string gitPath,
        string fullPath,
        IReadOnlyDictionary<string, HeadBlob> headBlobs,
        List<ObservedInput> symlinkInputs)
    {
        if (!headBlobs.TryGetValue(gitPath, out HeadBlob headBlob)
            || !headBlob.IsSymlink
            || !TryGetLinkTarget(fullPath, out string? linkTarget)
            || linkTarget is null)
        {
            return false;
        }

        string linkHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(linkTarget)))
            .ToLowerInvariant();
        symlinkInputs.Add(new ObservedInput(headBlob.ObjectId, linkHash));
        return true;
    }

    private static async Task<bool> HeadBlobsAreBinaryAsync(
        string gitRoot,
        IReadOnlyList<GitContentInput> inputs,
        CancellationToken cancellationToken)
    {
        foreach (GitContentInput input in inputs
                     .Distinct()
                     .OrderBy(input => input.Path, StringComparer.Ordinal))
        {
            if (!await HeadBlobIsBinaryAsync(gitRoot, input, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> HeadBlobIsBinaryAsync(
        string gitRoot,
        GitContentInput input,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = gitRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("cat-file");
        startInfo.ArgumentList.Add("--filters");
        startInfo.ArgumentList.Add("--path=" + input.Path);
        startInfo.ArgumentList.Add(input.HeadObjectId);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return false;
            }
        }
        catch (Win32Exception)
        {
            return false;
        }

        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        byte[] probe = ArrayPool<byte>.Shared.Rent(_binaryProbeBytes);
        try
        {
            int probeLength = 0;
            while (probeLength < _binaryProbeBytes)
            {
                int read = await process.StandardOutput.BaseStream.ReadAsync(
                        probe.AsMemory(probeLength, _binaryProbeBytes - probeLength),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    Task waitForExit = process.WaitForExitAsync(cancellationToken);
                    await ObserveProcessTasksFailFastAsync(error, waitForExit).ConfigureAwait(false);
                    return process.ExitCode == 0
                        && IsBinaryProbe(probe.AsSpan(0, probeLength), reachedEnd: true);
                }

                probeLength += read;
            }

            await TerminateGitProcessAsync(process, error).ConfigureAwait(false);
            return IsBinaryProbe(probe.AsSpan(0, probeLength), reachedEnd: false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            await TerminateGitProcessAsync(process, error).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TerminateGitProcessAsync(process, error).ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(probe, clearArray: true);
        }
    }

    private static bool TryResolveIncludedSymlink(
        string workspaceRoot,
        string gitRoot,
        string symlinkPath,
        IReadOnlyDictionary<string, HeadBlob> headBlobs,
        HashSet<string> observedPaths,
        List<ObservedInput> symlinkInputs,
        out string targetGitPath)
    {
        var visited = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        string currentPath = symlinkPath;
        while (true)
        {
            if (!WorkspacePathGuard.IsWithinRoot(workspaceRoot, currentPath))
            {
                targetGitPath = string.Empty;
                return false;
            }

            string relativePath = Path.GetRelativePath(workspaceRoot, currentPath);
            string[] components = relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            string candidatePath = workspaceRoot;
            bool resolvedLink = false;
            for (int index = 0; index < components.Length; index++)
            {
                candidatePath = Path.Combine(candidatePath, components[index]);
                if (!TryGetLinkTarget(candidatePath, out string? linkTarget))
                {
                    targetGitPath = string.Empty;
                    return false;
                }

                if (linkTarget is null)
                {
                    continue;
                }

                string normalizedCandidate = Path.GetFullPath(candidatePath);
                if (!visited.Add(normalizedCandidate))
                {
                    targetGitPath = string.Empty;
                    return false;
                }

                string gitPath = Path.GetRelativePath(gitRoot, normalizedCandidate).Replace('\\', '/');
                if (!headBlobs.TryGetValue(gitPath, out HeadBlob headBlob) || !headBlob.IsSymlink)
                {
                    targetGitPath = string.Empty;
                    return false;
                }

                observedPaths.Add(gitPath);
                string linkHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(linkTarget)))
                    .ToLowerInvariant();
                symlinkInputs.Add(new ObservedInput(headBlob.ObjectId, linkHash));

                currentPath = ResolveLinkPath(normalizedCandidate, linkTarget, components, index);
                resolvedLink = true;
                break;
            }

            if (resolvedLink)
            {
                continue;
            }

            targetGitPath = Path.GetRelativePath(gitRoot, currentPath).Replace('\\', '/');
            return File.Exists(currentPath);
        }
    }

    private static bool TryGetLinkTarget(string path, out string? linkTarget)
    {
        try
        {
            linkTarget = new FileInfo(path).LinkTarget;
            linkTarget ??= new DirectoryInfo(path).LinkTarget;
            return true;
        }
        catch (IOException)
        {
            linkTarget = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            linkTarget = null;
            return false;
        }
    }

    private static string ResolveLinkPath(
        string linkPath,
        string linkTarget,
        string[] components,
        int componentIndex)
    {
        string targetPath = Path.IsPathRooted(linkTarget)
            ? linkTarget
            : Path.Combine(Path.GetDirectoryName(linkPath)!, linkTarget);
        if (componentIndex + 1 < components.Length)
        {
            targetPath = Path.Combine(targetPath, Path.Combine(components[(componentIndex + 1)..]));
        }

        return Path.GetFullPath(targetPath);
    }

    private static async Task<bool> HashGitPathsMatchHeadAsync(
        string gitRoot,
        IReadOnlyList<GitContentInput> inputs,
        CancellationToken cancellationToken)
    {
        GitContentInput[] standardInputs = inputs
            .Where(input => CanUseGitStandardInputPath(input.Path))
            .OrderBy(input => input.Path, StringComparer.Ordinal)
            .ToArray();
        if (!await HashGitStandardPathsMatchHeadAsync(gitRoot, standardInputs, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        const int commandLengthBudget = 24 * 1024;
        GitContentInput[] unusualInputs = inputs
            .Where(input => !CanUseGitStandardInputPath(input.Path))
            .ToArray();
        foreach (GitContentInput[] batch in BatchGitInputs(unusualInputs, commandLengthBudget))
        {
            var arguments = new List<string>(batch.Length + 3) { "hash-object", "--filters", "--" };
            arguments.AddRange(batch.Select(input => input.Path));
            GitCommandResult hashes = await RunGitAsync(gitRoot, arguments, cancellationToken).ConfigureAwait(false);
            if (hashes.ExitCode != 0)
            {
                return false;
            }

            string[] objectIds = hashes.Output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (objectIds.Length != batch.Length)
            {
                return false;
            }

            for (int index = 0; index < batch.Length; index++)
            {
                if (!string.Equals(batch[index].HeadObjectId, objectIds[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    internal static bool CanUseGitStandardInputPath(string path) =>
        !path.Contains('\r')
        && !path.Contains('\n')
        && !path.StartsWith('"');

    private static async Task<bool> HashGitStandardPathsMatchHeadAsync(
        string gitRoot,
        IReadOnlyList<GitContentInput> inputs,
        CancellationToken cancellationToken)
    {
        if (inputs.Count == 0)
        {
            return true;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = gitRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("hash-object");
        startInfo.ArgumentList.Add("--filters");
        startInfo.ArgumentList.Add("--stdin-paths");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return false;
            }
        }
        catch (Win32Exception)
        {
            return false;
        }

        Task writePaths = WriteGitPathsAsync(process.StandardInput, inputs, cancellationToken);
        Task<bool> verifyHashes = VerifyGitHashesAsync(process.StandardOutput, inputs, cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        Task waitForExit = process.WaitForExitAsync(cancellationToken);
        try
        {
            await ObserveProcessTasksFailFastAsync(writePaths, verifyHashes, error, waitForExit)
                .ConfigureAwait(false);
            return process.ExitCode == 0 && await verifyHashes.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                            or InvalidDataException
                                            or ObjectDisposedException)
        {
            await TerminateGitProcessAsync(process, writePaths, verifyHashes, error, waitForExit)
                .ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TerminateGitProcessAsync(process, writePaths, verifyHashes, error, waitForExit)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WriteGitPathsAsync(
        StreamWriter input,
        IReadOnlyList<GitContentInput> inputs,
        CancellationToken cancellationToken)
    {
        foreach (GitContentInput inputPath in inputs)
        {
            await input.WriteLineAsync(inputPath.Path.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        input.Close();
    }

    private static async Task<bool> VerifyGitHashesAsync(
        StreamReader output,
        IReadOnlyList<GitContentInput> inputs,
        CancellationToken cancellationToken)
    {
        bool matches = true;
        foreach (GitContentInput input in inputs)
        {
            string? objectId = await output.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (objectId is null)
            {
                throw new EndOfStreamException("Git hash output ended before every path was verified.");
            }

            matches &= string.Equals(objectId, input.HeadObjectId, StringComparison.Ordinal);
        }

        if (await output.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InvalidDataException("Git hash output contained an unexpected extra record.");
        }

        return matches;
    }

    private static IEnumerable<GitContentInput[]> BatchGitInputs(
        IReadOnlyList<GitContentInput> inputs,
        int commandLengthBudget)
    {
        var batch = new List<GitContentInput>();
        int length = 0;
        foreach (GitContentInput input in inputs.OrderBy(input => input.Path, StringComparer.Ordinal))
        {
            int argumentLength = checked(input.Path.Length + 3);
            if (batch.Count > 0 && length + argumentLength > commandLengthBudget)
            {
                yield return batch.ToArray();
                batch.Clear();
                length = 0;
            }

            batch.Add(input);
            length = checked(length + argumentLength);
        }

        if (batch.Count > 0)
        {
            yield return batch.ToArray();
        }
    }

    private static async Task<bool> VerifyGitBatchAsync(
        string gitRoot,
        IReadOnlyList<ObservedInput> inputs,
        CancellationToken cancellationToken)
    {
        if (inputs.Count == 0)
        {
            return true;
        }

        ObservedInput[] orderedInputs = inputs
            .OrderBy(input => input.ObjectId, StringComparer.Ordinal)
            .ToArray();
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = gitRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("cat-file");
        startInfo.ArgumentList.Add("--batch");
        startInfo.ArgumentList.Add("-Z");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return false;
            }
        }
        catch (Win32Exception)
        {
            return false;
        }

        Task writeRequests = WriteGitBatchRequestsAsync(
            process.StandardInput.BaseStream,
            orderedInputs,
            cancellationToken);
        Task<bool> verifyOutput = VerifyGitBatchOutputAsync(
            process.StandardOutput.BaseStream,
            orderedInputs,
            cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        Task waitForExit = process.WaitForExitAsync(cancellationToken);
        try
        {
            await ObserveProcessTasksFailFastAsync(writeRequests, verifyOutput, error, waitForExit)
                .ConfigureAwait(false);
            return process.ExitCode == 0 && await verifyOutput.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                            or InvalidDataException
                                            or ObjectDisposedException)
        {
            await TerminateGitProcessAsync(process, writeRequests, verifyOutput, error, waitForExit)
                .ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TerminateGitProcessAsync(process, writeRequests, verifyOutput, error, waitForExit)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ObserveProcessTasksFailFastAsync(params Task[] tasks)
    {
        var pending = new List<Task>(tasks);
        while (pending.Count > 0)
        {
            Task completed = await Task.WhenAny(pending).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            pending.Remove(completed);
        }
    }

    private static async Task WriteGitBatchRequestsAsync(
        Stream input,
        IReadOnlyList<ObservedInput> inputs,
        CancellationToken cancellationToken)
    {
        byte[] terminator = [0];
        foreach (ObservedInput observedInput in inputs)
        {
            byte[] request = Encoding.ASCII.GetBytes(observedInput.ObjectId);
            await input.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            await input.WriteAsync(terminator, cancellationToken).ConfigureAwait(false);
        }

        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        input.Close();
    }

    private static async Task<bool> VerifyGitBatchOutputAsync(
        Stream output,
        IReadOnlyList<ObservedInput> inputs,
        CancellationToken cancellationToken)
    {
        var reader = new GitBatchStreamReader(output);
        bool matches = true;
        foreach (ObservedInput input in inputs)
        {
            string headerValue = await reader.ReadNullTerminatedAsciiAsync(cancellationToken).ConfigureAwait(false);
            string[] header = headerValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (header.Length != 3
                || !string.Equals(header[1], "blob", StringComparison.Ordinal)
                || !int.TryParse(
                    header[2],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int length)
                || length < 0)
            {
                throw new InvalidDataException("Git batch output contained an invalid blob header.");
            }

            string contentHash = await reader.ReadSha256Async(length, cancellationToken).ConfigureAwait(false);
            matches &= string.Equals(contentHash, input.ContentHash, StringComparison.Ordinal);
        }

        await reader.RequireEndAsync(cancellationToken).ConfigureAwait(false);
        return matches;
    }

    private static async Task TerminateGitProcessAsync(Process process, params Task[] tasks)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }

        foreach (Task task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException
                                                or InvalidDataException
                                                or InvalidOperationException
                                                or Win32Exception
                                                or OperationCanceledException
                                                or ObjectDisposedException)
            {
            }
        }
    }

    private static bool TryParseHeadBlobs(
        string output,
        out Dictionary<string, HeadBlob> blobs)
    {
        blobs = new Dictionary<string, HeadBlob>(PathComparer);
        foreach (string entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int pathSeparator = entry.IndexOf('\t');
            if (pathSeparator <= 0)
            {
                return false;
            }

            string[] metadata = entry[..pathSeparator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length != 3)
            {
                return false;
            }

            if (metadata[1] != "blob")
            {
                continue;
            }

            if (!blobs.TryAdd(
                    entry[(pathSeparator + 1)..],
                    new HeadBlob(metadata[2], metadata[0] == "120000")))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
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

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new GitCommandResult(-1, string.Empty);
            }
        }
        catch (Win32Exception)
        {
            return new GitCommandResult(-1, string.Empty);
        }

        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return new GitCommandResult(process.ExitCode, await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static string ComputeInputsDigest(IReadOnlyList<WorkspaceFileDescriptor> descriptors)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] utf8Buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            AppendDigestValue(hash, "couplet.workspace-inputs.v2", utf8Buffer);
            foreach (WorkspaceFileDescriptor file in descriptors
                         .Where(file => file.Disposition == WorkspaceFileDisposition.Included)
                         .OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                AppendDigestValue(hash, file.Path, utf8Buffer);
                AppendDigestValue(
                    hash,
                    file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    utf8Buffer);
                AppendDigestValue(hash, file.ContentHash, utf8Buffer);
                AppendDigestValue(hash, file.Language, utf8Buffer);
                AppendDigestValue(hash, file.TextOnly ? "1" : "0", utf8Buffer);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(utf8Buffer, clearArray: true);
        }
    }

    private static void AppendDigestValue(
        IncrementalHash hash,
        string? value,
        byte[] utf8Buffer)
    {
        value ??= string.Empty;
        Span<char> lengthCharacters = stackalloc char[11];
        if (!value.Length.TryFormat(
                lengthCharacters,
                out int lengthCharacterCount,
                provider: System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("Unable to frame a workspace digest value.");
        }

        Span<byte> prefix = stackalloc byte[12];
        int prefixLength = Encoding.UTF8.GetBytes(lengthCharacters[..lengthCharacterCount], prefix);
        prefix[prefixLength++] = (byte)':';
        hash.AppendData(prefix[..prefixLength]);

        Encoder encoder = Encoding.UTF8.GetEncoder();
        ReadOnlySpan<char> remaining = value.AsSpan();
        bool completed;
        do
        {
            encoder.Convert(
                remaining,
                utf8Buffer,
                flush: true,
                out int charactersUsed,
                out int bytesUsed,
                out completed);
            hash.AppendData(utf8Buffer.AsSpan(0, bytesUsed));
            remaining = remaining[charactersUsed..];
        }
        while (!completed);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    internal sealed class GitBatchStreamReader(Stream stream)
    {
        private const int _bufferSize = 64 * 1024;
        private const int _maximumHeaderBytes = 1024;
        private readonly byte[] _buffer = new byte[_bufferSize];
        private int _count;
        private int _offset;

        internal async Task<string> ReadNullTerminatedAsciiAsync(CancellationToken cancellationToken)
        {
            var writer = new ArrayBufferWriter<byte>();
            while (true)
            {
                int value = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (value < 0)
                {
                    throw new EndOfStreamException("Git batch output ended before the blob header.");
                }

                if (value == 0)
                {
                    return Encoding.ASCII.GetString(writer.WrittenSpan);
                }

                if (writer.WrittenCount >= _maximumHeaderBytes)
                {
                    throw new InvalidDataException("Git batch output contained an oversized blob header.");
                }

                Span<byte> destination = writer.GetSpan(1);
                destination[0] = (byte)value;
                writer.Advance(1);
            }
        }

        internal async Task<string> ReadSha256Async(int length, CancellationToken cancellationToken)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int remaining = length;
            while (remaining > 0)
            {
                if (_offset == _count)
                {
                    await FillAsync(cancellationToken).ConfigureAwait(false);
                    if (_count == 0)
                    {
                        throw new EndOfStreamException("Git batch output ended inside blob content.");
                    }
                }

                int take = Math.Min(remaining, _count - _offset);
                hash.AppendData(_buffer.AsSpan(_offset, take));
                _offset += take;
                remaining -= take;
            }

            if (await ReadByteAsync(cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new InvalidDataException("Git batch blob content was not NUL terminated.");
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        internal async Task RequireEndAsync(CancellationToken cancellationToken)
        {
            if (await ReadByteAsync(cancellationToken).ConfigureAwait(false) >= 0)
            {
                throw new InvalidDataException("Git batch output contained an unexpected extra record.");
            }
        }

        private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (_offset == _count)
            {
                await FillAsync(cancellationToken).ConfigureAwait(false);
                if (_count == 0)
                {
                    return -1;
                }
            }

            return _buffer[_offset++];
        }

        private async Task FillAsync(CancellationToken cancellationToken)
        {
            _offset = 0;
            _count = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    private readonly record struct HeadBlob(string ObjectId, bool IsSymlink);

    private readonly record struct GitContentInput(string Path, string HeadObjectId);

    private readonly record struct ObservedInput(string ObjectId, string ContentHash);

    private readonly record struct GitCommandResult(int ExitCode, string Output);
}
