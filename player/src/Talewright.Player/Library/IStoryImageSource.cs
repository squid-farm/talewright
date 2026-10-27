namespace Talewright.Player.Library;

/// <summary>
/// Opens one catalogued image from a story-owned backing store. Archive-backed implementations must enforce the
/// archive policy during actual reads and must not charge repeated reads of the same archive member as new content.
/// The instance owns its backing resources until <see cref="IAsyncDisposable.DisposeAsync"/> completes.
/// </summary>
public interface IStoryImageSource : IAsyncDisposable
{
    ValueTask<Stream> OpenAsync(string path, CancellationToken cancellationToken);
}
