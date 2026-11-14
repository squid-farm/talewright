using Microsoft.JSInterop;
using Talewright.Core.Identity;
using Talewright.Talemark.Model;

namespace Talewright.Player.Library;

public sealed class StoryImageWarmer : IAsyncDisposable
{
    internal const int MaxConcurrentTransfers = 3;
    internal static readonly TimeSpan HistorySettlingDelay = TimeSpan.FromMilliseconds(500);

    private sealed class Plan(IReadOnlyList<SceneImageGroup> groups)
    {
        private readonly int[] _positions = new int[groups.Count];
        private int _nextScene;

        public string? NextPath(Func<string, bool> alreadyOwned)
        {
            if (groups.Count == 0) return null;
            var remainingScenes = groups.Count;
            while (remainingScenes-- > 0)
            {
                var scene = _nextScene;
                _nextScene = (_nextScene + 1) % groups.Count;
                var paths = groups[scene].Paths;
                while (_positions[scene] < paths.Count)
                {
                    var path = paths[_positions[scene]++];
                    if (!alreadyOwned(path)) return path;
                }
            }
            return null;
        }
    }

    private sealed class Entry(string path, StoryImages images, StoryImages.Lease lease)
    {
        public string Path { get; } = path;
        public StoryImages Images { get; } = images;
        public StoryImages.Lease Lease { get; } = lease;
        public Task Transfer { get; set; } = Task.CompletedTask;
        public bool Released { get; set; }
    }

    private readonly object _gate = new();
    private readonly IJSRuntime _js;
    private readonly int _maxConcurrentTransfers;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Dictionary<string, Entry> _owned = new(StringComparer.Ordinal);
    private readonly HashSet<Entry> _running = [];
    private readonly HashSet<Task> _releases = [];
    private readonly HashSet<Task> _delays = [];
    private HashSet<Entry> _transition = [];
    private HashSet<string> _desiredPaths = new(StringComparer.Ordinal);
    private object? _owner;
    private StoryDefinition? _story;
    private StoryImages? _images;
    private StoryDefinition? _mapStory;
    private StoryMap? _storyMap;
    private Plan? _plan;
    private CancellationTokenSource? _delayCancellation;
    private bool _startsAllowed;
    private bool _disposed;

