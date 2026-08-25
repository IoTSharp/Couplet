using System.Security.Cryptography;
using System.Text;
using Couplet.Core.Graph;
using Couplet.Core.Languages;

namespace Couplet.Application.Languages;

/// <summary>
/// 提供 C1 内置 C# 与 TypeScript/JavaScript partial semantic adapters。
/// </summary>
public static class BuiltinLanguageAdapters
{
    private static readonly IReadOnlyList<ILanguageAdapter> _all =
    [
        new DeclarationLanguageAdapter("csharp", "csharp", [".cs"], ["class", "struct", "interface", "enum", "record"]),
        new DeclarationLanguageAdapter("typescript_javascript", "typescript", [".ts", ".tsx"], ["class", "interface", "enum", "type"]),
        new DeclarationLanguageAdapter("typescript_javascript", "javascript", [".js", ".jsx", ".mjs", ".cjs"], ["class"]),
    ];

    /// <summary>获取按 adapter ID 和语言排序的内置适配器。</summary>
    public static IReadOnlyList<ILanguageAdapter> All { get; } = _all;

    /// <summary>
    /// 查找指定语言的适配器。
    /// </summary>
    /// <param name="language">规范化语言标识符。</param>
    /// <returns>匹配适配器；没有时为空。</returns>
    public static ILanguageAdapter? Find(string language) =>
        _all.SingleOrDefault(adapter => string.Equals(adapter.Capability.Language, language, StringComparison.Ordinal));

    private sealed class DeclarationLanguageAdapter : ILanguageAdapter
    {
        private const string _version = "1.0.0";
        private readonly HashSet<string> _typeKeywords;

        internal DeclarationLanguageAdapter(
            string family,
            string language,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string> typeKeywords)
        {
            _typeKeywords = new HashSet<string>(typeKeywords, StringComparer.Ordinal);
            Capability = new LanguageCapability
            {
                AdapterId = "couplet.lexical." + language,
                AdapterVersion = _version,
                Family = family,
                Language = language,
                Extensions = extensions,
                Tier = SemanticTier.Partial,
            };
        }

        public LanguageCapability Capability { get; }

        public IndexedFile Parse(LanguageParseRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            string fileId = StableId.CreateFile(request.WorkspaceId, request.Path);
            IReadOnlyList<LexicalToken> tokens = LexicalTokenizer.Tokenize(request.Content);
            var positions = new Utf8PositionMap(request.Path, request.Content);
            string namespaceName = Capability.Language == "csharp" ? FindNamespace(tokens) : string.Empty;
            Dictionary<int, int> bracePairs = Pair(tokens, "{", "}");
            Dictionary<int, int> parenthesisPairs = Pair(tokens, "(", ")");
            List<Declaration> declarations = FindTypes(tokens, bracePairs, namespaceName);
            FindFunctionsAndMethods(tokens, bracePairs, parenthesisPairs, namespaceName, declarations);
            declarations.Sort((left, right) => left.NameToken.Start.CompareTo(right.NameToken.Start));

            var symbols = new List<IndexedSymbol>(declarations.Count);
            foreach (Declaration declaration in declarations)
            {
                string symbolId = StableId.CreateSymbol(
                    request.WorkspaceId,
                    Capability.Language,
                    declaration.QualifiedIdentity);
                string? containerId = declaration.ContainerIdentity is null
                    ? null
                    : StableId.CreateSymbol(request.WorkspaceId, Capability.Language, declaration.ContainerIdentity);
                symbols.Add(new IndexedSymbol
                {
                    Id = symbolId,
                    Kind = declaration.Kind,
                    DisplayName = declaration.NameToken.Text.TrimStart('@'),
                    QualifiedIdentity = declaration.QualifiedIdentity,
                    Signature = declaration.Signature,
                    ContainerId = containerId,
                    Language = Capability.Language,
                    Definition = positions.Span(declaration.NameToken.Start, declaration.NameToken.End),
                    Provenance = new Provenance
                    {
                        WorkspaceId = request.WorkspaceId,
                        SourceRevision = request.SourceRevision,
                        IndexRevision = request.IndexRevision,
                        AdapterId = Capability.AdapterId,
                        AdapterVersion = Capability.AdapterVersion,
                        ContentHash = request.ContentHash,
                    },
                    Confidence = new Confidence
                    {
                        Kind = ConfidenceKind.Exact,
                        Score = 1,
                        Rule = Capability.AdapterId + ".declaration.v1",
                    },
                });
            }

            List<IndexedChunk> chunks = CreateChunks(request, fileId, declarations, symbols, positions);
            return new IndexedFile
            {
                Id = fileId,
                Path = request.Path,
                ContentHash = request.ContentHash,
                Length = Encoding.UTF8.GetByteCount(request.Content),
                Language = Capability.Language,
                SemanticTier = Capability.Tier,
                AdapterId = Capability.AdapterId,
                AdapterVersion = Capability.AdapterVersion,
                Symbols = symbols,
                Chunks = chunks,
            };
        }

