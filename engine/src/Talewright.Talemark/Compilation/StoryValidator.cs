using Talewright.Core.Identity;
using Talewright.Rules.Storylets;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Talemark.Parsing;

namespace Talewright.Talemark.Compilation;

/// <summary>
/// Whole-story structural checks that need every scene at once (<c>TW3xxx</c>): the start scene exists, no
/// scene is a dead end, choices have explicit targets, hubs have content, and every scene is
/// reachable. Reference checks (unknown scenes, stats, items…) already happened while parsing.
/// </summary>
internal static class StoryValidator
{
    public static void Validate(StoryManifest manifest, IReadOnlyList<ParsedScene> scenes, DiagnosticBag diagnostics)
    {
        var byId = new Dictionary<SceneId, ParsedScene>();
        foreach (var scene in scenes)
        {
            byId.TryAdd(scene.Definition.Id, scene);
        }

        var start = new SceneId(manifest.Start);
        if (manifest.Start.Length > 0 && !byId.ContainsKey(start))
        {
            diagnostics.Error("TW3007", $"Start scene '{manifest.Start}' does not exist.", manifest.StartLocation);
        }

        var byTag = new Dictionary<string, HashSet<SceneId>>(StringComparer.Ordinal);
        foreach (var (scene, _) in scenes)
        {
            foreach (var tag in scene.Tags)
            {
                if (!byTag.TryGetValue(tag, out var ids))
                {
                    byTag[tag] = ids = [];
                }
                ids.Add(scene.Id);
            }
        }

        foreach (var (scene, location) in scenes)
        {
            CheckExits(scene, location, diagnostics);
            if (scene.HubLimit is not null && scene.Hub is null)
            {
                diagnostics.Error("TW1006", "@hub-limit requires @hub in the same scene.", location);
            }
            if (scene.Hub is { } hub && !ResolveTags(hub, byTag).Any(id => id != scene.Id))
            {
                diagnostics.Warning("TW3008", $"Hub '{scene.Id}' has no other scenes matching its tags, so it offers nothing.", location);
            }
        }

        // Broken references distort the scene graph, so a single bad divert would flag everything after it as
        // unreachable. Only report reachability once the story is otherwise error-free.
        if (!byId.ContainsKey(start) || diagnostics.HasErrors)
        {
            return;
        }

        var reached = new HashSet<SceneId> { start };
        var queue = new Queue<SceneId>([start]);
        while (queue.Count > 0)
        {
            foreach (var next in Successors(byId[queue.Dequeue()].Definition, byTag))
            {
                if (byId.ContainsKey(next) && reached.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        foreach (var (scene, location) in scenes)
        {
            if (!reached.Contains(scene.Id))
            {
                diagnostics.Warning("TW3009", $"Scene '{scene.Id}' can never be reached from '{start}'.", location);
            }
        }
    }

    private static void CheckExits(SceneDefinition scene, SourceLocation location, DiagnosticBag diagnostics)
    {
        var seenChoice = false;
        CheckSteps(scene.Steps, ref seenChoice, diagnostics);
        if (!SceneSteps.Targets(scene.Steps).Any()
            && !SceneSteps.Reachable(scene.Steps).OfType<ChoiceStep>().Any() && scene.Hub is null)
        {
            diagnostics.Error("TW3002", $"Scene '{scene.Id}' is a dead end: add choices, a '-> target', or '-> END'.", location);
        }
    }

    private static void CheckSteps(IReadOnlyList<SceneStep> steps, ref bool seenChoice, DiagnosticBag diagnostics)
    {
        var reachable = true;
        foreach (var step in steps)
        {
            if (!reachable)
            {
                diagnostics.Warning("TW3012", "This line cannot run: an earlier step always leaves this block.", step.Source.Start);
            }
            if (seenChoice && step is not (ChoiceStep or ConditionalStep))
            {
                diagnostics.Error("TW3011", "Choices must be the final block of a scene. Move prose, effects, diverts and checks before the first choice.", step.Source.Start);
            }
            if (step is ConditionalStep branch)
            {
                CheckSteps(branch.Then, ref seenChoice, diagnostics);
                CheckSteps(branch.Else, ref seenChoice, diagnostics);
            }
            if (step is ChoiceStep choice)
            {
                seenChoice = true;
                var missingTarget = choice.Choice.Check is { } check
                    ? check.Success.Divert is null || check.Failure.Divert is null
                    : choice.Choice.Divert is null;
                if (missingTarget)
                {
                    diagnostics.Error("TW3003", "Every choice and each choice skill-check outcome needs its own '-> target' or '-> END'. There is no scene fallback.", step.Source.Start);
                }
            }
            reachable &= SceneSteps.CanContinue(step);
        }
    }

    private static IEnumerable<SceneId> ResolveTags(StoryletTags query, IReadOnlyDictionary<string, HashSet<SceneId>> byTag)
    {
        var sets = query.Tags.Select(tag => byTag.GetValueOrDefault(tag)).ToArray();
        if (query.MatchAll && sets.Any(set => set is null))
        {
            return [];
        }

        var candidates = sets.OfType<HashSet<SceneId>>().OrderBy(set => set.Count).ToArray();
        if (candidates.Length == 0)
        {
            return [];
        }
        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        var result = new HashSet<SceneId>(candidates[0]);
        foreach (var set in candidates.Skip(1))
        {
            if (query.MatchAll)
            {
                result.IntersectWith(set);
            }
            else
            {
                result.UnionWith(set);
            }
        }
        return result;
    }

    private static IEnumerable<SceneId> Successors(SceneDefinition scene, IReadOnlyDictionary<string, HashSet<SceneId>> byTag)
    {
        foreach (var target in SceneSteps.Targets(scene.Steps))
        {
            if (target?.Scene is { } id)
            {
                yield return id;
            }
        }

        if (scene.Hub is { } hub && SceneSteps.CanContinue(scene.Steps))
        {
            foreach (var candidate in ResolveTags(hub, byTag))
            {
                if (candidate != scene.Id)
                {
                    yield return candidate;
                }
            }
        }
    }
}
