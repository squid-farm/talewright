using Talewright.Narrative.Templates;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Parsing;

namespace Talewright.Talemark.Vocabulary;

/// <summary>Author-facing spelling and help for a recognized Talemark syntax kind.</summary>
public readonly record struct AuthoringSyntaxHelp(string Spelling, string Description);

/// <summary>Shared syntax help for authoring tokens and completion keywords.</summary>
public static class AuthoringSyntaxVocabulary
{
    private readonly record struct Entry(AuthoringTokenKind Kind, string Spelling, string Description,
        bool ExpressionToken = false, bool DirectiveHelp = true, bool Lookup = true);

    private static readonly Entry[] Entries = CreateEntries();
    private static readonly Dictionary<AuthoringTokenKind, AuthoringSyntaxHelp> HelpByKind =
        Entries.ToDictionary(entry => entry.Kind, entry => new AuthoringSyntaxHelp(entry.Spelling, entry.Description));
    private static readonly Dictionary<string, Entry> HelpBySpelling = Entries.Where(entry => entry.Lookup)
        .ToDictionary(entry => entry.Spelling, StringComparer.Ordinal);
    private static readonly Dictionary<string, AuthoringTokenKind> ExpressionKinds = Entries
        .Where(entry => entry.ExpressionToken)
        .ToDictionary(entry => entry.Spelling, entry => entry.Kind, StringComparer.Ordinal);
    private static readonly Dictionary<string, AuthoringTokenKind> CompletionAliases = new(StringComparer.Ordinal)
    {
        [ExpressionVocabulary.FlagPrefix] = AuthoringTokenKind.Flag,
        [ExpressionVocabulary.VarPrefix] = AuthoringTokenKind.Variable,
        [ExpressionVocabulary.FactionPrefix] = AuthoringTokenKind.Faction,
        [ExpressionVocabulary.ItemPrefix] = AuthoringTokenKind.Item,
        [ExpressionVocabulary.ScenePrefix] = AuthoringTokenKind.SceneContext,
        [ExpressionVocabulary.PlayerPrefix] = AuthoringTokenKind.Visits,
        [ExpressionVocabulary.ImageCall] = AuthoringTokenKind.Image,
    };
    private static readonly IReadOnlyDictionary<string, string> ModifierDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [TextModifierRegistry.BoldName] = "Applies bold formatting to the value without changing its text.",
        [TextModifierRegistry.ItalicName] = "Applies italic formatting to the value without changing its text.",
        [ExpressionVocabulary.Decorative] = "Marks image media as purely decorative so assistive technology ignores it. Do not combine it with descriptive alt text.",
    };

    /// <summary>Characters that can start a Talemark completion request.</summary>
    public static IReadOnlyList<string> CompletionTriggerCharacters { get; } = Array.AsReadOnly(new[]
    {
        ExpressionVocabulary.Separator.ToString(),
        SceneVocabulary.MetadataMarker.ToString(),
        ExpressionVocabulary.PredicateMarker.ToString(),
        ExpressionVocabulary.ModifierMarker.ToString(),
        SceneVocabulary.SpeechMarker.ToString(),
        SceneVocabulary.Divert[^1].ToString(),
        ExpressionVocabulary.OpenParen.ToString(),
        TextVocabulary.QuoteMark.ToString(),
        SceneVocabulary.EndIf[0].ToString(),
    }.Distinct(StringComparer.Ordinal).ToArray());

    /// <summary>Whether a completion spelling names the given source token before its delimiter.</summary>
    public static bool CompletionMatchesToken(string spelling, string token) =>
        spelling.TrimEnd(ExpressionVocabulary.Separator, ExpressionVocabulary.OpenParen) == token;

    /// <summary>Formats the marker and registered name of a text modifier.</summary>
    public static string FormatModifier(string name) => ExpressionVocabulary.ModifierMarker + name;

    /// <summary>Escapes text for insertion inside an existing Talemark quoted string.</summary>
    public static string EscapeQuotedText(string text) => QuotedStringSyntax.EscapeContent(text);

    /// <summary>Formats a hub's tag selector.</summary>
    public static string FormatHubSelector(IEnumerable<string> tags, bool matchAll) =>
        string.Join($" {(matchAll ? ExpressionVocabulary.And : ExpressionVocabulary.Or)} ", tags);

    /// <summary>Gets help for a parser-recognized syntax kind.</summary>
    public static bool TryGetHelp(AuthoringTokenKind kind, out AuthoringSyntaxHelp help) =>
        HelpByKind.TryGetValue(kind, out help);

    /// <summary>Gets help for a parser-offered completion spelling, including prefixes and calls.</summary>
    public static bool TryGetKeywordHelp(string spelling, out AuthoringSyntaxHelp help)
    {
        if (HelpBySpelling.TryGetValue(spelling, out var entry))
        {
            help = new(entry.Spelling, entry.Description);
            return true;
        }
        if (ModifierDescriptions.TryGetValue(spelling, out var description))
        {
            help = new(FormatModifier(spelling), description);
            return true;
        }
        if (CompletionAliases.TryGetValue(spelling, out var kind))
            return TryGetHelp(kind, out help);
        help = default;
        return false;
    }

    /// <summary>Gets syntax help for a parsed directive name.</summary>
    public static bool TryGetDirectiveHelp(string spelling, out AuthoringSyntaxHelp help)
    {
        if (HelpBySpelling.TryGetValue(spelling, out var entry) && entry.DirectiveHelp)
        {
            help = new(entry.Spelling, entry.Description);
            return true;
        }
        help = default;
        return false;
    }

    internal static AuthoringTokenKind ExpressionKind(string text) =>
        ExpressionKinds.GetValueOrDefault(text, AuthoringTokenKind.Other);

    internal static AuthoringTokenKind ConditionalKind(string text)
    {
        if (HelpBySpelling.TryGetValue(text, out var entry) &&
            entry.Kind is AuthoringTokenKind.If or AuthoringTokenKind.Else or AuthoringTokenKind.EndIf)
            return entry.Kind;
        throw new ArgumentException("Expected a recognized conditional keyword.", nameof(text));
    }

    internal static IReadOnlyDictionary<string, string> KeywordDetails(params string[] spellings)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var spelling in spellings)
        {
            if (!TryGetKeywordHelp(spelling, out var help))
                throw new InvalidOperationException($"No authoring help is defined for '{spelling}'.");
            result.Add(spelling, help.Description);
        }
        return result;
    }

    private static Entry[] CreateEntries()
    {
        const string speechOrGreater = "Speech marker at line start; in expressions, tests whether the left value is larger.";
        const string namePart = "Character name part used in substitutions.";
        var separator = ExpressionVocabulary.Separator;
        var argumentSeparator = ExpressionVocabulary.ArgumentSeparator;
        var closeParen = ExpressionVocabulary.CloseParen;
        var modifier = ExpressionVocabulary.ModifierMarker;
        var quote = TextVocabulary.QuoteMark;
        var inlineOpen = SceneVocabulary.InlineOpen;
        var inlineClose = SceneVocabulary.InlineClose;
        var oneMany = $"{modifier}{OneManyTextModifier.ModifierName}{ExpressionVocabulary.OpenParen}{quote}singular{quote}{argumentSeparator} {quote}plural{quote}{closeParen}";
        var flag = $"{ExpressionVocabulary.FlagPrefix}id";
        var faction = $"{ExpressionVocabulary.FactionPrefix}id";
        return
        [
            new(AuthoringTokenKind.Title, SceneVocabulary.Title, "Scene title shown to players."),
            new(AuthoringTokenKind.Requires, SceneVocabulary.Requires, "Boolean condition controlling whether this scene is available."),
            new(AuthoringTokenKind.Shown, SceneVocabulary.Shown, "Shows a choice or hub scene while its requirement is false, optionally replacing its label with reader text."),
            new(AuthoringTokenKind.Tags, SceneVocabulary.Tags, "Comma-separated scene tags."),
            new(AuthoringTokenKind.Priority, SceneVocabulary.Priority, "Integer priority used when selecting an available scene."),
            new(AuthoringTokenKind.Once, SceneVocabulary.Once, "Marks a scene as available only once."),
            new(AuthoringTokenKind.Hub, SceneVocabulary.Hub,
                $"Offers available storylets matching one tag, every tag in a conjunction such as 'camp {ExpressionVocabulary.And} night', " +
                $"or at least one tag in a disjunction such as 'night {ExpressionVocabulary.Or} watch'."),
            new(AuthoringTokenKind.HubLimit, SceneVocabulary.HubLimit, "Positive maximum number of choices offered by this hub."),
            new(AuthoringTokenKind.End, SceneVocabulary.End, "Ends the story instead of diverting to another scene."),
            new(AuthoringTokenKind.SceneHeader, SceneVocabulary.SceneHeader, $"Declares a scene: {SceneVocabulary.SceneHeader} scene.id. Put scene metadata immediately after this header."),
            new(AuthoringTokenKind.Divert, SceneVocabulary.Divert, $"Diverts to a scene ID, or {SceneVocabulary.End} to finish the story, as soon as this line is reached. Later lines do not run."),
            new(AuthoringTokenKind.Effects, SceneVocabulary.EffectsMarker.ToString(), "Effects: applies semicolon-separated effects when this line is reached, then runs triggers before the next line."),
            new(AuthoringTokenKind.Choice, SceneVocabulary.ChoiceMarker.ToString(), $"Choice: {SceneVocabulary.ChoiceMarker} {SceneVocabulary.OpenBracket}label{SceneVocabulary.CloseBracket} with optional conditions and effects, followed by a divert or skill check. Omit the label with {SceneVocabulary.ChoiceMarker} {SceneVocabulary.Divert} target to show Continue.", Lookup: false),
            new(AuthoringTokenKind.SkillCheckBlock, SceneVocabulary.SkillCheckBlock, $"Entry skill-check block, rolled when this line is reached: {SceneVocabulary.SkillCheckBlock} {SceneVocabulary.OpenBracket}actor{SceneVocabulary.CloseBracket} stat {SceneVocabulary.Difficulty} number, followed by success/failure outcomes and a closing {SceneVocabulary.SkillCheckFence}."),
            new(AuthoringTokenKind.SkillCheck, SceneVocabulary.SkillCheck, $"{SceneVocabulary.SkillCheck} {SceneVocabulary.OpenBracket}actor{SceneVocabulary.CloseBracket} stat {SceneVocabulary.Difficulty} number: resolves a check using the selected ruleset, with success/failure outcomes.", ExpressionToken: true),
            new(AuthoringTokenKind.If, SceneVocabulary.If, $"Begins conditional text with {inlineOpen}{SceneVocabulary.If} condition{inlineClose}. Close it with {inlineOpen}{SceneVocabulary.EndIf}{inlineClose}; {inlineOpen}{SceneVocabulary.Else}{inlineClose} is optional.", ExpressionToken: true),
            new(AuthoringTokenKind.Else, SceneVocabulary.Else, $"Alternative text for the preceding {inlineOpen}{SceneVocabulary.If} condition{inlineClose}.", ExpressionToken: true),
            new(AuthoringTokenKind.EndIf, SceneVocabulary.EndIf, "Closes the current conditional text block."),
            new(AuthoringTokenKind.Visited, ExpressionVocabulary.Visited, $"{ExpressionVocabulary.Player} {ExpressionVocabulary.Visited} scene: true when the player has visited the scene.", ExpressionToken: true),
            new(AuthoringTokenKind.SceneContext, ExpressionVocabulary.Scene, $"{ExpressionVocabulary.ScenePrefix}{ExpressionVocabulary.Player}:{ExpressionVocabulary.Visits}: the current scene's visit count.", ExpressionToken: true),
            new(AuthoringTokenKind.Visits, ExpressionVocabulary.Visits, $"{ExpressionVocabulary.Visits}: number of times the player has entered a scene.", ExpressionToken: true),
            new(AuthoringTokenKind.Flag, ExpressionVocabulary.Flag, $"{flag}: a declared boolean story flag.", ExpressionToken: true),
            new(AuthoringTokenKind.Variable, ExpressionVocabulary.Var, $"{ExpressionVocabulary.VarPrefix}id: a story variable with a fixed integer or text type.", ExpressionToken: true),
            new(AuthoringTokenKind.Faction, ExpressionVocabulary.Faction, $"{ExpressionVocabulary.FactionPrefix}id: the faction's display name. {ExpressionVocabulary.FactionPrefix}id{separator}{ExpressionVocabulary.Standing} selects its numeric standing, {ExpressionVocabulary.FactionPrefix}id{separator}{ExpressionVocabulary.Tier} its reputation tier, and {ExpressionVocabulary.FactionPrefix}id{separator}{ExpressionVocabulary.Name} its display name explicitly.", ExpressionToken: true),
            new(AuthoringTokenKind.Item, ExpressionVocabulary.Item, $"{ExpressionVocabulary.ItemPrefix}id: the item's display name; {ExpressionVocabulary.ItemPrefix}id{separator}{ExpressionVocabulary.Name} is an explicit form. {modifier}{TextModifierRegistry.IndefiniteArticleName}, {modifier}{TextModifierRegistry.DefiniteArticleName}, {modifier}{TextModifierRegistry.ThisName}, {modifier}{TextModifierRegistry.ThatName} and {modifier}{TextModifierRegistry.PossessiveName} use its name grammar; {oneMany} selects from name-agreement, not quantity. Copying the name to a text variable copies text only.", ExpressionToken: true),
            new(AuthoringTokenKind.Inventory, ExpressionVocabulary.Inventory, $"The {ExpressionVocabulary.Inventory} namespace reads an item's held state as {ExpressionVocabulary.Player}:{ExpressionVocabulary.Inventory}:item:{ExpressionVocabulary.Count}; counts are 0 when not held and read-only.", DirectiveHelp: false),
            new(AuthoringTokenKind.Count, ExpressionVocabulary.Count, $"The numeric quantity in a {ExpressionVocabulary.Player}:{ExpressionVocabulary.Inventory}:item:{ExpressionVocabulary.Count} reference; reads 0 when the character holds none and changes only with {ExpressionVocabulary.Give} or {ExpressionVocabulary.Take}.", DirectiveHelp: false),
            new(AuthoringTokenKind.Image, ExpressionVocabulary.Image, $"{ExpressionVocabulary.ImageCall}{quote}path{quote}{argumentSeparator} {quote}alt text{quote}{closeParen}: inserts image media in narrative text, dialogue or choices. The path is a literal string relative to images/. Alt text is optional and uses the same substitutions as prose. Use {modifier}{ExpressionVocabulary.Decorative} after the call for a purely decorative image; do not supply descriptive alt text with it. File lookup and image rendering are separate features.", ExpressionToken: true),
            new(AuthoringTokenKind.Set, ExpressionVocabulary.Set, $"{ExpressionVocabulary.Set} {flag}: sets a flag to {ExpressionVocabulary.True}.", ExpressionToken: true),
            new(AuthoringTokenKind.Clear, ExpressionVocabulary.Clear, $"{ExpressionVocabulary.Clear} {flag}: sets a flag to {ExpressionVocabulary.False}.", ExpressionToken: true),
            new(AuthoringTokenKind.Give, ExpressionVocabulary.Give, $"{ExpressionVocabulary.Give} character item {SceneVocabulary.OpenBracket}quantity{SceneVocabulary.CloseBracket}: adds inventory; quantity is a numeric expression and defaults to one.", ExpressionToken: true),
            new(AuthoringTokenKind.Take, ExpressionVocabulary.Take, $"{ExpressionVocabulary.Take} character item {SceneVocabulary.OpenBracket}quantity{SceneVocabulary.CloseBracket}: removes inventory; quantity is a numeric expression and defaults to one.", ExpressionToken: true),
            new(AuthoringTokenKind.Has, ExpressionVocabulary.Has, $"character {ExpressionVocabulary.Has} item: tests whether a character holds an item.", ExpressionToken: true),
            new(AuthoringTokenKind.And, ExpressionVocabulary.And,
                $"Boolean conjunction; in an @hub selector, requires every listed tag.", ExpressionToken: true),
            new(AuthoringTokenKind.Or, ExpressionVocabulary.Or,
                "Boolean disjunction; in an @hub selector, requires at least one listed tag.", ExpressionToken: true),
            new(AuthoringTokenKind.Not, ExpressionVocabulary.Not, "Negates a boolean condition.", ExpressionToken: true),
            new(AuthoringTokenKind.True, ExpressionVocabulary.True, "Boolean true literal.", ExpressionToken: true),
            new(AuthoringTokenKind.False, ExpressionVocabulary.False, "Boolean false literal.", ExpressionToken: true),
            new(AuthoringTokenKind.Plus, ExpressionVocabulary.Plus.ToString(), "Adds numeric operands.", ExpressionToken: true),
            new(AuthoringTokenKind.Minus, ExpressionVocabulary.Minus.ToString(), "Subtracts numeric operands, or negates a number.", ExpressionToken: true),
            new(AuthoringTokenKind.Multiply, ExpressionVocabulary.Star.ToString(), "Multiplies numeric operands.", ExpressionToken: true),
            new(AuthoringTokenKind.Divide, ExpressionVocabulary.Slash.ToString(), "Divides numeric operands.", ExpressionToken: true),
            new(AuthoringTokenKind.Assign, ExpressionVocabulary.Assign.ToString(), "Assigns a value of the target's type. Text variables require text; numeric targets require numbers.", ExpressionToken: true),
            new(AuthoringTokenKind.AddAssign, ExpressionVocabulary.AddAssign, "Adds a numeric value to an effect target.", ExpressionToken: true),
            new(AuthoringTokenKind.SubtractAssign, ExpressionVocabulary.SubtractAssign, "Subtracts a numeric value from an effect target.", ExpressionToken: true),
            new(AuthoringTokenKind.Equal, ExpressionVocabulary.EqualTo, "Tests two compatible values for equality.", ExpressionToken: true),
            new(AuthoringTokenKind.NotEqual, ExpressionVocabulary.NotEqualTo, "Tests two compatible values for inequality.", ExpressionToken: true),
            new(AuthoringTokenKind.Less, ExpressionVocabulary.Less.ToString(), "Tests whether the left value is smaller.", ExpressionToken: true),
            new(AuthoringTokenKind.LessOrEqual, ExpressionVocabulary.LessOrEqual, "Tests whether the left value is smaller or equal.", ExpressionToken: true),
            new(AuthoringTokenKind.Speech, SceneVocabulary.SpeechMarker.ToString(), speechOrGreater, Lookup: false),
            new(AuthoringTokenKind.Greater, ExpressionVocabulary.Greater.ToString(), speechOrGreater, ExpressionToken: true),
            new(AuthoringTokenKind.GreaterOrEqual, ExpressionVocabulary.GreaterOrEqual, "Tests whether the left value is larger or equal.", ExpressionToken: true),
            new(AuthoringTokenKind.FirstName, ExpressionVocabulary.FirstName, namePart, DirectiveHelp: false),
            new(AuthoringTokenKind.LastName, ExpressionVocabulary.LastName, namePart, DirectiveHelp: false),
            new(AuthoringTokenKind.Name, ExpressionVocabulary.Name, "Character, faction or item display name used in references.", DirectiveHelp: false),
            new(AuthoringTokenKind.Standing, ExpressionVocabulary.Standing, "Numeric player standing with a faction.", DirectiveHelp: false),
            new(AuthoringTokenKind.Tier, ExpressionVocabulary.Tier, "Faction standing tier, such as hostile or friendly.", DirectiveHelp: false),
            new(AuthoringTokenKind.Pronoun, ExpressionVocabulary.Pronoun, $"Character pronoun properties: {separator}{ExpressionVocabulary.SubjectForm}, {separator}{ExpressionVocabulary.ObjectForm}, {separator}{ExpressionVocabulary.ReflexiveForm}, {separator}{ExpressionVocabulary.PossessivePronoun}{separator}{ExpressionVocabulary.Determiner} or {separator}{ExpressionVocabulary.PossessivePronoun}{separator}{ExpressionVocabulary.Independent}. The bare pronoun gives the subject form. Use {oneMany} to select wording from effective pronoun agreement.", DirectiveHelp: false),
            new(AuthoringTokenKind.Subject, ExpressionVocabulary.SubjectForm, "Pronoun form: subject, such as she, he or they.", DirectiveHelp: false),
            new(AuthoringTokenKind.Object, ExpressionVocabulary.ObjectForm, "Pronoun form: object, such as her, him or them.", DirectiveHelp: false),
            new(AuthoringTokenKind.PossessivePronoun, ExpressionVocabulary.PossessivePronoun, "Possessive pronoun properties: determiner (her, their) or independent (hers, theirs).", DirectiveHelp: false),
            new(AuthoringTokenKind.Reflexive, ExpressionVocabulary.ReflexiveForm, "Pronoun form: reflexive, such as herself or themselves.", DirectiveHelp: false),
            new(AuthoringTokenKind.Determiner, ExpressionVocabulary.Determiner, "Possessive determiner before a noun, such as her or their.", DirectiveHelp: false),
            new(AuthoringTokenKind.Independent, ExpressionVocabulary.Independent, "Independent possessive that stands alone, such as hers or theirs.", DirectiveHelp: false),
            new(AuthoringTokenKind.OneMany, OneManyTextModifier.ModifierName, $"Agreement selector: {oneMany}. Selects from effective pronoun agreement (including singular they) or item name-agreement, never inventory quantity or uncountable.", DirectiveHelp: false),
        ];
    }
}
