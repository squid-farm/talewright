using Talewright.Core.Identity;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

internal static partial class TaleSyntaxParser
{
    private sealed partial class SceneState
    {
        private readonly int _headerIndent;
        private readonly int _headerIdColumn;
        private readonly int _headerLength;
        private readonly string _id;
        private readonly List<TaleLinePart> _parts;
        private readonly List<TaleClause> _clauses;
        private readonly CancellationToken _cancellationToken;
        private readonly List<TaleSyntaxDiagnostic> _diagnostics = [];
        // Open {if} blocks: header-relative line and column, and whether {else} has been seen.
        private readonly List<(int Line, int Column, bool InElse)> _blocks = [];
        private int _suppressedDepth;
        private (int Line, int Column) _suppressedAt;
        private bool _hasHub;
        private bool _hasHubLimit;
        private bool _hasTitle;
        private bool _hasContinues;
        private bool _hasClear;
        // The skill check whose outcome lines are being attached: its line and column, and the outcomes seen so far.
        private (int Line, int Column, bool InsideBlock, bool Success, bool Failure)? _check;

        /// <summary>Starts a scene, reusing the previous scene's buffers.</summary>
        public SceneState(int start, int line, int headerIndent, int headerIdColumn, int headerLength, string id,
            List<TaleLineSyntax> lines, List<TaleLinePart> parts, List<TaleClause> clauses,
            CancellationToken cancellationToken)
        {
            Start = start;
            Line = line;
            _headerIndent = headerIndent;
            _headerIdColumn = headerIdColumn;
            _headerLength = headerLength;
            _id = id;
            Lines = lines;
            _parts = parts;
            _clauses = clauses;
            _cancellationToken = cancellationToken;
            lines.Clear();
            parts.Clear();
            clauses.Clear();
        }

        public int Start { get; }
        public int Line { get; }
        public bool HeaderDone;
        public List<TaleLineSyntax> Lines { get; }

        /// <summary>Adds the line's parts and block state, reporting structural errors.</summary>
        public TaleLineSyntax Describe(TaleLineSyntax line, ReadOnlySpan<char> content)
        {
            if (_check is not null && TryAttach(ref line, content))
                return line;

            if (_suppressedDepth > 0)
            {
                if (line.Kind == TaleLineKind.BlockIf)
                    _suppressedDepth++;
                else if (line.Kind == TaleLineKind.BlockEndIf)
                    _suppressedDepth--;
                return line with { IsSuppressed = true };
            }

            switch (line.Kind)
            {
                case TaleLineKind.Metadata:
                    return Metadata(line, content);
                case TaleLineKind.BlockIf or TaleLineKind.BlockElse or TaleLineKind.BlockEndIf:
                    return Block(line, content);
                case TaleLineKind.Speech:
                    return Speech(line, content);
                case TaleLineKind.Effects:
                    var effects = content[1..];
                    return WithPart(line, new(0, 0, line.Indent + 1 + Lead(effects), effects.Trim().Length));
                case TaleLineKind.Divert:
                    return Divert(line, content);
                case TaleLineKind.Choice:
                {
                    var first = _clauses.Count;
                    return WithClauses(line, first, Choice(line, content[1..].ToString()));
                }
                case TaleLineKind.SkillCheckBlock:
                {
                    var first = _clauses.Count;
                    return WithClauses(line, first, BlockCheck(line, content));
                }
                case TaleLineKind.SuccessOutcome or TaleLineKind.FailureOutcome:
                    Error(line, CompilerDiagnosticCodes.MalformedSkillCheckOutcome,
                        $"{SceneVocabulary.SuccessOutcome}/{SceneVocabulary.FailureOutcome} lines must directly follow " +
                        $"a {SceneVocabulary.SkillCheck} choice or be inside a '{SceneVocabulary.SkillCheckBlock}' block.");
                    return line with { IsInvalid = true };
                case TaleLineKind.MisplacedMetadata:
                    Error(line, CompilerDiagnosticCodes.InvalidSceneMetadata,
                        $"Metadata ({SceneVocabulary.MetadataMarker}{SceneVocabulary.Title}, " +
                        $"{SceneVocabulary.MetadataMarker}{SceneVocabulary.Tags}, …) must come directly after the " +
                        $"'{SceneVocabulary.SceneHeader}' line.");
                    return line with { IsInvalid = true };
                default:
                    return line;
            }
        }

