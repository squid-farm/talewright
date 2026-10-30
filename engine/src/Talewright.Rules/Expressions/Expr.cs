using Talewright.Core.Identity;
using Talewright.Core.Rulesets;

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

public sealed record NumberLiteral(int Value) : Expr
{
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

/// <summary><c>faction:guild</c>: player standing with a faction.</summary>
public sealed record StandingRef(FactionId Faction) : Expr
{
    public override ExprType Type => ExprType.Number;
}

/// <summary><c>faction:guild:tier</c>: standing tier (also produced implicitly when comparing a faction to a tier name).</summary>
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

/// <summary><c>faction:guild:name</c>.</summary>
public sealed record FactionNameRef(FactionId Faction) : Expr
{
    public override ExprType Type => ExprType.Text;
}

/// <summary><c>item:rope</c>: item display name.</summary>
public sealed record ItemNameRef(ItemId Item) : Expr
{
    public override ExprType Type => ExprType.Text;
}

/// <summary><c>player has rope</c>.</summary>
public sealed record HasItem(EntityId Character, ItemId Item) : Expr
{
    public override ExprType Type => ExprType.Bool;
}

/// <summary><c>visited(temple.hall)</c>.</summary>
public sealed record Visited(SceneId Scene) : Expr
{
    public override ExprType Type => ExprType.Bool;
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
