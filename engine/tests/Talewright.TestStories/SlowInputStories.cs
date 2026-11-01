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
}

/// <summary>Reproducible authoring inputs for measuring and testing known slow shapes.</summary>
public static class SlowInputStories
{
    private const string Manifest = """
        game:
          id: slow-input
          title: Slow input
          start: start
          seed: 42
        characters:
          player: {first-name: Tester}
        vars: {x: 1, count: 1}
        flags: [ready]
        """;

    public static StorySource Create(SlowInputShape shape, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        var tale = new StringBuilder("=== start\n");
        switch (shape)
        {
            case SlowInputShape.ParagraphLines:
                for (var i = 0; i < size; i++)
                {
                    tale.Append("Line {var:x}. {if flag:ready}shown{/if}.\n");
                }
                break;
            case SlowInputShape.InlineSubstitutions:
                for (var i = 0; i < size; i++)
                {
                    tale.Append("{var:count}");
                }
                tale.Append('\n');
                break;
            case SlowInputShape.NestedConditions:
                for (var i = 0; i < size; i++)
                {
                    tale.Append("{if flag:ready}\n");
                }
                tale.Append("Nested.\n");
                for (var i = 0; i < size; i++)
                {
                    tale.Append("{/if}\n");
                }
                break;
            case SlowInputShape.UnmatchedEmphasis:
                for (var i = 0; i < size; i++)
                {
                    tale.Append("*a ");
                }
                tale.Append('\n');
                break;
            case SlowInputShape.UnclosedLinks:
                for (var i = 0; i < size; i++)
                {
                    tale.Append("[a](");
                }
                tale.Append('\n');
                break;
            case SlowInputShape.Choices:
            case SlowInputShape.ChoicesWithExpressions:
                for (var i = 0; i < size; i++)
                {
                    tale.Append(CultureInfo.InvariantCulture, $"* [Choice {i}]");
                    if (shape == SlowInputShape.ChoicesWithExpressions)
                    {
                        tale.Append(" (requires: var:count > 0) { var:count += 1 }");
                    }
                    tale.Append(" -> END\n");
                }
                break;
            case SlowInputShape.NestedParentheses:
                tale.Append("~ var:count = ").Append('(', size).Append('1').Append(')', size).Append('\n');
                break;
            case SlowInputShape.UnaryPrefixes:
                tale.Append("~ var:count = ").Append('-', size).Append("1\n");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown slow-input shape.");
        }

        if (shape is not (SlowInputShape.Choices or SlowInputShape.ChoicesWithExpressions))
        {
            tale.Append("-> END\n");
        }

        return new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest,
            ["main.tale"] = tale.ToString(),
        });
    }
}
