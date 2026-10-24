using System.Reflection;
using Talewright.Talemark.Compilation;

namespace Talewright.Player.Library;

/// <summary>A story the player can pick from the library screen.</summary>
/// <param name="Id">Library-specific key passed back to <see cref="IStoryLibrary.LoadAsync"/>.</param>
/// <param name="Title">Display title (from story.talewright.yaml, or the id if it can't be read).</param>
/// <param name="Origin">Where it came from, e.g. "Built-in" or a file path.</param>
public sealed record StoryEntry(string Id, string Title, string Origin);

/// <summary>
/// Where stories come from. Each host registers one (or a <see cref="CompositeStoryLibrary"/>): the web host
/// uses stories embedded in its assembly, the desktop host adds folders/zips from the command line.
/// A library is only the lookup mechanism; it says nothing about packaging. Today the bundled samples are embedded
/// in each host, and a published game (planned: <c>talewright publish</c>, M2) will be a single output containing
/// the engine plus one story, served through the same interface.
/// </summary>
public interface IStoryLibrary
{
    /// <summary>Lists the available stories.</summary>
    Task<IReadOnlyList<StoryEntry>> ListAsync();

    /// <summary>Loads the raw files of a story listed by <see cref="ListAsync"/>.</summary>
    Task<StorySource> LoadAsync(string id);
}

/// <summary>
/// Stories compiled into an assembly as embedded resources named <c>{prefix}{story}/{path}</c>, e.g.
/// <c>stories/LostRelic/chapters/01-camp.tale</c>. Works everywhere, including the browser, with no HTTP.
/// </summary>
public sealed class EmbeddedStoryLibrary(Assembly assembly, string prefix = "stories/") : IStoryLibrary
{
    private Dictionary<string, Dictionary<string, string>>? _stories;

    private Dictionary<string, Dictionary<string, string>> Stories => _stories ??= Scan();

    /// <inheritdoc/>
    public Task<IReadOnlyList<StoryEntry>> ListAsync() =>
        Task.FromResult<IReadOnlyList<StoryEntry>>(Stories
            .Select(s => new StoryEntry(s.Key, new StorySource(s.Value).ReadTitle() ?? s.Key, "Built-in"))
            .OrderBy(e => e.Title, StringComparer.CurrentCulture)
            .ToList());

    /// <inheritdoc/>
    public Task<StorySource> LoadAsync(string id) =>
        Task.FromResult(Stories.TryGetValue(id, out var files)
            ? new StorySource(files)
            : throw new KeyNotFoundException($"No built-in story '{id}'."));

    private Dictionary<string, Dictionary<string, string>> Scan()
    {
        var stories = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var path = name.Replace('\\', '/');
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var slash = path.IndexOf('/', prefix.Length);
            if (slash < 0)
            {
                continue;
            }

            var story = path[prefix.Length..slash];
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            if (!stories.TryGetValue(story, out var files))
            {
                stories[story] = files = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            files[path[(slash + 1)..]] = reader.ReadToEnd();
        }

        return stories.Where(s => s.Value.ContainsKey(StorySource.ManifestFileName)).ToDictionary();
    }
}

/// <summary>Stories on disk: each path is a story folder (containing story.talewright.yaml) or a <c>.zip</c>. Desktop only.</summary>
public sealed class FileSystemStoryLibrary(IEnumerable<string> paths) : IStoryLibrary
{
    private readonly List<string> _paths = paths.Select(Path.GetFullPath).ToList();

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoryEntry>> ListAsync()
    {
        var entries = new List<StoryEntry>();
        foreach (var path in _paths)
        {
            string title;
            try
            {
                title = (await LoadAsync(path)).ReadTitle() ?? Path.GetFileNameWithoutExtension(path);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                title = $"{Path.GetFileName(path)} (unreadable: {ex.Message})";
            }

            entries.Add(new StoryEntry(path, title, path));
        }

        return entries;
    }

    /// <inheritdoc/>
    public Task<StorySource> LoadAsync(string id)
    {
        if (Directory.Exists(id))
        {
            return Task.FromResult(StorySource.FromDirectory(id));
        }

        using var stream = File.OpenRead(id);
        return Task.FromResult(StorySource.FromZip(stream));
    }
}

/// <summary>Combines several libraries into one list. Ids are prefixed with the library index.</summary>
public sealed class CompositeStoryLibrary(params IStoryLibrary[] libraries) : IStoryLibrary
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoryEntry>> ListAsync()
    {
        var all = new List<StoryEntry>();
        for (var i = 0; i < libraries.Length; i++)
        {
            all.AddRange((await libraries[i].ListAsync()).Select(e => e with { Id = $"{i}|{e.Id}" }));
        }

        return all;
    }

    /// <inheritdoc/>
    public Task<StorySource> LoadAsync(string id)
    {
        var bar = id.IndexOf('|', StringComparison.Ordinal);
        return libraries[int.Parse(id[..bar], System.Globalization.CultureInfo.InvariantCulture)].LoadAsync(id[(bar + 1)..]);
    }
}
