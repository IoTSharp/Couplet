using System.Text.Json;
using Couplet.Application.Capabilities;
using Couplet.Application.Serialization;
using Couplet.Core.Capabilities;

namespace Couplet.Tests;

public sealed class SerializationContractTests
{
    [Fact]
    public void Serialize_CapabilityReport_UsesSourceGeneratedSnakeCaseContract()
    {
        var service = new CapabilityReportService(new StubProbe());
        CapabilityReport report = service.Create(ComponentKind.Cli);

        string json = CoupletJsonSerializer.Serialize(report);

        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        Assert.NotNull(CoupletJsonContext.Default.CapabilityReport);
        Assert.Contains("\"schema_version\":\"cpl-007.capabilities.v1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sonnet_db_core\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SchemaVersion", json, StringComparison.Ordinal);
    }

    private sealed class StubProbe : ISonnetDbCapabilityProbe
    {
        public DependencyReport Probe() => new()
        {
            Id = "SonnetDB.Core",
            Mode = "test",
            Requested = "test",
            ResolvedVersion = "test",
            ResolvedCommit = "test",
            HandshakeVersion = "couplet.sonnetdb_handshake.v1",
            State = "unavailable",
            Reason = "test",
            DeclaresTrimCompatible = true,
            DeclaresAotCompatible = true,
            GraphApiPresent = true,
            Capabilities = [],
        };
    }
}
