using Talewright.Tale.Compilation;

namespace Talewright.Runtime.Tests;

public class LostRelicTests
{
    [Fact]
    public void Shipped_sample_compiles_without_errors_or_warnings()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Talewright.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var result = StoryCompiler.Compile(StorySource.FromDirectory(Path.Combine(directory.FullName, "samples", "LostRelic")));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
    }
}
