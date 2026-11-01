using System.Text;
using Bogus;

namespace Talewright.Testing;

/// <summary>A generated story character with a first and last name and a survival stat.</summary>
public sealed record TaleCharacter
{
    public string Id { get; init; } = "";
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";
    public int Survival { get; init; }
}

/// <summary>A generated story item.</summary>
public sealed record TaleItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
}

/// <summary>
/// A generated cast: a player, companions and items, plus the flags and variables that
/// <see cref="TaleFaker"/> conditions and effects use. <see cref="Manifest"/> declares all of them.
/// </summary>
public sealed record TaleCast(string Player, IReadOnlyList<TaleCharacter> Companions, IReadOnlyList<TaleItem> Items)
{
    public static TaleVariableRef Chapter { get; } = new("chapter");
    public static TaleVariableRef Gold { get; } = new("gold");
    public static TaleVariableRef Trust { get; } = new("trust");
    public static TaleFlagRef Scouted { get; } = new("scouted");
    public static TaleFlagRef Alarm { get; } = new("alarm");

    public static TaleCast Create(int seed, int companions = 3, int items = 4)
    {
        var characters = new Faker<TaleCharacter>("en")
            .UseSeed(seed)
            .RuleFor(character => character.FirstName, f => TaleFaker.Letters(f.Name.FirstName()))
            .RuleFor(character => character.LastName, f => TaleFaker.Letters(f.Name.LastName()))
            .RuleFor(character => character.Survival, f => f.Random.Number(1, 5))
            .RuleFor(character => character.Id, (f, character) => $"{character.FirstName.ToLowerInvariant()}-{f.IndexFaker}");
        var things = new Faker<TaleItem>("en")
            .UseSeed(seed)
            .RuleFor(item => item.Name, f => $"{f.Commerce.ProductMaterial()} {f.Commerce.Product()}".ToLowerInvariant())
            .RuleFor(item => item.Id, (f, item) => $"{item.Name.Replace(' ', '-')}-{f.IndexFaker}");
        var people = characters.Generate(companions + 1);
        return new TaleCast(people[0].FirstName, people[1..], things.Generate(items));
    }

    public IReadOnlyList<string> Requirements =>
    [
        ExpressionFaker.AtLeast(TaleCharacterRef.Player.Field("dex"), 12),
        ExpressionFaker.AtLeast(Gold, 5),
        Scouted.ToString(),
        ExpressionFaker.Not(Alarm),
        .. Items.Select(item => ExpressionFaker.HasItem(TaleCharacterRef.Player, new TaleItemRef(item.Id))),
        .. Companions.Select(companion => ExpressionFaker.AtLeast(new TaleCharacterRef(companion.Id).Field("survival"), 3)),
    ];

    public IReadOnlyList<string> Effects =>
    [
        EffectFaker.Increase(Gold, 5),
        EffectFaker.Set(Scouted),
        EffectFaker.Set(Alarm),
        EffectFaker.Increase(Trust, 1),
        EffectFaker.Decrease(TaleCharacterRef.Player.Field("hp"), 1),
        .. Items.Select(item => EffectFaker.Give(TaleCharacterRef.Player, new TaleItemRef(item.Id))),
    ];

    public IReadOnlyList<string> Substitutions =>
    [
        SubstitutionFaker.Character(TaleCharacterRef.Player, "first-name"),
        SubstitutionFaker.Variable(Gold),
        .. Companions.Select(companion => SubstitutionFaker.Character(new TaleCharacterRef(companion.Id), "first-name")),
        .. Items.Select(item => SubstitutionFaker.Item(new TaleItemRef(item.Id))),
    ];

    public string Manifest(string id, string start, int seed)
    {
        var manifest = new ManifestFaker(id, id, start, seed)
        {
            Version = "1.0.0",
            BaseRuleset = "generic",
        };
        manifest.Flag(Scouted.Id);
        manifest.Flag(Alarm.Id);
        manifest.Number(Chapter.Id, 0);
        manifest.Number(Gold.Id, 0);
        manifest.Number(Trust.Id, 0);
        manifest.IncludeItemsSection();
        foreach (var item in Items)
        {
            manifest.Item(item.Id, item.Name);
        }

        manifest.Character(TaleCharacterRef.Player.Id, Player, stats: new Dictionary<string, int> { ["dex"] = 12 });
        foreach (var companion in Companions)
        {
            manifest.Character(companion.Id, companion.FirstName, companion.LastName,
                new Dictionary<string, int> { ["survival"] = companion.Survival });
        }

        return manifest.Build(trailingNewline: true);
    }
}

/// <summary>
/// Generates realistic Talemark text from a seeded Bogus <see cref="Faker"/>: titles, choice labels,
/// prose with substitutions, speaker lines and inline conditions, choices and whole scenes. The same seed and
/// cast produce the same text within a Bogus major version. <see cref="Manifest"/> declares everything it uses.
/// </summary>
/// <example><code>
/// var fake = new TaleFaker(seed: 7);
/// var source = new StorySource(new Dictionary&lt;string, string&gt;
/// {
///     [StorySource.ManifestFileName] = fake.Manifest("camp"),
///     ["story.tale"] = fake.Scene("camp", fake.Choice("END")),
/// });
/// </code></example>
public sealed class TaleFaker
{
    public TaleFaker(int seed, TaleCast? cast = null)
    {
        Seed = seed;
        Bogus = new Faker("en") { Random = new Randomizer(seed) };
        Cast = cast ?? TaleCast.Create(seed);
    }

