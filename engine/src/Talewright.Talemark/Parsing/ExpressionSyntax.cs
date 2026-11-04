using System.Collections.Immutable;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

internal readonly record struct ExpressionSpan(int Start, int Length)
{
    internal int End => Start + Length;
}

internal abstract record ExpressionNode(ExpressionSpan Span);

internal sealed record ErrorExpressionNode(ExpressionSpan Span) : ExpressionNode(Span);

internal sealed record NumberExpressionNode(int Value, ExpressionSpan Span) : ExpressionNode(Span);

internal sealed record TextExpressionNode(string Value, ExpressionSpan Span) : ExpressionNode(Span);

internal sealed record BooleanExpressionNode(bool Value, ExpressionSpan Span) : ExpressionNode(Span);

internal sealed record NameExpressionNode(ExpressionSpan Name, ExpressionSpan Span) : ExpressionNode(Span);

internal sealed record NamedPredicateExpressionNode(ExpressionSpan Name, ExpressionSpan Span) : ExpressionNode(Span);

internal sealed record VisitedExpressionNode(ExpressionSpan Scene, ExpressionSpan Span) : ExpressionNode(Span);

internal sealed record ParenthesizedExpressionNode(ExpressionNode Inner, ExpressionSpan Span) : ExpressionNode(Span);

internal sealed record ReferenceExpressionNode(ReferenceNamespace Kind, ExpressionSpan Namespace, ExpressionSpan Name,
    ExpressionSpan? Member, ExpressionSpan? SubMember, ExpressionSpan Span) : ExpressionNode(Span);

internal sealed record HasItemExpressionNode(ExpressionSpan Character, ExpressionSpan Item,
    ExpressionSpan Span) : ExpressionNode(Span);

internal sealed record UnaryExpressionNode(UnaryOp Operator, ExpressionNode Operand, ExpressionSpan OperatorSpan,
    ExpressionSpan Span) : ExpressionNode(Span);

internal sealed record BinaryExpressionNode(BinaryOp Operator, ExpressionNode Left, ExpressionNode Right,
    ExpressionSpan OperatorSpan, ExpressionSpan Span) : ExpressionNode(Span);

internal sealed record ExpressionSyntaxTree(TokenBuffer Tokens, ExpressionNode Root, bool HasErrors);

internal sealed record ModifierCallSyntax(string Name, int Offset, string? Argument, string? PluralArgument);

internal sealed record SubstitutionSyntaxTree(TokenBuffer Tokens, ExpressionNode Value,
    ImmutableArray<ModifierCallSyntax> Modifiers, bool HasErrors);

internal abstract record EffectSyntax(ExpressionSpan Span);

internal sealed record ErrorEffectSyntax(ExpressionSpan Span) : EffectSyntax(Span);

internal sealed record SetFlagEffectSyntax(bool Set, ExpressionSpan Flag, ExpressionSpan Span) : EffectSyntax(Span);

internal sealed record GiveTakeEffectSyntax(bool Give, ExpressionSpan Character, ExpressionSpan Item,
    ExpressionNode? Quantity, ExpressionSpan Span) : EffectSyntax(Span);

internal sealed record AssignmentEffectSyntax(ReferenceNamespace Kind, ExpressionSpan Namespace, ExpressionSpan Target,
    ExpressionSpan? Member, AssignOp Operator, ExpressionNode Amount, ExpressionSpan Span) : EffectSyntax(Span);

internal sealed record EffectsSyntaxTree(TokenBuffer Tokens, ImmutableArray<EffectSyntax> Effects, bool HasErrors);
