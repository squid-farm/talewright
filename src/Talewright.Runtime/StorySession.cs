using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.Randomness;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Narrative.Grammar;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Checks;
using Talewright.Rules.Diagnostics;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Rules.Storylets;
using Talewright.Rules.Triggers;
using Talewright.Tale.Model;

namespace Talewright.Runtime;

/// <summary>
/// The engine loop: drives a <see cref="StoryDefinition"/> through the rules layer (effects, checks, triggers,
/// storylet selection) and the narrative layer (rendering), recording everything in an <see cref="EventLog"/>.
/// </summary>
/// <remarks>
/// <para><b>Entering a scene</b>: <c>SceneEntered</c> → entry effects (<c>~</c> lines whose conditions hold) →
/// triggers and leveling → render the body → then one of:</para>
/// <list type="bullet">
/// <item>an entry <c>::: skill-check</c>: resolve it now, apply the outcome, and offer a single
/// <see cref="ContinueKey"/> choice leading to the outcome's target (or end, if it leads to END);</item>
/// <item>otherwise: offer explicit choices whose <c>requires</c> hold, plus (for <c>@hub</c> scenes) every
/// eligible storylet with the hub tag;</item>
/// <item>if that leaves nothing: offer <see cref="ContinueKey"/> to the first matching <c>-&gt;</c>
/// (or, for <c>-&gt; END</c>, end the story with this scene as the final frame).</item>
/// </list>
/// <para><b>Choosing</b>: <c>ChoiceMade</c> → the choice's effects → its skill check and outcome (if any) →
/// triggers and leveling → move to the choice's target, or fall through to the scene's first matching
/// <c>-&gt;</c> (evaluated <em>after</em> the effects, so effects can steer the divert).</para>
/// <para>All randomness comes from the seeded dice, so a seed plus the list of choice keys reproduces a
/// playthrough exactly (the basis for M2 saves).</para>
/// </remarks>
public sealed class StorySession : IStorySession
{
    /// <summary>Key of the synthetic "Continue" choice.</summary>
    public const string ContinueKey = "continue";

    /// <summary>Prefix of hub storylet choice keys (<c>storylet:scene.id</c>).</summary>
    public const string StoryletKeyPrefix = "storylet:";

    private readonly EventLog _log;
    private readonly TriggerEngine _triggers;
    private readonly TemplateRenderer _renderer;
    private readonly bool _captureEntry;
    private readonly Action<RuleDecision>? _observer;
    private readonly Action<SceneEntryBoundary, IReadOnlyList<GameEvent>>? _beforeEntry;
    private readonly Dictionary<string, Action<List<RenderedBlock>>> _actions = new(StringComparer.Ordinal);
    private IReadOnlyList<RenderedBlock> _pendingNotes = [];
    private readonly List<RenderedBlock> _entryNotes = [];
    private DivertTarget? _entryCheckTarget;

    /// <summary>Creates a session. <paramref name="seed"/> defaults to the story's seed, else a random one.</summary>
    public StorySession(StoryDefinition story, ulong? seed = null, IGrammar? grammar = null, CharacterSelection? character = null)
        : this(story, seed, grammar, character, setup: null, observer: null)
    {
    }

    public StorySession(StoryDefinition story, ulong? seed, IGrammar? grammar,
        CharacterSelection? character, Action<RuleDecision>? observer)
        : this(story, seed, grammar, character, setup: null, observer)
    {
    }

    internal StorySession(StoryDefinition story, ulong seed, CharacterSelection? character, AuthorSetup setup,
        Action<RuleDecision>? observer = null, Action<SceneEntryBoundary, IReadOnlyList<GameEvent>>? beforeEntry = null)
        : this(story, seed, grammar: null, character, setup, observer, beforeEntry)
    {
    }

