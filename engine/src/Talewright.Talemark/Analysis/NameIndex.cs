using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Parsing;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Analysis;

/// <summary>
/// One authored occurrence of a named identity: a declaration, or a reference the parser recognized in a name slot.
/// A flag set or variable assigned by an effect is a reference, even though it also implicitly declares the name.
/// </summary>
public sealed record NameOccurrence(AuthoringSymbolKind Kind, string Name, SourceRange Range, bool IsDeclaration);

/// <summary>
/// Story-wide occurrences of author-declared names, keyed by identity. A kind and name together form an identity, so
/// <c>flag:x</c>, <c>var:x</c> and a scene <c>x</c> never share occurrences. Only names declared somewhere in the
/// story, including flags and variables declared by being set, have occurrences; names owned by rulesets or
/// registrations do not.
/// </summary>
public sealed class NameIndex
{
    private static readonly HashSet<AuthoringSymbolKind> DeclaredKinds =
    [
        AuthoringSymbolKind.Scene, AuthoringSymbolKind.Character, AuthoringSymbolKind.Stat, AuthoringSymbolKind.Item,
        AuthoringSymbolKind.Faction, AuthoringSymbolKind.Flag, AuthoringSymbolKind.Variable, AuthoringSymbolKind.Story,
        AuthoringSymbolKind.Tag, AuthoringSymbolKind.Trigger
    ];

    private static readonly Comparer<NameOccurrence> SourceOrder = Comparer<NameOccurrence>.Create((left, right) =>
    {
        var file = string.CompareOrdinal(left.Range.Start.File, right.Range.Start.File);
        if (file != 0) return file;
        var start = Compare(left.Range.Start, right.Range.Start);
        return start != 0 ? start : left.Kind.CompareTo(right.Kind);
    });

    private readonly Dictionary<string, NameOccurrence[]> _files;
    private readonly Dictionary<(AuthoringSymbolKind Kind, string Name), NameOccurrence[]> _identities;

    private NameIndex(Dictionary<string, NameOccurrence[]> files,
        Dictionary<(AuthoringSymbolKind Kind, string Name), NameOccurrence[]> identities)
    {
        _files = files;
        _identities = identities;
    }

    /// <summary>The occurrence at <paramref name="position"/>, preferring one that contains it over one ending there.</summary>
    public NameOccurrence? At(SourceLocation position)
    {
        if (position.File is null || !_files.TryGetValue(position.File, out var occurrences)) return null;
        NameOccurrence? ending = null;
        foreach (var occurrence in occurrences)
        {
            if (Compare(occurrence.Range.Start, position) > 0) break;
            var end = Compare(position, occurrence.Range.End);
            if (end < 0) return occurrence;
            if (end == 0) ending ??= occurrence;
        }
        return ending;
    }

    /// <summary>Every declaration and reference of one identity, in file and source order.</summary>
    public IReadOnlyList<NameOccurrence> Occurrences(AuthoringSymbolKind kind, string name) =>
        _identities.TryGetValue((kind, name), out var occurrences) ? occurrences : [];

