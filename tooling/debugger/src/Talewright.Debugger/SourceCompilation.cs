using Talewright.Tale.Compilation;

namespace Talewright.Debugger;

public sealed record SourceCompilation(StorySource? Source, CompilationResult? Compilation, string? Error)
{
    public static SourceCompilation Load(string root)
    {
        try
        {
            var source = StorySource.FromDirectory(root);
            return new(source, StoryCompiler.Compile(source), null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return new(null, null, ex.Message);
        }
    }
}
