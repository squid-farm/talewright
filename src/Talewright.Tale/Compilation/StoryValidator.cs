using Talewright.Core.Identity;
using Talewright.Rules.Storylets;
using Talewright.Tale.Diagnostics;
using Talewright.Tale.Model;
using Talewright.Tale.Parsing;

namespace Talewright.Tale.Compilation;

/// <summary>
/// Whole-story structural checks that need every scene at once (<c>TW3xxx</c>): the start scene exists, no
/// scene is a dead end, fall-through choices have somewhere to fall to, hubs have content, and every scene is
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
            if (scene.HubLimit is not null && scene.HubSelection is null)
            {
                diagnostics.Error("TW1006", "@hub-limit requires @hub in the same scene.", location);
            }
            if (scene.HubSelection is { } hub && !ResolveTags(hub, byTag).Any(id => id != scene.Id))
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
        var hasOwnDivert = scene.Diverts.Count > 0;
        if (!hasOwnDivert && scene.Choices.Count == 0 && scene.EntryCheck is null && scene.HubSelection is null)
        {
            diagnostics.Error("TW3002", $"Scene '{scene.Id}' is a dead end: add choices, a '-> target', or '-> END'.", location);
            return;
        }

        if (hasOwnDivert)
        {
            return;
        }

        var fallsThrough =
            scene.Choices.Any(c => c.Check is null && c.Divert is null)
            || scene.Choices.Any(c => c.Check is { } check && (check.Success.Divert is null || check.Failure.Divert is null))
            || scene.EntryCheck is { } entry && (entry.Success.Divert is null || entry.Failure.Divert is null);
        if (fallsThrough)
        {
            diagnostics.Error("TW3003", $"Scene '{scene.Id}' has a choice or check outcome without '-> target', but the scene has no '-> target' of its own to fall through to.", location);
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
        IEnumerable<DivertTarget?> targets = scene.Diverts.Select(d => d.Target)
            .Concat(scene.Choices.Select(c => c.Divert))
            .Concat(scene.Choices.Where(c => c.Check is not null).SelectMany(c => new[] { c.Check!.Success.Divert, c.Check.Failure.Divert }))
            .Concat(scene.EntryCheck is { } e ? [e.Success.Divert, e.Failure.Divert] : []);
        foreach (var target in targets)
        {
            if (target?.Scene is { } id)
            {
                yield return id;
            }
        }

        if (scene.HubSelection is { } hub)
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
