using Talewright.Core.Identity;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

internal static partial class TaleSyntaxParser
{
    private sealed partial class SceneState
    {
        /// <summary>Adds a <c>::: skill-check</c> block's clauses and returns whether the block is invalid.</summary>
        private bool BlockCheck(TaleLineSyntax line, ReadOnlySpan<char> content)
        {
            var spec = content[SceneVocabulary.SkillCheckFence.Length..].TrimStart();
            if (!spec.StartsWith(SceneVocabulary.SkillCheck, StringComparison.Ordinal))
            {
                Error(line, CompilerDiagnosticCodes.InvalidBlock,
                    $"Only '{SceneVocabulary.SkillCheckBlock}' blocks are supported.");
                return true;
            }

            var keyword = line.Indent + content.Length - spec.Length;
            SkillCheck(line, keyword, spec[SceneVocabulary.SkillCheck.Length..].ToString(), null, 0, insideBlock: true);
            _check = (line.Line, line.Indent, true, false, false);
            return false;
        }

        /// <summary>
        /// Adds the clauses of a <c>[actor] stat dc expr</c> spec that starts after the <c>skill-check</c> keyword at
        /// <paramref name="keyword"/>.
        /// </summary>
        private void SkillCheck(TaleLineSyntax line, int keyword, string text, DelimiterScanResult? scan, int scanOffset,
            bool insideBlock)
        {
            var column = keyword + SceneVocabulary.SkillCheck.Length;
            var separator = text.IndexOf(SceneVocabulary.DifficultySeparator, StringComparison.Ordinal);
            if (separator >= 0)
            {
                scan ??= DelimiterScanner.Scan(text, DelimiterScanMode.CodeGroup, cancellationToken: _cancellationToken);
                var trailing = ChoiceSyntax.FindTrailingClause(text,
                    separator + SceneVocabulary.DifficultySeparator.Length, scan, scanOffset, _cancellationToken);
                if (trailing >= 0)
                {
                    TrailingChoiceText(line, text, trailing, column, afterCheck: true, insideBlock);
                    text = text[..trailing].TrimEnd();
                }
            }

            TaleClause? hiddenClause = null;
            if (TryStripHiddenModifier(text, out var withoutHidden, out var comma, out var hiddenStart))
            {
                text = withoutHidden;
                hiddenClause = new(TaleClauseKind.CheckHidden, column + comma, column + hiddenStart,
                    SceneVocabulary.Hidden.Length);
            }

            // Words are separated by spaces only; dc is the first word spelled exactly "dc".
            var words = 0;
            var difficultyWord = -1;
            var checkIndex = _clauses.Count;
            Clause(default);
            if (hiddenClause is { } clause)
                Clause(clause);
            for (var pos = 0; pos < text.Length;)
            {
                if (text[pos] == ' ')
                {
                    pos++;
                    continue;
                }

                var end = text.IndexOf(' ', pos);
                if (end < 0)
                    end = text.Length;
                if (difficultyWord < 0)
                {
                    if (text.AsSpan(pos, end - pos).SequenceEqual(SceneVocabulary.Difficulty))
                        difficultyWord = words;
                    else if (words < 2)
                        Clause(new(TaleClauseKind.CheckWord, column + pos, column + pos, end - pos));
                }

                words++;
                pos = end;
            }

            separator = text.IndexOf(SceneVocabulary.DifficultySeparator, StringComparison.Ordinal);
            if (separator >= 0)
            {
                var value = separator + SceneVocabulary.DifficultySeparator.Length;
                Clause(new(TaleClauseKind.Difficulty, column + separator + 1, column + value, text.Length - value));
            }
            else if (text.EndsWith(SceneVocabulary.DifficultyPrefix, StringComparison.Ordinal))
            {
                Clause(new(TaleClauseKind.Difficulty, column + text.Length - SceneVocabulary.Difficulty.Length,
                    column + text.Length, 0));
            }

            var malformed = difficultyWord is < 1 or > 2 || difficultyWord == words - 1;
            _clauses[checkIndex] = new(TaleClauseKind.SkillCheck, keyword, column, text.Length)
            {
                IsInvalid = malformed,
                DifficultyWord = difficultyWord,
            };
            if (malformed)
            {
                Error(line, CompilerDiagnosticCodes.MalformedSkillCheck,
                    $"Skill checks look like '{SceneVocabulary.SkillCheck} {SceneVocabulary.OpenBracket}actor" +
                    $"{SceneVocabulary.CloseBracket} skill{SceneVocabulary.DifficultySeparator}12'.", column);
            }
        }

