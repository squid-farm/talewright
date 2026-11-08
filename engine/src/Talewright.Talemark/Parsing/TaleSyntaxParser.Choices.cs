using Talewright.Core.Identity;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

internal static partial class TaleSyntaxParser
{
    private sealed partial class SceneState
    {
        /// <summary>Adds a choice's clauses and returns whether the choice is dropped.</summary>
        /// <param name="line">The choice line.</param>
        /// <param name="rest">The line after the choice marker.</param>
        private bool Choice(TaleLineSyntax line, string rest)
        {
            var indent = line.Indent;
            var pos = TextScanning.SkipWhitespace(rest, 0);
            var hasLabel = false;
            if (pos < rest.Length && rest[pos] == SceneVocabulary.OpenBracket)
            {
                var labelScan = DelimiterScanner.Scan(rest, DelimiterScanMode.ChoiceLabel, pos, _cancellationToken);
                if (!labelScan.TryGetClose(pos, out var close))
                {
                    UnclosedLabel(line, rest, pos, labelScan);
                    return true;
                }

                Clause(new(TaleClauseKind.Label, indent + 1 + pos, indent + 2 + pos, close - pos - 1));
                hasLabel = true;
                pos = TextScanning.SkipWhitespace(rest, close + 1);
            }

            var hasDivert = false;
            var hasRequires = false;
            var hasEffects = false;
            var clauseScan = DelimiterScanner.Scan(rest, DelimiterScanMode.CodeGroup, pos, _cancellationToken);
            while (pos < rest.Length)
            {
                var column = indent + 1 + pos;
                var remaining = rest.AsSpan(pos);
                if (ChoiceSyntax.TryReadChoiceRequires(rest, pos, _cancellationToken, out var valueStart))
                {
                    if (hasRequires)
                        Error(line, CompilerDiagnosticCodes.MalformedChoice,
                            $"Duplicate '{SceneVocabulary.ChoiceRequiresExample}' condition. " +
                            "A choice accepts only one.", column);
                    if (!clauseScan.TryGetClose(pos, out var end))
                    {
                        Clause(new(TaleClauseKind.UnclosedRequires, column, indent + 1 + valueStart,
                            rest.Length - valueStart));
                        Error(line, CompilerDiagnosticCodes.MalformedChoice,
                            $"'{SceneVocabulary.ChoiceRequiresExample}' is missing its closing " +
                            $"'{ExpressionVocabulary.CloseParen}'.", column);
                        return true;
                    }

                    Clause(new(TaleClauseKind.Requires, column, indent + 1 + valueStart, end - valueStart));
                    hasRequires = true;
                    pos = TextScanning.SkipWhitespace(rest, end + 1);
                }
                else if (ChoiceSyntax.TryReadShownWithoutRequirement(rest, pos, _cancellationToken))
                {
                    Error(line, CompilerDiagnosticCodes.MalformedChoice,
                        $"'{SceneVocabulary.Shown}' requires a choice requirement, e.g. " +
                        $"'{SceneVocabulary.ChoiceRequiresExample}, {SceneVocabulary.Shown}'.", column);
                    return true;
                }
                else if (remaining[0] == SceneVocabulary.InlineOpen)
                {
                    if (hasEffects)
                        Error(line, CompilerDiagnosticCodes.MalformedChoice,
                            "Duplicate effect block. A choice accepts only one.", column);
                    if (!clauseScan.TryGetClose(pos, out var end))
                    {
                        Clause(new(TaleClauseKind.UnclosedEffects, column, column + 1, rest.Length - pos - 1));
                        Error(line, CompilerDiagnosticCodes.MalformedChoice,
                            $"Effect block is missing its closing '{SceneVocabulary.InlineClose}'.", column);
                        return true;
                    }

                    Clause(new(TaleClauseKind.Effects, column, column + 1, end - pos - 1));
                    hasEffects = true;
                    pos = TextScanning.SkipWhitespace(rest, end + 1);
                }
                else if (remaining.StartsWith(SceneVocabulary.Divert, StringComparison.Ordinal))
                {
                    hasDivert = true;
                    var targetEnd = TextScanning.SkipWhitespace(rest, pos + SceneVocabulary.Divert.Length);
                    var checkpoint = _cancellationToken.CanBeCanceled ? new CancellationCheckpoint(_cancellationToken) : null;
                    while (targetEnd < rest.Length && !char.IsWhiteSpace(rest[targetEnd]) &&
                           rest[targetEnd] is not (SceneVocabulary.InlineOpen or ExpressionVocabulary.OpenParen) &&
                           !rest.AsSpan(targetEnd).StartsWith(SceneVocabulary.Divert, StringComparison.Ordinal))
                    {
                        checkpoint?.Check();
                        targetEnd++;
                    }

                    var targetStart = pos + SceneVocabulary.Divert.Length;
                    DivertClause(line, column, column + SceneVocabulary.Divert.Length,
                        rest.AsSpan(targetStart, targetEnd - targetStart));
                    var trailing = TextScanning.SkipWhitespace(rest, targetEnd);
                    if (trailing < rest.Length)
                        TrailingChoiceText(line, rest, trailing, indent + 1, afterCheck: false, insideBlock: false);
                    pos = rest.Length;
                }
                else if (remaining.StartsWith(SceneVocabulary.SkillCheck, StringComparison.Ordinal))
                {
                    if (!hasLabel)
                    {
                        Error(line, CompilerDiagnosticCodes.MalformedChoice,
                            $"Textless choices need '{SceneVocabulary.Divert} target' or '{SceneVocabulary.Divert} " +
                            $"{SceneVocabulary.End}'; skill checks require a '{SceneVocabulary.OpenBracket}label" +
                            $"{SceneVocabulary.CloseBracket}'.");
                        return true;
                    }

                    var specOffset = pos + SceneVocabulary.SkillCheck.Length;
                    SkillCheck(line, column, rest[specOffset..], clauseScan, specOffset, insideBlock: false);
                    _check = (line.Line, column, false, false, false);
                    pos = rest.Length;
                }
                else
                {
                    if (remaining[0] == ExpressionVocabulary.OpenParen)
                    {
                        var end = clauseScan.TryGetClose(pos, out var close) ? close : rest.Length;
                        Clause(new(TaleClauseKind.InvalidCondition, column, column + 1, end - pos - 1));
                    }

                    var help = !hasLabel
                        ? $"Use '{SceneVocabulary.ChoiceMarker} {SceneVocabulary.OpenBracket}label" +
                          $"{SceneVocabulary.CloseBracket} ...' or a textless '{SceneVocabulary.ChoiceMarker} ... " +
                          $"{SceneVocabulary.Divert} target'."
                        : $"Expected {SceneVocabulary.ChoiceRequiresExample}, " +
                          $"{SceneVocabulary.InlineOpen} effects " +
                          $"{SceneVocabulary.InlineClose}, {SceneVocabulary.Divert} target or {SceneVocabulary.SkillCheck}.";
                    var excerpt = ChoiceSyntax.Excerpt(remaining);
                    var bareTarget = IdentifierRules.IsValid(remaining)
                        ? $" Insert '{SceneVocabulary.Divert}' before the target."
                        : "";
                    Error(line, CompilerDiagnosticCodes.MalformedChoice,
                        $"Unexpected '{excerpt}' in choice.{bareTarget} {help}", column);
                    return true;
                }
            }

            if (!hasLabel && !hasDivert)
            {
                Error(line, CompilerDiagnosticCodes.MalformedChoice,
                    $"Textless choices need '{SceneVocabulary.Divert} target' or '{SceneVocabulary.Divert} " +
                    $"{SceneVocabulary.End}'.");
                return true;
            }

            return false;
        }

