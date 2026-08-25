using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Couplet.Application.Indexing;

internal static class IndexRevisionFactory
{
    private const string _prefix = "cpl_idx_";

    internal static string Create(
        string workspaceId,
        string sourceRevision,
        string? previousIndexRevision,
        IReadOnlyList<string> producerVersions)
    {
        long ordinal = ParseOrdinal(previousIndexRevision) + 1;
        string input = string.Join('\0',
            [workspaceId, sourceRevision, .. producerVersions.Order(StringComparer.Ordinal)]);
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        return $"{_prefix}{ordinal.ToString("D16", CultureInfo.InvariantCulture)}_{digest[..20]}";
    }

    private static long ParseOrdinal(string? revision)
    {
        if (revision is null || !revision.StartsWith(_prefix, StringComparison.Ordinal))
        {
            return 0;
        }

        int separator = revision.IndexOf('_', _prefix.Length);
        return separator > _prefix.Length
            && long.TryParse(
                revision.AsSpan(_prefix.Length, separator - _prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long ordinal)
            ? ordinal
            : 0;
    }
}
