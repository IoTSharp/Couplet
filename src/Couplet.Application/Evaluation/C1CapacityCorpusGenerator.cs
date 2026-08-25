using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Couplet.Core.Evaluation;

namespace Couplet.Application.Evaluation;

/// <summary>
/// 生成符号密度贴近 C1 门禁下限的确定性双语言容量语料。
/// </summary>
public static class C1CapacityCorpusGenerator
{
    private const int _linesPerFile = 1_000;

    /// <summary>
    /// 生成一个 Medium 或 Large C1 容量语料；目标目录必须不存在或为空。
    /// </summary>
    /// <param name="scale">固定语料档位。</param>
    /// <param name="generatorVersion">生成器版本。</param>
    /// <param name="outputDirectory">输出目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文件、LOC、声明符号和内容 hash。</returns>
    public static async Task<C1CorpusGenerationReport> GenerateAsync(
        CorpusScaleDefinition scale,
        string generatorVersion,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scale);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatorVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (scale.TargetLinesOfCode <= 0 || scale.MinimumSymbols <= 0 || scale.Languages.Count == 0)
        {
            throw new ArgumentException("C1 capacity scale must define positive lines, symbols, and languages.", nameof(scale));
        }

        string root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new IOException("C1 capacity output directory must be empty.");
        }

        Directory.CreateDirectory(root);
        long totalLines = 0;
        long totalSymbols = 0;
        long totalFiles = 0;
        for (int languageIndex = 0; languageIndex < scale.Languages.Count; languageIndex++)
        {
            LanguageShare language = scale.Languages[languageIndex];
            long languageLines = languageIndex == scale.Languages.Count - 1
                ? scale.TargetLinesOfCode - totalLines
                : (long)Math.Floor(scale.TargetLinesOfCode * language.Share);
            long languageSymbols = languageIndex == scale.Languages.Count - 1
                ? scale.MinimumSymbols - totalSymbols
                : (long)Math.Floor(scale.MinimumSymbols * language.Share);
            (long files, long lines, long symbols) = await GenerateLanguageAsync(
                root,
                language.Language,
                languageLines,
                languageSymbols,
                scale.Seed,
                cancellationToken).ConfigureAwait(false);
            totalFiles += files;
            totalLines += lines;
            totalSymbols += symbols;
        }

        return new C1CorpusGenerationReport
        {
            Scale = scale.Id,
            GeneratorVersion = generatorVersion,
            Files = totalFiles,
            LinesOfCode = totalLines,
            DeclaredSymbols = totalSymbols,
            CorpusHash = HashCorpus(root, cancellationToken),
        };
    }

    private static async Task<(long Files, long Lines, long Symbols)> GenerateLanguageAsync(
        string root,
        string language,
        long targetLines,
        long targetSymbols,
        int seed,
        CancellationToken cancellationToken)
    {
        string extension = language switch
        {
            "csharp" => ".cs",
            "typescript" => ".ts",
            "javascript" => ".js",
            _ => throw new ArgumentException("C1 capacity corpus only supports C# and TypeScript/JavaScript.", nameof(language)),
        };
        long fileCount = (targetLines + _linesPerFile - 1) / _linesPerFile;
        if (targetSymbols < fileCount || targetSymbols > targetLines - fileCount)
        {
            throw new ArgumentException("C1 capacity symbol density cannot fit the requested line count.", nameof(targetSymbols));
        }

        string directory = Path.Combine(root, language);
        Directory.CreateDirectory(directory);
        long remainingLines = targetLines;
        long remainingSymbols = targetSymbols;
        for (long fileIndex = 0; fileIndex < fileCount; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long remainingFiles = fileCount - fileIndex;
            int fileLines = checked((int)Math.Min(_linesPerFile, remainingLines));
            int fileSymbols = checked((int)(remainingSymbols / remainingFiles));
            if (remainingSymbols % remainingFiles != 0)
            {
                fileSymbols++;
            }

            if (fileSymbols > fileLines - 1)
            {
                throw new InvalidOperationException("C1 capacity symbol distribution exceeded file line capacity.");
            }

            string path = Path.Combine(directory, $"unit-{fileIndex:D6}{extension}");
            string content = CreateFile(language, fileIndex, fileLines, fileSymbols, seed);
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            remainingLines -= fileLines;
            remainingSymbols -= fileSymbols;
        }

        return (fileCount, targetLines, targetSymbols);
    }

    private static string CreateFile(string language, long fileIndex, int lines, int symbols, int seed)
    {
        var content = new StringBuilder(checked(lines * 72));
        string typeName = $"Unit{fileIndex:D6}";
        content.AppendLine(language switch
        {
            "csharp" => $"namespace CoupletCapacity.Generated; public static class {typeName} {{",
            _ => $"export class {typeName} {{",
        });

        int methods = symbols - 1;
        int bodyLines = lines - 2;
        for (int line = 0; line < bodyLines; line++)
        {
            if (line < methods)
            {
                content.AppendLine(language switch
                {
                    "csharp" => $"public static int Method{line:D4}(int value) {{ return value + {seed.ToString(CultureInfo.InvariantCulture)} + {line}; }}",
                    "typescript" => $"static method{line:D4}(value: number): number {{ return value + {seed} + {line}; }}",
                    _ => $"static method{line:D4}(value) {{ return value + {seed} + {line}; }}",
                });
            }
            else
            {
                content.Append("// capacity filler ").Append(seed).Append(':').Append(fileIndex).Append(':').AppendLine(line.ToString(CultureInfo.InvariantCulture));
            }
        }

        content.AppendLine("}");
        return content.ToString();
    }

    private static string HashCorpus(string root, CancellationToken cancellationToken)
    {
        using IncrementalHash aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
            aggregate.AppendData(Encoding.UTF8.GetBytes(relativePath));
            aggregate.AppendData([0]);
            using FileStream stream = File.OpenRead(path);
            aggregate.AppendData(SHA256.HashData(stream));
        }

        return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
    }
}
