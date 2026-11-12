using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.Randomness;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Narrative.Grammar;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Diagnostics;
using Talewright.Rules.Expressions;
using Talewright.Rules.Triggers;
using Talewright.Talemark.Model;

namespace Talewright.Runtime;

/// <summary>
/// The engine loop: drives a <see cref="StoryDefinition"/> through the rules layer (effects, checks, triggers,
/// storylet selection) and the narrative layer (rendering), recording everything in an <see cref="EventLog"/>.
/// </summary>
/// <remarks>
/// <para>Scenes execute top to bottom through <see cref="SceneExecutor"/>. Triggers and leveling settle
/// after each effect batch. An immediate divert stops execution; otherwise collected choices and hub
/// storylets are offered. Choices always have explicit targets.</para>
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
    private readonly SceneExecutor _executor;
    private readonly ReplayIdentityTable _replayIdentities;
    private readonly bool _captureEntry;
    private readonly Action<RuleDecision>? _observer;
    private readonly Action<SceneEntryBoundary, IReadOnlyList<GameEvent>>? _beforeEntry;
    private readonly Dictionary<string,
        (Action<List<RenderedBlock>> Execute, ReadOnlyMemory<byte>? ReplayIdentity)> _actions = new(StringComparer.Ordinal);
    private IReadOnlyList<RenderedBlock> _pendingNotes = [];
    private IReadOnlyList<RenderedBlock> _entryPrelude = [];
    private readonly List<RenderedBlock> _editNotes = [];

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
        _replayIdentities = story.ReplayIdentities;
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
        var renderer = new TemplateRenderer(grammar ?? new EnglishGrammar(), story.Predicates);
        _executor = new SceneExecutor(story, _log, renderer, AfterBatch, observer, _captureEntry, MakeChoice);
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
        AfterBatch();
        _editNotes.AddRange(NotesSince(mark));
        var scene = Story.GetScene(Current!.Scene!.Value);
        var execution = _executor.Run(scene, refresh: true);
        var blocks = new List<RenderedBlock>(_pendingNotes);
        blocks.AddRange(_entryPrelude);
        blocks.AddRange(execution.Blocks);
        blocks.AddRange(_editNotes);
        if (execution.Target is not null) SkipHub(scene);
        if (execution.Target?.IsEnd == true)
        {
            throw new InvalidOperationException("This edit leaves no live continuation. Start a fresh scene with prerequisite overrides instead.");
        }
        var choices = execution.Target is null ? execution.Choices : ContinueTo(scene, execution.Target);
        var menu = BuildMenu(scene, choices, includeHubOffers: execution.Target is null);
        if (menu.Choices.Count == 0)
        {
            throw new InvalidOperationException("This edit leaves no live continuation. Start a fresh scene with prerequisite overrides instead.");
        }
        Present(scene, blocks, menu);
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
        action.Execute(notes);
        return Current;
    }

    internal bool ChooseReplay(byte[] replayIdentity, out ChoiceView? selected)
    {
        selected = null;
        if (Current?.Scene is not { } currentScene)
        {
            return false;
        }

        var scene = Story.GetScene(currentScene);
        foreach (var step in _executor.ReplayChoices)
        {
            if (!_replayIdentities.For(step.Choice).AsSpan().SequenceEqual(replayIdentity))
            {
                continue;
            }

            selected = _executor.View(step.Choice);
            MakeChoice(scene, step, []);
            return true;
        }

        if (scene.Hub is not null)
        {
            foreach (var (storylet, _) in Story.Scenes.Select((candidate, index) => (candidate, index))
                .Where(entry => entry.candidate.Id != scene.Id)
                .OrderByDescending(entry => entry.candidate.Priority)
                .ThenBy(entry => entry.index))
            {
                var target = new DivertTarget(storylet.Id);
                var identity = _replayIdentities.ForDivert(scene.Id, target);
                if (!identity.Span.SequenceEqual(replayIdentity))
                {
                    continue;
                }

                var key = StoryletKeyPrefix + storylet.Id.Value;
                if (_actions.TryGetValue(key, out var action))
                {
                    selected = Current.Choices.First(choice => choice.Key == key);
                    action.Execute([]);
                }
                else
                {
                    selected = ChoiceAvailability.CreateView(key, HubLabelSource(storylet), identity);
                    _log.Append(new ChoiceMade(scene.Id, key));
                    Enter(target, []);
                }
                return true;
            }
        }

        foreach (var (key, action) in _actions)
        {
            if (action.ReplayIdentity is not { } identity || !identity.Span.SequenceEqual(replayIdentity))
            {
                continue;
            }

            selected = Current.Choices.First(choice => choice.Key == key);
            action.Execute([]);
            return true;
        }

        return false;
    }

    // ---- scene entry -----------------------------------------------------------------------------------------------

    internal SceneEntryBoundary? EntryBoundary { get; private set; }

    private StoryFrame Enter(DivertTarget target, List<RenderedBlock> pending)
    {
        BeforeTransition();
        if (target.IsEnd)
        {
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
        AfterBatch();
        var prelude = NotesSince(mark).ToArray();
        if (_captureEntry)
        {
            _pendingNotes = pending.ToArray();
            _entryPrelude = prelude;
            _editNotes.Clear();
        }
        var execution = _executor.Run(scene);
        var blocks = new List<RenderedBlock>(pending);
        blocks.AddRange(prelude);
        blocks.AddRange(execution.Blocks);
        if (execution.Target is { } next)
        {
            SkipHub(scene);
            return next.IsEnd ? EndStory(blocks, scene) : Present(scene, blocks,
                BuildMenu(scene, ContinueTo(scene, next), includeHubOffers: false));
        }
        var menu = BuildMenu(scene, execution.Choices);
        if (menu.Choices.Count == 0)
        {
            blocks.Add(new RenderedNote(NoteKind.Info, $"The story cannot continue from '{scene.Id}': no choice or divert applies here."));
            return EndStory(blocks, scene);
        }
        return Present(scene, blocks, menu);
    }

    private void BeforeTransition()
    {
        EntryBoundary = null;
        _actions.Clear();
    }

    private void SkipHub(SceneDefinition scene)
    {
        if (_observer is null || scene.Hub is null) return;
        foreach (var storylet in Story.Scenes)
            _observer(new(DecisionKind.Storylet, storylet.Id.Value, scene.Id, DecisionOutcome.NotEvaluated,
                DecisionReason.ExplicitTarget,
                storylet.Requires is null ? null : ExpressionTrace.NotEvaluated(storylet.Requires),
                _log.Events.Count, _log.Events.Count));
    }

    private StoryMenu BuildMenu(SceneDefinition scene, IReadOnlyList<VisibleChoice> choices,
        bool includeHubOffers = true) =>
        StoryMenuBuilder.Build(Story, scene, choices, Context,
            storylet => CreateHubCandidate(scene, storylet), _observer, _log.Events.Count, includeHubOffers);

    private ChoiceCandidate CreateHubCandidate(SceneDefinition scene, SceneDefinition storylet)
    {
        var key = StoryletKeyPrefix + storylet.Id.Value;
        var target = new DivertTarget(storylet.Id);
        var label = HubLabelSource(storylet);
        return new ChoiceCandidate(key, storylet.Requires, label,
            ChoiceAvailability.ShownLabelSource(storylet.Shown, label, _executor.Render),
            notes =>
            {
                _log.Append(new ChoiceMade(scene.Id, key));
                Enter(target, notes);
            },
            _replayIdentities.ForDivert(scene.Id, target));
    }

    private Func<IReadOnlyList<RenderedInline>> HubLabelSource(SceneDefinition storylet) =>
        storylet.ChoiceLabel is { } choiceLabel
            ? () => _executor.Render(choiceLabel)
            : () => [new RenderedText(storylet.Title ?? storylet.Id.Value, TextStyle.None)];

    private List<VisibleChoice> ContinueTo(
        SceneDefinition source, DivertTarget target)
    {
        var candidate = new ChoiceCandidate(ContinueKey, null, () => ChoiceView.ContinueLabel, null,
            notes =>
            {
                _log.Append(new ChoiceMade(source.Id, ContinueKey));
                Enter(target, notes);
            },
            _replayIdentities.ForDivert(source.Id, target));
        return [new(candidate, ChoiceAvailability.CreateView(candidate), IsAvailable: true)];
    }

    // ---- choosing --------------------------------------------------------------------------------------------------

    private void MakeChoice(SceneDefinition scene, ChoiceStep step, List<RenderedBlock> notes)
    {
        var choice = step.Choice;
        var mark = _log.Events.Count;
        _log.Append(new ChoiceMade(scene.Id, choice.Key));
        _executor.ApplyBatch(choice.Effects);
        var target = choice.Check is { } check ? _executor.ResolveCheck(check, step.Id) : choice.Divert;
        notes.AddRange(NotesSince(mark));
        if (target is null)
        {
            throw new InvalidOperationException($"Choice '{choice.Key}' in '{scene.Id}' has no explicit target.");
        }

        Enter(target, notes);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    /// <summary>Runs triggers, then applies any level-ups, after any batch of changes.</summary>
    private void AfterBatch()
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

    private IEnumerable<RenderedBlock> NotesSince(int mark) =>
        EventNarrator.Describe(_log.Events.Skip(mark).ToList(), _log.State);

    private StoryFrame Present(SceneDefinition scene, List<RenderedBlock> blocks, StoryMenu menu)
    {
        _actions.Clear();
        foreach (var choice in menu.Choices.Where(choice => choice.IsAvailable))
        {
            _actions[choice.Candidate.Key] = (choice.Candidate.Destination, choice.View.ReplayIdentity);
        }

        Current = new StoryFrame(scene.Id, scene.Title, new RenderedDocument(blocks),
            menu.AvailableChoices, IsEnded: false)
        {
            ContinuesPrevious = scene.ContinuesPrevious,
            ClearsPrevious = scene.ClearsPrevious,
            Menu = menu.Entries,
        };
        return Current;
    }

    private StoryFrame EndStory(List<RenderedBlock> blocks, SceneDefinition? scene)
    {
        if (!_log.State.IsEnded)
        {
            _log.Append(new StoryEnded());
        }

        _actions.Clear();
        var title = scene is { ChoiceLabel: not null } ? scene.Title : scene?.Title ?? Current?.Title;
        Current = new StoryFrame(scene?.Id ?? Current?.Scene, title,
            new RenderedDocument(blocks), [], IsEnded: true)
        {
            ContinuesPrevious = scene?.ContinuesPrevious ?? Current?.ContinuesPrevious ?? false,
            ClearsPrevious = scene?.ClearsPrevious ?? false,
        };
        return Current;
    }
}

internal sealed record SceneEntryBoundary(SceneId Scene, int EventCount, IReadOnlyList<RenderedBlock> Pending,
    IReadOnlyList<string> TriggerEdges);