    private StorySession(StoryDefinition story, ulong? seed, IGrammar? grammar, CharacterSelection? character,
        AuthorSetup? setup, Action<RuleDecision>? observer,
        Action<SceneEntryBoundary, IReadOnlyList<GameEvent>>? beforeEntry = null)
    {
        character = CharacterSelection.Validate(story, character);
        Story = story;
        Seed = seed ?? story.Seed ?? DeterministicDice.NewSeed();
        _log = new EventLog(story.World, Seed);
        if (character is not null)
        {
            var player = story.World.Characters[EntityId.Player];
            _log.Append(new CharacterIdentityChanged(EntityId.Player,
                character.FirstName ?? player.FirstName, character.LastName ?? player.LastName,
                character.Pronouns is null ? player.Pronouns : Pronouns.FromKeyword(character.Pronouns)!));
            var stats = new Dictionary<StatId, int>();
            if (story.Creation!.Presets.FirstOrDefault(p => p.Id == character.Preset) is { } preset)
            {
                foreach (var (stat, value) in preset.Stats)
                {
                    stats[stat] = value;
                }
            }
            foreach (var (stat, value) in character.Stats)
            {
                stats[new StatId(stat)] = value;
            }
            foreach (var (stat, value) in stats.OrderBy(kv => kv.Key.Value, StringComparer.Ordinal))
            {
                _log.Append(new StatChanged(EntityId.Player, stat, _log.State.GetBaseStat(EntityId.Player, stat), value));
            }
        }
        setup?.Apply(_log);
        _captureEntry = setup is not null;
        _observer = observer;
        _beforeEntry = beforeEntry;
        _triggers = new TriggerEngine(story.Triggers, story.Predicates, _log.State, observer, _log.Events.Count);
        _renderer = new TemplateRenderer(grammar ?? new EnglishGrammar(), story.Predicates);
    }

    /// <inheritdoc/>
    public StoryDefinition Story { get; }

    /// <inheritdoc/>
    public ulong Seed { get; }

    /// <inheritdoc/>
    public IGameStateView State => _log.State;

    /// <inheritdoc/>
    public IReadOnlyList<GameEvent> Events => _log.Events;

    /// <inheritdoc/>
    public StoryFrame? Current { get; private set; }

    private EvaluationContext Context => new(_log.State, Story.Predicates);

    /// <inheritdoc/>
    public StoryFrame Start()
        => StartAt(Story.Start);

    internal StoryFrame StartAt(SceneId scene)
    {
        if (Current is not null)
        {
            throw new InvalidOperationException("The session has already started.");
        }

        return Enter(new DivertTarget(scene), []);
    }

    internal void EditAuthorState(AuthorSetup values)
    {
        RequireAuthorBoundary();
        var mark = _log.Events.Count;
        values.Apply(_log);
        AfterChanges();
        _entryNotes.AddRange(NotesSince(mark));
        var scene = Story.GetScene(Current!.Scene!.Value);
        var blocks = new List<RenderedBlock>(_pendingNotes);
        blocks.AddRange(_renderer.Render(scene.Body, _log.State).Blocks);
        blocks.AddRange(_entryNotes);
        if (scene.EntryCheck is not null)
        {
            // Its effects and roll already happened on entry. Keep the resolved continuation.
            SkipChoices(scene);
            Present(scene, blocks, ContinueTo(_entryCheckTarget
                ?? throw new InvalidOperationException("The entry-check continuation is unavailable. Start a fresh scene.")));
            return;
        }
        var choices = OfferChoices(scene);
        if (choices.Count > 0)
        {
            SkipDiverts(scene, DecisionReason.ChoicesAvailable);
            Present(scene, blocks, choices);
            return;
        }
        var next = FirstMatchingDivert(scene);
        if (next is null || next.IsEnd)
        {
            throw new InvalidOperationException("This edit leaves no live continuation. Start a fresh scene with prerequisite overrides instead.");
        }
        Present(scene, blocks, ContinueTo(next));
    }

    internal void JumpAuthor(SceneId target)
    {
        RequireAuthorBoundary();
        Story.GetScene(target);
        Enter(new DivertTarget(target), []);
    }

    private void RequireAuthorBoundary()
    {
        if (!_captureEntry || Current is null || Current.IsEnded || Current.Scene is null)
        {
            throw new InvalidOperationException("Live edits and preserve-state jumps require an active passage. For an ended run, use a fresh-world scene start.");
        }
    }

