using System.Buffers.Binary;
using System.IO.Compression;
using Talewright.Player.Library;
using Talewright.Talemark.Compilation;

namespace Talewright.Player.E2E.Tests;

public sealed class ArchiveImageTests
{
    [Theory]
    [InlineData("")]
    [InlineData("wrapped/")]
    public async Task Split_configuration_and_lazy_images_survive_archive_ownership(string prefix)
    {
        using var files = new ImageStoryFiles();
        var source = StorySource.FromDirectory(files.Root);
        var input = new MemoryStream();
        using (var output = new ZipArchive(input, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in source.Files.Keys.Concat(source.ImagePaths))
            {
                using var entry = output.CreateEntry(prefix + path).Open();
                using var file = File.OpenRead(Path.Combine(files.Root, path));
                file.CopyTo(entry);
            }
        }
        input.Position = 0;
        var loaded = ArchiveStoryImages.Open(input);
        Assert.True(StoryCompiler.Compile(loaded.Source).Success);
        Assert.Equal(source.Files.OrderBy(pair => pair.Key), loaded.Source.Files.OrderBy(pair => pair.Key));
        Assert.True(source.ImagePaths.SetEquals(loaded.Source.ImagePaths));
        Assert.NotNull(loaded.Images);
        await using (var actual = await loaded.Images.OpenAsync("images/photo.jpeg"))
        {
            using var copy = new MemoryStream();
            await actual.CopyToAsync(copy);
            Assert.Equal(File.ReadAllBytes(files.ImagePath), copy.ToArray());
        }
        Assert.True(input.CanRead);
        await loaded.DisposeAsync();
        Assert.False(input.CanRead);
        Assert.True(StoryCompiler.Compile(loaded.Source).Success);
    }

    [Fact]
    public async Task Images_above_former_combined_cap_allow_full_repeated_and_partial_reads()
    {
        using var input = ImageArchive(51 * 1024 * 1024);
        await using var loaded = ArchiveStoryImages.Open(input);
        Assert.NotNull(loaded.Images);
        await using (var partial = await loaded.Images.OpenAsync("images/large.png"))
            Assert.Equal(1, await partial.ReadAsync(new byte[1]));
        for (var i = 0; i < 2; ++i)
        {
            await using var stream = await loaded.Images.OpenAsync("images/large.png");
            await stream.CopyToAsync(Stream.Null);
            Assert.Equal(51 * 1024 * 1024, stream.Position);
        }
    }

    [Fact]
    public async Task Framework_bounds_understated_entry_output_without_expanding_the_hidden_payload()
    {
        using var input = ImageArchive(51 * 1024 * 1024);
        UnderstateImageLength(input);
        await using var loaded = ArchiveStoryImages.Open(input);
        Assert.NotNull(loaded.Images);
        await using var stream = await loaded.Images.OpenAsync("images/large.png");
        await stream.CopyToAsync(Stream.Null);
        Assert.Equal(1, stream.Position);
        Assert.True(StoryCompiler.Compile(loaded.Source).Success);
    }

    [Fact]
    public async Task Declared_entry_size_is_enforced_on_actual_sync_and_async_reads_without_poisoning_other_images()
    {
        var input = ImageArchive(1024);
        var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var source = StorySource.FromZip(archive, out var images);
        await using var owner = new ArchiveStoryImages(input, archive, images);
        foreach (var asyncRead in new[] { false, true })
        {
            await using var stream = new ArchiveStoryImages.ImageStream(owner, "images/deceptive.png",
                new MemoryStream([1, 2, 3]), 2, CancellationToken.None);
            var buffer = new byte[2];
            Assert.Equal(2, asyncRead ? await stream.ReadAsync(buffer) : stream.Read(buffer));
            var error = asyncRead
                ? await Assert.ThrowsAsync<InvalidDataException>(() => stream.CopyToAsync(Stream.Null))
                : Assert.Throws<InvalidDataException>(() => stream.ReadByte());
            Assert.Contains("declared ZIP entry size", error.Message);
            Assert.Equal(2, stream.Position);
        }
        await using var valid = await owner.OpenAsync("images/large.png", CancellationToken.None);
        await valid.CopyToAsync(Stream.Null);
        Assert.Equal(1024, valid.Position);
        Assert.True(StoryCompiler.Compile(source).Success);
    }