    internal StoryImageWarmer(IJSRuntime js, int maxConcurrentTransfers = MaxConcurrentTransfers,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentTransfers);
        _js = js;
        _maxConcurrentTransfers = maxConcurrentTransfers;
        _delay = delay ?? Task.Delay;
    }

    internal bool Navigate(object owner, StoryDefinition story, StoryImages? images, SceneId? current, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(story);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        IReadOnlyList<SceneImageGroup> groups = images is null
            ? Array.Empty<SceneImageGroup>()
            : StoryImageDiscovery.ConnectedScenes(story, current, GetStoryMap(story));
        var desired = groups.SelectMany(group => group.Paths).ToHashSet(StringComparer.Ordinal);
        var arriving = images is null || current is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : StoryImageDiscovery.SceneImages(story, current.Value).ToHashSet(StringComparer.Ordinal);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var sameGeneration = ReferenceEquals(_story, story) && ReferenceEquals(_images, images);
            var transition = sameGeneration
                ? _owned.Values.Where(entry => arriving.Contains(entry.Path)).ToHashSet()
                : [];
            var retained = transition.Concat(_owned.Values.Where(entry => desired.Contains(entry.Path))).ToHashSet();
            foreach (var entry in _owned.Values.ToArray())
            {
                if (!retained.Contains(entry)) ReleaseLocked(entry);
            }

            _owner = owner;
            _story = story;
            _images = images;
            _plan = new Plan(groups);
            _desiredPaths = desired;
            _transition = transition;
            ReplaceDelayLocked(delay);
            if (_startsAllowed) PumpLocked();
            return transition.Count > 0;
        }
    }

    internal void CompleteHandoff()
    {
        lock (_gate)
        {
            foreach (var entry in _transition)
            {
                if (!_desiredPaths.Contains(entry.Path)) ReleaseLocked(entry);
            }
            _transition.Clear();
        }
    }

    internal void Clear(object? owner = null)
    {
        lock (_gate)
        {
            if (_disposed || owner is not null && !ReferenceEquals(_owner, owner)) return;
            _owner = null;
            _story = null;
            _images = null;
            _mapStory = null;
            _storyMap = null;
            _plan = null;
            _desiredPaths.Clear();
            _transition.Clear();
            ReplaceDelayLocked(TimeSpan.Zero);
            foreach (var entry in _owned.Values.ToArray()) ReleaseLocked(entry);
            _owned.Clear();
        }
    }

    private void ReplaceDelayLocked(TimeSpan delay)
    {
        _delayCancellation?.Cancel();
        _delayCancellation = null;
        _startsAllowed = delay == TimeSpan.Zero;
        if (_startsAllowed) return;

        var cancellation = new CancellationTokenSource();
        _delayCancellation = cancellation;
        Task? task = null;
        task = StartAfterDelayAsync(delay, cancellation, () => task!);
        _delays.Add(task);
    }

    private StoryMap GetStoryMap(StoryDefinition story)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(_mapStory, story)) return _storyMap!;
            var map = StoryMap.Build(story);
            _mapStory = story;
            return _storyMap = map;
        }
    }

    private async Task StartAfterDelayAsync(TimeSpan delay, CancellationTokenSource cancellation, Func<Task> getTask)
    {
        await Task.Yield();
        try
        {
            await _delay(delay, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Image warming delay failed: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_delayCancellation, cancellation))
                {
                    _delayCancellation = null;
                    if (!_disposed)
                    {
                        _startsAllowed = true;
                        PumpLocked();
                    }
                }
                _delays.Remove(getTask());
            }
            cancellation.Dispose();
        }
    }

    private void PumpLocked()
    {
        if (_disposed || !_startsAllowed || _plan is null || _images is null) return;
        while (_running.Count < _maxConcurrentTransfers &&
               _plan.NextPath(_owned.ContainsKey) is { } path)
        {
            Entry entry;
            try
            {
                entry = new Entry(path, _images, _images.Acquire(path, _js));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Image warming could not acquire '{path}': {ex.Message}");
                continue;
            }

            _owned.Add(path, entry);
            _running.Add(entry);
            entry.Transfer = CompleteTransferAsync(entry);
        }
    }

    private async Task CompleteTransferAsync(Entry entry)
    {
        await Task.Yield();
        var failed = false;
        try
        {
            await entry.Lease.Url;
        }
        catch (Exception ex)
        {
            failed = true;
            Console.Error.WriteLine($"Image warming failed for '{entry.Path}': {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(entry);
                if (failed) ReleaseLocked(entry);
                PumpLocked();
            }
        }
    }

    private void ReleaseLocked(Entry entry)
    {
        if (_owned.TryGetValue(entry.Path, out var current) && ReferenceEquals(current, entry))
            _owned.Remove(entry.Path);
        if (entry.Released) return;
        entry.Released = true;
        var release = entry.Lease.DisposeAsync().AsTask();
        _releases.Add(release);
        _ = ObserveReleaseAsync(release, entry.Path);
    }

    private async Task ObserveReleaseAsync(Task release, string path)
    {
        try
        {
            await release.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Image warming cleanup failed for '{path}': {ex.Message}");
        }
        finally
        {
            lock (_gate) _releases.Remove(release);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                _delayCancellation?.Cancel();
                _delayCancellation = null;
                _owner = null;
                _story = null;
                _images = null;
                _mapStory = null;
                _storyMap = null;
                _plan = null;
                _desiredPaths.Clear();
                _transition.Clear();
                foreach (var entry in _owned.Values.ToArray()) ReleaseLocked(entry);
                _owned.Clear();
            }
        }

        while (true)
        {
            Task[] pending;
            lock (_gate)
            {
                pending = _running.Select(entry => entry.Transfer)
                    .Concat(_releases)
                    .Concat(_delays)
                    .ToArray();
            }
            if (pending.Length == 0) return;
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Image warming shutdown failed: {ex.Message}");
            }
        }
    }
}
