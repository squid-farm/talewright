using System.Collections.Immutable;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Parsing;

namespace Talewright.Talemark.Vocabulary;

/// <summary>Reference namespace selected by the word before the first ':' in a reference.</summary>
internal enum ReferenceNamespace
{
    Character,
    Scene,
    Faction,
    Item,
    Flag,
    Variable,
}

/// <summary>
/// The single definition of Talemark expression, substitution and effect spellings. Code compares source text
/// against these definitions or the typed values they map to, never against hand-typed strings.
/// </summary>
internal static class ExpressionVocabulary
{
    internal const string And = "and";
    internal const string Or = "or";
    internal const string Not = "not";
    internal const string True = "true";
    internal const string False = "false";
    internal const string Visited = "visited";
    internal const string Scene = "scene";
    internal const string Player = "player";
    internal const string Visits = "visits";
    internal const string Has = "has";
    internal const string Image = "image";
    internal const string Decorative = "decorative";

    internal const string Faction = "faction";
    internal const string Item = "item";
    internal const string Flag = "flag";
    internal const string Var = "var";

    internal const string FirstName = "first-name";
    internal const string LastName = "last-name";
    internal const string Name = "name";
    internal const string Inventory = "inventory";
    internal const string Count = "count";
    internal const string Pronoun = "pronoun";
    internal const string Tier = "tier";
    internal const string Standing = "standing";
    internal const string SubjectForm = "subject";
    internal const string ObjectForm = "object";
    internal const string ReflexiveForm = "reflexive";
    internal const string PossessivePronoun = "possessive-pronoun";
    internal const string Determiner = "determiner";
    internal const string Independent = "independent";

    internal const string Set = "set";
    internal const string Clear = "clear";
    internal const string Give = "give";
    internal const string Take = "take";

    internal const string EqualTo = "==";
    internal const string NotEqualTo = "!=";
    internal const string LessOrEqual = "<=";
    internal const string GreaterOrEqual = ">=";
    internal const string AddAssign = "+=";
    internal const string SubtractAssign = "-=";

    internal const char Separator = ':';
    internal const char StatementSeparator = ';';
    internal const char OpenParen = '(';
    internal const char CloseParen = ')';
    internal const char ArgumentSeparator = ',';
    internal const char ModifierMarker = '#';
    internal const char PredicateMarker = '@';
    internal const char Plus = '+';
    internal const char Minus = '-';
    internal const char Star = '*';
    internal const char Slash = '/';
    internal const char Less = '<';
    internal const char Greater = '>';
    internal const char Assign = '=';
    internal const char NotEqualMarker = '!';

    /// <summary>Lexer punctuation, longest spelling first so two-character operators win.</summary>
    internal static readonly ImmutableArray<(string Spelling, TokenKind Kind)> Symbols =
    [
        (EqualTo, TokenKind.Equal),
        (NotEqualTo, TokenKind.NotEqual),
        (LessOrEqual, TokenKind.LessOrEqual),
        (GreaterOrEqual, TokenKind.GreaterOrEqual),
        (AddAssign, TokenKind.PlusAssign),
        (SubtractAssign, TokenKind.MinusAssign),
        (Separator.ToString(), TokenKind.Colon),
        (StatementSeparator.ToString(), TokenKind.Semicolon),
        (OpenParen.ToString(), TokenKind.LeftParen),
        (CloseParen.ToString(), TokenKind.RightParen),
        (ModifierMarker.ToString(), TokenKind.Hash),
        (Plus.ToString(), TokenKind.Plus),
        (Minus.ToString(), TokenKind.Minus),
        (Star.ToString(), TokenKind.Star),
        (Slash.ToString(), TokenKind.Slash),
        (Less.ToString(), TokenKind.Less),
        (Greater.ToString(), TokenKind.Greater),
        (Assign.ToString(), TokenKind.Assign),
    ];

    internal static readonly string FactionPrefix = Faction + Separator;
    internal static readonly string PlayerPrefix = Player + Separator;
    internal static readonly string ScenePrefix = Scene + Separator;
    internal static readonly string ItemPrefix = Item + Separator;
    internal static readonly string FlagPrefix = Flag + Separator;
    internal static readonly string VarPrefix = Var + Separator;
    internal static readonly string ImageCall = Image + OpenParen;

    internal static readonly ImmutableArray<string> CharacterProperties = [FirstName, LastName, Name, Pronoun];
    internal static readonly ImmutableArray<string> FactionProperties = [Name, Standing, Tier];
    internal static readonly ImmutableArray<string> ItemProperties = [Name];
    internal static readonly ImmutableArray<string> PronounProperties = [SubjectForm, ObjectForm, PossessivePronoun, ReflexiveForm];
    internal static readonly ImmutableArray<string> PossessiveProperties = [Determiner, Independent];
    internal static readonly ImmutableArray<string> EffectVerbs = [Set, Clear, Give, Take];

