using System.Text.Json;
using Talewright.Core.Rulesets;
using Talewright.Core.World;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Authoring;

public sealed record AuthoringHover(SourceRange Range, string Markdown, string PlainText);

/// <summary>Editor-neutral help from parsed source slots and explicit project registrations.</summary>
public static class StoryHover
{
    private static readonly JsonDocument Schema = LoadSchema();

    public static AuthoringHover? Get(StoryAnalysis analysis, SourceLocation position)
    {
        var context = GetContext(analysis, position);
        var preview = StoryPreview.Get(analysis, position.File).FirstOrDefault(candidate => Contains(candidate.Range, position));
        if (preview is null) return context;
        var markdown = preview.Value is { } value
            ? $"**Result:** {Code(StoryPreview.Quote(value))} **(default)**\n\nAuthored defaults, not current gameplay state.\n\n" +
              string.Join("\n\n", preview.Steps.Select(step => $"{Code(step.Name)}: {Code(StoryPreview.Quote(step.Value))}"))
            : preview.PlainText;
        return new(context?.Range ?? preview.Range,
            markdown + (context is null ? "" : "\n\n---\n\n" + context.Markdown),
            preview.PlainText + (context is null ? "" : "\n\n" + context.PlainText));
    }

    private static AuthoringHover? GetContext(StoryAnalysis analysis, SourceLocation position)
    {
        if (analysis.Diagnostics.Any(diagnostic => diagnostic.Code is AuthoringDiagnosticCodes.CustomFeaturesUnavailable or AuthoringDiagnosticCodes.StoryAnalysisUnavailable))
            throw new InvalidOperationException("Hover is unavailable until project authoring metadata is supplied and valid.");
        var declaration = analysis.DeclaredAt(position);
        if (declaration is not null) return Describe(analysis, declaration, declaration.Declaration!);
        var sites = analysis.CompletionSitesAt(position);
        foreach (var site in sites.Where(site => site.Text.Length > 0 && Contains(site.Range, position)))
        {
            var symbol = (site.Candidates ?? analysis.Symbols).FirstOrDefault(candidate =>
                site.Kinds.Contains(candidate.Kind) && candidate.Name == site.Text);
            if (symbol is not null) return Describe(analysis, symbol, site.Range);
            if (site.Kinds.Contains(AuthoringSymbolKind.Directive) &&
                AuthoringSyntaxVocabulary.TryGetDirectiveHelp(site.Text, out var directive))
                return Help(site.Text, directive.Description, site.Range);
            if (site.Keywords.Contains(site.Text))
            {
                var description = site.KeywordDetails?.GetValueOrDefault(site.Text);
                if (description is null && AuthoringSyntaxVocabulary.TryGetKeywordHelp(site.Text, out var keyword))
                    description = keyword.Description;
                if (description is not null) return Help(site.Text, description, site.Range);
            }
        }
        foreach (var token in analysis.Tokens.Where(token => Contains(token.Range, position)))
        {
            var unresolvedReference = sites.Any(site =>
                site.Text == token.Text && Contains(site.Range, position) &&
                site.Kinds.Any(kind => kind != AuthoringSymbolKind.Directive) &&
                !site.Keywords.Any(keyword => AuthoringSyntaxVocabulary.CompletionMatchesToken(keyword, site.Text)));
            if (token.ManifestPath is { } path)
            {
                var description = SchemaHelp(path);
                if (description is not null) return Help(token.Text, description, token.Range);
            }
            else if (!unresolvedReference && AuthoringSyntaxVocabulary.TryGetHelp(token.Kind, out var help))
                return Help(help.Spelling, help.Description, token.Range);
        }
        return null;
    }