    public int Seed { get; }
    public Faker Bogus { get; }
    public TaleCast Cast { get; }
    public int SceneCount { get; private set; }
    public long ProseBytes { get; private set; }

    public (int Min, int Max) WordsPerScene { get; init; } = (30, 150);
    public double ChoiceRequires { get; init; } = 0.2;
    public double ChoiceEffects { get; init; } = 0.2;
    public double Substitution { get; init; } = 0.35;
    public double SpeakerLine { get; init; } = 0.4;
    public double Bold { get; init; } = 0.15;
    public double InlineIf { get; init; } = 0.1;

    public bool Chance(double probability) => Bogus.Random.Bool((float)probability);

    public T Pick<T>(IReadOnlyList<T> items) => items[Bogus.Random.Number(items.Count - 1)];

    public int Between((int Min, int Max) range) => Bogus.Random.Number(range.Min, range.Max);

    public string Manifest(string start) => Cast.Manifest("generated-story", start, Seed);

    public string Title() => Clean(Chance(0.5) ? Bogus.Address.City() : $"The {Bogus.Commerce.ProductAdjective()} {Bogus.Commerce.Product()}");

    public string Label() => Capitalize(Clean($"{Bogus.Hacker.Verb()} the {Bogus.Hacker.Noun()}"));

    public string Requirement() => Pick(Cast.Requirements);

    public string Effect() => Pick(Cast.Effects);

    /// <summary>A scene with a title, prose and the given lines. Lines starting with <c>@</c> are metadata.</summary>
    public string Scene(string id, params string[] lines)
    {
        var text = new StringBuilder(SceneFaker.Header(id)).Append('\n');
        if (!lines.Any(line => line.StartsWith(SceneFaker.TitlePrefix, StringComparison.Ordinal)))
        {
            text.Append(SceneFaker.Title(Title())).Append('\n');
        }

        foreach (var line in lines.Where(line => line.StartsWith('@')))
        {
            text.Append(line).Append('\n');
        }

        var prose = Prose();
        ProseBytes += prose.Length;
        SceneCount++;
        text.Append('\n').Append(prose);
        foreach (var line in lines.Where(line => !line.StartsWith('@')))
        {
            text.Append(line).Append('\n');
        }

        return text.Append('\n').ToString();
    }

    /// <summary>A choice with a generated label and optional effect; <paramref name="gated"/> choices may get <c>requires</c>.</summary>
    public string Choice(string target, string? label = null, bool gated = false, string? requires = null)
    {
        requires ??= gated && Chance(ChoiceRequires) ? Requirement() : null;
        var effect = Chance(ChoiceEffects) ? Effect() : null;
        return ChoiceFaker.Create(target, label ?? Label(), requires, effect);
    }

    public static string Divert(string target) => DivertFaker.To(target);

    public string Prose()
    {
        var text = new StringBuilder();
        for (var words = Between(WordsPerScene); words > 0;)
        {
            var sentences = new List<string>();
            for (var count = Bogus.Random.Number(2, 4); count > 0 && words > 0; count--)
            {
                sentences.Add(Sentence());
                words -= sentences[^1].Count(character => character == ' ') + 1;
            }

            if (Chance(InlineIf))
            {
                sentences.Add(IfFaker.Inline(TaleCast.Scouted, Sentence()));
            }

            text.AppendJoin(' ', sentences).Append("\n\n");
        }

        if (Chance(SpeakerLine))
        {
            text.Append(SceneFaker.Speaker(new TaleCharacterRef(Pick(Cast.Companions).Id), Sentence())).Append("\n\n");
        }

        return text.ToString();
    }

    public string Sentence()
    {
        var sentence = Bogus.Random.Number(2) switch
        {
            0 => Bogus.Rant.Review(Pick(Cast.Items).Name),
            1 => Bogus.Hacker.Phrase(),
            _ => $"{Bogus.Name.FirstName()} said the road to {Bogus.Address.City()} was {Bogus.Commerce.ProductAdjective()}.",
        };
        var words = Capitalize(Clean(sentence)).Split(' ');
        if (Chance(Substitution))
        {
            words[Bogus.Random.Number(words.Length - 1)] = Pick(Cast.Substitutions);
        }

        if (Chance(Bold))
        {
            var index = Bogus.Random.Number(words.Length - 1);
            words[index] = MarkupFaker.Strong(words[index]);
        }

        var text = string.Join(' ', words);
        return text[^1] is '.' or '!' or '?' ? text : text + ".";
    }

    internal static string Letters(string text)
    {
        var letters = new string(text.Where(char.IsAsciiLetter).ToArray());
        return letters.Length > 0 ? letters : "Sam";
    }

    // Generated text keeps only characters that are never Talemark syntax.
    private static string Clean(string text) =>
        string.Join(' ', new string(text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or ',' or '\'' or '!' or '?' or '-' ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Capitalize(string text) => text.Length == 0 ? "Well" : char.ToUpperInvariant(text[0]) + text[1..];
}
