using Couplet.Core.Indexing;
using Couplet.Core.Languages;

namespace Couplet.Application.Indexing;

/// <summary>
/// 比较两个完整 snapshots 并产生确定性的增量变化计划。
/// </summary>
public static class IncrementalIndexPlanner
{
    /// <summary>
    /// 计算 added、modified、deleted、renamed 和 unchanged 文件。
    /// </summary>
    /// <param name="previous">前一 snapshot；首次构建时为空。</param>
    /// <param name="current">目标 snapshot。</param>
    /// <returns>稳定排序的变化计划。</returns>
    public static IncrementalIndexPlan Plan(WorkspaceIndexSnapshot? previous, WorkspaceIndexSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is not null
            && !string.Equals(previous.WorkspaceId, current.WorkspaceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Snapshots must belong to the same workspace.", nameof(previous));
        }

        bool producerChanged = previous is not null
            && !previous.ProducerVersions.SequenceEqual(current.ProducerVersions, StringComparer.Ordinal);
        if (previous is null || producerChanged)
        {
            return new IncrementalIndexPlan
            {
                WorkspaceId = current.WorkspaceId,
                SourceRevision = current.SourceRevision,
                IndexRevision = current.IndexRevision,
                PreviousIndexRevision = previous?.IndexRevision,
                Changes = current.Files.Select(file => Change(IndexFileChangeKind.Added, file.Path, null, file.ContentHash)).ToArray(),
                RebuildRequired = producerChanged,
                RebuildReason = producerChanged ? "producer_version_changed" : null,
            };
        }

        Dictionary<string, IndexedFile> oldByPath = previous.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        Dictionary<string, IndexedFile> newByPath = current.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var changes = new List<IndexFileChange>();
        var added = newByPath.Keys.Except(oldByPath.Keys, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var deleted = oldByPath.Keys.Except(newByPath.Keys, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

        Dictionary<string, string[]> addedByHash = added
            .GroupBy(path => newByPath[path].ContentHash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        foreach (string oldPath in deleted.Order(StringComparer.Ordinal).ToArray())
        {
            string hash = oldByPath[oldPath].ContentHash;
            string[] oldMatches = deleted.Where(path => oldByPath[path].ContentHash == hash).ToArray();
            if (oldMatches.Length == 1
                && addedByHash.TryGetValue(hash, out string[]? newMatches)
                && newMatches.Length == 1)
            {
                string newPath = newMatches[0];
                changes.Add(Change(IndexFileChangeKind.Renamed, newPath, oldPath, hash));
                deleted.Remove(oldPath);
                added.Remove(newPath);
            }
        }

        foreach (string path in oldByPath.Keys.Intersect(newByPath.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            IndexedFile oldFile = oldByPath[path];
            IndexedFile newFile = newByPath[path];
            bool unchanged = oldFile.ContentHash == newFile.ContentHash
                && oldFile.AdapterId == newFile.AdapterId
                && oldFile.AdapterVersion == newFile.AdapterVersion
                && oldFile.SemanticTier == newFile.SemanticTier;
            changes.Add(Change(
                unchanged ? IndexFileChangeKind.Unchanged : IndexFileChangeKind.Modified,
                path,
                path,
                newFile.ContentHash));
        }

        changes.AddRange(added.Order(StringComparer.Ordinal)
            .Select(path => Change(IndexFileChangeKind.Added, path, null, newByPath[path].ContentHash)));
        changes.AddRange(deleted.Order(StringComparer.Ordinal)
            .Select(path => Change(IndexFileChangeKind.Deleted, null, path, null)));

        return new IncrementalIndexPlan
        {
            WorkspaceId = current.WorkspaceId,
            SourceRevision = current.SourceRevision,
            IndexRevision = current.IndexRevision,
            PreviousIndexRevision = previous.IndexRevision,
            Changes = changes
                .OrderBy(change => change.Path ?? change.PreviousPath, StringComparer.Ordinal)
                .ThenBy(change => change.Kind)
                .ToArray(),
            RebuildRequired = false,
        };
    }

    private static IndexFileChange Change(
        IndexFileChangeKind kind,
        string? path,
        string? previousPath,
        string? contentHash) => new()
        {
            Kind = kind,
            Path = path,
            PreviousPath = previousPath,
            ContentHash = contentHash,
        };
}
