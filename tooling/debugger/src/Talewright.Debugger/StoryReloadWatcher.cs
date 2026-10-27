using Talewright.Talemark.Compilation;

namespace Talewright.Debugger;

/// <summary>
/// Compiles complete bounded source snapshots off the UI thread. Only the latest generation can
/// adopt, on the supplied UI dispatcher; play and source adoption therefore share one serial lane.
/// </summary>
public sealed class StoryReloadWatcher : IAsyncDisposable
{
    private readonly WorkbenchModel _model;
    private readonly Func<Action, Task> _dispatch;
    private readonly Action _changed;
    private readonly Func<string, SourceCompilation> _compile;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _compilationLane = new(1);
    private readonly HashSet<Task> _pending = [];
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounce;
    private long _generation;
    private bool _yamlDirty;
    private bool _disposed;

    public StoryReloadWatcher(WorkbenchModel model, Func<Action, Task> dispatch, Action changed,
        Func<string, SourceCompilation>? compile = null)
    {
        _model = model;
        _dispatch = dispatch;
        _changed = changed;
        _compile = compile ?? SourceCompilation.Load;
    }

    public Task RescanAsync(bool restart = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ++_generation;
            _debounce?.Cancel();
            try
            {
                _watcher?.Dispose();
                _watcher = null;
                _watcher = new FileSystemWatcher(_model.Root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                _watcher.Changed += OnChange;
                _watcher.Created += OnChange;
                _watcher.Deleted += OnChange;
                _watcher.Renamed += OnRename;
                _watcher.Error += OnError;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                _yamlDirty = true;
                return Track(ReportErrorAsync(ex.Message, _generation));
            }
            return Queue(restart, debounce: false);
        }
    }

    private void OnChange(object sender, FileSystemEventArgs args)
    {
        if (args.ChangeType != WatcherChangeTypes.Changed
            || StorySource.IsStoryFile(args.FullPath)
            || Path.GetRelativePath(_model.Root, args.FullPath).Replace('\\', '/')
                .StartsWith("images/", StringComparison.Ordinal))
        {
            Notify(sender, args.FullPath);
        }
    }
    private void OnRename(object sender, RenamedEventArgs args)
    {
        Notify(sender, args.OldFullPath);
        Notify(sender, args.FullPath);
    }

    private void Notify(object sender, string path)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _watcher))
            {
                return;
            }
            if (StorySource.IsConfigurationFile(path))
            {
                _yamlDirty = true;
            }
            Queue(restart: false, debounce: true);
        }
    }

    private void OnError(object sender, ErrorEventArgs args)
    {
        lock (_gate)
        {
            if (!_disposed && ReferenceEquals(sender, _watcher))
            {
                ++_generation;
                _debounce?.Cancel();
                _yamlDirty = true;
                _watcher!.EnableRaisingEvents = false;
                Track(ReportErrorAsync(args.GetException().Message, _generation));
            }
        }
    }

    private Task ReportErrorAsync(string message, long generation) => _dispatch(() =>
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                return;
            }
            _model.WatchFailed(message);
            _changed();
        }
    });

    private Task Queue(bool restart, bool debounce)
    {
        var generation = ++_generation;
        _debounce?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _debounce = cancellation;
        return Track(RunAsync(generation, restart, debounce, cancellation));
    }

    private Task Track(Task task)
    {
        _pending.Add(task);
        _ = task.ContinueWith(completed =>
        {
            if (completed.IsFaulted)
            {
                Console.Error.WriteLine($"Author source reload failed unexpectedly: {completed.Exception}");
            }
            lock (_gate)
            {
                _pending.Remove(completed);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private async Task RunAsync(long generation, bool restart, bool debounce, CancellationTokenSource cancellation)
    {
        // Queue holds the watcher gate; never enter the UI dispatcher synchronously under that lock.
        await Task.Yield();
        try
        {
            await _dispatch(() =>
            {
                lock (_gate)
                {
                    if (_disposed || generation != _generation)
                    {
                        return;
                    }
                    _model.SourceChanged();
                    _changed();
                }
            });
            if (debounce)
            {
                await Task.Delay(250, cancellation.Token);
            }
            SourceCompilation candidate;
            await _compilationLane.WaitAsync(cancellation.Token);
            try
            {
                candidate = await Task.Run(() => _compile(_model.Root), cancellation.Token);
            }
            finally
            {
                _compilationLane.Release();
            }
            cancellation.Token.ThrowIfCancellationRequested();
            await _dispatch(() =>
            {
                bool yamlDirty;
                lock (_gate)
                {
                    if (_disposed || generation != _generation)
                    {
                        return;
                    }
                    yamlDirty = _yamlDirty;
                }
                var adopted = _model.ApplySource(candidate, restart, yamlDirty, action =>
                {
                    lock (_gate)
                    {
                        if (_disposed || generation != _generation)
                        {
                            return false;
                        }
                        action();
                        return true;
                    }
                });
                lock (_gate)
                {
                    if (_disposed || generation != _generation)
                    {
                        return;
                    }
                    if (adopted && restart)
                    {
                        _yamlDirty = false;
                    }
                    _changed();
                }
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Superseded generations and shutdown must not publish status or candidates.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_debounce, cancellation))
                {
                    _debounce = null;
                }
                cancellation.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            ++_generation;
            _watcher?.Dispose();
            _shutdown.Cancel();
            pending = _pending.ToArray();
        }
        try
        {
            await Task.WhenAll(pending);
        }
        finally
        {
            _compilationLane.Dispose();
            _shutdown.Dispose();
        }
    }
}
