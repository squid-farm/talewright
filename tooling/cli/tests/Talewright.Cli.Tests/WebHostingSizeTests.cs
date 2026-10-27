using System.Text;
using Talewright.Talemark.Compilation;

namespace Talewright.Cli.Tests;

public sealed class WebHostingSizeTests
{
    [Fact]
    public void Source_estimate_counts_split_configuration_and_supported_images_only()
    {
        var root = Path.Combine(Path.GetTempPath(), $"talewright-hosting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "characters"));
        Directory.CreateDirectory(Path.Combine(root, "images", "maps"));
        try
        {
            File.WriteAllText(Path.Combine(root, StorySource.ManifestFileName), "game:\n  id: hosting\n");
            File.WriteAllText(Path.Combine(root, "characters", "player.talewright.yaml"), "characters:\n  player: {first-name: \u00c9va}\n");
            File.WriteAllText(Path.Combine(root, "main.tale"), "=== start\nHello.\n-> END");
            File.WriteAllBytes(Path.Combine(root, "images", "maps", "map.png"), [1, 2, 3]);
            File.WriteAllText(Path.Combine(root, "notes.yaml"), "not packaged");
            File.WriteAllText(Path.Combine(root, "images", "draft.bmp"), "not supported");
            var source = StorySource.FromDirectory(root);
            var textBytes = source.Files.Values.Sum(text => (long)Encoding.UTF8.GetByteCount(text));
            Assert.Equal(new WebHostingSize(textBytes + 3,
                Math.Max(3, source.Files.Values.Max(text => Encoding.UTF8.GetByteCount(text))), 4),
                WebHostingSize.Estimate(source, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Recorded_boundaries_are_advisory_and_exact_values_do_not_warn()
    {
        using var output = new StringWriter();
        new WebHostingSize(WebHostingSize.TotalWarningBytes, WebHostingSize.FileWarningBytes,
            WebHostingSize.FileWarningCount).WriteWarning(output, estimate: false);
        Assert.Empty(output.ToString());
        foreach (var report in new[]
        {
            new WebHostingSize(WebHostingSize.TotalWarningBytes + 1, 1, 1),
            new WebHostingSize(1, WebHostingSize.FileWarningBytes + 1, 1),
            new WebHostingSize(1, 1, WebHostingSize.FileWarningCount + 1),
        })
        {
            using var warning = new StringWriter();
            report.WriteWarning(warning, estimate: false);
            Assert.Contains("Advisory only", warning.ToString());
            Assert.Contains("final extracted web payload", warning.ToString());
            Assert.Contains("largest file", warning.ToString());
        }
    }

    [Fact]
    public void Source_estimate_and_final_output_use_the_same_calculation_but_distinct_basis_labels()
    {
        var sizes = Enumerable.Repeat(1L, WebHostingSize.FileWarningCount + 1);
        var report = WebHostingSize.Measure(sizes);
        Assert.Equal(1001, report.TotalBytes);
        Assert.Equal(1, report.LargestFileBytes);
        Assert.Equal(1001, report.FileCount);
        using var estimate = new StringWriter();
        using var final = new StringWriter();
        report.WriteWarning(estimate, estimate: true);
        report.WriteWarning(final, estimate: false);
        Assert.Contains("source-only estimate", estimate.ToString());
        Assert.Contains("excludes generated runtime", estimate.ToString());
        Assert.Contains("includes generated runtime", final.ToString());
    }
}
