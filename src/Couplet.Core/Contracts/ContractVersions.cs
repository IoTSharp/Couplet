namespace Couplet.Core.Contracts;

/// <summary>
/// Couplet C0 冻结的公共合同版本。
/// </summary>
public static class ContractVersions
{
    /// <summary>
    /// 获取代码图 schema 版本。
    /// </summary>
    public const string CodeGraph = "couplet.code_graph.v1";

    /// <summary>
    /// 获取 generation 发布合同版本。
    /// </summary>
    public const string Generation = "couplet.generation.v1";

    /// <summary>
    /// 获取 SonnetDB capability handshake 版本。
    /// </summary>
    public const string CapabilityHandshake = "couplet.sonnetdb_handshake.v1";

    /// <summary>
    /// 获取 MCP 工具合同版本。
    /// </summary>
    public const string Mcp = "couplet.mcp.v1";

    /// <summary>
    /// 获取安全策略合同版本。
    /// </summary>
    public const string Security = "couplet.security.v1";

    /// <summary>
    /// 获取 fixture manifest 合同版本。
    /// </summary>
    public const string FixtureManifest = "couplet.fixture_manifest.v1";

    /// <summary>
    /// 获取 golden answer 合同版本。
    /// </summary>
    public const string GoldenAnswers = "couplet.golden_answers.v1";

    /// <summary>
    /// 获取 paired Agent eval 合同版本。
    /// </summary>
    public const string AgentEval = "couplet.agent_eval.v1";

    /// <summary>
    /// 获取 C0 evidence 报告合同版本。
    /// </summary>
    public const string C0Evidence = "couplet.c0_evidence.v1";

    /// <summary>
    /// 获取 C1 workspace discovery 合同版本。
    /// </summary>
    public const string WorkspaceDiscovery = "couplet.workspace_discovery.v1";

    /// <summary>
    /// 获取 C1 增量索引机器合同版本。
    /// </summary>
    public const string Indexing = "couplet.indexing.v1";

    /// <summary>
    /// 获取 C1 generation 内增量规划 snapshot 合同版本。
    /// </summary>
    public const string IndexPlanningSnapshot = "couplet.index_planning_snapshot.v1";

    /// <summary>
    /// 获取 C1 index staging 报告合同版本。
    /// </summary>
    public const string IndexStage = "couplet.index_stage.v1";

    /// <summary>
    /// 获取 C1 staging generation 重开检查合同版本。
    /// </summary>
    public const string StagingInspection = "couplet.staging_inspection.v1";

    /// <summary>
    /// 获取 C1 容量 evidence 报告合同版本。
    /// </summary>
    public const string C1CapacityEvidence = "couplet.c1_capacity_evidence.v1";
}
