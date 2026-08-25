using System.Text.Json;
using Couplet.Application.Mcp;
using Couplet.Core.Mcp;

namespace Couplet.Tests;

public sealed class McpContractTests
{
    [Fact]
    public void SchemaCatalog_V1Snapshot_ContainsExactlyEightReadOnlyTypedTools()
    {
        McpSchemaCatalog catalog = McpSchemaCatalog.Load();
        using JsonDocument snapshot = JsonDocument.Parse(catalog.Snapshot);

        JsonElement[] tools = snapshot.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(8, tools.Length);
        Assert.Equal(McpToolNames.All, tools.Select(tool => tool.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        Assert.All(tools, tool =>
        {
            Assert.True(tool.GetProperty("read_only").GetBoolean());
            Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
            Assert.True(tool.GetProperty("inputSchema").GetProperty("additionalProperties").ValueKind == JsonValueKind.False);
            Assert.True(tool.TryGetProperty("outputSchema", out _));
        });
    }

    [Fact]
    public void ProcessLine_ToolsList_ReturnsExpandedSchemasAndReadOnlyAnnotations()
    {
        McpProtocolHost host = CreateHost();

        string response = host.ProcessLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}", CancellationToken.None)!;

        Assert.DoesNotContain("\"$ref\"", response, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement[] tools = document.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(8, tools.Length);
        Assert.All(tools, tool =>
        {
            Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
            Assert.False(tool.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        });
    }

    [Theory]
    [InlineData("Codex", "1.2.3")]
    [InlineData("Claude Code", "4.5.6")]
    public void ProcessLine_InitializeForSupportedClients_ReturnsSameWorkspaceContract(
        string client,
        string version)
    {
        McpProtocolHost host = CreateHost();
        string request = "{\"jsonrpc\":\"2.0\",\"id\":\"init\",\"method\":\"initialize\","
            + "\"params\":{\"protocolVersion\":\"2025-06-18\",\"clientInfo\":{\"name\":\""
            + client
            + "\",\"version\":\""
            + version
            + "\"},\"capabilities\":{}}}";

        string response = host.ProcessLine(request, CancellationToken.None)!;

        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("workspace", result.GetProperty("couplet").GetProperty("binding").GetProperty("workspace_id").GetString());
        Assert.Equal(5, result.GetProperty("couplet").GetProperty("capabilities").GetArrayLength());
    }

    [Theory]
    [InlineData("Codex", "1.2.3")]
    [InlineData("Claude Code", "4.5.6")]
    public void ProcessLine_C1ToolsForSupportedClients_NeverExposeUnpublishedStaging(
        string client,
        string version)
    {
        McpProtocolHost host = CreateHost();
        string initialize = "{\"jsonrpc\":\"2.0\",\"id\":\"init\",\"method\":\"initialize\","
            + "\"params\":{\"protocolVersion\":\"2025-06-18\",\"clientInfo\":{\"name\":\""
            + client
            + "\",\"version\":\""
            + version
            + "\"},\"capabilities\":{}}}";
        Assert.NotNull(host.ProcessLine(initialize, CancellationToken.None));

        (string Tool, string Arguments)[] calls =
        [
            (McpToolNames.WorkspaceStatus,
                "{\"protocol_version\":\"1\",\"budget\":{\"max_items\":20,\"max_tokens\":1000,\"max_bytes\":4096,\"deadline_ms\":1000}}"),
            (McpToolNames.CodeSearch,
                "{\"protocol_version\":\"1\",\"budget\":{\"max_items\":20,\"max_tokens\":1000,\"max_bytes\":4096,\"deadline_ms\":1000},\"query\":\"Sample\",\"mode\":\"fulltext\"}"),
            (McpToolNames.SymbolGet,
                "{\"protocol_version\":\"1\",\"budget\":{\"max_items\":20,\"max_tokens\":1000,\"max_bytes\":4096,\"deadline_ms\":1000},\"symbol_id\":\"cpl_symbol_unpublished\"}"),
        ];

        foreach ((string tool, string arguments) in calls)
        {
            string request = "{\"jsonrpc\":\"2.0\",\"id\":\"" + tool
                + "\",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool
                + "\",\"arguments\":" + arguments + "}}";
            string response = host.ProcessLine(request, CancellationToken.None)!;

            using JsonDocument document = JsonDocument.Parse(response);
            JsonElement result = document.RootElement.GetProperty("result");
            Assert.True(result.GetProperty("isError").GetBoolean());
            JsonElement error = result.GetProperty("structuredContent").GetProperty("error");
            Assert.Equal(McpErrorCodes.CapabilityUnavailable, error.GetProperty("code").GetString());
            Assert.Equal("generation_publish_blocked", error.GetProperty("reason").GetString());
            Assert.Equal("CG-005", error.GetProperty("gap_id").GetString());
            Assert.DoesNotContain("items", response, StringComparison.Ordinal);
            Assert.DoesNotContain("staging", response, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ProcessLine_InitializeWithUnsupportedProtocol_ReturnsStableJsonRpcError()
    {
        McpProtocolHost host = CreateHost();

        string response = host.ProcessLine(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2099-01-01\"}}",
            CancellationToken.None)!;

        using JsonDocument document = JsonDocument.Parse(response);
        Assert.Equal(-32602, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("Unsupported protocol version", document.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void Dispatch_SameValidRequest_ReturnsStableCapabilityErrorShape()
    {
        WorkspaceBinding binding = Binding();
        using JsonDocument arguments = JsonDocument.Parse("""
            {"protocol_version":"1","budget":{"max_items":20,"max_tokens":1000,"max_bytes":4096,"deadline_ms":1000}}
            """);

        McpDispatchResult first = McpToolContractDispatcher.Dispatch(
            McpToolNames.WorkspaceStatus,
            arguments.RootElement,
            binding,
            "request-1",
            CancellationToken.None);
        McpDispatchResult second = McpToolContractDispatcher.Dispatch(
            McpToolNames.WorkspaceStatus,
            arguments.RootElement,
            binding,
            "request-1",
            CancellationToken.None);

        Assert.Equal(McpErrorCodes.CapabilityUnavailable, first.Error.Code);
        Assert.Equal("generation_publish_blocked", first.Error.Reason);
        Assert.Equal(first.Error.Code, second.Error.Code);
        Assert.Equal(first.Error.Reason, second.Error.Reason);
        Assert.Equal(first.Error.GapId, second.Error.GapId);
    }

    [Fact]
    public void Dispatch_UnknownField_ReturnsInvalidRequest()
    {
        using JsonDocument arguments = JsonDocument.Parse("""
            {"protocol_version":"1","budget":{"max_items":20,"max_tokens":1000,"max_bytes":4096,"deadline_ms":1000},"unexpected":true}
            """);

        McpDispatchResult result = McpToolContractDispatcher.Dispatch(
            McpToolNames.WorkspaceStatus,
            arguments.RootElement,
            Binding(),
            "request-2",
            CancellationToken.None);

        Assert.Equal(McpErrorCodes.InvalidRequest, result.Error.Code);
        Assert.Equal("request_schema_mismatch", result.Error.Reason);
    }

    [Fact]
    public void Dispatch_ExplicitUnconfiguredProvider_ReturnsProviderUnavailable()
    {
        using JsonDocument arguments = JsonDocument.Parse("""
            {"protocol_version":"1","budget":{"max_items":20,"max_tokens":1000,"max_bytes":4096,"deadline_ms":1000},"query":"graph","mode":"vector","provider_id":"online"}
            """);

        McpDispatchResult result = McpToolContractDispatcher.Dispatch(
            McpToolNames.CodeSearch,
            arguments.RootElement,
            Binding(),
            "request-3",
            CancellationToken.None);

        Assert.Equal(McpErrorCodes.ProviderUnavailable, result.Error.Code);
    }

    [Fact]
    public void Check_CancelDeadlineAndBudget_ReturnStableErrors()
    {
        QueryBudget budget = Budget();
        WorkspaceBinding binding = Binding();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(McpErrorCodes.Cancelled, McpRequestGuard.Check(budget, TimeSpan.Zero, 0, 0, 0, binding, "c", cancellation.Token)!.Code);
        Assert.Equal(McpErrorCodes.DeadlineExceeded, McpRequestGuard.Check(budget, TimeSpan.FromSeconds(2), 0, 0, 0, binding, "d", CancellationToken.None)!.Code);
        Assert.Equal(McpErrorCodes.BudgetExhausted, McpRequestGuard.Check(budget, TimeSpan.Zero, budget.MaxItems, 0, 0, binding, "b", CancellationToken.None)!.Code);
    }

    [Fact]
    public void CursorCodec_TamperOrRevisionChange_IsRejected()
    {
        var codec = new CursorCodec(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var payload = new CursorPayload
        {
            WorkspaceId = "workspace",
            Tool = McpToolNames.CodeSearch,
            QueryHash = "query",
            IndexRevision = "index-1",
            Offset = 20,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        };

        string cursor = codec.Encode(payload);

        Assert.True(codec.TryDecode(cursor, "workspace", McpToolNames.CodeSearch, "query", "index-1", DateTimeOffset.UtcNow, out CursorPayload? decoded));
        Assert.Equal(20, decoded!.Offset);
        Assert.False(codec.TryDecode(cursor + "x", "workspace", McpToolNames.CodeSearch, "query", "index-1", DateTimeOffset.UtcNow, out _));
        Assert.False(codec.TryDecode(cursor, "workspace", McpToolNames.CodeSearch, "query", "index-2", DateTimeOffset.UtcNow, out _));
    }

    [Theory]
    [InlineData(WorkspaceDatabaseState.Empty, true, null)]
    [InlineData(WorkspaceDatabaseState.Legacy, false, McpErrorCodes.CapabilityUnavailable)]
    [InlineData(WorkspaceDatabaseState.Corrupt, false, McpErrorCodes.IndexCorrupt)]
    public void Evaluate_DatabaseState_ReturnsExpectedInitializeContract(
        WorkspaceDatabaseState state,
        bool succeeds,
        string? errorCode)
    {
        WorkspaceInitializationResult result = WorkspaceInitializationEvaluator.Evaluate(
            Binding(),
            state,
            McpWorkspaceBinder.CreateCapabilities(),
            "initialize");

        Assert.Equal(succeeds, result.Response is not null);
        Assert.Equal(errorCode, result.Error?.Code);
    }

    private static McpProtocolHost CreateHost() => new(Binding());

    private static WorkspaceBinding Binding() => new()
    {
        WorkspaceId = "workspace",
        RepositoryIdentity = "local:workspace",
        SourceRevision = "source-1",
        IndexRevision = "index-1",
    };

    private static QueryBudget Budget() => new()
    {
        MaxItems = 20,
        MaxTokens = 1000,
        MaxBytes = 4096,
        DeadlineMs = 1000,
    };
}
