using Talewright.Core.Identity;
using Talewright.Core.Randomness;
using Talewright.Core.Rulesets;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Core;

namespace Talewright.Rules.Expressions;

/// <summary>Static type of an expression. The <c>.tale</c> compiler type-checks every expression before play.</summary>
public enum ExprType
{
    Number,
    Bool,
    Text,
    /// <summary>A reputation tier; comparisons between tiers use tier order.</summary>
    Tier,
}

/// <summary>
/// Typed expression tree. Built by the compiler (or by C# code directly) and evaluated by
/// <see cref="ExpressionEvaluator"/>. There is no string parsing and no runtime code generation here.
/// </summary>
public abstract record Expr
{
    public abstract ExprType Type { get; }
}

/// <summary>A fixed item identifier or an expression that resolves to an item identifier at runtime.</summary>
public abstract record ItemOperand
{
    public static implicit operator ItemOperand(ItemId item) => new FixedItemOperand(item);
    public static implicit operator ItemOperand(Expr expression) => new ResolvedItemOperand(expression);

    /// <summary>The item this operand names now, or null when it holds nothing.</summary>
    public ItemId? Resolve(EvaluationContext context) => ResolveReference(context)?.Resolve(context.State);

    /// <summary>
    /// The item or held group this operand refers to, or null for an unset value or an item the story no longer
    /// declares. A group reference is returned even when its group is gone; callers decide what that means.
    /// </summary>
    public ItemReference? ResolveReference(EvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return this switch
        {
            FixedItemOperand fixedItem => ItemReference.ForItem(fixedItem.Item),
            ResolvedItemOperand resolved => ResolveRuntime(resolved.Expression, context),
            _ => throw new NotSupportedException($"Unknown item operand {GetType().Name}."),
        };
    }

    private static ItemReference? ResolveRuntime(Expr expression, EvaluationContext context)
    {
        if (expression.Type != ExprType.Text)
            throw new InvalidOperationException("A runtime item operand must be a text expression.");

        var value = ExpressionEvaluator.Evaluate(expression, context);
        if (value.Type != ExprType.Text)
            throw new InvalidOperationException("A runtime item expression evaluated to a non-text value.");
        return ItemReference.TryDecode(value.Text, out var reference)
            && context.State.World.Items.ContainsKey(reference.Item)
            && (reference.Holder is not { } holder || context.State.World.Characters.ContainsKey(holder))
                ? reference
                : null;
    }
}

public sealed record FixedItemOperand(ItemId Item) : ItemOperand
{
    public override string ToString() => Item.Value;
}

public sealed record ResolvedItemOperand(Expr Expression) : ItemOperand
{
    public Expr Expression { get; init; } = Validate(Expression);

    public override string ToString() => $"<{Expression}>";

    private static Expr Validate(Expr expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (expression.Type != ExprType.Text)
            throw new ArgumentException("A runtime item operand must be a text expression.", nameof(expression));
        return expression;
    }
}

public sealed record NumberLiteral(ExactNumber Value) : Expr
{
    public NumberLiteral(int value) : this(ExactNumber.FromInteger(value)) { }

    public override ExprType Type => ExprType.Number;
}

public sealed record BoolLiteral(bool Value) : Expr
{
    public override ExprType Type => ExprType.Bool;
}

public sealed record TextLiteral(string Value) : Expr
{
    public override ExprType Type => ExprType.Text;
}

public sealed record TierLiteral(ReputationTier Tier) : Expr
{
    public override ExprType Type => ExprType.Tier;
}

/// <summary><c>player:dex</c>: effective stat value.</summary>
public sealed record StatRef(EntityId Character, StatId Stat) : Expr
{
    public override ExprType Type => ExprType.Number;
}

/// <summary><c>faction:guild:standing</c>: player's numeric standing with a faction.</summary>
public sealed record StandingRef(FactionId Faction) : Expr
{
    public override ExprType Type => ExprType.Number;
}

/// <summary><c>faction:guild:tier</c>: standing tier.</summary>
public sealed record StandingTierRef(FactionId Faction) : Expr
{
    public override ExprType Type => ExprType.Tier;
}

public sealed record FlagRef(FlagId Flag) : Expr
{
    public override ExprType Type => ExprType.Bool;
}

public sealed record VarRef(VarId Variable) : Expr
{
    public override ExprType Type => ExprType.Number;
}

public sealed record TextVarRef(VarId Variable) : Expr
{
    public override ExprType Type => ExprType.Text;
}

public enum NamePart
{
    First,
    Last,
    Full,
}

/// <summary><c>player:first-name</c>, <c>player:last-name</c>, <c>player:name</c>.</summary>
public sealed record NameRef(EntityId Character, NamePart Part) : Expr
{
    public override ExprType Type => ExprType.Text;
}

public enum PronounForm
{
    Subject,
    Object,
    Possessive,
    PossessivePronoun,
    Reflexive,
}

/// <summary>
/// Character pronoun properties such as <c>player:pronoun:object</c> and
/// <c>player:pronoun:possessive-pronoun:independent</c>; <c>player:pronoun</c> is the subject shorthand.
/// </summary>
public sealed record PronounRef(EntityId Character, PronounForm Form) : Expr
{
    public override ExprType Type => ExprType.Text;
}

