using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Parsing;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Authoring;

internal enum VariableUsageType { Unknown, Number, Text }

/// <summary>Infers whether an undeclared variable is read as a number or as text from the syntax around it.</summary>
internal static class VariableUsageInference
{
    internal static VariableUsageType Infer(StoryAnalysis analysis, SourceLocation location, string name)
    {
        foreach (var context in analysis.Expressions)
        {
            if (context.Range.Start.File != location.File || context.Range.Start.Line != location.Line ||
                context.Range.End.Line != location.Line)
                continue;
            var offset = location.Column - context.Range.Start.Column;
            if (offset < 0 || offset + name.Length > context.Text.Length ||
                string.CompareOrdinal(context.Text, offset, name, 0, name.Length) != 0)
                continue;
            return Infer(analysis, context, offset);
        }

        return VariableUsageType.Unknown;
    }

    private static VariableUsageType Infer(StoryAnalysis analysis, ExpressionContext context, int offset)
    {
        var text = context.Text;
        var tokens = Lexer.Tokenize(text);
        var diagnostics = new DiagnosticBag();
        SourceLocation Locate(int at) => new(context.Range.Start.File, 1, at + 1);
        switch (context.Kind)
        {
            case ExpressionContextKind.Condition or ExpressionContextKind.Number:
            {
                var tree = ExpressionSyntaxParser.ParseExpression(tokens, context.Text.Length, Locate, diagnostics);
                var rootType = context.Kind == ExpressionContextKind.Number
                    ? VariableUsageType.Number
                    : VariableUsageType.Unknown;
                return InferWithin(analysis, text, tree.Root, offset, rootType) ?? VariableUsageType.Unknown;
            }
            case ExpressionContextKind.Substitution:
            {
                var tree = ExpressionSyntaxParser.ParseSubstitution(tokens, context.Text.Length, Locate, diagnostics);
                return InferWithin(analysis, text, tree.Value, offset, VariableUsageType.Unknown) ?? VariableUsageType.Unknown;
            }
            case ExpressionContextKind.Effects:
            {
                var tree = ExpressionSyntaxParser.ParseEffects(tokens, context.Text.Length, Locate, diagnostics);
                foreach (var effect in tree.Effects)
                {
                    var result = effect switch
                    {
                        GiveTakeEffectSyntax { Quantity: { } quantity } =>
                            InferWithin(analysis, text, quantity, offset, VariableUsageType.Number),
                        AssignmentEffectSyntax assignment =>
                            InferWithin(analysis, text, assignment.Amount, offset, AssignedType(analysis, text, assignment)),
                        _ => null
                    };
                    if (result is { } type) return type;
                }

                return VariableUsageType.Unknown;
            }
            default:
                return VariableUsageType.Unknown;
        }
    }

    private static VariableUsageType AssignedType(StoryAnalysis analysis, string text, AssignmentEffectSyntax assignment)
    {
        if (assignment.Operator != AssignOp.Set || assignment.Kind != ReferenceNamespace.Variable)
            return VariableUsageType.Number;
        return DeclaredType(analysis, Text(text, assignment.Target));
    }

    // Returns null when the reference is not inside this node; otherwise the type its nearest typed parent requires.
    private static VariableUsageType? InferWithin(
        StoryAnalysis analysis, string text, ExpressionNode node, int offset, VariableUsageType required)
    {
        switch (node)
        {
            case ReferenceExpressionNode { Kind: ReferenceNamespace.Variable } reference
                when reference.Name.Start == offset:
                return required;
            case ParenthesizedExpressionNode parenthesized:
                return InferWithin(analysis, text, parenthesized.Inner, offset, required);
            case UnaryExpressionNode unary:
                return InferWithin(analysis, text, unary.Operand, offset,
                    unary.Operator == UnaryOp.Negate ? VariableUsageType.Number : VariableUsageType.Unknown);
            case BinaryExpressionNode binary:
            {
                var left = binary.Operator switch
                {
                    BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply or BinaryOp.Divide or
                        BinaryOp.Less or BinaryOp.LessOrEqual or BinaryOp.Greater or BinaryOp.GreaterOrEqual =>
                        VariableUsageType.Number,
                    BinaryOp.Equal or BinaryOp.NotEqual => OperandType(analysis, text, binary.Right),
                    _ => VariableUsageType.Unknown
                };
                var right = binary.Operator is BinaryOp.Equal or BinaryOp.NotEqual
                    ? OperandType(analysis, text, binary.Left)
                    : left;
                return InferWithin(analysis, text, binary.Left, offset, left) ??
                    InferWithin(analysis, text, binary.Right, offset, right);
            }
            default:
                return null;
        }
    }

    private static VariableUsageType OperandType(StoryAnalysis analysis, string text, ExpressionNode node) => node switch
    {
        NumberExpressionNode => VariableUsageType.Number,
        TextExpressionNode => VariableUsageType.Text,
        ParenthesizedExpressionNode parenthesized => OperandType(analysis, text, parenthesized.Inner),
        UnaryExpressionNode { Operator: UnaryOp.Negate } => VariableUsageType.Number,
        BinaryExpressionNode
        {
            Operator: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply or BinaryOp.Divide
        } => VariableUsageType.Number,
        ReferenceExpressionNode { Kind: ReferenceNamespace.Variable, Member: null } reference =>
            DeclaredType(analysis, Text(text, reference.Name)),
        _ => VariableUsageType.Unknown
    };

    private static VariableUsageType DeclaredType(StoryAnalysis analysis, string name) =>
        analysis.Symbols.FirstOrDefault(symbol => symbol.Kind == AuthoringSymbolKind.Variable && symbol.Name == name)?.Type switch
        {
            ManifestVocabulary.SymbolInteger => VariableUsageType.Number,
            ManifestVocabulary.SymbolText => VariableUsageType.Text,
            _ => VariableUsageType.Unknown
        };

    private static string Text(string text, ExpressionSpan span) => text.Substring(span.Start, span.Length);
}
