using Talewright.Rules.Expressions;
using Talewright.Talemark.Formatting;

namespace Talewright.Talemark.Model;

/// <summary>Preview. The kind of story state referenced by a condition.</summary>
public enum StoryStateReadKind
{
    /// <summary>A character stat, such as <c>player:dex</c>.</summary>
    Stat,

    /// <summary>A faction standing, such as <c>faction:river</c>.</summary>
    Standing,

    /// <summary>A faction standing tier, such as <c>faction:river:tier</c>.</summary>
    StandingTier,

    /// <summary>A flag, such as <c>flag:ready</c>.</summary>
    Flag,

    /// <summary>A number variable, such as <c>var:coins</c>.</summary>
    NumberVariable,

    /// <summary>A text variable, such as <c>var:weather</c>.</summary>
    TextVariable,

    /// <summary>A character name property, such as <c>player:first-name</c>.</summary>
    CharacterName,

    /// <summary>A character pronoun property, such as <c>player:pronoun:object</c>.</summary>
    CharacterPronoun,

    /// <summary>A faction name, such as <c>faction:river:name</c>.</summary>
    FactionName,

    /// <summary>An item name, such as <c>item:lantern</c>.</summary>
    ItemName,

    /// <summary>Whether a character holds an item, such as <c>player has lantern</c>.</summary>
    ItemsHeld,

    /// <summary>Whether a scene has been visited, such as <c>visited(camp)</c>.</summary>
    VisitedScene,

    /// <summary>A named C# predicate, such as <c>@CanGo</c>.</summary>
    NamedPredicate,
}

/// <summary>Preview. One distinct reference to story state used by a condition.</summary>
/// <param name="Kind">The kind of state reference.</param>
/// <param name="Reference">The reference in canonical Talemark syntax.</param>
/// <param name="IsOpaque">True for named predicates, whose C# implementation cannot be inspected.</param>
public sealed record StoryStateRead(StoryStateReadKind Kind, string Reference, bool IsOpaque);

/// <summary>
/// Preview. The formatted parts of a divert or hub-offer condition, their combined Talemark text, and the state they read.
/// An empty <see cref="Parts"/> list and <see cref="Text"/> mean there is no expression requirement.
/// </summary>
public sealed class StoryCondition
{
    private StoryCondition(IReadOnlyList<string> parts, string text, IReadOnlyList<StoryStateRead> reads)
    {
        Parts = parts;
        Text = text;
        Reads = reads;
    }

    /// <summary>Condition expressions in source order; an <c>{else}</c> part is formatted as a negation.</summary>
    public IReadOnlyList<string> Parts { get; }

    /// <summary>The condition parts combined with <c>and</c>, preserving their grouping; empty when unconditional.</summary>
    public string Text { get; }

    /// <summary>Distinct state references in first-use order across the condition parts.</summary>
    public IReadOnlyList<StoryStateRead> Reads { get; }

    internal static StoryCondition FromExpressions(IEnumerable<Expr> expressions)
    {
        var parts = expressions.ToArray();
        if (parts.Any(expression => expression.Type != ExprType.Bool))
            throw new ArgumentException("Story condition parts must be boolean expressions.", nameof(expressions));

        var reads = new List<StoryStateRead>();
        var seen = new HashSet<StoryStateRead>();
        foreach (var expression in parts)
            AddReads(expression, reads, seen);

        var text = parts.Length switch
        {
            0 => string.Empty,
            1 => ExpressionFormatter.Format(parts[0]),
            _ => ExpressionFormatter.Format(parts.Skip(1).Aggregate(parts[0],
                (combined, part) => new BinaryExpr(BinaryOp.And, combined, part))),
        };
        return new StoryCondition(parts.Select(ExpressionFormatter.Format).ToArray(), text, reads.ToArray());
    }

    private static void AddReads(Expr expression, List<StoryStateRead> reads, HashSet<StoryStateRead> seen)
    {
        switch (expression)
        {
            case NumberLiteral or BoolLiteral or TextLiteral or TierLiteral:
                return;
            case UnaryExpr unary:
                AddReads(unary.Operand, reads, seen);
                return;
            case BinaryExpr binary:
                AddReads(binary.Left, reads, seen);
                AddReads(binary.Right, reads, seen);
                return;
        }

        var read = expression switch
        {
            StatRef value => Create(StoryStateReadKind.Stat, value),
            StandingRef value => Create(StoryStateReadKind.Standing, value),
            StandingTierRef value => Create(StoryStateReadKind.StandingTier, value),
            FlagRef value => Create(StoryStateReadKind.Flag, value),
            VarRef value => Create(StoryStateReadKind.NumberVariable, value),
            TextVarRef value => Create(StoryStateReadKind.TextVariable, value),
            NameRef value => Create(StoryStateReadKind.CharacterName, value),
            PronounRef value => Create(StoryStateReadKind.CharacterPronoun, value),
            FactionNameRef value => Create(StoryStateReadKind.FactionName, value),
            ItemNameRef value => Create(StoryStateReadKind.ItemName, value),
            HasItem value => Create(StoryStateReadKind.ItemsHeld, value),
            Visited value => Create(StoryStateReadKind.VisitedScene, value),
            NamedPredicate value => Create(StoryStateReadKind.NamedPredicate, value, isOpaque: true),
            _ => throw new NotSupportedException($"Expression kind '{expression.GetType().Name}' has no story-read visitor."),
        };
        if (seen.Add(read))
            reads.Add(read);
    }

    private static StoryStateRead Create(StoryStateReadKind kind, Expr expression, bool isOpaque = false) =>
        new(kind, ExpressionFormatter.Format(expression), isOpaque);
}
