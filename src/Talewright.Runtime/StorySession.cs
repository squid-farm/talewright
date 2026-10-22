using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.Randomness;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Narrative.Grammar;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Checks;
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
    private readonly Dictionary<string, Action<List<RenderedBlock>>> _actions = new(StringComparer.Ordinal);

    /// <summary>Creates a session. <paramref name="seed"/> defaults to the story's seed, else a random one.</summary>
    public StorySession(StoryDefinition story, ulong? seed = null, IGrammar? grammar = null, CharacterSelection? character = null)
        : this(story, seed, grammar, character, setup: null)
    {
    }

    internal StorySession(StoryDefinition story, ulong seed, CharacterSelection? character, AuthorSetup setup)
        : this(story, seed, grammar: null, character, setup)
    {
    }

    private StorySession(StoryDefinition story, ulong? seed, IGrammar? grammar, CharacterSelection? character, AuthorSetup? setup)
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
        _triggers = new TriggerEngine(story.Triggers, story.Predicates, _log.State);
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
            EntryBoundary = new SceneEntryBoundary(scene.Id, mark, Array.AsReadOnly(pending.ToArray()));
        }
        _log.Append(new SceneEntered(scene.Id));
        foreach (var entry in scene.EntryEffects)
        {
            if (entry.Condition is null || ExpressionEvaluator.IsTrue(entry.Condition, Context))
            {
                EffectExecutor.Execute(entry.Effect, _log, Story.Predicates);
            }
        }

        AfterChanges();
        var blocks = new List<RenderedBlock>(pending);
        blocks.AddRange(_renderer.Render(scene.Body, _log.State).Blocks);
        blocks.AddRange(NotesSince(mark));

        DivertTarget? next;
        if (scene.EntryCheck is { } check)
        {
            var checkMark = _log.Events.Count;
            next = ResolveCheck(check, scene);
            blocks.AddRange(NotesSince(checkMark));
        }
        else
        {
            var choices = OfferChoices(scene);
            if (choices.Count > 0)
            {
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
            if (choice.Requires is null || ExpressionEvaluator.IsTrue(choice.Requires, Context))
            {
                var view = new ChoiceView(choice.Key, _renderer.RenderInlines(choice.Label, _log.State));
                offered.Add((view, notes => MakeChoice(scene, choice, notes)));
            }
        }

        if (scene.HubSelection is { } hub)
        {
            foreach (var storylet in StoryletSelector.SelectEligible(Story.Scenes, hub, Context, scene.Id, scene.HubLimit))
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
        var result = SkillCheckResolver.Resolve(check.Check, _log, Story.Randomness, Story.Predicates);
        var outcome = result.Success ? check.Success : check.Failure;
        EffectExecutor.ExecuteAll(outcome.Effects, _log, Story.Predicates);
        AfterChanges();
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

    private DivertTarget? FirstMatchingDivert(SceneDefinition scene) =>
        scene.Diverts.FirstOrDefault(d => d.Condition is null || ExpressionEvaluator.IsTrue(d.Condition, Context))?.Target;

    private IEnumerable<RenderedBlock> NotesSince(int mark) =>
        EventNarrator.Describe(_log.Events.Skip(mark).ToList(), _log.State);

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

internal sealed record SceneEntryBoundary(SceneId Scene, int EventCount, IReadOnlyList<RenderedBlock> Pending);
