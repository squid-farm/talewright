using Talewright.Talemark.Compilation;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class PerformanceCorpusTests
{
    [Fact]
    public void Typical_real_file_corpus_is_deterministic_and_compiles_from_directory_and_zip()
    {
        using var first = Create("first");
        using var second = Create("second");

        Assert.Equal(first.Metadata, second.Metadata);
        Assert.Equal(10, first.Metadata.StoryFileCount);
        Assert.Equal(100, first.Metadata.SceneCount);
        Assert.Equal(110, first.Metadata.ChoiceCount);
        Assert.Equal(512 * 1024, first.Metadata.ExpandedBytes);
        Assert.Equal("db3965b7d0dfff072710a0f31991f8e7c1918d99354ec459550918450f579a37", first.Metadata.Sha256);
        Assert.True(first.Metadata.CompressedBytes < 20 * 1024 * 1024);

        var directoryStory = Compile(StorySource.FromDirectory(first.Root));
        using var archive = File.OpenRead(first.ArchivePath);
        var zippedStory = Compile(StorySource.FromZip(archive));
        Assert.Equal(directoryStory.Id, zippedStory.Id);
        Assert.Equal(100, directoryStory.Scenes.Count);
        Assert.Equal(directoryStory.Scenes.Count, zippedStory.Scenes.Count);
    }

    [Fact]
    public void Low_compressibility_corpus_is_seeded_and_stays_within_upload_limit()
    {
        var tier = PerformanceCorpusTier.Get("Typical") with
        {
            Name = "Low-compressibility",
            ExpandedBytes = 10 * 1024 * 1024,
        };
        using var first = PerformanceCorpus.Create(tier, NewRoot("random-one"), lowCompressibility: true);
        using var second = PerformanceCorpus.Create(tier, NewRoot("random-two"), lowCompressibility: true);

        Assert.Equal(first.Metadata.Sha256, second.Metadata.Sha256);
        Assert.Equal(10 * 1024 * 1024, first.Metadata.ExpandedBytes);
        Assert.InRange(first.Metadata.CompressedBytes, 1, 20 * 1024 * 1024);
        Assert.True(Compile(StorySource.FromDirectory(first.Root)).Scenes.Count == tier.SceneCount);
    }

    [Fact]
    public void Fixed_byte_layout_corpora_compare_few_large_files_with_many_small_files()
    {
        using var fewFiles = CreateLayout("few-files", 2);
        using var manyFiles = CreateLayout("many-files", 100);

        Assert.Equal(fewFiles.Metadata.ExpandedBytes, manyFiles.Metadata.ExpandedBytes);
        Assert.Equal(5 * 1024 * 1024, fewFiles.Metadata.ExpandedBytes);
        Assert.Equal(fewFiles.Metadata.SceneCount, manyFiles.Metadata.SceneCount);
        Assert.Equal(100, fewFiles.Metadata.SceneCount);
        Assert.Equal(2, fewFiles.Metadata.StoryFileCount);
        Assert.Equal(100, manyFiles.Metadata.StoryFileCount);
        Assert.NotEqual(fewFiles.Metadata.Sha256, manyFiles.Metadata.Sha256);
        var fewLargestFile = Directory.EnumerateFiles(fewFiles.Root, "*.tale", SearchOption.AllDirectories)
            .Max(path => new FileInfo(path).Length);
        var manyLargestFile = Directory.EnumerateFiles(manyFiles.Root, "*.tale", SearchOption.AllDirectories)
            .Max(path => new FileInfo(path).Length);
        Assert.True(fewLargestFile > manyLargestFile);
        Assert.Equal(100, Compile(StorySource.FromDirectory(fewFiles.Root)).Scenes.Count);
        Assert.Equal(100, Compile(StorySource.FromDirectory(manyFiles.Root)).Scenes.Count);
    }

    private static PerformanceCorpus Create(string name) =>
        PerformanceCorpus.Create(PerformanceCorpusTier.Get("Typical"), NewRoot(name));

    private static PerformanceCorpus CreateLayout(string name, int fileCount) =>
        PerformanceCorpus.Create(new PerformanceCorpusTier(name, fileCount, 100, 5 * 1024 * 1024, 100), NewRoot(name));

    private static string NewRoot(string name) =>
        Path.Combine(Path.GetTempPath(), $"talewright-corpus-test-{name}-{Guid.NewGuid():N}");

    private static Talewright.Talemark.Model.StoryDefinition Compile(StorySource source)
    {
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return result.Story!;
    }
}
