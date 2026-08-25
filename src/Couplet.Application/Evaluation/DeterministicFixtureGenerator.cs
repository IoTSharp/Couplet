using System.Globalization;
using System.Text;
using Couplet.Core.Evaluation;

namespace Couplet.Application.Evaluation;

/// <summary>
/// 以流式方式生成可扩展到 Small、Medium、Large 的确定性多语言语料。
/// </summary>
public static class DeterministicFixtureGenerator
{
    private const int _linesPerFile = 500;

    /// <summary>
    /// 生成一个 fixture 档位；目标目录必须不存在或为空。
    /// </summary>
    /// <param name="scale">档位定义。</param>
    /// <param name="outputDirectory">输出目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际生成计数。</returns>
    public static async Task<FixtureGenerationReport> GenerateAsync(
        CorpusScaleDefinition scale,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scale);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        string fullOutput = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(fullOutput) && Directory.EnumerateFileSystemEntries(fullOutput).Any())
        {
            throw new IOException("Fixture output directory must be empty.");
        }

        Directory.CreateDirectory(fullOutput);
        long totalLines = 0;
        long totalSymbols = 0;
        long totalFiles = 0;

        for (int languageIndex = 0; languageIndex < scale.Languages.Count; languageIndex++)
        {
            LanguageShare language = scale.Languages[languageIndex];
            long languageLines = languageIndex == scale.Languages.Count - 1
                ? scale.TargetLinesOfCode - totalLines
                : (long)Math.Floor(scale.TargetLinesOfCode * language.Share);
            (long files, long lines, long symbols) = await GenerateLanguageAsync(
                fullOutput,
                language,
                languageLines,
                scale.Seed,
                cancellationToken).ConfigureAwait(false);
            totalFiles += files;
            totalLines += lines;
            totalSymbols += symbols;
        }

        return new FixtureGenerationReport
        {
            Scale = scale.Id,
            Files = totalFiles,
            LinesOfCode = totalLines,
            Symbols = totalSymbols,
            Relations = checked(totalSymbols * 3),
        };
    }

    private static async Task<(long Files, long Lines, long Symbols)> GenerateLanguageAsync(
        string root,
        LanguageShare language,
        long targetLines,
        int seed,
        CancellationToken cancellationToken)
    {
        string extension = language.Language switch
        {
            "csharp" => ".cs",
            "typescript" => ".ts",
            "javascript" => ".js",
            _ => ".txt",
        };
        string directory = Path.Combine(root, language.Language);
        Directory.CreateDirectory(directory);
        long remaining = targetLines;
        long files = 0;
        long symbols = 0;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int fileLines = (int)Math.Min(_linesPerFile, remaining);
            string path = Path.Combine(directory, $"unit-{files:D6}{extension}");
            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            long fileSymbols = await WriteFileAsync(
                writer,
                language.Language,
                files,
                fileLines,
                seed,
                cancellationToken).ConfigureAwait(false);
            symbols += fileSymbols;
            files++;
            remaining -= fileLines;
        }

        return (files, targetLines, symbols);
    }

    private static async Task<long> WriteFileAsync(
        StreamWriter writer,
        string language,
        long fileIndex,
        int lines,
        int seed,
        CancellationToken cancellationToken)
    {
        if (lines == 1)
        {
            await writer.WriteLineAsync($"// fixture {seed}:{fileIndex}").ConfigureAwait(false);
            return 0;
        }

        string header = language switch
        {
            "csharp" => $"namespace CoupletFixture.Generated; public static class Unit{fileIndex:D6} {{",
            "typescript" => $"export class Unit{fileIndex:D6} {{",
            "javascript" => $"export class Unit{fileIndex:D6} {{",
            _ => $"fixture unit {fileIndex:D6}",
        };
        await writer.WriteLineAsync(header).ConfigureAwait(false);
        long symbols = 0;
        for (int line = 1; line < lines - 1; line++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string body = language switch
            {
                "csharp" => $"public static int Method{line:D4}(int value) => value + {seed.ToString(CultureInfo.InvariantCulture)} + {line};",
                "typescript" => $"static method{line:D4}(value: number): number {{ return value + {seed} + {line}; }}",
                "javascript" => $"static method{line:D4}(value) {{ return value + {seed} + {line}; }}",
                _ => $"symbol {line:D4} depends-on {Math.Max(0, line - 1):D4}",
            };
            await writer.WriteLineAsync(body).ConfigureAwait(false);
            symbols++;
        }

        await writer.WriteLineAsync(language is "csharp" or "typescript" or "javascript" ? "}" : "end").ConfigureAwait(false);
        return symbols + 1;
    }
}