    internal IReadOnlyList<string> DeclaredNames(AuthoringSymbolKind kind, string? exceptFile = null) =>
        _identities
            .Where(identity => identity.Key.Kind == kind &&
                identity.Value.Any(occurrence => occurrence.IsDeclaration &&
                    (exceptFile is null || occurrence.Range.Start.File != exceptFile)))
            .Select(identity => identity.Key.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    internal static NameIndex Build(IReadOnlyList<AuthoringSymbol> symbols, IReadOnlyList<NameOccurrence> names,
        IReadOnlyList<CompletionSiteSource> sources)
    {
        var references = new HashSet<NameOccurrence>();
        foreach (var name in names.Where(name => !name.IsDeclaration)) references.Add(name);
        // Incomplete skill checks record one word as both an actor and a stat; such a word names neither yet.
        foreach (var group in sources
                     .Select(source => source.Site)
                     .Where(site => site is { Text.Length: > 0, Kinds.Count: 1 } &&
                         site.Kinds[0] != AuthoringSymbolKind.Tag && DeclaredKinds.Contains(site.Kinds[0]))
                     .GroupBy(site => site!.Range))
        {
            var kinds = group.Select(site => site!.Kinds[0]).Distinct().ToArray();
            if (kinds.Length == 1) references.Add(new(kinds[0], group.First()!.Text, group.Key, false));
        }
        foreach (var source in sources)
            if (source.Expression is { } expression)
                ExpressionReferences(expression, references);

        var declared = new HashSet<(AuthoringSymbolKind, string)>();
        var occurrences = new HashSet<NameOccurrence>(references);
        foreach (var declaration in symbols
                     .Where(symbol => symbol.Declaration is not null && DeclaredKinds.Contains(symbol.Kind))
                     .Select(symbol => new NameOccurrence(symbol.Kind, symbol.Name, symbol.Declaration!, true))
                     .Concat(names.Where(name => name.IsDeclaration)))
        {
            declared.Add((declaration.Kind, declaration.Name));
            if (!references.Contains(declaration with { IsDeclaration = false })) occurrences.Add(declaration);
        }

        var known = occurrences.Where(occurrence => declared.Contains((occurrence.Kind, occurrence.Name))).ToArray();
        Array.Sort(known, SourceOrder);
        return new(
            known.GroupBy(occurrence => occurrence.Range.Start.File, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal),
            known.GroupBy(occurrence => (occurrence.Kind, occurrence.Name))
                .ToDictionary(group => group.Key, group => group.ToArray()));
    }

    // Reads names from the same expression syntax the compiler binds, so each name keeps the meaning of its slot.
    private static void ExpressionReferences(ExpressionSiteSource source, HashSet<NameOccurrence> references)
    {
        var map = new ExpressionSourceMap(source);
        var text = source.Text;
        var diagnostics = new DiagnosticBag();
        SourceLocation Locate(int offset) => map.Map(offset, 0).Start;
        string Text(ExpressionSpan span) => text.Substring(span.Start, span.Length);
        void Add(AuthoringSymbolKind kind, ExpressionSpan span)
        {
            if (span.Length > 0) references.Add(new(kind, Text(span), map.Map(span.Start, span.Length), false));
        }

        void Reference(ReferenceExpressionNode reference)
        {
            switch (reference.Kind)
            {
                case ReferenceNamespace.Flag: Add(AuthoringSymbolKind.Flag, reference.Name); return;
                case ReferenceNamespace.Variable: Add(AuthoringSymbolKind.Variable, reference.Name); return;
                case ReferenceNamespace.Faction: Add(AuthoringSymbolKind.Faction, reference.Name); return;
                case ReferenceNamespace.Item: Add(AuthoringSymbolKind.Item, reference.Name); return;
                case ReferenceNamespace.Character:
                    var name = Text(reference.Name);
                    var member = reference.Member is { } memberSpan ? Text(memberSpan) : null;
                    if (name == ExpressionVocabulary.Player && member == ExpressionVocabulary.Visits)
                    {
                        Add(AuthoringSymbolKind.Scene, reference.Namespace);
                        return;
                    }
                    Add(AuthoringSymbolKind.Character, reference.Namespace);
                    if (member is null && name != ExpressionVocabulary.Pronoun &&
                        !ExpressionVocabulary.TryGetNamePart(name, out _))
                        Add(AuthoringSymbolKind.Stat, reference.Name);
                    return;
            }
        }

        void Expression(ExpressionNode? node)
        {
            switch (node)
            {
                case ParenthesizedExpressionNode parenthesized: Expression(parenthesized.Inner); break;
                case UnaryExpressionNode unary: Expression(unary.Operand); break;
                case BinaryExpressionNode binary:
                    Expression(binary.Left);
                    Expression(binary.Right);
                    break;
                case PlayerVisitedExpressionNode visited:
                    Add(AuthoringSymbolKind.Character, visited.Character);
                    Add(AuthoringSymbolKind.Scene, visited.Scene);
                    break;
                case InventoryCountExpressionNode inventoryCount:
                    Add(AuthoringSymbolKind.Character, inventoryCount.Character);
                    Add(AuthoringSymbolKind.Item, inventoryCount.Item);
                    break;
                case InventoryItemNameExpressionNode inventoryItemName:
                    Add(AuthoringSymbolKind.Character, inventoryItemName.Character);
                    Add(AuthoringSymbolKind.Item, inventoryItemName.Item);
                    break;
                case HasItemExpressionNode hasItem:
                    Add(AuthoringSymbolKind.Character, hasItem.Character);
                    Add(AuthoringSymbolKind.Item, hasItem.Item);
                    break;
                case ReferenceExpressionNode reference: Reference(reference); break;
            }
        }

        switch (source.Kind)
        {
            case ExpressionContextKind.Condition or ExpressionContextKind.Number:
                Expression(ExpressionSyntaxParser.ParseExpression(source.Tokens, source.ExpressionLength, Locate,
                    diagnostics).Root);
                break;
            case ExpressionContextKind.Substitution:
                Expression(ExpressionSyntaxParser.ParseSubstitution(source.Tokens, source.ExpressionLength, Locate,
                    diagnostics).Value);
                break;
            case ExpressionContextKind.Effects:
                foreach (var effect in ExpressionSyntaxParser.ParseEffects(source.Tokens, source.ExpressionLength,
                             Locate, diagnostics).Effects)
                {
                    switch (effect)
                    {
                        case SetFlagEffectSyntax setFlag: Add(AuthoringSymbolKind.Flag, setFlag.Flag); break;
                        case GiveTakeEffectSyntax giveTake:
                            Add(AuthoringSymbolKind.Character, giveTake.Character);
                            Add(AuthoringSymbolKind.Item, giveTake.Item);
                            Expression(giveTake.Quantity);
                            break;
                        case AssignmentEffectSyntax assignment:
                            if (assignment.Kind == ReferenceNamespace.Variable)
                                Add(AuthoringSymbolKind.Variable, assignment.Target);
                            else if (assignment.Kind == ReferenceNamespace.Faction)
                                Add(AuthoringSymbolKind.Faction, assignment.Target);
                            else if (assignment.Kind == ReferenceNamespace.Character)
                            {
                                Add(AuthoringSymbolKind.Character, assignment.Namespace);
                                Add(AuthoringSymbolKind.Stat, assignment.Target);
                            }
                            Expression(assignment.Amount);
                            break;
                    }
                }
                break;
        }
    }

    private static int Compare(SourceLocation left, SourceLocation right) =>
        left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Column.CompareTo(right.Column);
}
