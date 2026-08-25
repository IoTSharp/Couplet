namespace Couplet.Application.Workspaces;

internal static class GlobMatcher
{
    internal static bool IsMatch(string path, string pattern)
    {
        string normalizedPath = path.Replace('\\', '/').TrimStart('/');
        string normalizedPattern = pattern.Replace('\\', '/').Trim();
        if (normalizedPattern.Length == 0 || normalizedPattern[0] == '!')
        {
            return false;
        }

        normalizedPattern = normalizedPattern.TrimStart('/');
        if (normalizedPattern.EndsWith('/'))
        {
            normalizedPattern += "**";
        }

        if (!normalizedPattern.Contains('/'))
        {
            return normalizedPath.Split('/').Any(segment => MatchCore(segment, normalizedPattern));
        }

        return MatchCore(normalizedPath, normalizedPattern);
    }

    private static bool MatchCore(string path, string pattern)
    {
        var memo = new Dictionary<(int Path, int Pattern), bool>();
        return Match(0, 0);

        bool Match(int pathIndex, int patternIndex)
        {
            if (memo.TryGetValue((pathIndex, patternIndex), out bool cached))
            {
                return cached;
            }

            bool result;
            if (patternIndex == pattern.Length)
            {
                result = pathIndex == path.Length;
            }
            else if (pattern[patternIndex] == '*')
            {
                bool recursive = patternIndex + 1 < pattern.Length && pattern[patternIndex + 1] == '*';
                int nextPattern = patternIndex + (recursive ? 2 : 1);
                bool zeroDirectories = recursive
                    && nextPattern < pattern.Length
                    && pattern[nextPattern] == '/'
                    && Match(pathIndex, nextPattern + 1);
                result = zeroDirectories
                    || Match(pathIndex, nextPattern)
                    || (pathIndex < path.Length
                        && (recursive || path[pathIndex] != '/')
                        && Match(pathIndex + 1, patternIndex));
            }
            else if (pattern[patternIndex] == '?')
            {
                result = pathIndex < path.Length
                    && path[pathIndex] != '/'
                    && Match(pathIndex + 1, patternIndex + 1);
            }
            else
            {
                result = pathIndex < path.Length
                    && char.ToUpperInvariant(path[pathIndex]) == char.ToUpperInvariant(pattern[patternIndex])
                    && Match(pathIndex + 1, patternIndex + 1);
            }

            memo[(pathIndex, patternIndex)] = result;
            return result;
        }
    }
}
