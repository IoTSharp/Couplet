using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Couplet.Core.Contracts;
using Couplet.Core.Mcp;

namespace Couplet.Application.Mcp;

internal sealed class McpToolSchema
{
    internal required string Name { get; init; }
    internal required string Description { get; init; }
    internal required string Stage { get; init; }
    internal required JsonNode InputSchema { get; init; }
    internal required JsonNode OutputSchema { get; init; }
}

internal sealed class McpSchemaCatalog
{
    private const string _resourceName = "Couplet.Contracts.Mcp.v1.schema-catalog.json";

    private McpSchemaCatalog(string snapshot, IReadOnlyList<McpToolSchema> tools)
    {
        Snapshot = snapshot;
        Tools = tools;
    }

    internal string Snapshot { get; }

    internal IReadOnlyList<McpToolSchema> Tools { get; }

    internal static McpSchemaCatalog Load()
    {
        Assembly assembly = typeof(McpSchemaCatalog).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(_resourceName)
            ?? throw new InvalidOperationException($"Embedded MCP schema '{_resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        string snapshot = reader.ReadToEnd();
        JsonNode root = JsonNode.Parse(snapshot)
            ?? throw new InvalidDataException("MCP schema catalog is empty.");

        string version = root["schema_version"]?.GetValue<string>() ?? string.Empty;
        if (!string.Equals(version, ContractVersions.Mcp, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported MCP schema catalog '{version}'.");
        }

        JsonArray sourceTools = root["tools"]?.AsArray()
            ?? throw new InvalidDataException("MCP schema catalog has no tools array.");
        var tools = new List<McpToolSchema>(sourceTools.Count);
        foreach (JsonNode? sourceTool in sourceTools)
        {
            JsonObject tool = sourceTool?.AsObject()
                ?? throw new InvalidDataException("MCP schema catalog contains an invalid tool.");
            JsonNode input = tool["inputSchema"]
                ?? throw new InvalidDataException("MCP tool has no input schema.");
            JsonNode output = tool["outputSchema"]
                ?? throw new InvalidDataException("MCP tool has no output schema.");
            tools.Add(new McpToolSchema
            {
                Name = RequiredString(tool, "name"),
                Description = RequiredString(tool, "description"),
                Stage = RequiredString(tool, "stage"),
                InputSchema = Expand(input, input, root),
                OutputSchema = Expand(output, output, root),
            });
        }

        string[] actualNames = tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray();
        if (!actualNames.SequenceEqual(McpToolNames.All, StringComparer.Ordinal))
        {
            throw new InvalidDataException("MCP schema catalog does not contain exactly the eight v1 tools.");
        }

        return new McpSchemaCatalog(snapshot, tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray());
    }

    private static JsonNode Expand(JsonNode node, JsonNode localRoot, JsonNode catalogRoot)
    {
        if (node is JsonObject sourceObject
            && sourceObject.TryGetPropertyValue("$ref", out JsonNode? referenceNode)
            && referenceNode is not null)
        {
            string reference = referenceNode.GetValue<string>();
            JsonNode resolved = ResolveReference(reference, localRoot, catalogRoot);
            return Expand(resolved, localRoot, catalogRoot);
        }

        if (node is JsonObject objectNode)
        {
            var result = new JsonObject();
            foreach ((string key, JsonNode? value) in objectNode)
            {
                result[key] = value is null ? null : Expand(value, localRoot, catalogRoot);
            }

            return result;
        }

        if (node is JsonArray arrayNode)
        {
            var result = new JsonArray();
            foreach (JsonNode? value in arrayNode)
            {
                result.Add(value is null ? null : Expand(value, localRoot, catalogRoot));
            }

            return result;
        }

        return node.DeepClone();
    }

    private static JsonNode ResolveReference(string reference, JsonNode localRoot, JsonNode catalogRoot)
    {
        string[] segments = reference switch
        {
            string value when value.StartsWith("#/$defsCommon/", StringComparison.Ordinal) =>
                ["$defsCommon", value["#/$defsCommon/".Length..]],
            string value when value.StartsWith("#/$defs/", StringComparison.Ordinal) =>
                ["$defs", value["#/$defs/".Length..]],
            _ => throw new InvalidDataException($"Unsupported MCP schema reference '{reference}'."),
        };

        JsonNode? resolved = localRoot[segments[0]]?[segments[1]]
            ?? catalogRoot[segments[0]]?[segments[1]];
        return resolved?.DeepClone()
            ?? throw new InvalidDataException($"Unresolved MCP schema reference '{reference}'.");
    }

    private static string RequiredString(JsonObject value, string property) =>
        value[property]?.GetValue<string>()
        ?? throw new InvalidDataException($"MCP schema tool has no '{property}'.");
}
