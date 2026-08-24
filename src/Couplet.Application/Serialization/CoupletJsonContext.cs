using System.Text.Json.Serialization;
using Couplet.Core.Capabilities;

namespace Couplet.Application.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(CapabilityReport))]
[JsonSerializable(typeof(LifecycleReport))]
[JsonSerializable(typeof(ErrorReport))]
internal sealed partial class CoupletJsonContext : JsonSerializerContext;
