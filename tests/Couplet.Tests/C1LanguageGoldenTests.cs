using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Couplet.Application.Languages;
using Couplet.Application.Serialization;
using Couplet.Core.Graph;
using Couplet.Core.Indexing;
using Couplet.Core.Languages;

namespace Couplet.Tests;

public sealed class C1LanguageGoldenTests
{
    private const string _workspaceId = "cpl_workspace_c1_language_golden";
    private const string _sourceRevision = "source-c1-language-golden-v1";
    private const string _indexRevision = "index-c1-language-golden-v1";

    [Fact]
    public void Parse_CommittedC1LanguageFixture_MatchesVersionedGoldenSnapshot()
    {
        WorkspaceIndexSnapshot actual = BuildSnapshot();
        string actualJson = JsonSerializer.Serialize(
            actual,
            CoupletJsonContext.Default.WorkspaceIndexSnapshot);
        WorkspaceIndexSnapshot repeated = BuildSnapshot();
        string repeatedJson = JsonSerializer.Serialize(
            repeated,
            CoupletJsonContext.Default.WorkspaceIndexSnapshot);
        using JsonDocument golden = ReadGolden();
        JsonElement root = golden.RootElement;
        using JsonDocument actualDocument = JsonDocument.Parse(actualJson);

        Assert.Equal("couplet.c1_language_golden.v1", root.GetProperty("schema_version").GetString());
        Assert.Equal("couplet-c1-language-golden-v1", root.GetProperty("fixture_id").GetString());
        Assert.True(
            JsonElement.DeepEquals(root.GetProperty("snapshot"), actualDocument.RootElement),
            "The source-generated C1 language snapshot differs structurally from the committed golden JSON.");
        Assert.Equal(actualJson, repeatedJson);
    }

    [Fact]
    public void Parse_CommittedC1LanguageFixture_PreservesPartialSemanticBoundariesAndEvidence()
    {
        WorkspaceIndexSnapshot snapshot = BuildSnapshot();
        string fixtureRoot = LanguageFixtureRoot();
        using JsonDocument golden = ReadGolden();
        JsonElement root = golden.RootElement;

        Assert.Equal("Partial", root.GetProperty("semantic_tier").GetString());
        Assert.Equal(
            "lexical_declaration_inference_only",
            root.GetProperty("confidence_scope").GetString());
        Assert.Equal(
            ["csharp", "javascript", "typescript"],
            snapshot.Files.Select(file => file.Language).ToArray());

        foreach (IndexedFile file in snapshot.Files)
        {
            string content = File.ReadAllText(Path.Combine(fixtureRoot, file.Path.Replace('/', Path.DirectorySeparatorChar)), Encoding.UTF8);
            Assert.Equal(SemanticTier.Partial, file.SemanticTier);
            Assert.NotEqual(SemanticTier.Exact, file.SemanticTier);
            Assert.Equal("1.1.0", file.AdapterVersion);
            Assert.Equal(Hash(content), file.ContentHash);
            Assert.NotEmpty(file.Symbols);
            Assert.Equal(file.Symbols.Count, file.Chunks.Count);

            foreach (IndexedSymbol symbol in file.Symbols)
            {
                Assert.Equal(symbol.DisplayName, Utf8Slice(content, symbol.Definition));
                Assert.Equal(file.AdapterId, symbol.Provenance.AdapterId);
                Assert.Equal(file.AdapterVersion, symbol.Provenance.AdapterVersion);
                Assert.Equal(file.ContentHash, symbol.Provenance.ContentHash);
                Assert.Equal(_sourceRevision, symbol.Provenance.SourceRevision);
                Assert.Equal(_indexRevision, symbol.Provenance.IndexRevision);
                Assert.Equal(ConfidenceKind.Inferred, symbol.Confidence.Kind);
                Assert.Equal(0.9, symbol.Confidence.Score);
                Assert.Equal(file.AdapterId + ".declaration.v2", symbol.Confidence.Rule);
                Assert.Contains(file.Chunks, chunk => chunk.SymbolId == symbol.Id);
            }

            foreach (IndexedChunk chunk in file.Chunks)
            {
                Assert.Equal(chunk.Content, Utf8Slice(content, chunk.Span));
                Assert.Equal(Hash(chunk.Content), chunk.ContentHash);
                Assert.StartsWith("cpl_chunk_", chunk.Id, StringComparison.Ordinal);
            }
        }

        Assert.Contains(snapshot.Files.SelectMany(file => file.Symbols), symbol =>
            symbol.QualifiedIdentity == "Demo.Unicode.格式化器.Format(int value)");
        Assert.Contains(snapshot.Files.SelectMany(file => file.Symbols), symbol =>
            symbol.QualifiedIdentity == "Demo.Unicode.格式化器.Format(string value)");
        Assert.Contains(snapshot.Files.SelectMany(file => file.Symbols), symbol =>
            symbol.QualifiedIdentity == "Demo.Unicode.Secondary.Format(int value)");
        Assert.Contains(snapshot.Files.SelectMany(file => file.Symbols), symbol =>
            symbol.QualifiedIdentity == "Formatter.format(value:number)");
        Assert.Contains(snapshot.Files.SelectMany(file => file.Symbols), symbol =>
            symbol.QualifiedIdentity == "Formatter.format(value:string)");
        Assert.Contains(snapshot.Files.SelectMany(file => file.Symbols), symbol =>
            symbol.QualifiedIdentity == "format(value:string)");
        Assert.Contains(snapshot.Files.SelectMany(file => file.Symbols), symbol =>
            symbol.QualifiedIdentity == "Formatter.format(value)");
        Assert.Contains(snapshot.Files.SelectMany(file => file.Symbols), symbol =>
            symbol.QualifiedIdentity == "Secondary.format(value)");
        Assert.Contains(snapshot.Files.SelectMany(file => file.Symbols), symbol =>
            symbol.QualifiedIdentity == "format(value)");

        IndexedSymbol unicodeType = Assert.Single(snapshot.Files.SelectMany(file => file.Symbols), symbol =>
            symbol.QualifiedIdentity == "Demo.Unicode.格式化器");
        Assert.Equal("class 格式化器", unicodeType.Signature);
        Assert.True(unicodeType.Definition.StartByte > unicodeType.Definition.StartColumn);
        IndexedSymbol genericType = Assert.Single(snapshot.Files.SelectMany(file => file.Symbols), symbol =>
            symbol.Language == "typescript" && symbol.QualifiedIdentity == "Formatter");
        Assert.Equal("class Formatter", genericType.Signature);

        foreach (JsonElement unsupported in root.GetProperty("unsupported_declarations").EnumerateArray())
        {
            string language = unsupported.GetProperty("language").GetString()!;
            string displayName = unsupported.GetProperty("display_name").GetString()!;
            Assert.Equal(
                "generic_method_not_supported_by_lexical_v1",
                unsupported.GetProperty("reason").GetString());
            Assert.DoesNotContain(snapshot.Files
                .Where(file => file.Language == language)
                .SelectMany(file => file.Symbols), symbol => symbol.DisplayName == displayName);
        }
    }

