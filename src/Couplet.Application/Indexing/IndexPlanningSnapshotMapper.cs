using Couplet.Core.Indexing;

namespace Couplet.Application.Indexing;

/// <summary>
/// 从完整索引 snapshot 创建 generation 内的轻量规划 snapshot。
/// </summary>
public static class IndexPlanningSnapshotMapper
{
    /// <summary>
    /// 提取文件变化比较所需的稳定元数据。
    /// </summary>
    /// <param name="snapshot">完整索引 snapshot。</param>
    /// <returns>不包含源码正文、符号或 chunk 的规划 snapshot。</returns>
    public static IndexPlanningSnapshot Create(WorkspaceIndexSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new IndexPlanningSnapshot
        {
            WorkspaceId = snapshot.WorkspaceId,
            RepositoryIdentity = snapshot.RepositoryIdentity,
            WorktreeIdentity = snapshot.WorktreeIdentity,
            Branch = snapshot.Branch,
            HeadRevision = snapshot.HeadRevision,
            SourceRevision = snapshot.SourceRevision,
            IndexRevision = snapshot.IndexRevision,
            ProducerVersions = snapshot.ProducerVersions.ToArray(),
            Files = snapshot.Files
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .Select(file => new IndexPlanningFile
                {
                    Path = file.Path,
                    ContentHash = file.ContentHash,
                    SemanticTier = file.SemanticTier,
                    AdapterId = file.AdapterId,
                    AdapterVersion = file.AdapterVersion,
                })
                .ToArray(),
        };
    }
}