        private List<Declaration> FindTypes(
            IReadOnlyList<LexicalToken> tokens,
            Dictionary<int, int> bracePairs,
            string namespaceName)
        {
            var declarations = new List<Declaration>();
            for (int index = 0; index + 1 < tokens.Count; index++)
            {
                if (!_typeKeywords.Contains(tokens[index].Text))
                {
                    continue;
                }

                int nameIndex = index + 1;
                if (tokens[index].Text == "record" && tokens[nameIndex].Text is "class" or "struct")
                {
                    nameIndex++;
                }

                if (nameIndex >= tokens.Count || !IsIdentifier(tokens[nameIndex].Text))
                {
                    continue;
                }

                int openBrace = FindNext(tokens, nameIndex + 1, "{", ";");
                int endToken = openBrace >= 0 && bracePairs.TryGetValue(openBrace, out int closeBrace)
                    ? closeBrace
                    : FindNext(tokens, nameIndex + 1, ";");
                string? container = FindContainerIdentity(declarations, tokens[nameIndex].Start);
                string identity = Qualify(namespaceName, container, tokens[nameIndex].Text.TrimStart('@'));
                declarations.Add(new Declaration(
                    tokens[nameIndex],
                    CodeEntityKind.Type,
                    identity,
                    tokens[index].Text + " " + tokens[nameIndex].Text,
                    container,
                    tokens[index].Start,
                    endToken >= 0 ? tokens[endToken].End : tokens[nameIndex].End,
                    openBrace,
                    CloseBrace: openBrace >= 0 && bracePairs.TryGetValue(openBrace, out int paired) ? paired : -1));
            }

            return declarations;
        }

        private static void FindFunctionsAndMethods(
            IReadOnlyList<LexicalToken> tokens,
            Dictionary<int, int> bracePairs,
            Dictionary<int, int> parenthesisPairs,
            string namespaceName,
            List<Declaration> declarations)
        {
            for (int index = 0; index + 1 < tokens.Count; index++)
            {
                bool explicitFunction = tokens[index].Text == "function"
                    && index + 2 < tokens.Count
                    && IsIdentifier(tokens[index + 1].Text)
                    && tokens[index + 2].Text == "(";
                int nameIndex = explicitFunction ? index + 1 : index;
                int openParenthesis = nameIndex + 1;
                if (!IsIdentifier(tokens[nameIndex].Text)
                    || openParenthesis >= tokens.Count
                    || tokens[openParenthesis].Text != "("
                    || !parenthesisPairs.TryGetValue(openParenthesis, out int closeParenthesis)
                    || IsControlKeyword(tokens[nameIndex].Text))
                {
                    continue;
                }

                Declaration? container = SmallestContainingType(declarations, tokens[nameIndex].Start);
                if (!explicitFunction && container is null)
                {
                    continue;
                }

                int expectedDepth = container is null ? 0 : tokens[container.OpenBrace].BraceDepth + 1;
                if (tokens[nameIndex].BraceDepth != expectedDepth)
                {
                    continue;
                }

                int segmentStart = PreviousBoundary(tokens, nameIndex) + 1;
                if (tokens.Skip(segmentStart).Take(nameIndex - segmentStart).Any(token => token.Text == "="))
                {
                    continue;
                }

                int after = closeParenthesis + 1;
                while (after < tokens.Count && tokens[after].Text is "where" or "async")
                {
                    after++;
                }

                int bodyStart = FindNext(tokens, after, "{", ";", "=>");
                if (bodyStart < 0 || tokens[bodyStart].BraceDepth != expectedDepth)
                {
                    continue;
                }

                string parameters = NormalizeTokens(tokens, openParenthesis + 1, closeParenthesis);
                string containerIdentity = container?.QualifiedIdentity ?? namespaceName;
                string name = tokens[nameIndex].Text.TrimStart('@');
                string identity = Qualify(namespaceName, container?.QualifiedIdentity, name + "(" + parameters + ")");
                int endToken = tokens[bodyStart].Text == "{" && bracePairs.TryGetValue(bodyStart, out int closeBrace)
                    ? closeBrace
                    : FindNext(tokens, bodyStart, ";");
                string prefix = NormalizeTokens(tokens, segmentStart, nameIndex);
                string signature = (prefix.Length == 0 ? string.Empty : prefix + " ") + name + "(" + parameters + ")";
                declarations.Add(new Declaration(
                    tokens[nameIndex],
                    CodeEntityKind.Member,
                    identity,
                    signature,
                    string.IsNullOrEmpty(containerIdentity) ? null : containerIdentity,
                    tokens[Math.Max(segmentStart, explicitFunction ? index : segmentStart)].Start,
                    endToken >= 0 ? tokens[endToken].End : tokens[closeParenthesis].End,
                    bodyStart,
                    endToken));
                index = nameIndex;
            }
        }

