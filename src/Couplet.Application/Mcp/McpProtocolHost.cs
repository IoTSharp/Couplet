using System.Buffers;
using System.Text;
using System.Text.Json;
using Couplet.Application.Capabilities;
using Couplet.Application.Serialization;
using Couplet.Core.Mcp;

namespace Couplet.Application.Mcp;

/// <summary>
/// 提供 C0 只读 MCP stdio 协议骨架和版本化工具 schema。
/// </summary>
public sealed class McpProtocolHost
{
    private readonly WorkspaceBinding _binding;
    private readonly InitializeWorkspaceResponse _initialization;
    private readonly McpSchemaCatalog _catalog;
    private readonly IMcpToolExecutor? _executor;
    private readonly IReadOnlyList<McpCapability>? _toolGateCapabilities;

    /// <summary>
    /// 初始化协议宿主。
    /// </summary>
    /// <param name="binding">显式工作区绑定。</param>
    public McpProtocolHost(WorkspaceBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _binding = binding;
        _catalog = McpSchemaCatalog.Load();
        _initialization = WorkspaceInitializationEvaluator.Evaluate(
            binding,
            WorkspaceDatabaseState.Empty,
            McpWorkspaceBinder.CreateCapabilities(),
            "initialize").Response!;
    }

    /// <summary>
    /// 初始化带 typed 工具执行器的协议宿主。
    /// </summary>
    /// <param name="binding">显式工作区绑定。</param>
    /// <param name="executor">typed 工具执行器。</param>
    /// <param name="databaseState">初始化时观察到的数据库状态。</param>
    /// <param name="capabilities">连接公开的真实能力。</param>
    public McpProtocolHost(
        WorkspaceBinding binding,
        IMcpToolExecutor executor,
        WorkspaceDatabaseState databaseState,
        IReadOnlyList<McpCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(capabilities);
        _binding = binding;
        _executor = executor;
        _toolGateCapabilities = capabilities;
        _catalog = McpSchemaCatalog.Load();
        WorkspaceInitializationResult initialization = WorkspaceInitializationEvaluator.Evaluate(
            binding,
            databaseState,
            capabilities,
            "initialize");
        _initialization = initialization.Response
            ?? throw new ArgumentException(
                $"Database state {databaseState} cannot initialize an MCP host.",
                nameof(databaseState));
    }