    private static readonly ImmutableArray<(BinaryOp Value, string Spelling)> BinaryOperators =
    [
        (BinaryOp.Or, Or),
        (BinaryOp.And, And),
        (BinaryOp.Equal, EqualTo),
        (BinaryOp.NotEqual, NotEqualTo),
        (BinaryOp.Less, Less.ToString()),
        (BinaryOp.LessOrEqual, LessOrEqual),
        (BinaryOp.Greater, Greater.ToString()),
        (BinaryOp.GreaterOrEqual, GreaterOrEqual),
        (BinaryOp.Add, Plus.ToString()),
        (BinaryOp.Subtract, Minus.ToString()),
        (BinaryOp.Multiply, Star.ToString()),
        (BinaryOp.Divide, Slash.ToString()),
    ];

    private static readonly ImmutableArray<(UnaryOp Value, string Spelling)> UnaryOperators =
    [
        (UnaryOp.Not, Not),
        (UnaryOp.Negate, Minus.ToString()),
    ];

    private static readonly ImmutableArray<(AssignOp Value, string Spelling)> AssignmentOperators =
    [
        (AssignOp.Set, Assign.ToString()),
        (AssignOp.Add, AddAssign),
        (AssignOp.Subtract, SubtractAssign),
    ];

    private static readonly ImmutableArray<(NamePart Value, string Spelling)> NameParts =
    [
        (NamePart.First, FirstName),
        (NamePart.Last, LastName),
        (NamePart.Full, Name),
    ];

    private static readonly ImmutableArray<(PronounForm Value, string Spelling)> PronounForms =
    [
        (PronounForm.Subject, SubjectForm),
        (PronounForm.Object, ObjectForm),
        (PronounForm.Reflexive, ReflexiveForm),
    ];

    private static readonly ImmutableArray<(PronounForm Value, string Spelling)> PossessiveForms =
    [
        (PronounForm.Possessive, Determiner),
        (PronounForm.PossessivePronoun, Independent),
    ];

    internal static ReferenceNamespace Namespace(ReadOnlySpan<char> text) => text switch
    {
        Scene => ReferenceNamespace.Scene,
        Faction => ReferenceNamespace.Faction,
        Item => ReferenceNamespace.Item,
        Flag => ReferenceNamespace.Flag,
        Var => ReferenceNamespace.Variable,
        _ => ReferenceNamespace.Character,
    };

    internal static bool TryGetSpelling(BinaryOp op, out string spelling) => TryGet(BinaryOperators, op, out spelling);

    internal static bool TryGetSpelling(UnaryOp op, out string spelling) => TryGet(UnaryOperators, op, out spelling);

    internal static string Spelling(BinaryOp op) => Required(BinaryOperators, op);

    internal static string Spelling(UnaryOp op) => Required(UnaryOperators, op);

    internal static string Spelling(AssignOp op) => Required(AssignmentOperators, op);

    internal static bool TryGetSpelling(NamePart part, out string spelling) => TryGet(NameParts, part, out spelling);

    internal static bool TryGetNamePart(ReadOnlySpan<char> text, out NamePart part) => TryGet(NameParts, text, out part);

    /// <summary>Spelling after <c>pronoun:</c> for a non-possessive form; the subject form also has a bare shorthand.</summary>
    internal static bool TryGetPronounSpelling(PronounForm form, out string spelling) =>
        TryGet(PronounForms, form, out spelling);

    internal static bool TryGetPronounForm(ReadOnlySpan<char> text, out PronounForm form) =>
        TryGet(PronounForms, text, out form);

    /// <summary>Spelling after <c>pronoun:possessive-pronoun:</c>.</summary>
    internal static bool TryGetPossessiveSpelling(PronounForm form, out string spelling) =>
        TryGet(PossessiveForms, form, out spelling);

    internal static bool TryGetPossessiveForm(ReadOnlySpan<char> text, out PronounForm form) =>
        TryGet(PossessiveForms, text, out form);

    private static bool TryGet<T>(ImmutableArray<(T Value, string Spelling)> table, T value, out string spelling)
        where T : struct, Enum
    {
        foreach (var entry in table)
        {
            if (!EqualityComparer<T>.Default.Equals(entry.Value, value)) continue;
            spelling = entry.Spelling;
            return true;
        }

        spelling = string.Empty;
        return false;
    }

    private static bool TryGet<T>(ImmutableArray<(T Value, string Spelling)> table, ReadOnlySpan<char> text, out T value)
        where T : struct, Enum
    {
        foreach (var entry in table)
        {
            if (!text.SequenceEqual(entry.Spelling)) continue;
            value = entry.Value;
            return true;
        }

        value = default;
        return false;
    }

    private static string Required<T>(ImmutableArray<(T Value, string Spelling)> table, T value) where T : struct, Enum =>
        TryGet(table, value, out var spelling)
            ? spelling
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"{typeof(T).Name} has no Talemark spelling.");
}
