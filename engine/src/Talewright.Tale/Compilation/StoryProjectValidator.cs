using Talewright.Tale.Diagnostics;

namespace Talewright.Tale.Compilation;

/// <summary>Build-time validation for C# authoring projects with explicitly registered extensions.</summary>
public static class StoryProjectValidator
{
    /// <summary>Writes MSBuild-compatible diagnostics and returns 0, 1 for compiler errors, or 2 for input errors.</summary>
    public static int Validate(string directory, CompilerOptions options, TextWriter output)
    {
        StorySource source;
        try
        {
            directory = Path.GetFullPath(directory);
            source = StorySource.FromDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            output.WriteLine($"Talewright : error TWBUILD001: Could not read story: {ex.Message}");
            return 2;
        }

        var result = StoryCompiler.Compile(source, options);
        foreach (var diagnostic in result.Diagnostics)
        {
            var location = diagnostic.Location;
            var file = string.IsNullOrEmpty(location.File) ? Path.Combine(directory, StorySource.ManifestFileName)
                : Path.Combine(directory, location.File);
            var severity = diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning";
            output.WriteLine($"{file}({Math.Max(1, location.Line)},{Math.Max(1, location.Column)}): {severity} {diagnostic.Code}: {diagnostic.Message}");
        }

        return result.Success ? 0 : 1;
    }
}
