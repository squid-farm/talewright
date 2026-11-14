using Talewright.Core.Identity;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Model;

namespace Talewright.Player.Library;

internal sealed record SceneImageGroup(SceneId Scene, IReadOnlyList<string> Paths);

internal static class StoryImageDiscovery
{
    public static IReadOnlyList<SceneImageGroup> ConnectedScenes(StoryDefinition story, SceneId? current)
        => ConnectedScenes(story, current, StoryMap.Build(story));

    internal static IReadOnlyList<SceneImageGroup> ConnectedScenes(
        StoryDefinition story,
        SceneId? current,
        StoryMap map)
    {
        ArgumentNullException.ThrowIfNull(story);
        ArgumentNullException.ThrowIfNull(map);
        if (current is not { } scene) return [];

        var connections = map.Connections(scene);
        var neighbors = new List<SceneId>();
        var seenScenes = new HashSet<SceneId>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        AddIncoming(connections.Incoming);
        AddOutgoing(connections.Outgoing);

        return neighbors.Select(neighbor =>
        {
            var sceneDefinition = story.GetScene(neighbor);
            return new SceneImageGroup(neighbor, Images(sceneDefinition, seenPaths).ToArray());
        }).ToArray();

        void AddIncoming(IEnumerable<StoryConnection> items)
        {
            foreach (var connection in items)
            {
                var neighbor = connection switch
                {
                    StoryConnection.DivertConnection divert => divert.Divert.From,
                    StoryConnection.HubOfferConnection offer => offer.Offer.Hub,
                    _ => throw new ArgumentOutOfRangeException(nameof(items)),
                };
                Add(neighbor);
            }
        }

        void AddOutgoing(IEnumerable<StoryConnection> items)
        {
            foreach (var connection in items)
            {
                var neighbor = connection switch
                {
                    StoryConnection.DivertConnection divert => divert.Divert.Target.Scene,
                    StoryConnection.HubOfferConnection offer => offer.Offer.Scene,
                    _ => throw new ArgumentOutOfRangeException(nameof(items)),
                };
                if (neighbor is { } target) Add(target);
            }
        }

        void Add(SceneId neighbor)
        {
            if (neighbor != scene && seenScenes.Add(neighbor)) neighbors.Add(neighbor);
        }
    }

    public static IReadOnlyList<string> SceneImages(StoryDefinition story, SceneId scene) =>
        Images(story.GetScene(scene), new HashSet<string>(StringComparer.Ordinal)).ToArray();

    private static IEnumerable<string> Images(SceneDefinition scene, HashSet<string> seenPaths)
    {
        if (scene.ChoiceLabel is { } choiceLabel)
        {
            foreach (var path in Images(choiceLabel, seenPaths)) yield return path;
        }

        foreach (var step in SceneSteps.Walk(scene.Steps))
        {
            switch (step)
            {
                case ProseStep prose:
                    foreach (var path in Images(prose.Block, seenPaths)) yield return path;
                    break;
                case ChoiceStep { Choice.Label: { } label }:
                    foreach (var path in Images(label, seenPaths)) yield return path;
                    break;
            }
        }
    }

    private static IEnumerable<string> Images(TemplateBlock block, HashSet<string> seenPaths)
    {
        switch (block)
        {
            case ParagraphTemplate paragraph:
                foreach (var path in Images(paragraph.Inlines, seenPaths)) yield return path;
                break;
            case SpeechTemplate speech:
                foreach (var paragraph in speech.Paragraphs)
                    foreach (var path in Images(paragraph, seenPaths)) yield return path;
                break;
            case ConditionalBlockTemplate conditional:
                foreach (var child in conditional.Then)
                    foreach (var path in Images(child, seenPaths)) yield return path;
                foreach (var child in conditional.Else)
                    foreach (var path in Images(child, seenPaths)) yield return path;
                break;
        }
    }

    private static IEnumerable<string> Images(IEnumerable<TemplateInline> inlines, HashSet<string> seenPaths)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MediaTemplate media when seenPaths.Add(media.MediaId):
                    yield return media.MediaId;
                    break;
                case StyledTemplate styled:
                    foreach (var path in Images(styled.Children, seenPaths)) yield return path;
                    break;
                case ConditionalInlineTemplate conditional:
                    foreach (var path in Images(conditional.Then, seenPaths)) yield return path;
                    foreach (var path in Images(conditional.Else, seenPaths)) yield return path;
                    break;
            }
        }
    }
}
