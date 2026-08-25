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
        IReadOnlyDictionary<string, string> includedPaths)
    {
        Result = result;
        RootPath = rootPath;
        IncludedPaths = includedPaths;
    }

    /// <summary>获取不泄露绝对路径的发现结果。</summary>
    public WorkspaceDiscoveryResult Result { get; }

    internal string RootPath { get; }

    internal IReadOnlyDictionary<string, string> IncludedPaths { get; }
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
        string workspaceId = StableId.CreateWorkspace(git.RepositoryIdentity, git.WorktreeIdentity);
        IReadOnlyList<string> candidates = git.GitFiles is not null
            ? NormalizeGitCandidates(rootPath, git.GitRoot!, git.GitFiles)
            : EnumerateFiles(rootPath);

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

            try
            {
                var file = new FileInfo(fullPath);
                bool isSymlink = file.LinkTarget is not null;
                if (isSymlink)
                {
                    FileSystemInfo? target = file.ResolveLinkTarget(returnFinalTarget: true);
                    if (target is null || !WorkspacePathGuard.IsWithinRoot(rootPath, Path.GetFullPath(target.FullName)))
                    {
                        descriptors.Add(Excluded(relativePath, WorkspaceFileDisposition.SymlinkOutside, "symlink_outside_workspace"));
                        continue;
                    }
                }

                await using FileStream stream = new(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] probe = new byte[Math.Min(_binaryProbeBytes, checked((int)Math.Min(stream.Length, int.MaxValue)))];
                int probeLength = await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false);
                if (IsBinary(probe.AsSpan(0, probeLength)))
                {
                    descriptors.Add(new WorkspaceFileDescriptor
                    {
                        Path = relativePath,
                        Length = stream.Length,
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
                bool textOnly = stream.Length > policy.MaxSemanticFileBytes;
                descriptors.Add(new WorkspaceFileDescriptor
                {
                    Path = relativePath,
                    Length = stream.Length,
                    ContentHash = contentHash,
                    Disposition = WorkspaceFileDisposition.Included,
                    Reason = textOnly ? "large_file_text_only" : "included",
                    Language = LanguageFromPath(relativePath),
                    TextOnly = textOnly,
                    IsSymlink = isSymlink,
                });
                includedPaths.Add(relativePath, fullPath);
            }
            catch (IOException)
            {
                descriptors.Add(Excluded(relativePath, WorkspaceFileDisposition.Unreadable, "file_unreadable"));
            }
            catch (UnauthorizedAccessException)
            {
                descriptors.Add(Excluded(relativePath, WorkspaceFileDisposition.Unreadable, "file_access_denied"));
            }
        }

        string inputsDigest = ComputeInputsDigest(descriptors, git.StatusDigest, policy);
        string sourceRevision = git.HeadRevision is null
            ? "filesystem:" + inputsDigest
            : git.IsDirty ? git.HeadRevision + "+dirty." + inputsDigest[..20] : git.HeadRevision;
        var result = new WorkspaceDiscoveryResult
        {
            WorkspaceId = workspaceId,
            RepositoryIdentity = git.RepositoryIdentity,
            WorktreeIdentity = git.WorktreeIdentity,
            Branch = git.Branch,
            HeadRevision = git.HeadRevision,
            SourceRevision = sourceRevision,
            IsDirty = git.IsDirty,
            Files = descriptors.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(),
        };
        return new DiscoveredWorkspace(result, rootPath, includedPaths);
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
                && File.Exists(fullPathFromWorkspace)
                    ? fullPathFromWorkspace
                    : fullPathFromGit;
            if (File.Exists(fullPath) && WorkspacePathGuard.IsWithinRoot(workspaceRoot, fullPath))
            {
                result.Add(Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/'));
            }
        }

        return result.ToArray();
    }

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
        string reason) => new()
        {
            Path = path,
            Length = 0,
            Disposition = disposition,
            Reason = reason,
            Language = LanguageFromPath(path),
            TextOnly = true,
            IsSymlink = false,
        };

    private static bool IsBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Contains((byte)0))
        {
            return true;
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            return false;
        }
        catch (DecoderFallbackException)
        {
            return true;
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

    private static string ComputeInputsDigest(
        IReadOnlyList<WorkspaceFileDescriptor> descriptors,
        string statusDigest,
        WorkspaceDiscoveryPolicy policy)
    {
        var builder = new StringBuilder(statusDigest);
        foreach (WorkspaceFileDescriptor file in descriptors)
        {
            builder.Append('\n').Append(file.Path).Append('\0').Append(file.ContentHash).Append('\0').Append(file.Reason);
        }

        foreach (string pattern in policy.IgnorePatterns.Concat(policy.DenyPatterns).Concat(policy.GeneratedPatterns))
        {
            builder.Append('\n').Append(pattern);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
