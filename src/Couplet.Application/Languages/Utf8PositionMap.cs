using System.Buffers;
using System.Text;
using Couplet.Core.Graph;

namespace Couplet.Application.Languages;

internal sealed class Utf8PositionMap
{
    private readonly long[] _bytes;
    private readonly int[] _lines;
    private readonly int[] _columns;
    private readonly string _path;

    internal Utf8PositionMap(string path, string content)
    {
        _path = path;
        _bytes = new long[content.Length + 1];
        _lines = new int[content.Length + 1];
        _columns = new int[content.Length + 1];

        long bytes = 0;
        int line = 1;
        int column = 1;
        int index = 0;
        while (index < content.Length)
        {
            _bytes[index] = bytes;
            _lines[index] = line;
            _columns[index] = column;

            OperationStatus status = Rune.DecodeFromUtf16(content.AsSpan(index), out Rune rune, out int consumed);
            if (status != OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                consumed = 1;
            }

            for (int offset = 1; offset < consumed; offset++)
            {
                _bytes[index + offset] = bytes;
                _lines[index + offset] = line;
                _columns[index + offset] = column;
            }

            bytes += rune.Utf8SequenceLength;
            if (rune.Value == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column += rune.Utf8SequenceLength;
            }

            index += consumed;
        }

        _bytes[content.Length] = bytes;
        _lines[content.Length] = line;
        _columns[content.Length] = column;
    }

    internal SourceSpan Span(int start, int end) => new()
    {
        Path = _path,
        StartLine = _lines[start],
        StartColumn = _columns[start],
        StartByte = _bytes[start],
        EndLine = _lines[end],
        EndColumn = _columns[end],
        EndByte = _bytes[end],
    };
}