    [Fact]
    public void Malformed_archive_releases_input()
    {
        var malformed = new MemoryStream([1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => ArchiveStoryImages.Open(malformed));
        Assert.False(malformed.CanRead);
    }

    [Fact]
    public async Task Cancellation_and_teardown_close_retained_streams_without_reparsing()
    {
        var input = ImageArchive(1024);
        var loaded = ArchiveStoryImages.Open(input);
        Assert.NotNull(loaded.Images);
        using var cancellation = new CancellationTokenSource();
        await using var stream = await loaded.Images.OpenAsync("images/large.png", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadExactlyAsync(new byte[1]).AsTask());
        await loaded.DisposeAsync();
        Assert.False(input.CanRead);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await loaded.Images.OpenAsync("images/large.png"));
    }

    [Fact]
    public async Task Interleaved_readers_preserve_bytes_and_owner_teardown_closes_active_reads()
    {
        using var files = new ImageStoryFiles();
        var input = new MemoryStream(files.Zip());
        var loaded = ArchiveStoryImages.Open(input);
        Assert.NotNull(loaded.Images);
        await using var first = await loaded.Images.OpenAsync("images/photo.jpeg");
        await using var second = await loaded.Images.OpenAsync("images/pixel.png");
        var firstBytes = new MemoryStream();
        var secondBytes = new MemoryStream();
        using (firstBytes)
        using (secondBytes)
        {
            var firstBlock = new byte[17];
            var secondBlock = new byte[11];
            while (true)
            {
                var a = await first.ReadAsync(firstBlock);
                var b = await second.ReadAsync(secondBlock);
                firstBytes.Write(firstBlock, 0, a);
                secondBytes.Write(secondBlock, 0, b);
                if (a == 0 && b == 0) break;
            }
            Assert.Equal(File.ReadAllBytes(files.ImagePath), firstBytes.ToArray());
            Assert.Equal(File.ReadAllBytes(Path.Combine(files.Root, "images", "pixel.png")), secondBytes.ToArray());
        }
        await loaded.DisposeAsync();
        Assert.False(input.CanRead);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.ReadExactlyAsync(new byte[1]).AsTask());
        await loaded.DisposeAsync();
    }

    private static MemoryStream ImageArchive(int imageBytes)
    {
        var input = new MemoryStream();
        using (var output = new ZipArchive(input, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(output.CreateEntry(StorySource.ManifestFileName).Open()))
                writer.Write("game:\n  id: archive\n  title: Archive\n  start: start\ncharacters:\n  player: {first-name: Tester}\n");
            using (var writer = new StreamWriter(output.CreateEntry("main.tale").Open()))
                writer.Write("=== start\nArchive text.\n-> END");
            using var image = output.CreateEntry("images/large.png").Open();
            var block = new byte[64 * 1024];
            for (var remaining = imageBytes; remaining > 0; remaining -= Math.Min(block.Length, remaining))
                image.Write(block.AsSpan(0, Math.Min(block.Length, remaining)));
        }
        input.Position = 0;
        return input;
    }

    private static void UnderstateImageLength(MemoryStream input)
    {
        var bytes = input.GetBuffer().AsSpan(0, checked((int)input.Length));
        for (var i = 0; i <= bytes.Length - 46; ++i)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[i..]) != 0x02014b50) continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i + 28)..]);
            var name = System.Text.Encoding.UTF8.GetString(bytes.Slice(i + 46, nameLength));
            if (name == "images/large.png")
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[(i + 24)..], 1);
                return;
            }
        }
        throw new InvalidOperationException("Missing test image central directory.");
    }
}
