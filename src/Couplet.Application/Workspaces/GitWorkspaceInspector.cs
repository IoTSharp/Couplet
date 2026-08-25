using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Couplet.Application.Workspaces;

internal sealed class GitWorkspaceInfo
{
    internal required bool IsGit { get; init; }
    internal string? GitRoot { get; init; }
    internal string? HeadRevision { get; init; }
    internal string? Branch { get; init; }
    internal required string RepositoryIdentity { get; init; }
    internal required string WorktreeIdentity { get; init; }
    internal required bool IsDirty { get; init; }
    internal required string StatusDigest { get; init; }
    internal IReadOnlyList<string>? GitFiles { get; init; }
}

internal static class GitWorkspaceInspector
{
    internal static async Task<GitWorkspaceInfo> InspectAsync(string rootPath, CancellationToken cancellationToken)
    {
        try
        {
            CommandResult root = await RunAsync(rootPath, ["rev-parse", "--show-toplevel"], cancellationToken)
                .ConfigureAwait(false);
            if (root.ExitCode != 0 || string.IsNullOrWhiteSpace(root.Output))
            {
                return NonGit(rootPath);
            }

            string gitRoot = Path.GetFullPath(root.Output.Trim());
            CommandResult head = await RunAsync(rootPath, ["rev-parse", "--verify", "HEAD"], cancellationToken)
                .ConfigureAwait(false);
            CommandResult branch = await RunAsync(rootPath, ["branch", "--show-current"], cancellationToken)
                .ConfigureAwait(false);
            CommandResult remote = await RunAsync(rootPath, ["config", "--get", "remote.origin.url"], cancellationToken)
                .ConfigureAwait(false);
            CommandResult commonDirectory = await RunAsync(rootPath, ["rev-parse", "--git-common-dir"], cancellationToken)
                .ConfigureAwait(false);
            CommandResult gitDirectory = await RunAsync(rootPath, ["rev-parse", "--git-dir"], cancellationToken)
                .ConfigureAwait(false);
            CommandResult status = await RunAsync(
                rootPath,
                ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--", "."],
                cancellationToken).ConfigureAwait(false);
            CommandResult files = await RunAsync(
                rootPath,
                ["ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", "."],
                cancellationToken).ConfigureAwait(false);

            string repositoryIdentity = NormalizeRepositoryIdentity(
                remote.ExitCode == 0 ? remote.Output.Trim() : string.Empty,
                ResolveGitPath(gitRoot, commonDirectory.Output.Trim()));
            string worktreeIdentity = "worktree:" + Hash(ResolveGitPath(gitRoot, gitDirectory.Output.Trim()));

            return new GitWorkspaceInfo
            {
                IsGit = true,
                GitRoot = gitRoot,
                HeadRevision = head.ExitCode == 0 ? head.Output.Trim() : null,
                Branch = branch.ExitCode == 0 && !string.IsNullOrWhiteSpace(branch.Output) ? branch.Output.Trim() : null,
                RepositoryIdentity = repositoryIdentity,
                WorktreeIdentity = worktreeIdentity,
                IsDirty = status.ExitCode == 0 && status.Output.Length > 0,
                StatusDigest = Hash(status.Output),
                GitFiles = files.ExitCode == 0
                    ? files.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    : null,
            };
        }
        catch (Win32Exception)
        {
            return NonGit(rootPath);
        }
    }

    private static GitWorkspaceInfo NonGit(string rootPath)
    {
        string identity = "local:" + Hash(Path.GetFullPath(rootPath).Replace('\\', '/'));
        return new GitWorkspaceInfo
        {
            IsGit = false,
            RepositoryIdentity = identity,
            WorktreeIdentity = "primary",
            IsDirty = true,
            StatusDigest = Hash(string.Empty),
        };
    }

    private static string NormalizeRepositoryIdentity(string remote, string commonDirectory)
    {
        if (string.IsNullOrWhiteSpace(remote))
        {
            return "local:" + Hash(commonDirectory.Replace('\\', '/'));
        }

        if (Uri.TryCreate(remote, UriKind.Absolute, out Uri? uri) && !uri.IsFile)
        {
            var builder = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Host = uri.Host.ToLowerInvariant(),
                Query = string.Empty,
                Fragment = string.Empty,
            };
            return builder.Uri.AbsoluteUri.TrimEnd('/').TrimEndSuffix(".git");
        }

        int separator = remote.IndexOf(':', StringComparison.Ordinal);
        if (separator > 0 && !Path.IsPathRooted(remote))
        {
            string host = remote[..separator];
            int userSeparator = host.LastIndexOf('@');
            if (userSeparator >= 0)
            {
                host = host[(userSeparator + 1)..];
            }

            return (host.ToLowerInvariant() + "/" + remote[(separator + 1)..])
                .Replace('\\', '/')
                .TrimEnd('/')
                .TrimEndSuffix(".git");
        }

        return "remote:" + Hash(remote);
    }

    private static string ResolveGitPath(string gitRoot, string value) =>
        Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(gitRoot, value));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Normalize()))).ToLowerInvariant();

    private static async Task<CommandResult> RunAsync(
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
        if (!process.Start())
        {
            return new CommandResult(-1, string.Empty);
        }

        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return new CommandResult(process.ExitCode, await output.ConfigureAwait(false));
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

    private readonly record struct CommandResult(int ExitCode, string Output);

    private static string TrimEndSuffix(this string value, string suffix) =>
        value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? value[..^suffix.Length] : value;
}
