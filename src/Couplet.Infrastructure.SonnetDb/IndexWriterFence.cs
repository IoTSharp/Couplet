using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Couplet.Infrastructure.SonnetDb;

internal sealed class IndexWriterFence : IDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _processFences =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _processFence;
    private bool _disposed;

    private IndexWriterFence(FileStream stream, SemaphoreSlim processFence)
    {
        _stream = stream;
        _processFence = processFence;
    }

    internal static async Task<IndexWriterFence> AcquireAsync(
        string databaseRoot,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        cancellationToken.ThrowIfCancellationRequested();

        string root = Path.GetFullPath(databaseRoot);
        Directory.CreateDirectory(root);
        string digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(workspaceId))).ToLowerInvariant();
        string path = Path.Combine(root, $".couplet-index-writer-{digest[..24]}.lock");
        SemaphoreSlim processFence = _processFences.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await processFence.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(
                        path,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.Asynchronous);
                    return new IndexWriterFence(stream, processFence);
                }
                catch (IOException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            processFence.Release();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _stream.Dispose();
        }
        finally
        {
            _processFence.Release();
            _disposed = true;
        }
    }
}
