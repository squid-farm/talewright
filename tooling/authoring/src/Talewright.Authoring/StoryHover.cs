using System.Text.Json;
using Talewright.Tale.Analysis;
using Talewright.Tale.Diagnostics;

namespace Talewright.Authoring;

public sealed record AuthoringHover(SourceRange Range, string Markdown, string PlainText);

/// <summary>Editor-neutral help from parsed source slots and explicit project registrations.</summary>
public static class StoryHover
{
    private static readonly JsonDocument Schema = LoadSchema();

    private static readonly IReadOnlyDictionary<string, string> Syntax = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["title"] = "Scene title shown to players.",
        ["requires"] = "Boolean condition controlling whether this scene is available.",
        ["tags"] = "Comma-separated scene tags.",
        ["priority"] = "Integer priority used when selecting an available scene.",
        ["once"] = "Marks a scene as available only once.",
        ["hub"] = "Offers available storylets matching a tag, all(tag, tag), or any(tag, tag).",
        ["hub-limit"] = "Positive maximum number of choices offered by this hub.",
        ["END"] = "Ends the story instead of diverting to another scene.",
        ["==="] = "Declares a scene: === scene.id. Put scene metadata immediately after this header.",
        ["->"] = "Diverts to a scene ID, or END to finish the story.",
        ["~"] = "Entry effects: executes semicolon-separated effects when entering the scene.",
        ["*choice"] = "Choice: * [label] followed by optional conditions, effects, and a divert or skill check.",
        ["::: skill-check"] = "Entry skill-check block: ::: skill-check [actor] stat dc number, followed by success/failure outcomes and a closing :::.",
        ["skill-check"] = "skill-check [actor] stat dc number: resolves a check using the selected ruleset, with success/failure outcomes.",
        ["if"] = "Begins conditional text with {if condition}. Close it with {/if}; {else} is optional.",
        ["else"] = "Alternative text for the preceding {if condition}.",
        ["/if"] = "Closes the current conditional text block.",
        ["visited"] = "visited(scene): true when the scene has been visited.",
        ["flag"] = "flag:id: a declared boolean story flag.",
        ["var"] = "var:id: a story variable with a fixed integer or text type.",
        ["faction"] = "faction:id: numeric standing; faction:id:tier selects its reputation tier and faction:id:name its display name.",
        ["item"] = "item:id: the item's display name.",
        ["set"] = "set flag:id: sets a flag to true.",
        ["clear"] = "clear flag:id: sets a flag to false.",
        ["give"] = "give character item [quantity]: adds inventory; quantity is a numeric expression and defaults to one.",
        ["take"] = "take character item [quantity]: removes inventory; quantity is a numeric expression and defaults to one.",
        ["has"] = "character has item: tests whether a character holds an item.",
        ["and"] = "Boolean conjunction: both conditions must be true.",
        ["or"] = "Boolean disjunction: either condition may be true.",
        ["not"] = "Negates a boolean condition.",
        ["true"] = "Boolean true literal.",
        ["false"] = "Boolean false literal.",
        ["+"] = "Adds numeric operands.",
        ["-"] = "Subtracts numeric operands, or negates a number.",
        ["*"] = "Multiplies numeric operands.",
        ["/"] = "Divides numeric operands.",
        ["="] = "Assigns a value of the target's type. Text variables require text; numeric targets require numbers.",
        ["+="] = "Adds a numeric value to an effect target.",
        ["-="] = "Subtracts a numeric value from an effect target.",
        ["=="] = "Tests two compatible values for equality.",
        ["!="] = "Tests two compatible values for inequality.",
        ["<"] = "Tests whether the left value is smaller.",
        ["<="] = "Tests whether the left value is smaller or equal.",
        [">"] = "Speech marker at line start; in expressions, tests whether the left value is larger.",
        [">="] = "Tests whether the left value is larger or equal."
    };

    public static AuthoringHover? Get(StoryAnalysis analysis, SourceLocation position)
    {
        if (analysis.Diagnostics.Any(diagnostic => diagnostic.Code is "TW5001" or "TW5002"))
            throw new InvalidOperationException("Hover is unavailable until project authoring metadata is supplied and valid.");
        var declaration = analysis.Symbols.FirstOrDefault(symbol => symbol.Declaration is { } range && Contains(range, position));
        if (declaration is not null) return Describe(analysis, declaration, declaration.Declaration!);
        foreach (var site in analysis.CompletionSites.Where(site => site.Text.Length > 0 && Contains(site.Range, position)))
        {
            var symbol = (site.Candidates ?? analysis.Symbols).FirstOrDefault(candidate =>
                site.Kinds.Contains(candidate.Kind) && candidate.Name == site.Text);
            if (symbol is not null) return Describe(analysis, symbol, site.Range);
            if (site.Kinds.Contains(AuthoringSymbolKind.Directive) && Syntax.TryGetValue(site.Text, out var directive))
                return Help(site.Text, directive, site.Range);
            if (site.Keywords.Contains(site.Text))
            {
                var description = site.KeywordDetails?.GetValueOrDefault(site.Text) ?? site.Text switch
                {
                    "first-name" or "last-name" => "Character name part used in substitutions.",
                    "name" => "Character full name or faction display name used in substitutions.",
                    "tier" => "Faction standing tier, such as hostile or friendly.",
                    "pronoun" => "Character pronoun. Gives the subject form; add #object, #possessive or #reflexive for another form.",
                    _ => null,
                };
                if (description is not null) return Help(site.Text, description, site.Range);
            }
            if (site.Text == "END" && site.Keywords.Contains("END")) return Help("END", Syntax["END"], site.Range);
        }
        foreach (var token in analysis.Tokens.Where(token => Contains(token.Range, position)))
        {
            var unresolvedReference = analysis.CompletionSites.Any(site =>
                site.Text == token.Text && Contains(site.Range, position) &&
                site.Kinds.Any(kind => kind != AuthoringSymbolKind.Directive) &&
                !site.Keywords.Any(keyword => keyword.TrimEnd(':', '(') == site.Text));
            if (token.ManifestPath is { } path)
            {
                var description = SchemaHelp(path);
                if (description is not null) return Help(token.Text, description, token.Range);
            }
            else if (!unresolvedReference &&
                     token.Text is not ("title" or "requires" or "tags" or "priority" or "once" or "hub" or "hub-limit" or "END") &&
                     Syntax.TryGetValue(token.Text, out var help))
                return Help(token.Text == "*choice" ? "*" : token.Text, help, token.Range);
        }
        return null;
    }

    private static AuthoringHover Describe(StoryAnalysis analysis, AuthoringSymbol symbol, SourceRange range)
    {
        var summary = $"{symbol.Kind}: {symbol.Name} ({symbol.Type})";
        var markdown = $"**{symbol.Kind}** {Code(symbol.Name)} ({Code(symbol.Type)})";
        var plain = summary;
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
