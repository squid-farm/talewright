using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Runtime;
using Talewright.Talemark.Model;

namespace Talewright.Debugger;

/// <summary>Preview of a scene rendered against a read-only game state.</summary>
public sealed record MapScenePreview(
    string Scene,
    string? Title,
    StoryFrame Frame,
    bool UsesCurrentRunState);

/// <summary>Builds a scene preview without executing entry effects or skill checks.</summary>
public static class MapScenePreviewBuilder
{
    public static MapScenePreview Build(
        StoryDefinition story,
        SceneId sceneId,
        IGameStateView state,
        bool usesCurrentRunState)
    {
        var frame = ScenePreview.Build(story, sceneId, state);
        return new MapScenePreview(sceneId.Value, frame.Title, frame, usesCurrentRunState);
    }
}
