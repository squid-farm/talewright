using Talewright.Core.Identity;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Rules.Storylets;
using Talewright.Talemark.Analysis;

namespace Talewright.Talemark.Model;

/// <summary>Preview. The kind of line a divert (<c>-&gt;</c>) is written on.</summary>
public enum DivertKind
{
    /// <summary>A scene divert: a <c>-&gt; target</c> line.</summary>
    Scene,

    /// <summary>A choice's divert, including a textless choice.</summary>
    Choice,

    /// <summary>One outcome of a choice skill check.</summary>
    ChoiceSkillCheck,

    /// <summary>One outcome of an entry check that has a divert.</summary>
    EntryCheck,
}

/// <summary>Preview. The skill check outcome a divert follows.</summary>
public enum SkillCheckOutcome { Success, Failure }

/// <summary>Preview. A block <c>{if}</c> enclosing a divert; <see cref="InElse"/> is true in its <c>{else}</c> branch.</summary>
public sealed record DivertBlockCondition(Expr Condition, bool InElse);

/// <summary>
/// Preview. One divert (<c>-&gt;</c>) in a compiled story.
/// <see cref="Id"/> is the from-scene, the step id of the divert's line and, for a skill check outcome,
/// <c>success</c> or <c>failure</c>, for example <c>camp/step:12:1/success</c>. It is unique within one
/// compiled story and changes when lines move, like step ids.
/// </summary>
/// <param name="Id">Divert id, unique within one compiled story.</param>
/// <param name="From">The scene the divert is written in.</param>
/// <param name="Target">The scene it leads to, or END.</param>
/// <param name="Kind">The kind of line it is written on.</param>
/// <param name="Outcome">The skill check outcome for a check divert; otherwise null.</param>
/// <param name="StepId">Step id of the divert's line (<c>step:&lt;line&gt;:&lt;column&gt;</c>).</param>
/// <param name="Source">Source span of the divert's line.</param>
/// <param name="Order">Zero-based source order among the from-scene's diverts.</param>
/// <param name="ChoiceKey">The choice key (<c>c0</c>, <c>c1</c>…) for a choice or choice skill check divert.</param>
/// <param name="ChoiceLabel">The choice's label template, or null for a textless choice or a non-choice divert.</param>
/// <param name="ChoiceRequires">The choice's <c>requires</c>, as compiled.</param>
/// <param name="BlockConditions">Enclosing block <c>{if}</c>s, outermost first.</param>
/// <param name="Condition">The formatted condition and state reads for this divert.</param>
public sealed record StoryDivert(
    string Id,
    SceneId From,
    DivertTarget Target,
    DivertKind Kind,
    SkillCheckOutcome? Outcome,
    string StepId,
    SourceRange Source,
    int Order,
    string? ChoiceKey,
    IReadOnlyList<TemplateInline>? ChoiceLabel,
    Expr? ChoiceRequires,
    IReadOnlyList<DivertBlockCondition> BlockConditions,
    StoryCondition Condition);

/// <summary>
/// Preview. A hub offer: a scene matching a hub's tags, ignoring <c>@hub-limit</c>.
/// <see cref="Id"/> is the hub scene and the offered scene, for example <c>camp/hub:camp.well</c>.
/// </summary>
/// <param name="Id">The hub scene and offered scene.</param>
/// <param name="Hub">The scene offering the matching scenes.</param>
/// <param name="Scene">The offered scene.</param>
/// <param name="Tags">The hub's tag query.</param>
/// <param name="Condition">The offered scene's formatted <c>@requires</c> and state reads.</param>
/// <param name="Once">Whether the offered scene has <c>@once</c>.</param>
public sealed record HubOffer(
    string Id,
    SceneId Hub,
    SceneId Scene,
    StoryletTags Tags,
    StoryCondition Condition,
    bool Once);

/// <summary>
/// Preview. Every divert and hub offer in a story, from the lines that can run. Diverts follow scene declaration
/// order, then source order; hub offers follow hub, then offered scene, declaration order.
/// </summary>
public sealed class StoryDiverts
{
    private StoryDiverts(IReadOnlyList<StoryDivert> diverts, IReadOnlyList<HubOffer> hubOffers)
    {
        Diverts = diverts;
        HubOffers = hubOffers;
    }

    public IReadOnlyList<StoryDivert> Diverts { get; }
    public IReadOnlyList<HubOffer> HubOffers { get; }

    /// <summary>The story's diverts and hub offers, built once per story.</summary>
    public static StoryDiverts From(StoryDefinition story) => story.Reachability.Diverts;

    public static StoryDiverts From(IReadOnlyList<SceneDefinition> scenes) => From(scenes, null);

