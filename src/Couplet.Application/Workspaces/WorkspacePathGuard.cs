namespace Couplet.Application.Workspaces;

internal static class WorkspacePathGuard
{
    internal static bool IsWithinRoot(string rootPath, string candidate)
    {
        string relative = Path.GetRelativePath(rootPath, candidate);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith("../", StringComparison.Ordinal);
    }
}
