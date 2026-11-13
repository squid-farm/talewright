using System.Runtime.InteropServices;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

/// <summary>How a <c>.tale</c> line is classified, independent of story symbols.</summary>
internal enum TaleLineKind : byte
{
    Blank,
    Comment,
    /// <summary>An <c>@</c> line in a scene's header position; <see cref="TaleLineSyntax.MetadataKey"/> is null for unknown keys.</summary>
    Metadata,
    /// <summary>A known <c>@key</c> line after the scene header has ended.</summary>
    MisplacedMetadata,
    BlockIf,
    BlockElse,
    BlockEndIf,
    Speech,
    Effects,
    Choice,
    Divert,
    /// <summary>A line starting with <c>:::</c>; <see cref="TaleLineSyntax.IsBareFence"/> marks the closing fence.</summary>
    SkillCheckBlock,
    SuccessOutcome,
    FailureOutcome,
    /// <summary>Reader prose, or stray text when the line precedes the first scene.</summary>
    Prose,
}

/// <summary>
/// One source line. Offsets and line numbers are relative to the owning scene's header line (or to the file start for
/// pre-scene lines); map them with <see cref="TaleSceneSyntax.FileOffset"/> and <see cref="TaleSceneSyntax.FileLine"/>.
/// </summary>
/// <param name="Kind">The line classification.</param>
/// <param name="Start">Offset of the raw line start.</param>
/// <param name="Line">Zero-based line number.</param>
/// <param name="Length">Raw line length, excluding the line break.</param>
/// <param name="Indent">UTF-16 length of leading whitespace.</param>
/// <param name="ContentLength">Length of the line with leading and trailing whitespace removed.</param>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct TaleLineSyntax(
    TaleLineKind Kind,
    int Start,
    int Line,
    int Length,
    int Indent,
    int ContentLength)
{
    private const byte SuppressedFlag = 1;
    private const byte InvalidFlag = 2;
    private const byte AttachedFlag = 4;

    // Its meaning depends on Kind.
    private readonly byte _detail;
    private readonly byte _flags;

    // One-based index into the scene's parts, or for choice, skill-check block and outcome lines its first clause;
    // zero when the line has none.
    internal int PartIndex { get; init; }

    /// <summary>Number of clauses on a choice, skill-check block or outcome line.</summary>
    internal int ClauseCount { get; init; }

    /// <summary>The line is inside a block condition nested past the limit; binding ignores it.</summary>
    public bool IsSuppressed
    {
        get => (_flags & SuppressedFlag) != 0;
        init => _flags = value ? (byte)(_flags | SuppressedFlag) : (byte)(_flags & ~SuppressedFlag);
    }

    /// <summary>
    /// Syntax reported a structural error for this line, so binding gives it no meaning beyond tokens and
    /// references.
    /// </summary>
    public bool IsInvalid
    {
        get => (_flags & InvalidFlag) != 0;
        init => _flags = value ? (byte)(_flags | InvalidFlag) : (byte)(_flags & ~InvalidFlag);
    }

    /// <summary>
    /// The line belongs to the preceding skill check: an outcome, a comment or block blank line between outcomes, or
    /// the closing fence. The check binds it.
    /// </summary>
    public bool IsAttached
    {
        get => (_flags & AttachedFlag) != 0;
        init => _flags = value ? (byte)(_flags | AttachedFlag) : (byte)(_flags & ~AttachedFlag);
    }

    public SceneMetadataKey? MetadataKey
    {
        get => Kind == TaleLineKind.Metadata && _detail != 0 ? (SceneMetadataKey)(_detail - 1) : null;
        init
        {
            if (value is { } key)
                _detail = checked((byte)((int)key + 1));
        }
    }

    public bool IsBareFence
    {
        get => Kind == TaleLineKind.SkillCheckBlock && _detail != 0;
        init
        {
            if (value)
                _detail = 1;
        }
    }
}

/// <summary>
/// The parts of a metadata, speech, effects, divert or <c>{if}</c> line. Columns are relative to the raw line start.
/// </summary>
/// <param name="HeadStart">Column of the leading part: the metadata keyword, speaker or block keyword.</param>
/// <param name="HeadLength">Length of the leading part.</param>
/// <param name="ValueStart">
/// Column of the trimmed value: the metadata value, speech text, effects or divert target. For <c>{if}</c> it is the
/// text after the <c>if </c> prefix, untrimmed so expression offsets match the source.
/// </param>
/// <param name="ValueLength">Length of the value.</param>
internal readonly record struct TaleLinePart(int HeadStart, int HeadLength, int ValueStart, int ValueLength)
{
    /// <summary>
    /// Column where a metadata value begins before trimming: just after the first space, or the line indent when
    /// there is no value.
    /// </summary>
    public int MetadataValueColumn(TaleLineSyntax line) => HeadStart + HeadLength == line.Indent + line.ContentLength
        ? line.Indent
        : HeadStart + HeadLength + 1;
}