    private static AuthoringHover Describe(StoryAnalysis analysis, AuthoringSymbol symbol, SourceRange range)
    {
        if (symbol.Kind == AuthoringSymbolKind.Modifier &&
            AuthoringSyntaxVocabulary.TryGetKeywordHelp(symbol.Name, out var modifierHelp))
            return Help(modifierHelp.Spelling, modifierHelp.Description, range);

        var summary = $"{symbol.Kind}: {symbol.Name} ({symbol.Type})";
        var markdown = $"**{symbol.Kind}** {Code(symbol.Name)} ({Code(symbol.Type)})";
        var plain = summary;
        foreach (var (label, value) in Facts(analysis, symbol))
        {
            markdown += $"\n\n{label}: {Code(value)}";
            plain += $"\n{label}: {value}";
        }
        if (symbol.Declaration is { } declaration)
        {
            var location = $"{declaration.Start.File}:{declaration.Start.Line}:{declaration.Start.Column}";
            markdown += $"\n\nDeclared at {Code(location)}.";
            plain += $"\n\nDeclared at {location}.";
            if (analysis.Source.Files.TryGetValue(declaration.Start.File, out var text))
            {
                var line = text.ReplaceLineEndings("\n").Split('\n')[declaration.Start.Line - 1].Trim();
                markdown += $"\n\nSource: {Code(line)}";
                plain += $"\n\nSource: {line}";
            }
        }
        else
        {
            var origin = symbol.Kind is AuthoringSymbolKind.Predicate or AuthoringSymbolKind.Modifier or AuthoringSymbolKind.Ruleset
                ? "Available from the configured authoring registrations." : "Available from the selected ruleset.";
            markdown += "\n\n" + origin;
            plain += "\n\n" + origin;
        }
        return new(range, markdown, plain);
    }

    /// <summary>
    /// Authored-default facts for a symbol's hover card. Each kind lists only data the story declares today;
    /// future authored metadata (for example custom item properties) belongs here as additional facts.
    /// </summary>
    private static IEnumerable<(string Label, string Value)> Facts(StoryAnalysis analysis, AuthoringSymbol symbol)
    {
        if (symbol.Kind == AuthoringSymbolKind.Scene)
            return analysis.Scenes.FirstOrDefault(scene => scene.Definition.Id.Value == symbol.Name) is { } scene
                ? SceneFacts(scene) : [];
        if (analysis.World is not { } world) return [];
        return symbol.Kind switch
        {
            AuthoringSymbolKind.Character when world.Characters.Values.FirstOrDefault(c => c.Id.Value == symbol.Name) is { } character
                => CharacterFacts(character),
            AuthoringSymbolKind.Item when world.Items.Values.FirstOrDefault(i => i.Id.Value == symbol.Name) is { } item
                => ItemFacts(world, item),
            AuthoringSymbolKind.Stat when world.Ruleset.Stats.FirstOrDefault(s => s.Id.Value == symbol.Name) is { } stat
                => StatFacts(world, stat),
            AuthoringSymbolKind.Faction when world.Factions.Values.FirstOrDefault(f => f.Id.Value == symbol.Name) is { } faction
                => FactionFacts(world, faction),
            _ => [],
        };
    }

    private static IEnumerable<(string, string)> CharacterFacts(CharacterDefinition character)
    {
        var pronouns = character.Pronouns;
        yield return ("Name (default)", StoryPreview.Quote(character.FullName));
        yield return ("Subject", StoryPreview.Quote(pronouns.Subject));
        yield return ("Object", StoryPreview.Quote(pronouns.Object));
        yield return ("Possessive determiner", StoryPreview.Quote(pronouns.Possessive));
        yield return ("Independent possessive", StoryPreview.Quote(pronouns.PossessivePronoun));
        yield return ("Reflexive", StoryPreview.Quote(pronouns.Reflexive));
        yield return ("Pronoun agreement", StoryPreview.Quote(pronouns.Agreement.ToString()));
    }

    private static IEnumerable<(string, string)> ItemFacts(WorldDefinition world, ItemDefinition item)
    {
        yield return ("Name", StoryPreview.Quote(item.Name));
        if (item.Modifiers.Count > 0)
            yield return ("Stat modifiers", string.Join(", ", item.Modifiers.Select(m => $"{m.Key.Value} {Signed(m.Value)}")));
        var holders = world.Characters.Values.Where(c => c.Inventory.GetValueOrDefault(item.Id) > 0)
            .Select(c => $"{c.Id.Value} ×{c.Inventory[item.Id]}").ToArray();
        if (holders.Length > 0) yield return ("Starting holders", string.Join(", ", holders));
    }

