using System.Reflection;
using System.Runtime.CompilerServices;
using Couplet.Application.Capabilities;
using Couplet.Core.Capabilities;
using Couplet.Core.Contracts;
using SonnetDB.Documents;
using SonnetDB.Documents.Vector;
using SonnetDB.Engine;
using SonnetDB.FullText;
using SonnetDB.Graphs;
using SonnetDB.Kv;
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
using SonnetDB.Generations;
#endif

namespace Couplet.Infrastructure.SonnetDb;

/// <summary>
/// 从当前选择的 SonnetDB Core package 或源码联调依赖读取能力状态。
/// </summary>
public sealed class SonnetDbCapabilityProbe : ISonnetDbCapabilityProbe
{
    private const string _packageVersion = "3.1.0";

    /// <inheritdoc />
    public DependencyReport Probe()
    {
        Assembly assembly = typeof(GraphStore).Assembly;
        string resolvedVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";

        return new DependencyReport
        {
            Id = "SonnetDB.Core",
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
            Mode = "source_project",
            Requested = "latest_source",
#else
            Mode = "fixed_package",
            Requested = _packageVersion,
#endif
            ResolvedVersion = resolvedVersion,
            ResolvedCommit = ResolveCommit(resolvedVersion)
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
                ?? "source_working_tree",
#else
                ?? "unknown",
#endif
            HandshakeVersion = ContractVersions.CapabilityHandshake,
            State = "available",
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
            Reason = "source_generation_integration_verified",
#else
            Reason = "fixed_package_verified",
#endif
            DeclaresTrimCompatible = ReadBooleanMetadata(assembly, "IsTrimmable"),
            DeclaresAotCompatible = ReadBooleanMetadata(assembly, "IsAotCompatible"),
            GraphApiPresent = typeof(GraphStore).FullName == "SonnetDB.Graphs.GraphStore",
            Capabilities =
            [
                Available("database.lifecycle", "sonnetdb.embedded.v1", "c1_staging_integrated", "CG-005"),
                Available("kv.snapshot", typeof(KvReadSnapshot).FullName!, "c1_staging_integrated", "CG-005"),
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
                RuntimeFeature.IsDynamicCodeSupported
                    ? Available("database.background_maintenance", typeof(TsdbOptions).FullName!, "source_workers_enabled", "CG-005")
                    : Available("database.background_maintenance", typeof(TsdbOptions).FullName!, "source_aot_workers_enabled_pending_soak", "CG-006"),
#else
                RuntimeFeature.IsDynamicCodeSupported
                    ? Available("database.background_maintenance", typeof(TsdbOptions).FullName!, "runtime_workers_available", "CG-005")
                    : Unavailable("database.background_maintenance", typeof(TsdbOptions).FullName!, "native_aot_thread_interrupt_unsupported", "CG-006"),
#endif
                Available("document.collection", typeof(DocumentCollectionStore).FullName!, "c1_staging_integrated", "CG-005"),
                Available("fulltext.document", typeof(DocumentFullTextIndexStore).FullName!, "c1_staging_integrated", "CG-005"),
                Available("vector.document", typeof(DocumentVectorIndexStore).FullName!, "c3_not_implemented", "CG-002"),
                Available("graph.native", typeof(GraphStore).FullName!, "c2_release_gate_not_passed", "CG-001"),
                Available("graph.path_budgets", typeof(GraphTraversalOptions).FullName!, "c2_release_gate_not_passed", "CG-001"),
                Available("graph.diagnostics", typeof(GraphExplain).FullName!, "c2_release_gate_not_passed", "CG-001"),
#if COUPLET_SONNETDB_SOURCE_GENERATIONS
                Available("generation.atomic_publish", typeof(DatabaseGenerationManager).FullName!, "c1_runtime_integrated", "CG-005"),
#else
                Unavailable("generation.atomic_publish", "couplet.generation.v1", "cross_model_publish_not_verified", "CG-005"),
#endif
                Unavailable("hybrid.shared_plan", "couplet.hybrid_plan.v1", "public_typed_shared_plan_not_exposed", "CG-002"),
            ],
        };
    }

    private static DependencyCapability Available(
        string id,
        string contractVersion,
        string reason,
        params string[] gaps) => new()
        {
            Id = id,
            ContractVersion = contractVersion,
            IntegrationState = "available",
            ReleaseLevel = "unavailable",
            Reason = reason,
            BlockingGaps = gaps,
        };

    private static DependencyCapability Unavailable(
        string id,
        string contractVersion,
        string reason,
        string gap) => new()
        {
            Id = id,
            ContractVersion = contractVersion,
            IntegrationState = "unavailable",
            ReleaseLevel = "unavailable",
            Reason = reason,
            BlockingGaps = [gap],
        };

    private static bool ReadBooleanMetadata(Assembly assembly, string key) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(attribute =>
                string.Equals(attribute.Key, key, StringComparison.Ordinal)
                && string.Equals(attribute.Value, "True", StringComparison.OrdinalIgnoreCase));

    private static string? ResolveCommit(string informationalVersion)
    {
        int separator = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return separator >= 0 && separator + 1 < informationalVersion.Length
            ? informationalVersion[(separator + 1)..]
            : null;
    }
}
