using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Talewright.Player.Library;
using Talewright.Runtime;
using Talewright.Talemark.Compilation;

namespace Talewright.Player.E2E.Tests;

public sealed class PublishedStoryLibraryTests
{
    [Fact]
    public async Task Split_source_is_detached_from_transport_and_images_are_fetched_lazily_at_the_subpath()
    {
        using var files = new ImageStoryFiles();
        using var server = new StaticStory(files.Root);
        using var client = server.Client();
        var library = new PublishedStoryLibrary(client);
        await using var loaded = await library.OpenAsync("Published");
        var folder = StorySource.FromDirectory(files.Root);
        Assert.Equal(folder.Files.OrderBy(file => file.Key), loaded.Source.Files.OrderBy(file => file.Key));
        Assert.Equal(folder.ImagePaths.Order(), loaded.Source.ImagePaths.Order());
        Assert.DoesNotContain(server.Requests, uri => uri.AbsolutePath.Contains("/images/", StringComparison.Ordinal));
        Assert.All(server.Requests, uri => Assert.StartsWith("/games/published/story/", uri.AbsolutePath));
        Assert.Contains(await library.ListAsync(), entry => entry.Id == "Published");
        await using var image = await loaded.Images!.OpenAsync("images/photo.jpeg");
        using var bytes = new MemoryStream();
        await image.CopyToAsync(bytes);
        Assert.Equal(await File.ReadAllBytesAsync(files.ImagePath), bytes.ToArray());
        Assert.DoesNotContain(server.Requests, uri => uri.AbsolutePath.EndsWith("pixel.webp", StringComparison.Ordinal));
        Assert.Equal(server.Versions["images/photo.jpeg"], server.Requests.Last().Query["?v=".Length..]);

        var original = StoryCompiler.Compile(folder).Story!;
        var published = StoryCompiler.Compile(loaded.Source).Story!;
        Assert.Equal(original.ContentFingerprint, published.ContentFingerprint);
        var history = new StoryHistory(original, 42);
        history.Choose("c0");
        Assert.Equal(history.Save(), StoryHistory.Restore(published, history.Save()).Save());
    }

    [Fact]
    public async Task Text_changes_keep_image_urls_stable_and_changed_images_get_new_urls()
    {
        using var files = new ImageStoryFiles();
        using var server = new StaticStory(files.Root);
        using var client = server.Client();
        async Task<Uri> OpenImage()
        {
            await using var loaded = await new PublishedStoryLibrary(client).OpenAsync("Published");
            await using var image = await loaded.Images!.OpenAsync("images/photo.jpeg");
            return server.Requests.Last();
        }
        var first = await OpenImage();
        var firstText = server.Requests.First(uri => uri.AbsolutePath.EndsWith("main.tale", StringComparison.Ordinal));
        await File.AppendAllTextAsync(files.TalePath, "\n// Text-only update\n");
        server.Refresh();
        Assert.Equal(first, await OpenImage());
        Assert.NotEqual(firstText, server.Requests.Last(uri => uri.AbsolutePath.EndsWith("main.tale", StringComparison.Ordinal)));
        await File.WriteAllBytesAsync(files.ImagePath, [1, 2, 3]);
        server.Refresh();
        Assert.NotEqual(first, await OpenImage());
    }

    [Theory]
    [InlineData("../main.tale")]
    [InlineData("/main.tale")]
    [InlineData("https://example.test/main.tale")]
    [InlineData("nested\\main.tale")]
    [InlineData("nested//main.tale")]
    [InlineData("main.tale?query")]
    [InlineData("main.tale#fragment")]
    public async Task Invalid_index_paths_are_reported_before_any_content_request(string path)
    {
        using var files = new ImageStoryFiles();
        using var server = new StaticStory(files.Root);
        server.Index = JsonSerializer.Serialize(new Dictionary<string, string> { [path] = new('A', 64) });
        using var client = server.Client();
        await Assert.ThrowsAsync<InvalidDataException>(() => new PublishedStoryLibrary(client).OpenAsync("Published"));
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("""{"story.talewright.yaml":null}""")]
    [InlineData("""{"story.talewright.yaml":"bad"}""")]
    [InlineData("""{"story.talewright.yaml":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","story.talewright.yaml":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""")]
    public async Task Invalid_indexes_fail_explicitly(string index)
    {
        using var files = new ImageStoryFiles();
        using var server = new StaticStory(files.Root) { Index = index };
        using var client = server.Client();
        await Assert.ThrowsAsync<InvalidDataException>(() => new PublishedStoryLibrary(client).OpenAsync("Published"));
    }

    [Fact]
    public async Task Missing_image_fails_independently_and_failed_discovery_can_be_retried()
    {
        using var files = new ImageStoryFiles();
        using var server = new StaticStory(files.Root);
        using var client = server.Client();
        var library = new PublishedStoryLibrary(client);
        var index = server.Index;
        server.Index = "{bad";
        await Assert.ThrowsAsync<InvalidDataException>(() => library.OpenAsync("Published"));
        server.Index = index;
        await using var loaded = await library.OpenAsync("Published");
        File.Delete(files.ImagePath);
        await Assert.ThrowsAsync<IOException>(async () => await loaded.Images!.OpenAsync("images/photo.jpeg"));
        await using var working = await loaded.Images!.OpenAsync("images/pixel.png");
        Assert.True(working.Length > 0);
        Assert.True(StoryCompiler.Compile(loaded.Source).Success);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await loaded.Images.OpenAsync("images/../pixel.png"));
    }

    [Fact]
    public async Task Text_only_story_does_not_require_images_and_missing_text_is_an_operational_error()
    {
        using var files = new ImageStoryFiles();
        File.WriteAllText(files.TalePath, "=== start\nHello.\n* [Finish] -> END");
        Directory.Delete(Path.Combine(files.Root, "images"), recursive: true);
        using var server = new StaticStory(files.Root);
        using var client = server.Client();
        await using var loaded = await new PublishedStoryLibrary(client).OpenAsync("Published");
        Assert.Empty(loaded.Source.ImagePaths);
        Assert.True(StoryCompiler.Compile(loaded.Source).Success);
        File.Delete(files.TalePath);
        await Assert.ThrowsAsync<IOException>(() => new PublishedStoryLibrary(client).OpenAsync("Published"));
    }

    private sealed class StaticStory : HttpMessageHandler
    {
        private readonly string _root;
        public List<Uri> Requests { get; } = [];
        public Dictionary<string, string> Versions { get; private set; } = [];
        public string Index { get; set; } = "";

        public StaticStory(string root) { _root = root; Refresh(); }

        public void Refresh()
        {
            Versions = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .ToDictionary(file => Path.GetRelativePath(_root, file).Replace('\\', '/'),
                    file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
            Index = JsonSerializer.Serialize(Versions);
        }

        public HttpClient Client() => new(this, disposeHandler: false)
        {
            BaseAddress = new Uri("https://example.test/games/published/story/"),
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            var relative = Uri.UnescapeDataString(uri.AbsolutePath["/games/published/story/".Length..]);
            if (relative == "index.json")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Index, Encoding.UTF8, "application/json") });
            var path = Path.Combine(_root, relative);
            return Task.FromResult(File.Exists(path)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(path)) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
