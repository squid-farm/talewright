namespace Talewright.Player.Library;

/// <summary>
/// Opens one catalogued image from a story-owned backing store. Archive-backed implementations must enforce
/// declared entry sizes during actual reads without imposing a combined story/image size budget.
/// The instance owns its backing resources until <see cref="IAsyncDisposable.DisposeAsync"/> completes.
/// </summary>
public interface IStoryImageSource : IAsyncDisposable
{
    ValueTask<Stream> OpenAsync(string path, CancellationToken cancellationToken);
}
