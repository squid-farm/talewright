using System.Globalization;
using System.Text;
using Talewright.Talemark.Compilation;

namespace Talewright.Testing;

public enum SlowInputShape
{
    ParagraphLines,
    InlineSubstitutions,
    NestedConditions,
    UnmatchedEmphasis,
    UnclosedLinks,
    Choices,
    ChoicesWithExpressions,
    NestedParentheses,
    UnaryPrefixes,
    BrokenChoiceLines,
    UnclosedChoiceLabel,
}

/// <summary>Reproducible authoring inputs for measuring and testing known slow shapes.</summary>
public static class SlowInputStories
{
    public static StorySource Create(SlowInputShape shape, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        var manifest = new ManifestFaker("slow-input", "Slow input", "start", 42);
        manifest.Character("player", "Tester");
        var x = manifest.Number("x", 1);
        var count = manifest.Number("count", 1);
        var ready = manifest.Flag("ready");
        var tale = new StringBuilder().Append(SceneFaker.Header("start")).Append('\n');
        switch (shape)
        {
            case SlowInputShape.ParagraphLines:
            {
                var line = $"Line {SubstitutionFaker.Variable(x)}. {IfFaker.Inline(ready, "shown")}.\n";
                for (var i = 0; i < size; i++)
                {
                    tale.Append(line);
                }
                break;
            }
            case SlowInputShape.InlineSubstitutions:
            {
                var substitution = SubstitutionFaker.Variable(count);
                for (var i = 0; i < size; i++)
                {
                    tale.Append(substitution);
                }
                tale.Append('\n');
                break;
            }
            case SlowInputShape.NestedConditions:
            {
                var open = IfFaker.Open(ready);
                for (var i = 0; i < size; i++)
                {
                    tale.Append(open).Append('\n');
                }
                tale.Append("Nested.\n");
                for (var i = 0; i < size; i++)
                {
                    tale.Append(IfFaker.Close).Append('\n');
                }
                break;
            }
            case SlowInputShape.UnmatchedEmphasis:
                for (var i = 0; i < size; i++)
                {
                    tale.Append(MarkupFaker.EmphasisOpen("a "));
                }
                tale.Append('\n');
                break;
            case SlowInputShape.UnclosedLinks:
                for (var i = 0; i < size; i++)
                {
                    tale.Append(MarkupFaker.LinkOpen("a"));
                }
                tale.Append('\n');
                break;
            case SlowInputShape.Choices:
            case SlowInputShape.ChoicesWithExpressions:
            {
                var requires = shape == SlowInputShape.ChoicesWithExpressions ? ExpressionFaker.GreaterThan(count, 0) : null;
                var effect = shape == SlowInputShape.ChoicesWithExpressions ? EffectFaker.Increase(count, 1) : null;
                for (var i = 0; i < size; i++)
                {
                    var label = $"Choice {i.ToString(CultureInfo.InvariantCulture)}";
                    tale.Append(ChoiceFaker.Create("END", label, requires, effect)).Append('\n');
                }
                break;
            }
            case SlowInputShape.BrokenChoiceLines:
            {
                var line = ChoiceFaker.Create("start", $"Go {SubstitutionFaker.Variable(count)}",
                    new TaleFlagRef("a").ToString(), EffectFaker.Set(new TaleFlagRef("b")));
                line = line.Remove(line.IndexOf(']'), 1);
                for (var i = 0; i < size; i++)
                {
                    tale.Append(line).Append('\n');
                }
                break;
            }
            case SlowInputShape.UnclosedChoiceLabel:
            {
                var line = ChoiceFaker.Create("start", new string('a', size));
                tale.Append(line.Remove(line.IndexOf(']'), 1)).Append('\n');
                break;
            }
            case SlowInputShape.NestedParentheses:
            {
                var expression = string.Concat(
                    new string(ExpressionFaker.OpenParenthesis, size), "1",
                    new string(ExpressionFaker.CloseParenthesis, size));
                tale.Append(EffectFaker.Line(EffectFaker.Assign(count, expression))).Append('\n');
                break;
            }
            case SlowInputShape.UnaryPrefixes:
            {
                var expression = new string(ExpressionFaker.UnaryMinus, size) + "1";
                tale.Append(EffectFaker.Line(EffectFaker.Assign(count, expression))).Append('\n');
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown slow-input shape.");
        }

        if (shape is not (SlowInputShape.Choices or SlowInputShape.ChoicesWithExpressions or
            SlowInputShape.BrokenChoiceLines or SlowInputShape.UnclosedChoiceLabel))
        {
            tale.Append(DivertFaker.To("END")).Append('\n');
        }

        return new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = manifest.Build(),
            ["main.tale"] = tale.ToString(),
        });
    }
}