    private static WorkspaceIndexSnapshot BuildSnapshot()
    {
        string fixtureRoot = LanguageFixtureRoot();
        var files = new List<IndexedFile>();
        foreach (string sourcePath in Directory.EnumerateFiles(fixtureRoot, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            string path = Path.GetRelativePath(fixtureRoot, sourcePath).Replace('\\', '/');
            string language = Path.GetExtension(sourcePath) switch
            {
                ".cs" => "csharp",
                ".ts" => "typescript",
                ".js" => "javascript",
                _ => throw new InvalidDataException("c1_language_fixture_extension_unsupported"),
            };
            string content = File.ReadAllText(sourcePath, Encoding.UTF8);
            ILanguageAdapter adapter = Assert.IsAssignableFrom<ILanguageAdapter>(BuiltinLanguageAdapters.Find(language));
            files.Add(adapter.Parse(new LanguageParseRequest
            {
                WorkspaceId = _workspaceId,
                SourceRevision = _sourceRevision,
                IndexRevision = _indexRevision,
                Path = path,
                ContentHash = Hash(content),
                Content = content,
            }));
        }

        return new WorkspaceIndexSnapshot
        {
            WorkspaceId = _workspaceId,
            RepositoryIdentity = "fixture:couplet-c1-language-golden-v1",
            WorktreeIdentity = "fixture",
            SourceRevision = _sourceRevision,
            IndexRevision = _indexRevision,
            ProducerVersions = BuiltinLanguageAdapters.All
                .Select(adapter => adapter.Capability.AdapterId + "@" + adapter.Capability.AdapterVersion)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            Files = files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(),
            Failures = [],
        };
    }

    private static string FindRepositoryRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null && !File.Exists(Path.Combine(current, "Couplet.slnx")))
        {
            current = Directory.GetParent(current)?.FullName;
        }

        return current ?? throw new DirectoryNotFoundException("Could not locate Couplet repository root.");
    }

    private static string LanguageFixtureRoot() =>
        Path.Combine(FindRepositoryRoot(), "fixtures", "c1", "language");

    private static JsonDocument ReadGolden() => JsonDocument.Parse(
        File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "fixtures", "c1", "language-golden.v1.json"),
            Encoding.UTF8));

    private static string Utf8Slice(string content, SourceSpan span)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        return Encoding.UTF8.GetString(bytes.AsSpan(
            checked((int)span.StartByte),
            checked((int)(span.EndByte - span.StartByte))));
    }

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