        private void UnclosedLabel(TaleLineSyntax line, string rest, int open, DelimiterScanResult labelScan)
        {
            var unclosedBrace = -1;
            var checkpoint = _cancellationToken.CanBeCanceled ? new CancellationCheckpoint(_cancellationToken) : null;
            foreach (var issue in labelScan.Issues)
            {
                checkpoint?.Check();
                if (issue.Kind == DelimiterKind.Brace &&
                    issue.Issue is (DelimiterIssueKind.UnclosedOpener or DelimiterIssueKind.MismatchedOpener) &&
                    (unclosedBrace < 0 || issue.Offset < unclosedBrace))
                    unclosedBrace = issue.Offset;
            }

            var unclosedImage = unclosedBrace >= 0 && ImageSyntax.IsCall(rest.AsSpan(unclosedBrace + 1));
            var hint = ChoiceSyntax.FindMissingBracketHint(rest, open, labelScan, _cancellationToken);
            // Clauses after the label are not reader text, so the partial label stops before them.
            var end = unclosedImage ? Math.Min(hint.LabelEnd, unclosedBrace) : hint.LabelEnd;
            var column = line.Indent + 1;
            Clause(new(TaleClauseKind.UnclosedLabel, column + open, column + open + 1, end - open - 1));
            if (unclosedBrace >= 0)
            {
                Clause(new(unclosedImage ? TaleClauseKind.UnclosedImage : TaleClauseKind.UnclosedBrace,
                    column + unclosedBrace, column + unclosedBrace + 1, rest.Length - unclosedBrace - 1));
            }

            var message = $"Choice label is missing its closing '{SceneVocabulary.CloseBracket}'.";
            if (hint.Marker is not null)
                message += $" Place '{SceneVocabulary.CloseBracket}' before '{hint.Marker}'.";
            Error(line, CompilerDiagnosticCodes.MalformedChoice, message, column + open);
        }

