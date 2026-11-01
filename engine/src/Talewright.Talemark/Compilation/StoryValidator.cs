using Talewright.Core.Identity;
using Talewright.Rules.Expressions;
using Talewright.Rules.Storylets;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Talemark.Parsing;

namespace Talewright.Talemark.Compilation;

/// <summary>
/// Whole-story structural checks that need every scene at once (<c>TW3xxx</c>): the start scene exists, no
/// scene is a dead end, choices have explicit targets and a guaranteed option, hubs have content,
/// and every scene is reachable. Reference checks (unknown scenes, stats, items…) already happened
/// while parsing.
/// </summary>
internal static class StoryValidator
{
    /// <summary>Returns the reachability TW3009 used, or null when the story has errors.</summary>
    public static StoryReachability? Validate(StoryManifest manifest, IReadOnlyList<ParsedScene> scenes, DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checkpoint = cancellationToken.CanBeCanceled ? new CancellationCheckpoint(cancellationToken) : null;
        var byId = new Dictionary<SceneId, ParsedScene>();
        foreach (var scene in scenes)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var tag in scene.Tags)
            {
                checkpoint?.Check();
                if (!byTag.TryGetValue(tag, out var ids))
                {
                    byTag[tag] = ids = [];
                }
                ids.Add(scene.Id);
            }
        }

        foreach (var (scene, location) in scenes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckExits(scene, location, diagnostics, cancellationToken, checkpoint);
            if (scene.HubLimit is not null && scene.Hub is null)
            {
                diagnostics.Error("TW1006", "@hub-limit requires @hub in the same scene.", location);
            }
            if (scene.Hub is { } hub && !StoryDiverts.ResolveTags(hub, byTag, checkpoint).Any(id => id != scene.Id))
            {
                diagnostics.Warning("TW3008", $"Hub '{scene.Id}' has no other scenes matching its tags, so it offers nothing.", location);
            }
        }

        // Broken references distort the scene graph, so a single bad divert would flag everything after it as
        // unreachable. Only report reachability once the story is otherwise error-free.
        if (!byId.ContainsKey(start) || diagnostics.HasErrors)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var definitions = scenes.Select(scene => scene.Definition).ToArray();
        var reachability = StoryReachability.Find(definitions, StoryDiverts.From(definitions, checkpoint), start, checkpoint);
        foreach (var (scene, location) in scenes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reachability.IsReached(scene.Id))
            {
                diagnostics.Warning("TW3009", $"Scene '{scene.Id}' can never be reached from '{start}'.", location);
            }
        }

        return reachability;
    }

    private static void CheckExits(SceneDefinition scene, SourceLocation location, DiagnosticBag diagnostics,
        CancellationToken cancellationToken, CancellationCheckpoint? checkpoint)
    {
        var seenChoice = false;
        CheckSteps(scene.Steps, ref seenChoice, diagnostics, checkpoint);
        cancellationToken.ThrowIfCancellationRequested();
        var hasReachableChoice = SceneSteps.Reachable(scene.Steps, checkpoint).OfType<ChoiceStep>().Any();
        cancellationToken.ThrowIfCancellationRequested();
        if (!SceneSteps.Targets(scene.Steps, checkpoint).Any()
            && !hasReachableChoice && scene.Hub is null)
        {
            diagnostics.Error("TW3002", $"Scene '{scene.Id}' is a dead end: add choices, a '-> target', or '-> END'.", location);
        }
        if ((hasReachableChoice || scene.Hub is not null) &&
            CanReachEndWithoutChoice(scene.Steps, checkpoint))
        {
            diagnostics.Warning("TW3013", $"Scene '{scene.Id}' may have no available choice. Add an unconditional choice such as '* -> target' or '* [The end] -> END'.", location);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool CanReachEndWithoutChoice(IReadOnlyList<SceneStep> steps, CancellationCheckpoint? checkpoint)
    {
        foreach (var step in steps)
        {
            checkpoint?.Check();
            if (step is ChoiceStep { Choice.Requires: null })
            {
                return false;
            }
            if (step is ConditionalStep branch)
            {
                var canReachEnd = branch.Condition switch
                {
                    BoolLiteral { Value: true } => CanReachEndWithoutChoice(branch.Then, checkpoint),
                    BoolLiteral { Value: false } => CanReachEndWithoutChoice(branch.Else, checkpoint),
                    _ => CanReachEndWithoutChoice(branch.Then, checkpoint) ||
                         CanReachEndWithoutChoice(branch.Else, checkpoint),
                };
                if (!canReachEnd)
                {
                    return false;
                }
            }
            else if (!SceneSteps.CanContinue(step, checkpoint))
            {
                return false;
            }
        }
        return true;
    }

    private static void CheckSteps(IReadOnlyList<SceneStep> steps, ref bool seenChoice, DiagnosticBag diagnostics,
        CancellationCheckpoint? checkpoint)
    {
        var reachable = true;
        foreach (var step in steps)
        {
            checkpoint?.Check();
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
                CheckSteps(branch.Then, ref seenChoice, diagnostics, checkpoint);
                CheckSteps(branch.Else, ref seenChoice, diagnostics, checkpoint);
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
            reachable &= SceneSteps.CanContinue(step, checkpoint);
        }
    }
}
