using System.Collections.Immutable;
using CorePronouns = Talewright.Core.World.Pronouns;
using Talewright.Core.Rulesets;

namespace Talewright.Talemark.Vocabulary;

/// <summary>Owned manifest property names and fixed values accepted by the Talewright configuration reader.</summary>
internal static class ManifestVocabulary
{
    internal const string Game = "game";
    internal const string Characters = "characters";
    internal const string Factions = "factions";
    internal const string Items = "items";
    internal const string Flags = "flags";
    internal const string Vars = "vars";
    internal const string Triggers = "triggers";

    internal const string Id = "id";
    internal const string Title = SceneVocabulary.Title;
    internal const string Version = "version";
    internal const string Start = "start";
    internal const string Randomness = "randomness";
    internal const string Seed = "seed";
    internal const string Undo = "undo";
    internal const string Autosave = "autosave";
    internal const string CharacterSheet = "character-sheet";
    internal const string Ruleset = "ruleset";
    internal const string RulesetNone = "none";
    internal const string Creation = "creation";

    internal const string When = "when";
    internal const string Do = "do";
    internal const string Once = SceneVocabulary.Once;
    internal const string Save = "save";

    internal const string Base = "base";
    internal const string Disable = "disable";
    internal const string Stats = "stats";
    internal const string Name = ExpressionVocabulary.Name;
    internal const string Category = "category";
    internal const string Min = "min";
    internal const string Max = "max";
    internal const string Default = "default";
    internal const string GoverningAttribute = "governing-attribute";

    internal const string FirstName = ExpressionVocabulary.FirstName;
    internal const string LastName = ExpressionVocabulary.LastName;
    internal const string Pronouns = "pronouns";
    internal const string Inventory = "inventory";
    internal const string NameAgreement = "name-agreement";
    internal const string Subject = ExpressionVocabulary.SubjectForm;
    internal const string Object = ExpressionVocabulary.ObjectForm;
    internal const string PossessivePronoun = ExpressionVocabulary.PossessivePronoun;
    internal const string Reflexive = ExpressionVocabulary.ReflexiveForm;
    internal const string Agreement = "agreement";
    internal const string Determiner = ExpressionVocabulary.Determiner;
    internal const string Independent = ExpressionVocabulary.Independent;
    internal const string Uncountable = "uncountable";
    internal const string Modifiers = "modifiers";
    internal const string Standing = ExpressionVocabulary.Standing;
    internal const string Presets = "presets";
    internal const string Label = "label";

    internal const string RandomnessOff = "off";
    internal const string RandomnessChecksOnly = "checks-only";
    internal const string StatCategoryAttribute = "attribute";
    internal const string StatCategorySkill = "skill";
    internal const string StatCategoryResource = "resource";
    internal const string StatCategoryProgression = "progression";
    internal const string StatCategoryCustom = "custom";
    internal static readonly ImmutableArray<string> StatCategories =
    [
        StatCategoryAttribute, StatCategorySkill, StatCategoryResource, StatCategoryProgression, StatCategoryCustom,
    ];
    private static readonly ImmutableDictionary<string, StatCategory> StatCategoryValues =
        ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase,
        [
            KeyValuePair.Create(StatCategoryAttribute, StatCategory.Attribute),
            KeyValuePair.Create(StatCategorySkill, StatCategory.Skill),
            KeyValuePair.Create(StatCategoryResource, StatCategory.Resource),
            KeyValuePair.Create(StatCategoryProgression, StatCategory.Progression),
            KeyValuePair.Create(StatCategoryCustom, StatCategory.Custom),
        ]);

    internal static bool TryParseStatCategory(string keyword, out StatCategory category) =>
        StatCategoryValues.TryGetValue(keyword, out category) ||
        Enum.TryParse(keyword, ignoreCase: true, out category);

    internal const string GrammaticalSingular = "singular";
    internal const string GrammaticalPlural = "plural";

    internal const string SymbolScene = ExpressionVocabulary.Scene;
    internal const string SymbolStory = "story";
    internal const string SymbolCharacter = "character";
    internal const string SymbolItem = ExpressionVocabulary.Item;
    internal const string SymbolFaction = ExpressionVocabulary.Faction;
    internal const string SymbolInteger = "integer";
    internal const string SymbolBoolean = "boolean";
    internal const string SymbolText = "text";
    internal const string SymbolUnresolved = "unresolved";
    internal static readonly ImmutableArray<string> BuiltInPronounKeywords =
    [
        .. CorePronouns.BuiltInByKeyword.Keys,
    ];

    internal static readonly ImmutableArray<string> PropertyKeys =
    [
        Game, Characters, Factions, Items, Flags, Vars, Triggers,
        Id, Title, Version, Start, Randomness, Seed, Undo, Autosave, CharacterSheet, Ruleset, Creation,
        When, Do, Once, Save, Base, Disable, Stats, Name, Category, Min, Max, Default, GoverningAttribute,
        FirstName, LastName, Pronouns, Inventory, NameAgreement, Subject, Object, PossessivePronoun, Reflexive,
        Agreement, Determiner, Independent, Uncountable, Modifiers, Standing, Presets, Label,
    ];

    internal static readonly ImmutableArray<string> FixedKeywordValues =
    [
        RandomnessOff, RandomnessChecksOnly,
        .. StatCategories,
        GrammaticalSingular, GrammaticalPlural,
        .. BuiltInPronounKeywords,
    ];

    internal static readonly ImmutableArray<string> AuthoringSymbolTypes =
    [
        SymbolScene, SymbolStory, SymbolCharacter, SymbolItem, SymbolFaction,
        SymbolInteger, SymbolBoolean, SymbolText, SymbolUnresolved, StatCategoryAttribute, Ruleset,
    ];
}
