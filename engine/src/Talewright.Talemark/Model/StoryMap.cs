using Talewright.Core.Identity;

namespace Talewright.Talemark.Model;

/// <summary>Preview. A scene or END in a story map.</summary>
/// <param name="Id">The scene id, or <c>END</c> for the terminal node.</param>
/// <param name="Scene">The scene id, or null for END.</param>
/// <param name="Title">The scene's title, or null for END.</param>
/// <param name="Tags">The scene's tags in ordinal order, or an empty list for END.</param>
/// <param name="IsReachable">Whether the start scene can reach this node through diverts and hub offers.</param>
public sealed record StoryMapNode(
    string Id,
    SceneId? Scene,
    string? Title,
    IReadOnlyList<string> Tags,
    bool IsReachable)
{
    /// <summary>True when this node represents END rather than a scene.</summary>
    public bool IsEnd => Scene is null;
}

/// <summary>
/// Preview. Read-only map data for a successfully compiled story: its scenes and END, diverts, hub offers and reachability.
/// Reachability follows every divert and hub offer without evaluating whether their conditions can be met.
/// </summary>
public sealed class StoryMap
{
    private StoryMap(
        IReadOnlyList<StoryMapNode> nodes,
        IReadOnlyList<StoryDivert> diverts,
        IReadOnlyList<HubOffer> hubOffers)
    {
        Nodes = nodes;
        Diverts = diverts;
        HubOffers = hubOffers;
    }

    /// <summary>Scene nodes in declaration order, followed by END.</summary>
    public IReadOnlyList<StoryMapNode> Nodes { get; }

    /// <summary>Every divert, in scene declaration order and then source order.</summary>
    public IReadOnlyList<StoryDivert> Diverts { get; }

    /// <summary>Every hub offer, in hub declaration order and then offered-scene declaration order.</summary>
    public IReadOnlyList<HubOffer> HubOffers { get; }

    /// <summary>Builds the map for a successfully compiled story.</summary>
    /// <param name="story">A story returned by a successful <see cref="Compilation.StoryCompiler"/> compilation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="story"/> is null.</exception>
    public static StoryMap Build(StoryDefinition story)
    {
        ArgumentNullException.ThrowIfNull(story);
        var reachability = story.Reachability;
        var diverts = reachability.Diverts.Diverts;
        var nodes = story.Scenes.Select(scene => new StoryMapNode(
            scene.Id.Value,
            scene.Id,
            scene.Title,
            Array.AsReadOnly(scene.Tags.Order(StringComparer.Ordinal).ToArray()),
            reachability.IsReached(scene.Id))).ToList();
        var endIsReachable = diverts.Any(divert =>
            divert.Target.IsEnd && reachability.IsReached(divert.From));
        nodes.Add(new StoryMapNode("END", null, null, Array.Empty<string>(), endIsReachable));
        return new StoryMap(
            Array.AsReadOnly(nodes.ToArray()),
            diverts,
            reachability.Diverts.HubOffers);
    }
}