    internal static StoryDiverts From(IReadOnlyList<SceneDefinition> scenes, CancellationCheckpoint? checkpoint)
    {
        var order = new Dictionary<SceneId, int>();
        var scenesById = new Dictionary<SceneId, SceneDefinition>();
        var conditionsByScene = new Dictionary<SceneId, StoryCondition>();
        var byTag = new Dictionary<string, HashSet<SceneId>>(StringComparer.Ordinal);
        foreach (var scene in scenes)
        {
            checkpoint?.Token.ThrowIfCancellationRequested();
            order.TryAdd(scene.Id, order.Count);
            scenesById.TryAdd(scene.Id, scene);
            foreach (var tag in scene.Tags)
            {
                checkpoint?.Check();
                if (!byTag.TryGetValue(tag, out var ids))
                    byTag[tag] = ids = [];
                ids.Add(scene.Id);
            }
        }

        var hubOffers = new List<HubOffer>();
        foreach (var scene in scenes)
        {
            checkpoint?.Token.ThrowIfCancellationRequested();
            // A hub only offers scenes when its lines can reach the end of the scene.
            if (scene.Hub is not { } hub || !SceneSteps.CanContinue(scene.Steps, checkpoint))
                continue;
            foreach (var offered in OrderedOffers(ResolveTags(hub, byTag, checkpoint), scene.Id, order, checkpoint))
            {
                checkpoint?.Check();
                var offeredScene = scenesById[offered];
                if (!conditionsByScene.TryGetValue(offered, out var condition))
                {
                    condition = StoryCondition.FromExpressions(
                        offeredScene.Requires is { } requires ? [requires] : []);
                    conditionsByScene.Add(offered, condition);
                }
                hubOffers.Add(new HubOffer($"{scene.Id.Value}/hub:{offered.Value}", scene.Id, offered, hub,
                    condition, offeredScene.Once));
            }
        }

        var diverts = new List<StoryDivert>();
        foreach (var scene in scenes)
        {
            checkpoint?.Token.ThrowIfCancellationRequested();
            diverts.AddRange(SceneSteps.Diverts(scene, checkpoint));
        }
        checkpoint?.Token.ThrowIfCancellationRequested();
        var result = new StoryDiverts(diverts.ToArray(), hubOffers);
        checkpoint?.Token.ThrowIfCancellationRequested();
        return result;
    }

    private static IEnumerable<SceneId> OrderedOffers(IEnumerable<SceneId> matches, SceneId hub,
        IReadOnlyDictionary<SceneId, int> order, CancellationCheckpoint? checkpoint)
    {
        if (checkpoint is null)
        {
            foreach (var id in matches.Where(id => id != hub).OrderBy(id => order[id]))
                yield return id;
            yield break;
        }

        // A heap preserves declaration order while allowing cancellation during large offer sets.
        var ordered = new PriorityQueue<SceneId, int>();
        foreach (var id in matches)
        {
            checkpoint.Check();
            if (id != hub) ordered.Enqueue(id, order[id]);
        }
        while (ordered.TryDequeue(out var id, out _))
        {
            checkpoint.Check();
            yield return id;
        }
    }

    internal static IEnumerable<SceneId> ResolveTags(StoryletTags query,
        IReadOnlyDictionary<string, HashSet<SceneId>> byTag, CancellationCheckpoint? checkpoint = null)
    {
        var sets = new List<HashSet<SceneId>>();
        foreach (var tag in query.Tags)
        {
            checkpoint?.Check();
            if (byTag.TryGetValue(tag, out var set))
                sets.Add(set);
            else if (query.MatchAll)
                return [];
        }

        if (sets.Count == 0)
            return [];
        if (sets.Count == 1)
            return CheckMatches(sets[0], checkpoint);

        var candidates = OrderedSets(sets, checkpoint);

        var result = new HashSet<SceneId>();
        foreach (var id in candidates[0])
        {
            checkpoint?.Check();
            result.Add(id);
        }
        foreach (var set in candidates.Skip(1))
        {
            if (checkpoint is null && query.MatchAll)
                result.IntersectWith(set);
            else if (checkpoint is null)
                result.UnionWith(set);
            else if (query.MatchAll)
            {
                var intersection = new HashSet<SceneId>();
                foreach (var id in result)
                {
                    checkpoint.Check();
                    if (set.Contains(id)) intersection.Add(id);
                }
                result = intersection;
            }
            else
            {
                foreach (var id in set)
                {
                    checkpoint.Check();
                    result.Add(id);
                }
            }
        }
        return CheckMatches(result, checkpoint);
    }

    private static IReadOnlyList<HashSet<SceneId>> OrderedSets(
        IReadOnlyList<HashSet<SceneId>> sets, CancellationCheckpoint? checkpoint)
    {
        if (checkpoint is null) return sets.OrderBy(set => set.Count).ToArray();

        var ordered = new PriorityQueue<HashSet<SceneId>, (int Size, int Order)>();
        for (var index = 0; index < sets.Count; index++)
        {
            checkpoint.Check();
            ordered.Enqueue(sets[index], (sets[index].Count, index));
        }
        var result = new List<HashSet<SceneId>>(sets.Count);
        while (ordered.TryDequeue(out var set, out _))
        {
            checkpoint.Check();
            result.Add(set);
        }
        return result;
    }

    private static IEnumerable<SceneId> CheckMatches(IEnumerable<SceneId> matches, CancellationCheckpoint? checkpoint)
    {
        foreach (var id in matches)
        {
            checkpoint?.Check();
            yield return id;
        }
    }
}
