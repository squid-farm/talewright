using System.Text.Json;
using Talewright.Core.State;
using Talewright.Narrative.Grammar;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Analysis;

namespace Talewright.Authoring;

public sealed record PreviewStep(string Name, string Value);
public sealed record SubstitutionPreview(SourceRange Range, string? Value, IReadOnlyList<PreviewStep> Steps,
    string? UnavailableReason)
{
    public string PlainText => Value is null
        ? $"Preview unavailable: {UnavailableReason}"
        : $"Result: {StoryPreview.Quote(Value)} (default)\nAuthored defaults, not current gameplay state.\n\n" +
          string.Join("\n", Steps.Select(step => $"{step.Name}: {StoryPreview.Quote(step.Value)}"));
}

/// <summary>Read-only examples from typed substitutions; never invokes project registrations.</summary>
public static class StoryPreview
{
    public const int MaximumHintLength = 40;
    private static readonly JsonSerializerOptions QuoteOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IReadOnlyList<SubstitutionPreview> Get(StoryAnalysis analysis, string file)
    {
        if (analysis.Diagnostics.Any(diagnostic => diagnostic.Code is "TW5001" or "TW5002"))
            throw new InvalidOperationException("Preview is unavailable until project authoring metadata is supplied and valid.");
        if (analysis.World is not { } world) return [];
        var state = GameState.CreateInitial(world, 0);
        var context = new EvaluationContext(state, NamedPredicateRegistry.Empty);
        return analysis.Substitutions.Where(substitution => substitution.Range.Start.File == file)
            .Select(substitution => Preview(analysis, substitution, context)).ToArray();
    }

    public static string HintLabel(string value)
    {
        var label = Quote(value);
        if (label.Length <= MaximumHintLength) return label;
        var length = MaximumHintLength - 4;
        if (char.IsHighSurrogate(label[length - 1])) length--;
        return label[..length] + "...\"";
    }

    internal static string Quote(string value) => JsonSerializer.Serialize(value, QuoteOptions);

    private static SubstitutionPreview Preview(StoryAnalysis analysis, AuthoredSubstitution substitution, EvaluationContext context)
    {
        var template = substitution.Template;
        // Only these references have useful, unambiguous authored-default examples.
        var variable = template.Value switch
        {
            VarRef reference => reference.Variable.Value,
            TextVarRef reference => reference.Variable.Value,
            _ => null,
        };
        var declaredDefault = variable is not null && analysis.Tokens.Any(token =>
            token.ManifestPath is { Count: 2 } path && path[0] == "vars" && path[1] == variable);
        if (!declaredDefault &&
            template.Value is not (NameRef or PronounRef or ItemNameRef or FactionNameRef or
                TextLiteral or NumberLiteral or BoolLiteral or TierLiteral))
            return new(substitution.Range, null, [], "This value depends on gameplay state; no default example is shown.");
        if (template.Modifiers.Any(modifier => !TextModifierRegistry.IsBuiltIn(modifier)))
            return new(substitution.Range, null, [], "Custom or metadata-only manipulators are not executed while editing.");

        var value = ExpressionEvaluator.Evaluate(template.Value, context).ToString();
        var steps = new List<PreviewStep> { new("Input", value) };
        var item = template.Value is ItemNameRef itemName ? context.State.World.Items[itemName.Item] : null;
        var plural = item is not null
            ? item.NameAgreement == Talewright.Core.World.GrammaticalNumber.Plural
            : template.Value is PronounRef pronoun
                ? context.State.GetCharacterDefinition(pronoun.Character).Pronouns.Agreement ==
                  Talewright.Core.World.GrammaticalNumber.Plural
                : template.IsPlural;
        var modifierContext = new ModifierContext(EnglishGrammar.Instance, plural, item?.Uncountable ?? false);
        foreach (var modifier in template.Modifiers)
        {
            value = modifier.Apply(value, modifierContext);
            steps.Add(new("#" + modifier.Name, value));
        }
        return new(substitution.Range, value, steps.AsReadOnly(), null);
    }
}
