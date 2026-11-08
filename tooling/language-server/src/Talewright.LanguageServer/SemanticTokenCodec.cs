using Talewright.Talemark.Analysis;

namespace Talewright.LanguageServer;

internal static class SemanticTokenCodec
{
    internal static readonly string[] TokenTypes =
    [
        "comment",
        "keyword",
        "operator",
        "number",
        "string",
        "variable",
        "namespace",
        "property",
        "function",
        "modifier",
        "talemarkScene",
        "talemarkDivert",
        "talemarkPunctuation",
        "talemarkFormatting",
        "talemarkEscape",
        "talemarkStructure",
    ];

    internal static readonly string[] TokenModifiers = ["declaration"];

    internal static int[] Encode(IEnumerable<TalemarkClassification> classifications, string file,
        CancellationToken cancellationToken)
    {
        var matching = classifications
            .Where(classification => classification.Range.Start.File == file)
            .OrderBy(classification => classification.Range.Start.Line)
            .ThenBy(classification => classification.Range.Start.Column)
            .ThenBy(classification => classification.Range.End.Column)
            .ToArray();
        var data = new List<int>(matching.Length * 5);
        var previousLine = 0;
        var previousStart = 0;
        var previousEnd = 0;
        var hasPrevious = false;

        for (var index = 0; index < matching.Length; index++)
        {
            if ((index & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var classification = matching[index];
            var range = classification.Range;
            var line = range.Start.Line - 1;
            var start = range.Start.Column - 1;
            var length = range.End.Column - range.Start.Column;
            if (line < 0 || start < 0 || length <= 0 || range.End.Line != range.Start.Line)
                throw new InvalidDataException("Semantic token classifications must be positive, single-line ranges.");
            if (hasPrevious && line == previousLine && start < previousEnd)
                throw new InvalidDataException("Semantic token classifications must not overlap.");

            var deltaLine = line - previousLine;
            data.Add(deltaLine);
            data.Add(deltaLine == 0 ? start - previousStart : start);
            data.Add(length);
            data.Add(TokenTypeIndex(classification.Kind));
            data.Add(TokenModifierBits(classification.Modifiers));

            previousLine = line;
            previousStart = start;
            previousEnd = start + length;
            hasPrevious = true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return data.ToArray();
    }

    private static int TokenTypeIndex(TalemarkClassificationKind kind) => kind switch
    {
        TalemarkClassificationKind.Comment => 0,
        TalemarkClassificationKind.Keyword => 1,
        TalemarkClassificationKind.Operator => 2,
        TalemarkClassificationKind.Number => 3,
        TalemarkClassificationKind.String => 4,
        TalemarkClassificationKind.Identifier => 5,
        TalemarkClassificationKind.Namespace => 6,
        TalemarkClassificationKind.Property => 7,
        TalemarkClassificationKind.Function => 8,
        TalemarkClassificationKind.Modifier => 9,
        TalemarkClassificationKind.SceneIdentifier => 10,
        TalemarkClassificationKind.Divert => 11,
        TalemarkClassificationKind.Punctuation => 12,
        TalemarkClassificationKind.Formatting => 13,
        TalemarkClassificationKind.Escape => 14,
        TalemarkClassificationKind.StructuralMarker => 15,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static int TokenModifierBits(TalemarkClassificationModifiers modifiers)
    {
        const TalemarkClassificationModifiers known = TalemarkClassificationModifiers.Declaration;
        if ((modifiers & ~known) != 0)
            throw new ArgumentOutOfRangeException(nameof(modifiers), modifiers, null);
        return (modifiers & known) != 0 ? 1 : 0;
    }
}