/// <summary><c>faction:guild</c> or <c>faction:guild:name</c>: faction display name.</summary>
public sealed record FactionNameRef(FactionId Faction) : Expr
{
    public override ExprType Type => ExprType.Text;
}

/// <summary><c>item:rope</c> or <c>item:rope:name</c>: item display name.</summary>
public sealed record ItemNameRef(ItemOperand Item) : Expr
{
    public override ExprType Type => ExprType.Text;

    public ItemNameRef(ItemId item) : this(new FixedItemOperand(item)) { }
    public ItemNameRef(Expr item) : this(new ResolvedItemOperand(item)) { }
}

/// <summary>A read-only scalar property declared on an item.</summary>
public sealed record ItemPropertyRef(ItemId Item, string Property, ItemPropertyValue Value) : Expr
{
    public override ExprType Type => Value switch
    {
        ItemPropertyValue.Number => ExprType.Number,
        ItemPropertyValue.Boolean => ExprType.Bool,
        ItemPropertyValue.Text => ExprType.Text,
        _ => throw new InvalidOperationException("Unknown item property value type."),
    };
}

/// <summary><c>player has rope</c>.</summary>
public sealed record HasItem(EntityId Character, ItemOperand Item) : Expr
{
    public override ExprType Type => ExprType.Bool;

    public HasItem(EntityId character, ItemId item) : this(character, new FixedItemOperand(item)) { }
    public HasItem(EntityId character, Expr item) : this(character, new ResolvedItemOperand(item)) { }
}

/// <summary>
/// <c>var:tool == hammer</c>: whether an item variable currently names an item. A variable holding nothing equals no
/// item.
/// </summary>
public sealed record ItemComparison(ItemOperand Item, BinaryOp Op, ItemId Expected) : Expr
{
    public BinaryOp Op { get; init; } = Op is BinaryOp.Equal or BinaryOp.NotEqual
        ? Op
        : throw new ArgumentOutOfRangeException(nameof(Op), Op, "Item comparisons use == or != only.");

    public override ExprType Type => ExprType.Bool;
}

/// <summary>
/// <c>var:tool:charge</c>: a property read through an item variable. A held group gives its value; an item gives its
/// declared value; nothing, or an item without the property, gives the type's default.
/// </summary>
public sealed record ItemReferencePropertyRef(ItemOperand Item, string Property, ItemPropertyValueKind Kind) : Expr
{
    public override ExprType Type => Kind switch
    {
        ItemPropertyValueKind.Number => ExprType.Number,
        ItemPropertyValueKind.Boolean => ExprType.Bool,
        ItemPropertyValueKind.Text => ExprType.Text,
        _ => throw new InvalidOperationException("Unknown item property value type."),
    };
}

/// <summary><c>player visited temple.hall</c>.</summary>
public sealed record Visited(SceneId Scene) : Expr
{
    public override ExprType Type => ExprType.Bool;
}

/// <summary><c>scene:player:visits</c> or <c>camp:player:visits</c>: number of player visits.</summary>
public sealed record SceneVisitCountRef(SceneId? Scene, bool IsSceneAlias = false) : Expr
{
    public override ExprType Type => ExprType.Number;
}

/// <summary><c>player:inventory:rope:count</c>: quantity of an item held by a character.</summary>
public sealed record InventoryCountRef(EntityId Character, ItemOperand Item) : Expr
{
    public override ExprType Type => ExprType.Number;

    public InventoryCountRef(EntityId character, ItemId item) : this(character, new FixedItemOperand(item)) { }
    public InventoryCountRef(EntityId character, Expr item) : this(character, new ResolvedItemOperand(item)) { }
}

/// <summary>A declared property on the front held copy, or its definition value when none is held.</summary>
public sealed record InventoryItemPropertyRef(EntityId Character, ItemId Item, string Property,
    ItemPropertyValue DeclaredValue) : Expr
{
    public override ExprType Type => DeclaredValue switch
    {
        ItemPropertyValue.Number => ExprType.Number,
        ItemPropertyValue.Boolean => ExprType.Bool,
        ItemPropertyValue.Text => ExprType.Text,
        _ => throw new InvalidOperationException("Unknown item property value type."),
    };
}

/// <summary>A keyed skill check; without a DC it evaluates to the ruleset total, otherwise to pass/fail.</summary>
public sealed record SkillCheckExpr(EntityId Actor, StatId Skill, SkillCheckIdentity Identity, Expr? Difficulty,
    bool IsHidden = false) : Expr
{
    public override ExprType Type => Difficulty is null ? ExprType.Number : ExprType.Bool;
}

/// <summary><c>@CanPickLock</c>: a predicate registered from C# in <see cref="NamedPredicateRegistry"/>.</summary>
public sealed record NamedPredicate(string Name) : Expr
{
    public override ExprType Type => ExprType.Bool;
}

public enum UnaryOp
{
    Not,
    Negate,
    Round,
    RoundUp,
    RoundDown,
}

public sealed record UnaryExpr(UnaryOp Op, Expr Operand) : Expr
{
    public override ExprType Type => Op == UnaryOp.Not ? ExprType.Bool : ExprType.Number;
}

public enum BinaryOp
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    And,
    Or,
}

public sealed record BinaryExpr(BinaryOp Op, Expr Left, Expr Right) : Expr
{
    public override ExprType Type => Op switch
    {
        BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply or BinaryOp.Divide => ExprType.Number,
        _ => ExprType.Bool,
    };
}