/// <summary>A syntax diagnostic positioned relative to its owner (scene or file).</summary>
/// <summary>The kind of a clause on a choice, skill-check block or outcome line.</summary>
internal enum TaleClauseKind : byte
{
    /// <summary>Choice label; the value is the text between the brackets.</summary>
    Label,

    /// <summary>Choice label without its closing bracket; the value is the text up to where it should close.</summary>
    UnclosedLabel,

    /// <summary>Unclosed <c>{</c> inside an unclosed label; the value runs from after the brace to the line end.</summary>
    UnclosedBrace,

    /// <summary>Unclosed image call inside an unclosed label; the value runs from after the brace to the line end.</summary>
    UnclosedImage,

    /// <summary><c>(requires …)</c>; the value is the condition.</summary>
    Requires,

    /// <summary><c>(requires </c> without its closing parenthesis; the value runs to the line end.</summary>
    UnclosedRequires,

    /// <summary>Unexpected parenthesized choice text, retained for partial expression analysis.</summary>
    InvalidCondition,

    /// <summary><c>{ effects }</c>; the value is the text between the braces.</summary>
    Effects,

    /// <summary><c>{</c> without its closing brace; the value runs to the line end.</summary>
    UnclosedEffects,

    /// <summary><c>-&gt; target</c>; the value is the trimmed target.</summary>
    Divert,

    /// <summary>The <c>skill-check</c> keyword; the value is the spec after it, without any trailing clause.</summary>
    SkillCheck,

    /// <summary>An actor or stat word before <c>dc</c>; at most two follow a <see cref="SkillCheck"/>.</summary>
    CheckWord,

    /// <summary>The difficulty expression after <c>dc</c>; empty at the spec end when <c>dc</c> has no value.</summary>
    Difficulty,

    /// <summary>The optional <c>, hidden</c> skill-check modifier.</summary>
    CheckHidden,
}

/// <summary>A clause on a choice, skill-check block or outcome line. Columns are relative to the raw line start.</summary>
/// <param name="Kind">What the clause is.</param>
/// <param name="Start">Column of the clause marker, or of the value when it has none.</param>
/// <param name="ValueStart">Column of the value; see <see cref="TaleClauseKind"/> for what each kind's value is.</param>
/// <param name="ValueLength">Length of the value.</param>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct TaleClause(TaleClauseKind Kind, int Start, int ValueStart, int ValueLength)
{
    private const byte InvalidFlag = 0x80;
    private readonly byte _detail;

    /// <summary>
    /// Syntax reported an error for this clause: an invalid divert target, or a skill-check spec that is not
    /// <c>[actor] stat dc expr</c>.
    /// </summary>
    public bool IsInvalid
    {
        get => (_detail & InvalidFlag) != 0;
        init => _detail = value ? (byte)(_detail | InvalidFlag) : (byte)(_detail & ~InvalidFlag);
    }

    /// <summary>
    /// For <see cref="TaleClauseKind.SkillCheck"/>, the index of the <c>dc</c> word when it is 1 (stat) or 2 (actor
    /// and stat); otherwise 0.
    /// </summary>
    public int DifficultyWord
    {
        get => _detail & ~InvalidFlag;
        init => _detail = (byte)((_detail & InvalidFlag) | (value is 1 or 2 ? value : 0));
    }
}

internal sealed record TaleSyntaxDiagnostic(string Code, string Message, int Line, int Column);

/// <summary>
/// A scene: its <c>===</c> header plus every following line up to the next header or end of file. Body lines are
/// scene-relative so an unchanged scene keeps identical syntax when earlier text moves.
/// </summary>
internal sealed class TaleSceneSyntax
{
    private readonly TaleLinePart[] _parts;
    private readonly TaleClause[] _clauses;

    internal TaleSceneSyntax(int start, int line, int headerIndent, int headerIdColumn, int headerLength, string id,
        TaleLineSyntax[] lines, TaleLinePart[] parts, TaleClause[] clauses, int lineCount, TaleSyntaxDiagnostic[] diagnostics)
    {
        _parts = parts;
        _clauses = clauses;
        Start = start;
        Line = line;
        HeaderIndent = headerIndent;
        HeaderIdColumn = headerIdColumn;
        HeaderLength = headerLength;
        Id = id;
        Lines = lines;
        LineCount = lineCount;
        Diagnostics = diagnostics;
    }

    /// <summary>File offset of the header line.</summary>
    public int Start { get; }

    /// <summary>Zero-based file line of the header.</summary>
    public int Line { get; }

    public int HeaderIndent { get; }

    /// <summary>Zero-based source column of the scene ID in its header.</summary>
    public int HeaderIdColumn { get; }

