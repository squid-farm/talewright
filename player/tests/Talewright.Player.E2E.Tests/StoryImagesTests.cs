using Talewright.Player.Library;
using Talewright.Talemark.Compilation;

namespace Talewright.Player.E2E.Tests;

public sealed class StoryImagesTests
{
    [Fact]
    public async Task Actual_folder_load_compile_and_lazy_open_preserve_bytes_and_live_edits()
    {
        using var files = new ImageStoryFiles();
        IStoryLibrary library = new CompositeStoryLibrary(new FileSystemStoryLibrary([files.Root]));
        using var loaded = await library.OpenAsync("0|" + files.Root);
        var result = StoryCompiler.Compile(loaded.Source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(loaded.Images);
        await using (var stream = await loaded.Images.OpenAsync("images/photo.jpeg"))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(await File.ReadAllBytesAsync(files.ImagePath), copy.ToArray());
        }
        await File.WriteAllBytesAsync(files.ImagePath, [1, 2, 3]);
        await using (var edited = await loaded.Images.OpenAsync("images/photo.jpeg"))
        {
            Assert.Equal(3, edited.Length);
        }
        File.Delete(files.ImagePath);
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await loaded.Images.OpenAsync("images/photo.jpeg"));
    }

    [Theory]
    [InlineData("../photo.jpeg")]
    [InlineData("images/../photo.jpeg")]
    [InlineData("images\\photo.jpeg")]
    [InlineData("/images/photo.jpeg")]
    [InlineData("https://example.test/photo.jpeg")]
    [InlineData("images/photo.jpeg?query")]
    [InlineData("images/PHOTO.jpeg")]
    [InlineData("images/unlisted.jpeg")]
    [InlineData("images/unsupported.svg")]
    public async Task Unsafe_or_uncatalogued_paths_cannot_be_opened(string path)
    {
        using var files = new ImageStoryFiles();
        var source = StorySource.FromDirectory(files.Root);
        // Untrusted catalogues cannot opt unsafe/unsupported paths into the capability.
        var catalogue = path is "images/PHOTO.jpeg" or "images/unlisted.jpeg" ? source
            : new StorySource(source.Files, source.ImagePaths.Append(path));
        using var images = StoryImages.FromDirectory(catalogue, files.Root);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await images.OpenAsync(path));
    }

    [Fact]
    public async Task Discovery_and_compilation_do_not_open_image_bytes()
    {
        using var files = new ImageStoryFiles();
        using var exclusive = new FileStream(files.ImagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var loaded = await new FileSystemStoryLibrary([files.Root]).OpenAsync(files.Root);
        Assert.True(StoryCompiler.Compile(loaded.Source).Success);
        Assert.NotNull(loaded.Images);
        await Assert.ThrowsAsync<IOException>(async () => await loaded.Images.OpenAsync("images/photo.jpeg"));
    }

    [Fact]
    public async Task A_root_replaced_with_a_symlink_after_cataloguing_is_rejected()
    {
        using var files = new ImageStoryFiles();
        using var images = StoryImages.FromDirectory(StorySource.FromDirectory(files.Root), files.Root);
        var renamed = files.Root + "-original";
        Directory.Move(files.Root, renamed);
        try
        {
            Directory.CreateSymbolicLink(files.Root, renamed);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await images.OpenAsync("images/photo.jpeg"));
        }
        finally
        {
            Directory.Delete(files.Root);
            Directory.Move(renamed, files.Root);
        }
    }

    [Fact]
    public async Task Cancellation_and_owner_disposal_stop_new_reads()
    {
        using var files = new ImageStoryFiles();
        using var images = StoryImages.FromDirectory(StorySource.FromDirectory(files.Root), files.Root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await images.OpenAsync("images/photo.jpeg", cancellation.Token));
        var lifetime = images.Lifetime;
        images.Dispose();
        Assert.True(lifetime.IsCancellationRequested);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await images.OpenAsync("images/photo.jpeg"));
    }

    [Fact]
    public async Task A_file_replaced_with_a_symlink_after_cataloguing_is_rejected()
    {
        using var files = new ImageStoryFiles();
        using var images = StoryImages.FromDirectory(StorySource.FromDirectory(files.Root), files.Root);
        var original = files.ImagePath + ".original";
        File.Move(files.ImagePath, original);
        File.CreateSymbolicLink(files.ImagePath, original);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await images.OpenAsync("images/photo.jpeg"));
    }

    [Fact]
    public async Task A_directory_replaced_with_a_symlink_after_cataloguing_is_rejected()
    {
        using var files = new ImageStoryFiles();
        using var images = StoryImages.FromDirectory(StorySource.FromDirectory(files.Root), files.Root);
        var imagesPath = Path.Combine(files.Root, "images");
        var renamed = imagesPath + "-original";
        Directory.Move(imagesPath, renamed);
        Directory.CreateSymbolicLink(imagesPath, renamed);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await images.OpenAsync("images/photo.jpeg"));
    }

    [Fact]
    public async Task Embedded_open_uses_real_lazy_resources_and_is_opt_in_for_generated_hosts()
    {
        var assembly = typeof(StoryImagesTests).Assembly;
        using var loaded = await new EmbeddedStoryLibrary(assembly, enableImages: true).OpenAsync("LostRelic");
        Assert.NotNull(loaded.Images);
        foreach (var path in loaded.Source.ImagePaths)
        {
            await using var actual = await loaded.Images.OpenAsync(path);
            await using var expected = assembly.GetManifestResourceStream("stories/LostRelic/" + path)!;
            using var copy = new MemoryStream();
            await actual.CopyToAsync(copy);
            using var resourceCopy = new MemoryStream();
            await expected.CopyToAsync(resourceCopy);
            Assert.Equal(resourceCopy.ToArray(), copy.ToArray());
        }
        using var deferred = await new EmbeddedStoryLibrary(assembly).OpenAsync("LostRelic");
        Assert.Null(deferred.Images);
    }

    [Fact]
    public async Task Text_only_folder_and_deferred_zip_remain_compilable()
    {
        using var files = new ImageStoryFiles();
        await File.WriteAllTextAsync(files.TalePath, "=== start\nText only.\n-> END");
        using var loaded = await new FileSystemStoryLibrary([files.Root]).OpenAsync(files.Root);
        Assert.True(StoryCompiler.Compile(loaded.Source).Success);
        var zip = Path.Combine(files.Root, "story.zip");
        using (var stream = File.Create(zip))
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var path in new[] { StorySource.ManifestFileName, "main.tale" })
            {
                using var entry = archive.CreateEntry(path).Open();
                await using var source = File.OpenRead(Path.Combine(files.Root, path));
                await source.CopyToAsync(entry);
            }
        }
        using var archived = await new FileSystemStoryLibrary([zip]).OpenAsync(zip);
        Assert.True(StoryCompiler.Compile(archived.Source).Success);
        Assert.Null(archived.Images);
    }
}
