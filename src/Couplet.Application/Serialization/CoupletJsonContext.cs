using System.Text.Json.Serialization;
using Couplet.Core.Capabilities;
using Couplet.Core.Evaluation;
using Couplet.Core.Graph;
using Couplet.Core.Indexing;
using Couplet.Core.Mcp;
using Couplet.Core.Security;
using Couplet.Core.Workspaces;

namespace Couplet.Application.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(CapabilityReport))]
[JsonSerializable(typeof(LifecycleReport))]
[JsonSerializable(typeof(ErrorReport))]
[JsonSerializable(typeof(CodeGraphNode))]
[JsonSerializable(typeof(CodeGraphEdge))]
[JsonSerializable(typeof(GenerationManifest))]
[JsonSerializable(typeof(GenerationDeletion))]
[JsonSerializable(typeof(SecurityPolicy))]
[JsonSerializable(typeof(InitializeWorkspaceRequest))]
[JsonSerializable(typeof(InitializeWorkspaceResponse))]
[JsonSerializable(typeof(McpError))]
[JsonSerializable(typeof(CursorPayload))]
[JsonSerializable(typeof(WorkspaceStatusRequest))]
[JsonSerializable(typeof(CodeSearchRequest))]
[JsonSerializable(typeof(SymbolGetRequest))]
[JsonSerializable(typeof(SymbolRelationsRequest))]
[JsonSerializable(typeof(DependencyPathRequest))]
[JsonSerializable(typeof(ImpactAnalyzeRequest))]
[JsonSerializable(typeof(ChangeContextRequest))]
[JsonSerializable(typeof(ContextPackRequest))]
[JsonSerializable(typeof(McpToolResponse<WorkspaceStatusItem>))]
[JsonSerializable(typeof(McpToolResponse<CodeSearchItem>))]
[JsonSerializable(typeof(McpToolResponse<SymbolDetailsItem>))]
[JsonSerializable(typeof(McpToolResponse<SymbolRelationItem>))]
[JsonSerializable(typeof(McpToolResponse<DependencyPathItem>))]
[JsonSerializable(typeof(McpToolResponse<ImpactItem>))]
[JsonSerializable(typeof(McpToolResponse<ChangeContextItem>))]
[JsonSerializable(typeof(McpToolResponse<ContextPackItem>))]
[JsonSerializable(typeof(FixtureManifest))]
[JsonSerializable(typeof(GoldenAnswerSet))]
[JsonSerializable(typeof(AgentEvalManifest))]
[JsonSerializable(typeof(AgentEvalResultSet))]
[JsonSerializable(typeof(AgentEvalValidationResult))]
[JsonSerializable(typeof(FixtureGenerationReport))]
[JsonSerializable(typeof(C0EvidenceReport))]
[JsonSerializable(typeof(C1CapacityEvidenceReport))]
[JsonSerializable(typeof(WorkspaceDiscoveryResult))]
[JsonSerializable(typeof(WorkspaceChangeBatch))]
[JsonSerializable(typeof(WorkspaceIndexSnapshot))]
[JsonSerializable(typeof(IncrementalIndexPlan))]
[JsonSerializable(typeof(IndexStorageDocument))]
[JsonSerializable(typeof(IndexStageReport))]
[JsonSerializable(typeof(StagingGenerationInspection))]
internal sealed partial class CoupletJsonContext : JsonSerializerContext;