    /// <inheritdoc/>
    public StoryFrame Choose(string key)
    {
        if (Current is null)
        {
            throw new InvalidOperationException("Call Start() first.");
        }

        if (!_actions.TryGetValue(key, out var action))
        {
            throw new InvalidOperationException(
                $"'{key}' is not an available choice. Available: {string.Join(", ", _actions.Keys)}.");
        }

        var notes = new List<RenderedBlock>();
        EntryBoundary = null;
        action(notes);
        return Current;
    }

    // ---- scene entry -----------------------------------------------------------------------------------------------

    internal SceneEntryBoundary? EntryBoundary { get; private set; }

    private StoryFrame Enter(DivertTarget target, List<RenderedBlock> pending)
    {
        if (target.IsEnd)
        {
            EntryBoundary = null;
            return EndStory(pending, scene: null);
        }

        var scene = Story.GetScene(target.Scene!.Value);
        var mark = _log.Events.Count;
        if (_captureEntry)
        {
            EntryBoundary = new SceneEntryBoundary(scene.Id, mark, Array.AsReadOnly(pending.ToArray()),
                _triggers.CaptureEdgeMemory());
            _beforeEntry?.Invoke(EntryBoundary, _log.Events);
        }
        _log.Append(new SceneEntered(scene.Id));
        for (var i = 0; i < scene.EntryEffects.Count; i++)
        {
            var entry = scene.EntryEffects[i];
            var start = _log.Events.Count;
            var applies = Test(entry.Condition, out var trace);
            if (applies)
            {
                EffectExecutor.Execute(entry.Effect, _log, Story.Predicates);
            }
            if (_observer is not null)
            {
                Observe(DecisionKind.EntryEffect, $"entry:{i}", applies ? DecisionOutcome.Applied : DecisionOutcome.Excluded,
                    entry.Condition is null ? DecisionReason.Unconditional
                        : applies ? DecisionReason.ConditionTrue : DecisionReason.ConditionFalse, trace, start);
            }
        }

        AfterChanges();
        if (_captureEntry)
        {
            _pendingNotes = pending.ToArray();
            _entryNotes.Clear();
            _entryCheckTarget = null;
        }
        var blocks = new List<RenderedBlock>(pending);
        blocks.AddRange(_renderer.Render(scene.Body, _log.State).Blocks);
        AppendEntryNotes(blocks, mark);

        DivertTarget? next;
        if (scene.EntryCheck is { } check)
        {
            SkipChoices(scene);
            var checkMark = _log.Events.Count;
            next = ResolveCheck(check, scene);
            if (_captureEntry)
            {
                _entryCheckTarget = next;
            }
            AppendEntryNotes(blocks, checkMark);
        }
        else
        {
            var choices = OfferChoices(scene);
            if (choices.Count > 0)
            {
                SkipDiverts(scene, DecisionReason.ChoicesAvailable);
                return Present(scene, blocks, choices);
            }

            next = FirstMatchingDivert(scene);
        }

        if (next is null)
        {
            blocks.Add(new RenderedNote(NoteKind.Info, $"The story cannot continue from '{scene.Id}': no choice or divert applies here."));
            return EndStory(blocks, scene);
        }

        // A scene that just ends the story is shown as the final frame rather than behind a "Continue".
        return next.IsEnd ? EndStory(blocks, scene) : Present(scene, blocks, ContinueTo(next));
    }

    private List<(ChoiceView View, Action<List<RenderedBlock>> Action)> OfferChoices(SceneDefinition scene)
    {
        var offered = new List<(ChoiceView, Action<List<RenderedBlock>>)>();
        foreach (var choice in scene.Choices)
        {
            var applies = Test(choice.Requires, out var trace);
            Observe(DecisionKind.Choice, choice.Key, applies ? DecisionOutcome.Offered : DecisionOutcome.Excluded,
                choice.Requires is null ? DecisionReason.Unconditional
                    : applies ? DecisionReason.ConditionTrue : DecisionReason.ConditionFalse, trace, _log.Events.Count);
            if (applies)
            {
                var view = new ChoiceView(choice.Key, _renderer.RenderInlines(choice.Label, _log.State));
                offered.Add((view, notes => MakeChoice(scene, choice, notes)));
            }
        }

        if (scene.HubSelection is { } hub)
        {
            foreach (var storylet in StoryletSelector.SelectEligible(Story.Scenes, hub, Context, scene.Id,
                scene.HubLimit, _observer, _log.Events.Count))
            {
                var label = new RenderedInline[] { new RenderedText(storylet.Title ?? storylet.Id.Value, TextStyle.None) };
                var key = StoryletKeyPrefix + storylet.Id.Value;
                offered.Add((new ChoiceView(key, label), notes =>
                {
                    _log.Append(new ChoiceMade(scene.Id, key));
                    Enter(new DivertTarget(storylet.Id), notes);
                }));
            }
        }

        return offered;
    }

