using System.Reflection;
using Couplet.Application.Capabilities;
using Couplet.Core.Capabilities;
using SonnetDB.Graphs;

namespace Couplet.Infrastructure.SonnetDb;

/// <summary>
/// 从固定 SonnetDB Core 源码依赖读取构建与能力状态。
/// </summary>
public sealed class SonnetDbCapabilityProbe : ISonnetDbCapabilityProbe
{
    private const string _sourceCommit = "a0fefe15c4ea4d3a5f2a4a2c4f69d6930b9c6c70";

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
            Mode = "source_project_reference",
            Requested = $"source@{_sourceCommit}",
            ResolvedVersion = resolvedVersion,
            ResolvedCommit = _sourceCommit,
            State = "unavailable",
            Reason = "sonnetdb_capability_handshake_not_implemented",
            DeclaresTrimCompatible = ReadBooleanMetadata(assembly, "IsTrimmable"),
            DeclaresAotCompatible = ReadBooleanMetadata(assembly, "IsAotCompatible"),
            GraphApiPresent = typeof(GraphStore).FullName == "SonnetDB.Graphs.GraphStore",
        };
    }

    private static bool ReadBooleanMetadata(Assembly assembly, string key) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(attribute =>
                string.Equals(attribute.Key, key, StringComparison.Ordinal)
                && string.Equals(attribute.Value, "True", StringComparison.OrdinalIgnoreCase));
}
