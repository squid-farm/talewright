using Talewright.Core.Identity;
using Talewright.Core.World;
using Talewright.Narrative.Templates;
using Talewright.Rules.Checks;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Rules.Storylets;
using Talewright.Rules.Triggers;

namespace Talewright.Talemark.Model;

/// <summary>
/// A fully compiled, validated story. Immutable and free of source text: the runtime only ever sees this.
/// </summary>
public sealed class StoryDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Version { get; init; }
    public string? ContentFingerprint { get; init; }
    public bool UndoEnabled { get; init; } = true;
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

    private Dictionary<SceneId, SceneDefinition>? _byId;

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

    public override string ToString() => Scene?.Value ?? "END";
}

/// <summary>A divert that only applies when <see cref="Condition"/> holds (from <c>{if}</c> blocks).</summary>
public sealed record ConditionalDivert(Expr? Condition, DivertTarget Target);

/// <summary>An effect that only applies when <see cref="Condition"/> holds.</summary>
public sealed record ConditionalEffect(Expr? Condition, Effect Effect);

/// <summary>
/// What happens after a check: effects, then a divert. A null <see cref="Divert"/> falls through to the
/// scene's own <see cref="SceneDefinition.Diverts"/>.
/// </summary>
public sealed record CheckOutcome(IReadOnlyList<Effect> Effects, DivertTarget? Divert);

/// <summary>A skill check with its two outcomes.</summary>
public sealed record CheckBranch(SkillCheck Check, CheckOutcome Success, CheckOutcome Failure);

/// <summary>
/// One scene / storylet. Entering it: apply <see cref="EntryEffects"/>, render <see cref="Body"/>, then either
/// resolve <see cref="EntryCheck"/>, offer <see cref="Choices"/> (plus hub storylets), or follow the first
/// matching <see cref="Diverts"/>.
/// </summary>
public sealed class SceneDefinition : IStorylet
{
    public required SceneId Id { get; init; }
    public string? Title { get; init; }
    public Expr? Requires { get; init; }
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>();
    public int Priority { get; init; }
    public bool Once { get; init; }

    /// <summary>When set, every eligible scene matching this tag query is offered as a choice here.</summary>
    public StoryletTags? Hub { get; init; }
    public int? HubLimit { get; init; }

    public IReadOnlyList<ConditionalEffect> EntryEffects { get; init; } = [];
    public IReadOnlyList<TemplateBlock> Body { get; init; } = [];
    public CheckBranch? EntryCheck { get; init; }
    public IReadOnlyList<ChoiceDefinition> Choices { get; init; } = [];
    public IReadOnlyList<ConditionalDivert> Diverts { get; init; } = [];
}

/// <summary>
/// A player choice. <see cref="Check"/> or <see cref="Divert"/> decides where it leads; with neither, the choice
/// falls through to the scene's own <see cref="SceneDefinition.Diverts"/> (like an ink "gather").
/// </summary>
public sealed class ChoiceDefinition
{
    /// <summary>Stable key within the scene (declaration index), recorded in <c>ChoiceMade</c> events.</summary>
    public required string Key { get; init; }

    public required IReadOnlyList<TemplateInline> Label { get; init; }
    public Expr? Requires { get; init; }
    public IReadOnlyList<Effect> Effects { get; init; } = [];
    public CheckBranch? Check { get; init; }
    public DivertTarget? Divert { get; init; }
}
