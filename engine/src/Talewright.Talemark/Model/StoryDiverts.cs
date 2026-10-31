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

    public static StoryDiverts From(IReadOnlyList<SceneDefinition> scenes)
    {
        var order = new Dictionary<SceneId, int>();
        var scenesById = new Dictionary<SceneId, SceneDefinition>();
        var conditionsByScene = new Dictionary<SceneId, StoryCondition>();
        var byTag = new Dictionary<string, HashSet<SceneId>>(StringComparer.Ordinal);
        foreach (var scene in scenes)
        {
            order.TryAdd(scene.Id, order.Count);
            scenesById.TryAdd(scene.Id, scene);
            foreach (var tag in scene.Tags)
            {
                if (!byTag.TryGetValue(tag, out var ids))
                    byTag[tag] = ids = [];
                ids.Add(scene.Id);
            }
        }

        var hubOffers = new List<HubOffer>();
        foreach (var scene in scenes)
        {
            // A hub only offers scenes when its lines can reach the end of the scene.
            if (scene.Hub is not { } hub || !SceneSteps.CanContinue(scene.Steps))
                continue;
            foreach (var offered in ResolveTags(hub, byTag).Where(id => id != scene.Id).OrderBy(id => order[id]))
            {
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

        return new StoryDiverts(scenes.SelectMany(SceneSteps.Diverts).ToArray(), hubOffers);
    }

    internal static IEnumerable<SceneId> ResolveTags(StoryletTags query, IReadOnlyDictionary<string, HashSet<SceneId>> byTag)
    {
        var sets = query.Tags.Select(tag => byTag.GetValueOrDefault(tag)).ToArray();
        if (query.MatchAll && sets.Any(set => set is null))
            return [];

        var candidates = sets.OfType<HashSet<SceneId>>().OrderBy(set => set.Count).ToArray();
        if (candidates.Length == 0)
            return [];
        if (candidates.Length == 1)
            return candidates[0];

        var result = new HashSet<SceneId>(candidates[0]);
        foreach (var set in candidates.Skip(1))
        {
            if (query.MatchAll)
                result.IntersectWith(set);
            else
                result.UnionWith(set);
        }
        return result;
    }
}
