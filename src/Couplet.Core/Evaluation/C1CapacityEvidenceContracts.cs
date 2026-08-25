using Couplet.Core.Contracts;
using Couplet.Core.Indexing;

namespace Couplet.Core.Evaluation;

/// <summary>
/// C1 容量语料的确定性生成结果。
/// </summary>
public sealed class C1CorpusGenerationReport
{
    /// <summary>获取语料档位。</summary>
    public required string Scale { get; init; }
    /// <summary>获取生成器版本。</summary>
    public required string GeneratorVersion { get; init; }
    /// <summary>获取生成文件数。</summary>
    public required long Files { get; init; }
    /// <summary>获取生成代码行数。</summary>
    public required long LinesOfCode { get; init; }
    /// <summary>获取生成的声明符号数。</summary>
    public required long DeclaredSymbols { get; init; }
    /// <summary>获取按相对路径与内容计算的 SHA-256。</summary>
    public required string CorpusHash { get; init; }
}

/// <summary>
/// 一项 C1 容量观测的延迟与资源统计。
/// </summary>
public sealed class C1CapacityMetric
{
    /// <summary>获取稳定指标名。</summary>
    public required string Name { get; init; }
    /// <summary>获取冷或 warm 样本分类。</summary>
    public required string Temperature { get; init; }
    /// <summary>获取样本数。</summary>
    public required int Samples { get; init; }
    /// <summary>获取 P50 毫秒数。</summary>
    public required double P50Milliseconds { get; init; }
    /// <summary>获取 P95 毫秒数。</summary>
    public required double P95Milliseconds { get; init; }
    /// <summary>获取 P99 毫秒数。</summary>
    public required double P99Milliseconds { get; init; }
    /// <summary>获取每秒处理项数；不适用时为空。</summary>
    public double? ThroughputPerSecond { get; init; }
    /// <summary>获取观测期间的托管分配字节数。</summary>
    public required long AllocatedBytes { get; init; }
    /// <summary>获取观测到的进程 peak working set。</summary>
    public required long PeakWorkingSetBytes { get; init; }
    /// <summary>获取观测期间数据库目录增长字节数。</summary>
    public required long StorageGrowthBytes { get; init; }
    /// <summary>获取实际访问路径或阶段路径。</summary>
    public required string AccessPath { get; init; }
    /// <summary>获取候选数；固定包不暴露时为空。</summary>
    public long? Candidates { get; init; }
    /// <summary>获取检查数；固定包不暴露时为空。</summary>
    public long? Examined { get; init; }
    /// <summary>获取返回数；不适用时为空。</summary>
    public long? Returned { get; init; }
}

/// <summary>
/// C1 容量取证使用的硬件、运行时和环境身份。
/// </summary>
public sealed class C1CapacityEnvironment
{
    /// <summary>获取 CPU 身份。</summary>
    public required string Cpu { get; init; }
    /// <summary>获取物理核心数；无法确定时为空。</summary>
    public int? PhysicalCores { get; init; }
    /// <summary>获取逻辑核心数。</summary>
    public required int LogicalCores { get; init; }
    /// <summary>获取 GC 可用内存字节数。</summary>
    public required long AvailableMemoryBytes { get; init; }
    /// <summary>获取操作系统描述。</summary>
    public required string OperatingSystem { get; init; }
    /// <summary>获取 .NET runtime 描述。</summary>
    public required string Runtime { get; init; }
    /// <summary>获取 JIT 或 Native AOT 执行模式。</summary>
    public required string ExecutionMode { get; init; }
    /// <summary>获取文件系统。</summary>
    public required string FileSystem { get; init; }
    /// <summary>获取存储设备身份。</summary>
    public required string Storage { get; init; }
    /// <summary>获取电源配置。</summary>
    public required string PowerProfile { get; init; }
    /// <summary>获取后台负载说明。</summary>
    public required string BackgroundLoad { get; init; }
}

/// <summary>
/// C1 Medium 或 Large staging 容量证据报告。
/// </summary>
public sealed class C1CapacityEvidenceReport
{
    /// <summary>获取报告合同版本。</summary>
    public string SchemaVersion { get; init; } = ContractVersions.C1CapacityEvidence;
    /// <summary>获取 Couplet commit 或 working_tree 身份。</summary>
    public required string Commit { get; init; }
    /// <summary>获取固定语料 manifest SHA-256。</summary>
    public required string CorpusManifestHash { get; init; }
    /// <summary>获取语料生成结果。</summary>
    public required C1CorpusGenerationReport Corpus { get; init; }
    /// <summary>获取硬件与运行环境。</summary>
    public required C1CapacityEnvironment Environment { get; init; }
    /// <summary>获取首次 staging 的记录计数。</summary>
    public required GenerationCounts InitialCounts { get; init; }
    /// <summary>获取 100 文件变化后的 staging 记录计数。</summary>
    public required GenerationCounts IncrementalCounts { get; init; }
    /// <summary>获取实际修改文件数。</summary>
    public required int ModifiedFiles { get; init; }
    /// <summary>获取数据库最终字节数。</summary>
    public required long DatabaseBytes { get; init; }
    /// <summary>获取是否存在公开 active generation。</summary>
    public required bool Published { get; init; }
    /// <summary>获取 Correctness/Recovery gate 结果。</summary>
    public required bool CorrectnessRecoveryPassed { get; init; }
    /// <summary>获取 Performance/Capacity gate 结果。</summary>
    public required bool PerformanceCapacityPassed { get; init; }
    /// <summary>获取各阶段与查询观测。</summary>
    public required IReadOnlyList<C1CapacityMetric> Metrics { get; init; }
    /// <summary>获取稳定排序的失败、缺失证据和能力缺口。</summary>
    public required IReadOnlyList<string> Problems { get; init; }
    /// <summary>获取报告生成时间。</summary>
    public required DateTimeOffset GeneratedAtUtc { get; init; }
}
