using Talewright.Core.Identity;
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

        var tagged = scenes
            .SelectMany(s => s.Definition.Tags.Select(t => (Tag: t, s.Definition.Id)))
            .ToLookup(x => x.Tag, x => x.Id);

        foreach (var (scene, location) in scenes)
        {
            CheckExits(scene, location, diagnostics);
            if (scene.HubTag is { Length: > 0 } hub && !tagged.Contains(hub))
            {
                diagnostics.Warning("TW3008", $"Hub '{hub}' has no scenes tagged '{hub}', so it offers nothing.", location);
            }
        }

        if (!byId.ContainsKey(start))
        {
            return;
        }

        var reached = new HashSet<SceneId> { start };
        var queue = new Queue<SceneId>([start]);
        while (queue.Count > 0)
        {
            foreach (var next in Successors(byId[queue.Dequeue()].Definition, tagged))
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
        if (!hasOwnDivert && scene.Choices.Count == 0 && scene.EntryCheck is null && string.IsNullOrEmpty(scene.HubTag))
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

    private static IEnumerable<SceneId> Successors(SceneDefinition scene, ILookup<string, SceneId> tagged)
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

        if (!string.IsNullOrEmpty(scene.HubTag))
        {
            foreach (var id in tagged[scene.HubTag])
            {
                yield return id;
            }
        }
    }
}
