using System.Diagnostics.CodeAnalysis;
using Talewright.Core.Identity;
using Talewright.Core.Randomness;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Narrative.Templates;
using Talewright.Rules.Checks;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Rules.Storylets;
using Talewright.Rules.Triggers;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Model;

/// <summary>
/// A fully compiled, validated story. Contains source positions, but no source text.
/// </summary>
public sealed class StoryDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Version { get; init; }
    public string? ContentFingerprint { get; init; }
    /// <summary>Identity of the replay semantics and registered extensions, independent of story content.</summary>
    public string? ReplayCompatibilityId { get; init; }
    public bool UndoEnabled { get; init; } = true;
    public bool AutosaveEnabled { get; init; } = true;
    /// <summary>Character sheet sections and stats to present; hidden stats remain available to story rules.</summary>
    public CharacterSheetVisibility CharacterSheet { get; init; } = CharacterSheetVisibility.Shown;
    /// <summary>Authored creation settings; use <see cref="GetCreationOptions"/> for selectable stats.</summary>
    public CharacterCreation? Creation { get; init; }
    public required SceneId Start { get; init; }
    public required RandomnessMode Randomness { get; init; }

    /// <summary>Fixed seed from story.talewright.yaml, or null for a random seed per playthrough.</summary>
    public ulong? Seed { get; init; }

    public required WorldDefinition World { get; init; }

    /// <summary>Scenes in declaration order.</summary>
    public required IReadOnlyList<SceneDefinition> Scenes { get; init; }

    public required IReadOnlyList<TriggerDefinition> Triggers { get; init; }

    /// <summary>Predicates referenced by <c>@Name</c> expressions; the runtime evaluates with the same registry.</summary>
    public required NamedPredicateRegistry Predicates { get; init; }

    /// <summary>Whether a declared stat is currently visible to the player.</summary>
    public bool IsStatVisible(StatId stat, IGameStateView state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!World.Ruleset.StatsById.ContainsKey(stat))
        {
            throw new KeyNotFoundException($"Unknown stat '{stat}'.");
        }

        if (!CharacterSheet.Stats)
        {
            return false;
        }

        return !CharacterSheet.StatRules.TryGetValue(stat, out var condition)
            || ExpressionEvaluator.IsTrue(condition, new EvaluationContext(state, Predicates));
    }

    /// <summary>Creation options visible before selection, presets, or story effects are applied.</summary>
    public CharacterCreation? GetCreationOptions()
    {
        if (Creation is not { } creation || creation.Stats.Count == 0)
        {
            return Creation;
        }

        if (!CharacterSheet.Stats)
        {
            return creation.WithStats([]);
        }

        if (CharacterSheet.StatRules.Count == 0)
        {
            return creation;
        }

        var initialState = GameState.CreateInitial(World, Seed ?? 0);
        return creation.WithStats(creation.Stats.Where(pair => IsStatVisible(pair.Key, initialState)));
    }

    private Dictionary<SceneId, SceneDefinition>? _byId;
    private StoryReachability? _reachability;
    private readonly object _replayIdentityGate = new();
    private ReplayIdentityTable? _replayIdentities;

    /// <summary>The compiler keeps the graph it built for TW3009; other stories build it on first use.</summary>
    [AllowNull]
    internal StoryReachability Reachability
    {
        get => _reachability ??= StoryReachability.Find(Scenes, StoryDiverts.From(Scenes), Start);
        init => _reachability = value;
    }

    /// <summary>
    /// Replay identities for exactly this scene set. The compiler supplies them; other stories reuse a compiled
    /// table only when it was built for the same scenes, otherwise they build their own without changing shared
    /// scene or choice definitions.
    /// </summary>
    [AllowNull]
    internal ReplayIdentityTable ReplayIdentities
    {
        get
        {
            if (Volatile.Read(ref _replayIdentities) is { } identities) return identities;
            lock (_replayIdentityGate)
            {
                return _replayIdentities ??= CompiledReplayIdentities() ?? ChoiceReplayIdentity.Build(Scenes);
            }
        }
        init => _replayIdentities = value;
    }

    private ReplayIdentityTable? CompiledReplayIdentities()
    {
        if (Scenes.Count == 0 || Scenes[0].ReplayIdentityCache is not { } table || table.SceneCount != Scenes.Count)
        {
            return null;
        }

        foreach (var scene in Scenes)
        {
            if (!ReferenceEquals(scene.ReplayIdentityCache, table))
            {
                return null;
            }
        }
        return table;
    }

    public SceneDefinition GetScene(SceneId id) =>
        (_byId ??= Scenes.ToDictionary(s => s.Id)).TryGetValue(id, out var scene)
            ? scene
            : throw new KeyNotFoundException($"Unknown scene '{id}'.");
}

