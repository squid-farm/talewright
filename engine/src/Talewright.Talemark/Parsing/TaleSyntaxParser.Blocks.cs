using Talewright.Core.Identity;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

internal static partial class TaleSyntaxParser
{
    private sealed partial class SceneState
    {
        private TaleLineSyntax Block(TaleLineSyntax line, ReadOnlySpan<char> content)
        {
            var keyword = line.Kind switch
            {
                TaleLineKind.BlockIf => SceneVocabulary.If,
                TaleLineKind.BlockElse => SceneVocabulary.Else,
                _ => SceneVocabulary.EndIf,
            };
            var head = line.Indent + content.IndexOf(keyword, StringComparison.Ordinal);
            if (line.Kind != TaleLineKind.BlockIf)
                return Balance(WithPart(line, new(head, keyword.Length, 0, 0)));
            if (_blocks.Count == MaximumBlockConditionDepth)
            {
                Error(line, CompilerDiagnosticCodes.NestingLimitExceeded,
                    $"Conditions are nested more than {MaximumBlockConditionDepth} levels deep.");
                _suppressedDepth = 1;
                _suppressedAt = (line.Line, line.Indent);
                return WithPart(line, new(head, keyword.Length, 0, 0)) with { IsInvalid = true };
            }

            _blocks.Add((line.Line, line.Indent, false));
            var inner = content[1..^1];
            return WithPart(line, new(head, keyword.Length,
                line.Indent + 1 + Lead(inner) + SceneVocabulary.IfPrefix.Length,
                inner.Trim().Length - SceneVocabulary.IfPrefix.Length));
        }

        private TaleLineSyntax Balance(TaleLineSyntax line)
        {
            switch (line.Kind)
            {
                case TaleLineKind.BlockElse when _blocks.Count == 0 || _blocks[^1].InElse:
                    Error(line, CompilerDiagnosticCodes.UnbalancedCondition,
                        $"{SceneVocabulary.InlineOpen}{SceneVocabulary.Else}{SceneVocabulary.InlineClose} without a " +
                        $"matching {SceneVocabulary.InlineOpen}{SceneVocabulary.If}{SceneVocabulary.InlineClose}.");
                    return line with { IsInvalid = true };
                case TaleLineKind.BlockElse:
                    _blocks[^1] = _blocks[^1] with { InElse = true };
                    return line;
                case TaleLineKind.BlockEndIf when _blocks.Count == 0:
                    Error(line, CompilerDiagnosticCodes.UnbalancedCondition,
                        $"{SceneVocabulary.InlineOpen}{SceneVocabulary.EndIf}{SceneVocabulary.InlineClose} without a " +
                        $"matching {SceneVocabulary.InlineOpen}{SceneVocabulary.If}{SceneVocabulary.InlineClose}.");
                    return line with { IsInvalid = true };
                default:
                    _blocks.RemoveAt(_blocks.Count - 1);
                    return line;
            }
        }

    }
}
