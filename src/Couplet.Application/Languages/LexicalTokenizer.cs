namespace Couplet.Application.Languages;

internal readonly record struct LexicalToken(string Text, int Start, int End, int BraceDepth);

internal static class LexicalTokenizer
{
    internal static IReadOnlyList<LexicalToken> Tokenize(string content)
    {
        var tokens = new List<LexicalToken>();
        int depth = 0;
        int index = 0;
        while (index < content.Length)
        {
            char current = content[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '/' && index + 1 < content.Length && content[index + 1] == '/')
            {
                index += 2;
                while (index < content.Length && content[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < content.Length && content[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < content.Length && (content[index] != '*' || content[index + 1] != '/'))
                {
                    index++;
                }

                index = Math.Min(content.Length, index + 2);
                continue;
            }

            if (current is '\'' or '"' or '`')
            {
                index = SkipQuoted(content, index, current);
                continue;
            }

            if (IsIdentifierStart(current))
            {
                int start = index++;
                while (index < content.Length && IsIdentifierPart(content[index]))
                {
                    index++;
                }

                tokens.Add(new LexicalToken(content[start..index], start, index, depth));
                continue;
            }

            if (char.IsDigit(current))
            {
                int start = index++;
                while (index < content.Length && (char.IsLetterOrDigit(content[index]) || content[index] is '.' or '_'))
                {
                    index++;
                }

                tokens.Add(new LexicalToken(content[start..index], start, index, depth));
                continue;
            }

            if (current == '}')
            {
                depth = Math.Max(0, depth - 1);
            }

            int tokenLength = index + 1 < content.Length && IsTwoCharacterOperator(current, content[index + 1]) ? 2 : 1;
            tokens.Add(new LexicalToken(content.Substring(index, tokenLength), index, index + tokenLength, depth));
            if (current == '{')
            {
                depth++;
            }

            index += tokenLength;
        }

        return tokens;
    }

    private static int SkipQuoted(string content, int start, char quote)
    {
        int index = start + 1;
        while (index < content.Length)
        {
            if (content[index] == '\\')
            {
                index = Math.Min(content.Length, index + 2);
                continue;
            }

            if (content[index] == quote)
            {
                return index + 1;
            }

            index++;
        }

        return content.Length;
    }

    private static bool IsIdentifierStart(char value) =>
        value is '_' or '$' or '@' || char.IsLetter(value);

    private static bool IsIdentifierPart(char value) =>
        value is '_' or '$' || char.IsLetterOrDigit(value);

    private static bool IsTwoCharacterOperator(char first, char second) =>
        (first, second) is ('=', '>') or (':', ':') or ('?', '.') or ('=', '=') or ('!', '=')
            or ('<', '=') or ('>', '=') or ('&', '&') or ('|', '|') or ('+', '+') or ('-', '-');
}