        private static bool TryStripHiddenModifier(string text, out string checkText, out int comma, out int hiddenStart)
        {
            var end = text.Length;
            while (end > 0 && char.IsWhiteSpace(text[end - 1]))
                end--;
            hiddenStart = end - SceneVocabulary.Hidden.Length;
            comma = hiddenStart - 1;
            while (comma >= 0 && char.IsWhiteSpace(text[comma]))
                comma--;
            if (hiddenStart < 0 ||
                !text.AsSpan(hiddenStart, SceneVocabulary.Hidden.Length).SequenceEqual(SceneVocabulary.Hidden) ||
                comma < 0 || text[comma] != ExpressionVocabulary.ArgumentSeparator)
            {
                checkText = text;
                return false;
            }

            checkText = text[..comma].TrimEnd();
            return true;
        }

        /// <summary>Attaches the line to the open skill check if it continues it; otherwise closes the check.</summary>
        private bool TryAttach(ref TaleLineSyntax line, ReadOnlySpan<char> content)
        {
            var check = _check!.Value;
            if (line.Kind == TaleLineKind.Comment || (check.InsideBlock && line.Kind == TaleLineKind.Blank))
            {
                line = line with { IsAttached = true };
                return true;
            }

            if (check.InsideBlock && line is { Kind: TaleLineKind.SkillCheckBlock, IsBareFence: true })
            {
                line = line with { IsAttached = true };
                FinishCheck(closed: true);
                return true;
            }

            if (line.Kind is not (TaleLineKind.SuccessOutcome or TaleLineKind.FailureOutcome))
            {
                FinishCheck(closed: false);
                return false;
            }

            _check = line.Kind == TaleLineKind.SuccessOutcome ? check with { Success = true } : check with { Failure = true };
            var first = _clauses.Count;
            line = WithClauses(line, first, Outcome(line, content)) with { IsAttached = true };
            return true;
        }

        private void FinishCheck(bool closed)
        {
            var check = _check!.Value;
            _check = null;
            if (check.InsideBlock && !closed)
            {
                _diagnostics.Add(new(CompilerDiagnosticCodes.InvalidBlock,
                    $"'{SceneVocabulary.SkillCheckBlock}' block is never closed with " +
                    $"'{SceneVocabulary.SkillCheckFence}'.", check.Line, check.Column));
            }

            if (!check.Success || !check.Failure)
            {
                _diagnostics.Add(new(CompilerDiagnosticCodes.MalformedSkillCheckOutcome,
                    $"A skill check needs both a '{SceneVocabulary.SuccessOutcome}' and a " +
                    $"'{SceneVocabulary.FailureOutcome}' line.", check.Line, check.Column));
            }
        }

        /// <summary>Adds an outcome line's effects and divert clauses and returns whether the outcome is invalid.</summary>
        private bool Outcome(TaleLineSyntax line, ReadOnlySpan<char> content)
        {
            var prefix = line.Kind == TaleLineKind.SuccessOutcome
                ? SceneVocabulary.SuccessOutcome
                : SceneVocabulary.FailureOutcome;
            var text = content[prefix.Length..];
            var column = line.Indent + prefix.Length;
            var pos = text.Length - text.TrimStart().Length;
            if (pos < text.Length && text[pos] == SceneVocabulary.InlineOpen)
            {
                var scan = DelimiterScanner.Scan(text.ToString(), DelimiterScanMode.CodeGroup, pos, _cancellationToken);
                if (!scan.TryGetClose(pos, out var end))
                {
                    Clause(new(TaleClauseKind.UnclosedEffects, column + pos, column + pos + 1, text.Length - pos - 1));
                    Error(line, CompilerDiagnosticCodes.MalformedSkillCheckOutcome,
                        $"Outcome effect block is missing its closing '{SceneVocabulary.InlineClose}'.", column + pos);
                    return true;
                }

                Clause(new(TaleClauseKind.Effects, column + pos, column + pos + 1, end - pos - 1));
                pos = end + 1 + Lead(text[(end + 1)..]);
            }

            if (pos == text.Length)
                return false;
            if (!text[pos..].StartsWith(SceneVocabulary.Divert, StringComparison.Ordinal))
            {
                Error(line, CompilerDiagnosticCodes.MalformedSkillCheckOutcome,
                    $"Outcomes look like '{SceneVocabulary.SuccessOutcome} {SceneVocabulary.InlineOpen} effects " +
                    $"{SceneVocabulary.InlineClose} {SceneVocabulary.Divert} target'.", column + pos);
                return true;
            }

            DivertClause(line, column + pos, column + pos + SceneVocabulary.Divert.Length,
                text[(pos + SceneVocabulary.Divert.Length)..]);
            return false;
        }

    }
}
