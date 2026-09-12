using System.IO;

namespace HarnessSpy.Core.Services;

public sealed class DebouncedFileSystemWatcher : IDisposable
{
    private readonly IReadOnlyList<string> _roots;
    private readonly Action<IReadOnlyCollection<string>> _onChanged;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly HashSet<string> _pendingPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private Timer? _timer;
    private bool _disposed;

    public DebouncedFileSystemWatcher(
        IEnumerable<string> roots,
        Action<IReadOnlyCollection<string>> onChanged,
        TimeSpan? debounce = null)
    {
        _roots = roots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _onChanged = onChanged;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(450);
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_watchers.Count > 0)
        {
            return;
        }

        _timer = new Timer(Flush);
        foreach (string root in _roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                FileSystemWatcher watcher = new(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter =
                        NotifyFilters.FileName |
                        NotifyFilters.DirectoryName |
                        NotifyFilters.LastWrite |
                        NotifyFilters.Size |
                        NotifyFilters.CreationTime,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = true
                };
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                _watchers.Add(watcher);
            }
            catch (ArgumentException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (FileSystemWatcher watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
        _timer?.Dispose();
        _timer = null;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        Queue(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Queue(e.OldFullPath);
        Queue(e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        if (sender is FileSystemWatcher watcher)
        {
            Queue(watcher.Path);
        }
    }

    private void Queue(string path)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingPaths.Add(path);
            _timer?.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush(object? state)
    {
        string[] changed;
        lock (_gate)
        {
            if (_disposed || _pendingPaths.Count == 0)
            {
                return;
            }

            changed = [.. _pendingPaths];
            _pendingPaths.Clear();
        }

        _onChanged(changed);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