    private static IEnumerable<(string, string)> StatFacts(WorldDefinition world, StatDefinition stat)
    {
        yield return ("Name", StoryPreview.Quote(stat.Name));
        yield return ("Category", stat.Category.ToString());
        yield return ("Range", $"{stat.Min}..{(stat.Max == int.MaxValue ? "no maximum" : stat.Max)}");
        yield return ("Default", stat.Default.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (stat.GoverningAttribute is { } attribute) yield return ("Governing attribute", attribute.Value);
        var starting = world.Characters.Values.Where(c => c.Stats.ContainsKey(stat.Id))
            .Select(c => $"{c.Id.Value} {c.Stats[stat.Id]}").ToArray();
        if (starting.Length > 0) yield return ("Starting values", string.Join(", ", starting));
    }

    private static IEnumerable<(string, string)> FactionFacts(WorldDefinition world, FactionDefinition faction)
    {
        yield return ("Name", StoryPreview.Quote(faction.Name));
        yield return ("Initial standing", faction.InitialStanding.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (world.Ruleset.TierFor(faction.InitialStanding) is { } tier)
            yield return ("Initial tier", $"{tier.Id} ({StoryPreview.Quote(tier.Name)})");
    }

    private static IEnumerable<(string, string)> SceneFacts(AuthoredScene authoredScene)
    {
        var scene = authoredScene.Definition;
        if (scene.Title is { } title) yield return ("Title", StoryPreview.Quote(title));
        if (scene.Tags.Count > 0) yield return ("Tags", string.Join(", ", scene.Tags.Order(StringComparer.Ordinal)));
        if (authoredScene.HasExplicitPriority)
            yield return ("Priority", scene.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (scene.Once) yield return ("Once", bool.TrueString.ToLowerInvariant());
        if (scene.Hub is { } hub)
            yield return ("Hub", hub.Tags.Count == 1 && !hub.MatchAll
                ? hub.Tags[0] : AuthoringSyntaxVocabulary.FormatHubSelector(hub.Tags, hub.MatchAll));
        if (scene.HubLimit is { } limit) yield return ("Hub limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string Signed(int value) =>
        (value > 0 ? "+" : "") + value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static AuthoringHover Help(string name, string help, SourceRange range) =>
        new(range, $"{Code(name)}\n\n{help}", $"{name}\n\n{help}");

    private static string Code(string text)
    {
        var fence = "`";
        while (text.Contains(fence, StringComparison.Ordinal)) fence += "`";
        return $"{fence} {text.ReplaceLineEndings(" ")} {fence}";
    }

    private static bool Contains(SourceRange range, SourceLocation point) =>
        range.Start.File == point.File && Compare(range.Start, point) <= 0 && Compare(point, range.End) < 0;

    private static int Compare(SourceLocation left, SourceLocation right) =>
        left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Column.CompareTo(right.Column);

    private static string? SchemaHelp(IReadOnlyList<string> path)
    {
        JsonElement Resolve(JsonElement node)
        {
            while (node.TryGetProperty("$ref", out var reference))
                node = Schema.RootElement.GetProperty("definitions").GetProperty(reference.GetString()!["#/definitions/".Length..]);
            return node;
        }
        JsonElement? Step(JsonElement node, string segment)
        {
            node = Resolve(node);
            if (segment == "[]" && node.TryGetProperty("items", out var items)) return items;
            if (node.TryGetProperty("properties", out var properties) && properties.TryGetProperty(segment, out var property)) return property;
            if (node.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.Object) return additional;
            if (node.TryGetProperty("oneOf", out var variants))
                foreach (var variant in variants.EnumerateArray())
                    if (Step(variant, segment) is { } match) return match;
            return null;
        }
        var current = Schema.RootElement;
        foreach (var segment in path)
        {
            if (Step(current, segment) is not { } next) return null;
            current = next;
        }
        if (current.TryGetProperty("description", out var description)) return description.GetString();
        current = Resolve(current);
        if (current.TryGetProperty("description", out description)) return description.GetString();
        if (current.TryGetProperty("oneOf", out var choices))
        {
            var descriptions = choices.EnumerateArray().Select(Resolve)
                .Where(choice => choice.TryGetProperty("description", out _))
                .Select(choice => choice.GetProperty("description").GetString()).ToArray();
            if (descriptions.Length > 0) return string.Join(" ", descriptions);
        }
        return current.TryGetProperty("type", out var type) ? $"Manifest value of type {type.GetString()}." : null;
    }

    private static JsonDocument LoadSchema()
    {
        using var stream = typeof(StoryHover).Assembly.GetManifestResourceStream("Talewright.Authoring.story.schema.json")
            ?? throw new InvalidOperationException("The bundled manifest schema is missing.");
        return JsonDocument.Parse(stream);
    }
}