        private void DivertClause(TaleLineSyntax line, int start, int column, ReadOnlySpan<char> text)
        {
            var target = text.Trim();
            var valueStart = column + Lead(text);
            var error = DivertTargetError(target);
            Clause(new(TaleClauseKind.Divert, start, valueStart, target.Length) { IsInvalid = error is not null });
            if (error is { } found)
                Error(line, found.Code, found.Message, valueStart);
        }

        private void TrailingChoiceText(TaleLineSyntax line, string text, int pos, int column, bool afterCheck,
            bool insideBlock)
        {
            var remaining = text.AsSpan(pos);
            if (remaining[0] == ExpressionVocabulary.OpenParen &&
                ChoiceSyntax.TryReadChoiceRequires(text, pos, _cancellationToken, out _))
            {
                var conditionMessage = insideBlock
                    ? $"'{SceneVocabulary.ChoiceRequiresExample}' is misplaced after the block skill check."
                    : afterCheck
                        ? $"'{SceneVocabulary.ChoiceRequiresExample}' is misplaced after the skill check. " +
                          $"Put the condition before '{SceneVocabulary.SkillCheck}'."
                        : $"'{SceneVocabulary.ChoiceRequiresExample}' is misplaced after the divert. " +
                          $"Put the condition before '{SceneVocabulary.Divert}'.";
                Error(line, CompilerDiagnosticCodes.MalformedChoice,
                    conditionMessage, column + pos);
                return;
            }

            var message = remaining[0] switch
            {
                SceneVocabulary.InlineOpen when insideBlock =>
                    $"Effect block is misplaced after the skill check. Put effects on a " +
                    $"'{SceneVocabulary.SuccessOutcome}' or '{SceneVocabulary.FailureOutcome}' line.",
                SceneVocabulary.InlineOpen when afterCheck =>
                    $"Effect block is misplaced after the skill check. Put '{SceneVocabulary.InlineOpen} effects " +
                    $"{SceneVocabulary.InlineClose}' before '{SceneVocabulary.SkillCheck}'.",
                SceneVocabulary.InlineOpen =>
                    $"Effect block is misplaced after the divert. Put '{SceneVocabulary.InlineOpen} effects " +
                    $"{SceneVocabulary.InlineClose}' before '{SceneVocabulary.Divert}'.",
                _ when remaining.StartsWith(SceneVocabulary.Divert, StringComparison.Ordinal) && afterCheck =>
                    $"Divert is misplaced after the skill check. Outcomes belong on " +
                    $"'{SceneVocabulary.SuccessOutcome}' and '{SceneVocabulary.FailureOutcome}' lines.",
                _ => $"Unexpected '{ChoiceSyntax.Excerpt(remaining)}' after the " +
                     $"{(afterCheck ? "skill check" : "divert")}.",
            };
            Error(line, CompilerDiagnosticCodes.MalformedChoice, message, column + pos);
        }

    }
}