/// <summary>Where the story goes next: a scene, or the end.</summary>
public sealed record DivertTarget(SceneId? Scene)
{
    public static DivertTarget End { get; } = new((SceneId?)null);

    public bool IsEnd => Scene is null;

    public override string ToString() => Scene?.Value ?? SceneVocabulary.End;
}

/// <summary>
/// What happens after a check: one effect batch, then an optional immediate divert.
/// An entry check without a divert continues at the next scene step.
/// </summary>
public sealed record CheckOutcome(IReadOnlyList<Effect> Effects, DivertTarget? Divert);

/// <summary>A skill check with its two outcomes.</summary>
public sealed record CheckBranch(
    SkillCheck Check,
    CheckOutcome Success,
    CheckOutcome Failure,
    SkillCheckIdentity Identity);

/// <summary>
/// One scene / storylet, executed in source order.
/// </summary>
public sealed class SceneDefinition : IStorylet
{
    public required SceneId Id { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<TemplateInline>? ChoiceLabel { get; init; }
    public bool ContinuesPrevious { get; init; }
    public bool ClearsPrevious { get; init; }
    public Expr? Requires { get; init; }
    public ShownDefinition? Shown { get; init; }
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>();
    public int Priority { get; init; }
    public bool Once { get; init; }

    /// <summary>When set, every eligible scene matching this tag query is offered as a choice here.</summary>
    public StoryletTags? Hub { get; init; }
    public int? HubLimit { get; init; }

    public IReadOnlyList<SceneStep> Steps { get; init; } = [];
    /// <summary>Identities recorded by the compiler for the scene set that produced this scene.</summary>
    internal ReplayIdentityTable? ReplayIdentityCache { get; set; }
    public IReadOnlyList<ChoiceDefinition> Choices =>
        SceneSteps.Walk(Steps).OfType<ChoiceStep>().Select(step => step.Choice).ToArray();
}

/// <summary>
/// A player choice. Its divert, or both skill-check outcomes, must name an explicit target.
/// </summary>
public sealed class ChoiceDefinition
{
    /// <summary>Default text for an omitted choice label and for an automatic scene continuation.</summary>
    public const string ContinueLabelText = "Continue";

    /// <summary>Stable key within the scene (declaration index), recorded in <c>ChoiceMade</c> events.</summary>
    public required string Key { get; init; }

    /// <summary>Compiled identity of this choice's gameplay outcome, independent of presentation and position.</summary>
    internal byte[]? ReplayIdentity { get; set; }
    internal ReplayIdentityTable? ReplayIdentityOwner { get; set; }

    /// <summary>Authored label, or null for a textless choice. An explicitly empty label remains empty.</summary>
    public required IReadOnlyList<TemplateInline>? Label { get; init; }
    public Expr? Requires { get; init; }
    public ShownDefinition? Shown { get; init; }
    public IReadOnlyList<Effect> Effects { get; init; } = [];
    public CheckBranch? Check { get; init; }
    public DivertTarget? Divert { get; init; }
}

/// <summary>
/// A choice or hub scene to show while its requirement is false. A null text keeps the authored label or title;
/// an empty text is an explicit empty replacement.
/// </summary>
public sealed record ShownDefinition(IReadOnlyList<TemplateInline>? Text);
