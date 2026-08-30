using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Couplet.Core.Workspaces;

namespace Couplet.Application.Workspaces;

/// <summary>
/// 将 FileSystemWatcher 事件合并为可取消、有界且不静默丢失的变化批次。
/// </summary>
public sealed class WorkspaceChangeMonitor : IDisposable
{
    private const int QueueCapacity = 4096;
    private readonly string _rootPath;
    private readonly FileSystemWatcher _watcher;
    private readonly Channel<string> _changes;
    private int _fullRescanRequired;
    private int _disposed;

    internal bool FullRescanPending => Volatile.Read(ref _fullRescanRequired) != 0;

    /// <summary>
    /// 初始化工作区变化监视器。
    /// </summary>
    /// <param name="workspacePath">显式工作区目录。</param>
    public WorkspaceChangeMonitor(string workspacePath)
        : this(workspacePath, QueueCapacity)
    {
    }

    internal WorkspaceChangeMonitor(string workspacePath, int queueCapacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        _rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (!Directory.Exists(_rootPath))
        {
            throw new DirectoryNotFoundException("The explicitly configured workspace was not found.");
        }

        _changes = Channel.CreateBounded<string>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _watcher = new FileSystemWatcher(_rootPath)
        {
            Filter = "*",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// 持续读取经 debounce 合并的变化批次。
    /// </summary>
    /// <param name="debounce">首个事件后的固定合并窗口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步变化批次。</returns>
    public async IAsyncEnumerable<WorkspaceChangeBatch> WatchAsync(
        TimeSpan debounce,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(debounce, TimeSpan.Zero);

        while (await _changes.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            Drain(paths);
            if (debounce > TimeSpan.Zero)
            {
                await Task.Delay(debounce, cancellationToken).ConfigureAwait(false);
                Drain(paths);
            }

            bool fullRescan = Interlocked.Exchange(ref _fullRescanRequired, 0) != 0;
            yield return new WorkspaceChangeBatch
            {
                Paths = paths.ToArray(),
                RequiresFullRescan = fullRescan,
                Reason = fullRescan ? "watcher_overflow_or_error" : null,
            };
        }
    }

    /// <summary>
    /// 停止监视并释放系统 watcher。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnChanged;
        _watcher.Created -= OnChanged;
        _watcher.Deleted -= OnChanged;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
        _changes.Writer.TryComplete();
    }

    private void Drain(SortedSet<string> paths)
    {
        while (_changes.Reader.TryRead(out string? path))
        {
            if (path.Length > 0)
            {
                paths.Add(path);
            }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs eventArgs) => Queue(eventArgs.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs eventArgs)
    {
        Queue(eventArgs.OldFullPath);
        Queue(eventArgs.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs eventArgs)
    {
        RequireFullRescan();
    }

    private void Queue(string fullPath)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        string path = Path.GetRelativePath(_rootPath, fullPath).Replace('\\', '/');
        if (Path.IsPathRooted(path) || path == ".." || path.StartsWith("../", StringComparison.Ordinal))
        {
            RequireFullRescan();
            return;
        }

        if (!_changes.Writer.TryWrite(path))
        {
            RequireFullRescan();
        }
    }

    private void RequireFullRescan()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _fullRescanRequired, 1);
        _changes.Writer.TryWrite(string.Empty);
    }
}
