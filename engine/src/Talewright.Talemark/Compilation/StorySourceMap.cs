using System.Collections.ObjectModel;
using Talewright.Core.Identity;

namespace Talewright.Talemark.Compilation;

/// <summary>
/// Whole-line scene provenance: inclusive, 1-based lines from the declaration through the line
/// before the next declaration or EOF. Inter-scene comments/blank lines belong to the prior scene.
/// Line endings follow String.ReplaceLineEndings; a terminal line ending creates an empty final line.
/// This is not a statement-level execution map.
/// </summary>
public sealed record SceneSourceRange(string File, int StartLine, int EndLine);

/// <summary>Parser-owned ranges paired with the exact immutable source snapshot compiled successfully.</summary>
public sealed class StorySourceMap
{
    internal StorySourceMap(StorySource source, IDictionary<SceneId, SceneSourceRange> scenes)
    {
        Source = source;
        Scenes = new ReadOnlyDictionary<SceneId, SceneSourceRange>(scenes);
    }

    public StorySource Source { get; }
    public IReadOnlyDictionary<SceneId, SceneSourceRange> Scenes { get; }
}