        private TaleLineSyntax Speech(TaleLineSyntax line, ReadOnlySpan<char> content)
        {
            var body = content[1..];
            var colon = body.IndexOf(ExpressionVocabulary.Separator);
            var speaker = (colon < 0 ? body : body[..colon]).Trim();
            var headStart = line.Indent + 1 + Lead(body);
            if (colon <= 0 || !IdentifierRules.IsValid(speaker))
            {
                line = WithPart(line, new(headStart, speaker.Length, 0, 0));
                Error(line, CompilerDiagnosticCodes.MalformedDialogue,
                    $"Speech lines look like '{SceneVocabulary.SpeechMarker} who{ExpressionVocabulary.Separator} " +
                    "text', where who is a character id.");
                return line with { IsInvalid = true };
            }

            var text = body[(colon + 1)..];
            return WithPart(line, new(headStart, speaker.Length, line.Indent + colon + 2 + Lead(text), text.Trim().Length));
        }

        private TaleLineSyntax Divert(TaleLineSyntax line, ReadOnlySpan<char> content)
        {
            var text = content[SceneVocabulary.Divert.Length..];
            var target = text.Trim();
            var valueStart = line.Indent + SceneVocabulary.Divert.Length + Lead(text);
            line = WithPart(line, new(0, 0, valueStart, target.Length));
            if (DivertTargetError(target) is not { } error)
                return line;
            Error(line, error.Code, error.Message, valueStart);
            return line with { IsInvalid = true };
        }

        private void Clause(TaleClause clause) => _clauses.Add(clause);

        /// <summary>Points the line at the clauses added from <paramref name="first"/>.</summary>
        private TaleLineSyntax WithClauses(TaleLineSyntax line, int first, bool invalid) =>
            line with
            {
                PartIndex = _clauses.Count > first ? first + 1 : 0,
                ClauseCount = _clauses.Count - first,
                IsInvalid = invalid,
            };

        private TaleLineSyntax WithPart(TaleLineSyntax line, TaleLinePart part)
        {
            _parts.Add(part);
            return line with { PartIndex = _parts.Count };
        }

        private void Error(TaleLineSyntax line, string code, string message, int? column = null) =>
            _diagnostics.Add(new(code, message, line.Line, column ?? line.Indent));

        public TaleSceneSyntax Build(int endLine)
        {
            if (_check is not null)
                FinishCheck(closed: false);
            const string unclosed = "block is never closed with ";
            var diagnostics = new List<TaleSyntaxDiagnostic>(_diagnostics.Count + 1);
            if (!IdentifierRules.IsValid(_id) || _id is SceneVocabulary.End or ExpressionVocabulary.Scene)
            {
                diagnostics.Add(new(CompilerDiagnosticCodes.InvalidSceneId,
                    $"'{_id}' is not a valid scene id. Use an ASCII letter or '_' followed by letters, digits, '_' " +
                    $"and single '-' or '.' separators (e.g. temple.entrance); '{ExpressionVocabulary.Scene}' is reserved.", 0, _headerIndent));
            }

            diagnostics.AddRange(_diagnostics);
            if (_suppressedDepth > 0 || _blocks.Count > 0)
            {
                var message = $"{SceneVocabulary.InlineOpen}{SceneVocabulary.If}{SceneVocabulary.InlineClose} {unclosed}" +
                              $"{SceneVocabulary.InlineOpen}{SceneVocabulary.EndIf}{SceneVocabulary.InlineClose}.";
                if (_suppressedDepth > 0)
                    diagnostics.Add(new(CompilerDiagnosticCodes.UnbalancedCondition, message, _suppressedAt.Line,
                        _suppressedAt.Column));
                for (var index = _blocks.Count - 1; index >= 0; index--)
                    diagnostics.Add(new(CompilerDiagnosticCodes.UnbalancedCondition, message, _blocks[index].Line,
                        _blocks[index].Column));
            }
            return new TaleSceneSyntax(Start, Line, _headerIndent, _headerIdColumn, _headerLength, _id, Lines.ToArray(),
                _parts.ToArray(), _clauses.ToArray(), endLine - Line, diagnostics.ToArray());
        }
    }
}
