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
    public async Task Text_only_folder_and_archive_remain_compilable()
    {
        using var files = new ImageStoryFiles();
        await File.WriteAllTextAsync(files.TalePath, "=== start\nText only.\n-> END");
        using var loaded = await new FileSystemStoryLibrary([files.Root]).OpenAsync(files.Root);
        Assert.True(StoryCompiler.Compile(loaded.Source).Success);
        var zip = Path.Combine(files.Root, "story.zip");
        using (var stream = File.Create(zip))
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var path in loaded.Source.Files.Keys)
            {
                using var entry = archive.CreateEntry(path).Open();
                await using var source = File.OpenRead(Path.Combine(files.Root, path));
                await source.CopyToAsync(entry);
            }
        }
        using var archived = await new FileSystemStoryLibrary([zip]).OpenAsync(zip);
        Assert.True(StoryCompiler.Compile(archived.Source).Success);
        Assert.NotNull(archived.Images);
    }

    [Fact]
    public async Task Desktop_story_pack_loads_text_and_external_images_and_reports_damage()
    {
        using var files = new ImageStoryFiles();
        var source = StorySource.FromDirectory(files.Root);
        var pack = Path.Combine(files.Root, "story" + DesktopStoryPack.FileExtension);
        var externalImageDirectory = Path.Combine(files.Root, "story", "images");
        Directory.CreateDirectory(externalImageDirectory);
        File.Copy(files.ImagePath, Path.Combine(externalImageDirectory, "photo.jpeg"), overwrite: true);
        using (var output = File.Create(pack))
            DesktopStoryPack.Write(output, source);
        using var loaded = await new FileSystemStoryLibrary([pack]).OpenAsync(pack);
        Assert.True(StoryCompiler.Compile(loaded.Source).Success);
        Assert.NotNull(loaded.Images);
        await using var image = await loaded.Images.OpenAsync("images/photo.jpeg");
        Assert.True(image.CanRead);

        var damaged = Path.Combine(files.Root, "damaged" + DesktopStoryPack.FileExtension);
        var bytes = await File.ReadAllBytesAsync(pack);
        await File.WriteAllBytesAsync(damaged, bytes[..(bytes.Length / 2)]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new FileSystemStoryLibrary([damaged]).OpenAsync(damaged));
    }

    [Fact]
    public async Task Folder_named_like_a_story_pack_uses_the_folder_as_the_image_root()
    {
        using var files = new ImageStoryFiles();
        var folder = Path.Combine(files.Root, "folder" + DesktopStoryPack.FileExtension);
        Directory.CreateDirectory(folder);
        foreach (var path in StorySource.FromDirectory(files.Root).Files)
        {
            var destination = Path.Combine(folder, path.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllTextAsync(destination, path.Value);
        }
        foreach (var imagePath in StorySource.FromDirectory(files.Root).ImagePaths)
        {
            var destination = Path.Combine(folder, imagePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(files.Root, imagePath.Replace('/', Path.DirectorySeparatorChar)), destination, overwrite: true);
        }

        using var loaded = await new FileSystemStoryLibrary([folder]).OpenAsync(folder);
        var compile = StoryCompiler.Compile(loaded.Source);
        Assert.True(compile.Success, string.Join("\n", compile.Diagnostics));
        await using var image = await loaded.Images!.OpenAsync("images/photo.jpeg");
        Assert.True(image.CanRead);
    }
}