    /// <summary>
    /// 持续处理每行一条 JSON-RPC 消息，直到输入结束或取消。
    /// </summary>
    /// <param name="input">标准输入。</param>
    /// <param name="output">标准输出。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string? response = ProcessLine(line, cancellationToken);
            if (response is not null)
            {
                await output.WriteLineAsync(response).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal string? ProcessLine(string line, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("jsonrpc", out JsonElement jsonRpc)
                || jsonRpc.GetString() != "2.0"
                || !root.TryGetProperty("method", out JsonElement methodElement))
            {
                return WriteJsonRpcError(default, -32600, "Invalid Request");
            }

            bool hasId = root.TryGetProperty("id", out JsonElement id);
            string method = methodElement.GetString() ?? string.Empty;
            if (!hasId)
            {
                return null;
            }

            return method switch
            {
                "initialize" => WriteInitialize(id, root),
                "ping" => WriteEmptyResult(id),
                "tools/list" => WriteToolsList(id),
                "tools/call" => WriteToolCall(id, root, cancellationToken),
                _ => WriteJsonRpcError(id, -32601, "Method not found"),
            };
        }
        catch (JsonException)
        {
            return WriteJsonRpcError(default, -32700, "Parse error");
        }
    }

    private string WriteInitialize(JsonElement id, JsonElement root)
    {
        if (!root.TryGetProperty("params", out JsonElement parameters)
            || !parameters.TryGetProperty("protocolVersion", out JsonElement versionElement)
            || versionElement.GetString() is not { } requestedVersion
            || requestedVersion is not ("2025-06-18" or "2024-11-05"))
        {
            return WriteJsonRpcError(id, -32602, "Unsupported protocol version");
        }

        return Write(writer =>
        {
            WriteEnvelopeStart(writer, id);
            writer.WritePropertyName("result");
            writer.WriteStartObject();
            writer.WriteString("protocolVersion", requestedVersion);
            writer.WritePropertyName("capabilities");
            writer.WriteStartObject();
            writer.WritePropertyName("tools");
            writer.WriteStartObject();
            writer.WriteBoolean("listChanged", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("serverInfo");
            writer.WriteStartObject();
            writer.WriteString("name", "Couplet");
            writer.WriteString("version", ProductVersion.Current);
            writer.WriteEndObject();
            writer.WritePropertyName("couplet");
            JsonSerializer.Serialize(writer, _initialization, CoupletJsonContext.Default.InitializeWorkspaceResponse);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private string WriteToolsList(JsonElement id) => Write(writer =>
    {
        WriteEnvelopeStart(writer, id);
        writer.WritePropertyName("result");
        writer.WriteStartObject();
        writer.WritePropertyName("tools");
        writer.WriteStartArray();
        foreach (McpToolSchema tool in _catalog.Tools)
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("inputSchema");
            tool.InputSchema.WriteTo(writer);
            writer.WritePropertyName("outputSchema");
            tool.OutputSchema.WriteTo(writer);
            writer.WritePropertyName("annotations");
            writer.WriteStartObject();
            writer.WriteBoolean("readOnlyHint", true);
            writer.WriteBoolean("destructiveHint", false);
            writer.WriteBoolean("idempotentHint", true);
            writer.WriteBoolean("openWorldHint", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    private string WriteToolCall(JsonElement id, JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("params", out JsonElement parameters)
            || !parameters.TryGetProperty("name", out JsonElement nameElement)
            || !parameters.TryGetProperty("arguments", out JsonElement arguments)
            || nameElement.GetString() is not { Length: > 0 } tool)
        {
            return WriteJsonRpcError(id, -32602, "Invalid params");
        }

        string correlationId = CreateCorrelationId(id);
        McpDispatchResult result = McpToolContractDispatcher.Dispatch(
            tool,
            arguments,
            _binding,
            correlationId,
            _executor,
            cancellationToken,
            _toolGateCapabilities);
        if (result.Error is { } error)
        {
            return WriteToolError(id, error);
        }

        if (result.WorkspaceStatus is { } workspaceStatus
            && result.SerializedWorkspaceStatus is { } serializedWorkspaceStatus)
        {
            return WriteToolSuccess(
                id,
                workspaceStatus,
                serializedWorkspaceStatus);
        }

        if (result.CodeSearch is { } codeSearch
            && result.SerializedCodeSearch is { } serializedCodeSearch)
        {
            return WriteToolSuccess(
                id,
                codeSearch,
                serializedCodeSearch);
        }

        if (result.SymbolDetails is { } symbolDetails
            && result.SerializedSymbolDetails is { } serializedSymbolDetails)
        {
            return WriteToolSuccess(
                id,
                symbolDetails,
                serializedSymbolDetails);
        }

        return WriteToolError(id, new McpError
        {
            Code = McpErrorCodes.InternalError,
            Reason = "dispatch_result_invalid",
            Retryable = false,
            CurrentRevision = _binding.IndexRevision,
            CorrelationId = correlationId,
        });
    }

    private static string WriteToolError(JsonElement id, McpError error)
    {
        string errorJson = CoupletJsonSerializer.Serialize(error);

        return Write(writer =>
        {
            WriteEnvelopeStart(writer, id);
            writer.WritePropertyName("result");
            writer.WriteStartObject();
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", errorJson);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WritePropertyName("structuredContent");
            writer.WriteStartObject();
            writer.WritePropertyName("error");
            JsonSerializer.Serialize(writer, error, CoupletJsonContext.Default.McpError);
            writer.WriteEndObject();
            writer.WriteBoolean("isError", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static string WriteToolSuccess<T>(
        JsonElement id,
        T response,
        string responseJson)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseJson);
        return Write(writer =>
        {
            WriteEnvelopeStart(writer, id);
            writer.WritePropertyName("result");
            writer.WriteStartObject();
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", responseJson);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WritePropertyName("structuredContent");
            writer.WriteRawValue(responseJson);
            writer.WriteBoolean("isError", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static string WriteEmptyResult(JsonElement id) => Write(writer =>
    {
        WriteEnvelopeStart(writer, id);
        writer.WriteStartObject("result");
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    private static string WriteJsonRpcError(JsonElement id, int code, string message) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        if (id.ValueKind == JsonValueKind.Undefined)
        {
            writer.WriteNullValue();
        }
        else
        {
            id.WriteTo(writer);
        }

        writer.WritePropertyName("error");
        writer.WriteStartObject();
        writer.WriteNumber("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    private static void WriteEnvelopeStart(Utf8JsonWriter writer, JsonElement id)
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        id.WriteTo(writer);
    }

    private static string Write(Action<Utf8JsonWriter> action)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            action(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string CreateCorrelationId(JsonElement id)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(id.GetRawText()));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