    private List<(ChoiceView View, Action<List<RenderedBlock>> Action)> ContinueTo(DivertTarget target)
    {
        var label = new RenderedInline[] { new RenderedText("Continue", TextStyle.None) };
        return [(new ChoiceView(ContinueKey, label), notes =>
        {
            _log.Append(new ChoiceMade(Current!.Scene ?? Story.Start, ContinueKey));
            Enter(target, notes);
        })];
    }

    // ---- choosing --------------------------------------------------------------------------------------------------

    private void MakeChoice(SceneDefinition scene, ChoiceDefinition choice, List<RenderedBlock> notes)
    {
        var mark = _log.Events.Count;
        _log.Append(new ChoiceMade(scene.Id, choice.Key));
        EffectExecutor.ExecuteAll(choice.Effects, _log, Story.Predicates);
        AfterChanges();

        DivertTarget? target;
        if (choice.Check is { } check)
        {
            target = ResolveCheck(check, scene);
        }
        else
        {
            if (choice.Divert is not null)
            {
                SkipDiverts(scene, DecisionReason.ExplicitTarget);
            }
            target = choice.Divert ?? FirstMatchingDivert(scene);
        }

        notes.AddRange(NotesSince(mark));
        if (target is null)
        {
            notes.Add(new RenderedNote(NoteKind.Info, $"The story cannot continue from '{scene.Id}': no divert applies after this choice."));
            EndStory(notes, scene);
            return;
        }

        Enter(target, notes);
    }