    /// <summary>Raw header line length.</summary>
    public int HeaderLength { get; }

    /// <summary>Header text after <c>===</c>, trimmed; may be invalid.</summary>
    public string Id { get; }

    /// <summary>Body lines after the header.</summary>
    public IReadOnlyList<TaleLineSyntax> Lines { get; }

    /// <summary>Lines owned by the scene, including the header.</summary>
    public int LineCount { get; }

    /// <summary>Scene-owned syntax diagnostics with scene-relative lines.</summary>
    public IReadOnlyList<TaleSyntaxDiagnostic> Diagnostics { get; }

    /// <summary>The parts of a body line, or default when its kind has none.</summary>
    public TaleLinePart Part(TaleLineSyntax line) =>
        line.PartIndex == 0 || line.ClauseCount > 0 ? default : _parts[line.PartIndex - 1];

    /// <summary>The clauses of a choice, skill-check block or outcome line, in source order.</summary>
    public ReadOnlySpan<TaleClause> Clauses(TaleLineSyntax line) =>
        line.ClauseCount == 0 ? default : _clauses.AsSpan(line.PartIndex - 1, line.ClauseCount);

    public int FileOffset(int relativeOffset) => Start + relativeOffset;

    public int FileLine(int relativeLine) => Line + relativeLine;
}

/// <summary>
/// Symbol-independent syntax for one <c>.tale</c> file. The original text is shared, not copied per node; pre-scene
/// lines and their diagnostics are owned by the file.
/// </summary>
internal sealed class TaleFileSyntax
{
    internal TaleFileSyntax(string text, TaleLineSyntax[] preamble, TaleSceneSyntax[] scenes,
        TaleSyntaxDiagnostic[] diagnostics, int lineCount)
    {
        Text = text;
        Preamble = preamble;
        Scenes = scenes;
        Diagnostics = diagnostics;
        LineCount = lineCount;
    }

    public string Text { get; }

    /// <summary>Lines before the first scene header, with file-relative positions.</summary>
    public IReadOnlyList<TaleLineSyntax> Preamble { get; }

    public IReadOnlyList<TaleSceneSyntax> Scenes { get; }

    /// <summary>File-owned syntax diagnostics with file lines.</summary>
    public IReadOnlyList<TaleSyntaxDiagnostic> Diagnostics { get; }

    public int LineCount { get; }

    /// <summary>The raw text of a scene line.</summary>
    public ReadOnlySpan<char> RawText(TaleSceneSyntax scene, TaleLineSyntax line) =>
        Text.AsSpan(scene.FileOffset(line.Start), line.Length);

    /// <summary>The trimmed text of a scene line.</summary>
    public ReadOnlySpan<char> Content(TaleSceneSyntax scene, TaleLineSyntax line) =>
        Text.AsSpan(scene.FileOffset(line.Start + line.Indent), line.ContentLength);

    /// <summary>The leading part of a scene line; see <see cref="TaleLinePart.HeadStart"/>.</summary>
    public ReadOnlySpan<char> Head(TaleSceneSyntax scene, TaleLineSyntax line)
    {
        var part = scene.Part(line);
        return Text.AsSpan(scene.FileOffset(line.Start + part.HeadStart), part.HeadLength);
    }

    /// <summary>The trimmed value of a scene line; see <see cref="TaleLinePart.ValueStart"/>.</summary>
    public ReadOnlySpan<char> Value(TaleSceneSyntax scene, TaleLineSyntax line)
    {
        var part = scene.Part(line);
        return Text.AsSpan(scene.FileOffset(line.Start + part.ValueStart), part.ValueLength);
    }

    /// <summary>The value of a clause on a scene line.</summary>
    public ReadOnlySpan<char> Value(TaleSceneSyntax scene, TaleLineSyntax line, TaleClause clause) =>
        Text.AsSpan(scene.FileOffset(line.Start + clause.ValueStart), clause.ValueLength);

    /// <summary>The trimmed text of a pre-scene line.</summary>
    public ReadOnlySpan<char> Content(TaleLineSyntax preambleLine) =>
        Text.AsSpan(preambleLine.Start + preambleLine.Indent, preambleLine.ContentLength);

    internal void Report(string file, DiagnosticBag diagnostics)
    {
        foreach (var diagnostic in Diagnostics)
            diagnostics.Error(diagnostic.Code, diagnostic.Message,
                new SourceLocation(file, diagnostic.Line + 1, diagnostic.Column + 1));
    }

    internal static void Report(string file, TaleSceneSyntax scene, DiagnosticBag diagnostics)
    {
        foreach (var diagnostic in scene.Diagnostics)
            diagnostics.Error(diagnostic.Code, diagnostic.Message,
                new SourceLocation(file, scene.FileLine(diagnostic.Line) + 1, diagnostic.Column + 1));
    }
}
