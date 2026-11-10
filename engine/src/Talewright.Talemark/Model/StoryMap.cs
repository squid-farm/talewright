using Talewright.Core.Identity;
using Talewright.Talemark.Vocabulary;

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

/// <summary>One incoming or outgoing connection in a story map.</summary>
public abstract record StoryConnection
{
    private protected StoryConnection() { }

    /// <summary>A divert connection.</summary>
    /// <param name="Divert">The divert.</param>
    public sealed record DivertConnection(StoryDivert Divert) : StoryConnection;

    /// <summary>A hub-offer connection.</summary>
    /// <param name="Offer">The hub offer.</param>
    public sealed record HubOfferConnection(HubOffer Offer) : StoryConnection;
}

/// <summary>Incoming and outgoing connections for a scene.</summary>
/// <param name="Incoming">Connections leading to the scene.</param>
/// <param name="Outgoing">Connections leaving the scene.</param>
public sealed record StorySceneConnections(
    IReadOnlyList<StoryConnection> Incoming,
    IReadOnlyList<StoryConnection> Outgoing);

/// <summary>
/// Preview. Read-only map data for a successfully compiled story: its scenes and END, diverts, hub offers and reachability.
/// Reachability follows every divert and hub offer without evaluating whether their conditions can be met.
/// </summary>
public sealed class StoryMap
{
    private static readonly StorySceneConnections NoConnections = new(Array.Empty<StoryConnection>(), Array.Empty<StoryConnection>());
    private readonly IReadOnlyDictionary<SceneId, StorySceneConnections> _connections;

    private StoryMap(
        IReadOnlyList<StoryMapNode> nodes,
        IReadOnlyList<StoryDivert> diverts,
        IReadOnlyList<HubOffer> hubOffers,
        IReadOnlyList<SceneDefinition> scenes)
    {
        Nodes = nodes;
        Diverts = diverts;
        HubOffers = hubOffers;
        _connections = BuildConnections(scenes, diverts, hubOffers);
    }

    /// <summary>Scene nodes in declaration order, followed by END.</summary>
    public IReadOnlyList<StoryMapNode> Nodes { get; }

    /// <summary>Every divert, in scene declaration order and then source order.</summary>
    public IReadOnlyList<StoryDivert> Diverts { get; }

    /// <summary>Every hub offer, in hub declaration order and then offered-scene declaration order.</summary>
    public IReadOnlyList<HubOffer> HubOffers { get; }

    /// <summary>Returns the incoming and outgoing diverts and hub offers for a scene.</summary>
    /// <param name="scene">The scene to look up.</param>
    /// <returns>Connections in declaration order, or empty lists when the scene is not in this map.</returns>
    public StorySceneConnections Connections(SceneId scene) =>
        _connections.GetValueOrDefault(scene) ?? NoConnections;

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
        nodes.Add(new StoryMapNode(SceneVocabulary.End, null, null, Array.Empty<string>(), endIsReachable));
        return new StoryMap(
            Array.AsReadOnly(nodes.ToArray()),
            diverts,
            reachability.Diverts.HubOffers,
            story.Scenes);
    }

    /// <summary>Returns Preview details for a scene, including compiled templates and effects.</summary>
    /// <param name="scene">A scene from a successfully compiled story.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scene"/> is null.</exception>
    public static StorySceneDetails SceneDetails(SceneDefinition scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return StorySceneDetails.From(scene);
    }

    private static IReadOnlyDictionary<SceneId, StorySceneConnections> BuildConnections(
        IReadOnlyList<SceneDefinition> scenes,
        IReadOnlyList<StoryDivert> diverts,
        IReadOnlyList<HubOffer> hubOffers)
    {
        var sceneOrder = scenes.Select((scene, index) => (scene.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        var outgoing = scenes.ToDictionary(scene => scene.Id, _ => new List<StoryConnection>());
        var incoming = scenes.ToDictionary(scene => scene.Id,
            _ => new List<(int SceneOrder, int KindOrder, int ItemOrder, StoryConnection Connection)>());

        foreach (var divert in diverts)
        {
            var connection = new StoryConnection.DivertConnection(divert);
            outgoing[divert.From].Add(connection);
            if (divert.Target.Scene is { } target && incoming.TryGetValue(target, out var targetConnections))
                targetConnections.Add((sceneOrder[divert.From], 0, divert.Order, connection));
        }

        var offerOrder = new Dictionary<SceneId, int>();
        foreach (var offer in hubOffers)
        {
            var connection = new StoryConnection.HubOfferConnection(offer);
            outgoing[offer.Hub].Add(connection);
            var order = offerOrder.GetValueOrDefault(offer.Hub);
            offerOrder[offer.Hub] = order + 1;
            incoming[offer.Scene].Add((sceneOrder[offer.Hub], 1, order, connection));
        }

        return scenes.ToDictionary(
            scene => scene.Id,
            scene => new StorySceneConnections(
                Array.AsReadOnly(incoming[scene.Id]
                    .OrderBy(item => item.SceneOrder)
                    .ThenBy(item => item.KindOrder)
                    .ThenBy(item => item.ItemOrder)
                    .Select(item => item.Connection)
                    .ToArray()),
                Array.AsReadOnly(outgoing[scene.Id].ToArray())));
    }
}
