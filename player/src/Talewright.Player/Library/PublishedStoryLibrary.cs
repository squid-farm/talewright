using System.Text.Json;
using Talewright.Talemark.Compilation;

namespace Talewright.Player.Library;

/// <summary>Loads a published static story index and text; images are requested only when displayed.</summary>
/// <param name="client">Host-owned client whose base address is the published story directory.</param>
public sealed class PublishedStoryLibrary(HttpClient client) : IStoryLibrary
{
    private sealed record Catalog(StorySource Source, IReadOnlyDictionary<string, string> Versions);
    private Task<Catalog>? _catalog;

    public Task<IReadOnlyList<StoryEntry>> ListAsync() =>
        Task.FromResult<IReadOnlyList<StoryEntry>>([new("Published", "Published", "Published")]);

    public async Task<StorySource> LoadAsync(string id)
    {
        CheckId(id);
        return (await GetCatalogAsync()).Source;
    }

    public async Task<LoadedStory> OpenAsync(string id)
    {
        CheckId(id);
        var catalog = await GetCatalogAsync();
        return new(catalog.Source, StoryImages.FromSource(catalog.Source,
            new HttpImages(client, catalog.Versions)));
    }

    private static void CheckId(string id)
    {
        if (id != "Published") throw new KeyNotFoundException($"No published story '{id}'.");
    }

    private async Task<Catalog> GetCatalogAsync()
    {
        try { return await (_catalog ??= ReadCatalogAsync()); }
        catch
        {
            _catalog = null;
            throw;
        }
    }

    private async Task<Catalog> ReadCatalogAsync()
    {
        try
        {
            // Revalidate discovery on each page load; each content URL has its own stable version.
            using var stream = await client.GetStreamAsync($"index.json?load={Guid.NewGuid():N}");
            using var document = await JsonDocument.ParseAsync(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The published story index must be an object.");
            var index = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                var path = entry.Name;
                var hash = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : null;
                if (string.IsNullOrWhiteSpace(path) || path.Any(c => c is '\\' or ':' or '?' or '#' || char.IsControl(c)) ||
                    path.Split('/').Any(part => part.Length == 0 || part is "." or "..") ||
                    hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                    throw new InvalidDataException($"Invalid published story index entry '{path}'.");
                if (!index.TryAdd(path, hash))
                    throw new InvalidDataException($"Duplicate published story index entry '{path}'.");
            }
            if (!index.ContainsKey(StorySource.ManifestFileName))
                throw new InvalidDataException("The published story index has no root manifest.");
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in index.Keys.Where(StorySource.IsStoryFile).Order(StringComparer.Ordinal))
                files.Add(path, await client.GetStringAsync(ContentUrl(path, index[path])));
            var source = new StorySource(files, index.Keys.Where(path => path.StartsWith("images/", StringComparison.Ordinal)));
            return new(new StorySource(files, StoryImageResolver.CompletionPaths(source).Select(path => "images/" + path)), index);
        }
        catch (HttpRequestException ex)
        {
            throw new IOException($"Couldn't load the published story: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex)
        {
            throw new IOException($"Loading the published story timed out: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid published story index: {ex.Message}", ex);
        }
    }

    private static string ContentUrl(string path, string hash) =>
        string.Join("/", path.Split('/').Select(Uri.EscapeDataString)) + "?v=" + hash;

    private sealed class HttpImages(HttpClient client, IReadOnlyDictionary<string, string> versions) : IStoryImageSource
    {
        public async ValueTask<Stream> OpenAsync(string path, CancellationToken cancellationToken)
        {
            try { return await client.GetStreamAsync(ContentUrl(path, versions[path]), cancellationToken); }
            catch (HttpRequestException ex)
            {
                throw new IOException($"Couldn't load published image '{path}': {ex.Message}", ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException($"Loading published image '{path}' timed out: {ex.Message}", ex);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