    /// <summary>Resolves a check and applies its outcome; returns where to go next (null = stuck).</summary>
    private DivertTarget? ResolveCheck(CheckBranch check, SceneDefinition scene)
    {
        var result = SkillCheckResolver.Resolve(check.Check, _log, Story.Randomness, Story.Predicates, _observer);
        var outcome = result.Success ? check.Success : check.Failure;
        var start = _log.Events.Count;
        EffectExecutor.ExecuteAll(outcome.Effects, _log, Story.Predicates);
        AfterChanges();
        if (_observer is not null)
        {
            Observe(DecisionKind.CheckOutcome, $"{check.Check.Actor}:{check.Check.Skill}",
                DecisionOutcome.Applied, result.Success ? DecisionReason.CheckSucceeded : DecisionReason.CheckFailed, null, start);
        }
        if (outcome.Divert is not null)
        {
            SkipDiverts(scene, DecisionReason.ExplicitTarget);
        }
        return outcome.Divert ?? FirstMatchingDivert(scene);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    /// <summary>Runs triggers, then applies any level-ups, after any batch of changes.</summary>
    private void AfterChanges()
    {
        for (var pass = 0; pass < TriggerEngine.MaxPasses; pass++)
        {
            _triggers.Run(_log);
            var levelChanges = LevelChanges();
            if (levelChanges.Count == 0)
            {
                return;
            }

            _log.AppendRange(levelChanges);
        }

        _triggers.Run(_log);
        if (LevelChanges().Count > 0)
        {
            throw new InvalidOperationException(
                $"Level-up consequences did not settle after {TriggerEngine.MaxPasses} passes.");
        }
    }

    private List<GameEvent> LevelChanges()
    {
        var changes = new List<GameEvent>();
        foreach (var character in Story.World.Characters.Keys)
        {
            changes.AddRange(StateChanges.CheckLevel(_log.State, character));
        }

        return changes;
    }

    private DivertTarget? FirstMatchingDivert(SceneDefinition scene)
    {
        DivertTarget? selected = null;
        for (var i = 0; i < scene.Diverts.Count; i++)
        {
            var divert = scene.Diverts[i];
            if (selected is not null)
            {
                ObserveSkipped(DecisionKind.Divert, $"divert:{i}->{divert.Target}", divert.Condition, DecisionReason.EarlierDivert);
                continue;
            }
            var applies = Test(divert.Condition, out var trace);
            if (_observer is not null)
            {
                Observe(DecisionKind.Divert, $"divert:{i}->{divert.Target}",
                    applies ? DecisionOutcome.Selected : DecisionOutcome.Excluded,
                    divert.Condition is null ? DecisionReason.Unconditional
                        : applies ? DecisionReason.ConditionTrue : DecisionReason.ConditionFalse, trace, _log.Events.Count);
            }
            if (applies)
            {
                selected = divert.Target;
                if (_observer is null)
                {
                    break;
                }
            }
        }
        return selected;
    }

    private bool Test(Expr? expression, out ExpressionTrace? trace)
        => RuleObservation.Test(expression, Context, _observer is not null, out trace);

    private void Observe(DecisionKind kind, string subject, DecisionOutcome outcome, DecisionReason reason,
        ExpressionTrace? trace, int start) => _observer?.Invoke(new RuleDecision(kind, subject, State.CurrentScene,
            outcome, reason, trace, start, _log.Events.Count));

    private void ObserveSkipped(DecisionKind kind, string subject, Expr? expression, DecisionReason reason)
        => _observer?.Invoke(new RuleDecision(kind, subject, State.CurrentScene, DecisionOutcome.NotEvaluated,
            reason, expression is null ? null : ExpressionTrace.NotEvaluated(expression), _log.Events.Count, _log.Events.Count));

    private void SkipDiverts(SceneDefinition scene, DecisionReason reason)
    {
        if (_observer is null)
        {
            return;
        }
        for (var i = 0; i < scene.Diverts.Count; i++)
        {
            var divert = scene.Diverts[i];
            ObserveSkipped(DecisionKind.Divert, $"divert:{i}->{divert.Target}", divert.Condition, reason);
        }
    }

    private void SkipChoices(SceneDefinition scene)
    {
        if (_observer is null)
        {
            return;
        }
        foreach (var choice in scene.Choices)
        {
            ObserveSkipped(DecisionKind.Choice, choice.Key, choice.Requires, DecisionReason.EntryCheck);
        }
        if (scene.HubSelection is not null)
        {
            foreach (var storylet in Story.Scenes)
            {
                ObserveSkipped(DecisionKind.Storylet, storylet.Id.Value, storylet.Requires, DecisionReason.EntryCheck);
            }
        }
    }

    private IEnumerable<RenderedBlock> NotesSince(int mark) =>
        EventNarrator.Describe(_log.Events.Skip(mark).ToList(), _log.State);

    private void AppendEntryNotes(List<RenderedBlock> blocks, int mark)
    {
        var notes = NotesSince(mark);
        if (_captureEntry)
        {
            var captured = notes.ToArray();
            _entryNotes.AddRange(captured);
            blocks.AddRange(captured);
        }
        else
        {
            blocks.AddRange(notes);
        }
    }

    private StoryFrame Present(SceneDefinition scene, List<RenderedBlock> blocks, List<(ChoiceView View, Action<List<RenderedBlock>> Action)> choices)
    {
        _actions.Clear();
        foreach (var (view, action) in choices)
        {
            _actions[view.Key] = action;
        }

        Current = new StoryFrame(scene.Id, scene.Title, new RenderedDocument(blocks), choices.Select(c => c.View).ToList(), IsEnded: false);
        return Current;
    }

    private StoryFrame EndStory(List<RenderedBlock> blocks, SceneDefinition? scene)
    {
        if (!_log.State.IsEnded)
        {
            _log.Append(new StoryEnded());
        }

        _actions.Clear();
        Current = new StoryFrame(scene?.Id ?? Current?.Scene, scene?.Title ?? Current?.Title, new RenderedDocument(blocks), [], IsEnded: true);
        return Current;
    }
}

internal sealed record SceneEntryBoundary(SceneId Scene, int EventCount, IReadOnlyList<RenderedBlock> Pending,
    IReadOnlyList<string> TriggerEdges);