        private static List<IndexedChunk> CreateChunks(
            LanguageParseRequest request,
            string fileId,
            IReadOnlyList<Declaration> declarations,
            List<IndexedSymbol> symbols,
            Utf8PositionMap positions)
        {
            var chunks = new List<IndexedChunk>();
            if (declarations.Count == 0)
            {
                AddChunk(request, fileId, chunks, positions, 0, request.Content.Length, null);
                return chunks;
            }

            for (int index = 0; index < declarations.Count; index++)
            {
                Declaration declaration = declarations[index];
                AddChunk(
                    request,
                    fileId,
                    chunks,
                    positions,
                    declaration.ChunkStart,
                    declaration.ChunkEnd,
                    symbols[index].Id);
            }

            return chunks;
        }

        private static void AddChunk(
            LanguageParseRequest request,
            string fileId,
            List<IndexedChunk> chunks,
            Utf8PositionMap positions,
            int start,
            int end,
            string? symbolId)
        {
            if (end <= start)
            {
                return;
            }

            string content = request.Content[start..end];
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            int ordinal = chunks.Count;
            chunks.Add(new IndexedChunk
            {
                Id = StableId.CreateChunk(fileId, hash, ordinal),
                FileId = fileId,
                Ordinal = ordinal,
                ContentHash = hash,
                Content = content,
                Span = positions.Span(start, end),
                SymbolId = symbolId,
            });
        }

        private static string FindNamespace(IReadOnlyList<LexicalToken> tokens)
        {
            for (int index = 0; index + 1 < tokens.Count; index++)
            {
                if (tokens[index].Text != "namespace")
                {
                    continue;
                }

                var parts = new List<string>();
                for (int cursor = index + 1; cursor < tokens.Count && tokens[cursor].Text is not "{" and not ";"; cursor++)
                {
                    if (IsIdentifier(tokens[cursor].Text))
                    {
                        parts.Add(tokens[cursor].Text.TrimStart('@'));
                    }
                }

                return string.Join('.', parts);
            }

            return string.Empty;
        }

        private static Dictionary<int, int> Pair(IReadOnlyList<LexicalToken> tokens, string open, string close)
        {
            var result = new Dictionary<int, int>();
            var pending = new Stack<int>();
            for (int index = 0; index < tokens.Count; index++)
            {
                if (tokens[index].Text == open)
                {
                    pending.Push(index);
                }
                else if (tokens[index].Text == close && pending.TryPop(out int start))
                {
                    result[start] = index;
                }
            }

            return result;
        }

        private static int FindNext(IReadOnlyList<LexicalToken> tokens, int start, params string[] values)
        {
            for (int index = start; index < tokens.Count; index++)
            {
                if (values.Contains(tokens[index].Text, StringComparer.Ordinal))
                {
                    return index;
                }
            }

            return -1;
        }

        private static int PreviousBoundary(IReadOnlyList<LexicalToken> tokens, int index)
        {
            int depth = tokens[index].BraceDepth;
            for (int cursor = index - 1; cursor >= 0; cursor--)
            {
                if (tokens[cursor].BraceDepth < depth || (tokens[cursor].BraceDepth == depth && tokens[cursor].Text is ";" or "{" or "}"))
                {
                    return cursor;
                }
            }

            return -1;
        }

        private static string NormalizeTokens(IReadOnlyList<LexicalToken> tokens, int start, int end)
        {
            var builder = new StringBuilder();
            for (int index = start; index < end; index++)
            {
                string token = tokens[index].Text;
                if (builder.Length > 0 && IsIdentifier(token) && IsIdentifier(tokens[index - 1].Text))
                {
                    builder.Append(' ');
                }

                builder.Append(token);
            }

            return builder.ToString();
        }

        private static Declaration? SmallestContainingType(IReadOnlyList<Declaration> declarations, int position) =>
            declarations
                .Where(declaration => declaration.Kind == CodeEntityKind.Type
                    && declaration.ChunkStart <= position
                    && declaration.ChunkEnd >= position
                    && declaration.OpenBrace >= 0)
                .OrderBy(declaration => declaration.ChunkEnd - declaration.ChunkStart)
                .FirstOrDefault();

        private static string? FindContainerIdentity(IReadOnlyList<Declaration> declarations, int position) =>
            SmallestContainingType(declarations, position)?.QualifiedIdentity;

        private static string Qualify(string namespaceName, string? container, string name)
        {
            string prefix = !string.IsNullOrEmpty(container) ? container : namespaceName;
            return string.IsNullOrEmpty(prefix) ? name : prefix + "." + name;
        }

        private static bool IsIdentifier(string value)
        {
            string normalized = value.TrimStart('@');
            return normalized.Length > 0
                && (char.IsLetter(normalized[0]) || normalized[0] is '_' or '$');
        }

        private static bool IsControlKeyword(string value) => value is
            "if" or "for" or "foreach" or "while" or "switch" or "catch" or "using" or "lock"
            or "return" or "new" or "typeof" or "nameof" or "sizeof" or "checked" or "unchecked";

        private sealed record Declaration(
            LexicalToken NameToken,
            CodeEntityKind Kind,
            string QualifiedIdentity,
            string Signature,
            string? ContainerIdentity,
            int ChunkStart,
            int ChunkEnd,
            int OpenBrace,
            int CloseBrace);
    }
}
