using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Parsing;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Analysis;

/// <summary>Classifies Talemark syntax from its parser representation without binding story symbols.</summary>
public static class TalemarkClassifier
{
    /// <summary>Parses and classifies one <c>.tale</c> file without resolving any story symbols.</summary>
    public static IReadOnlyList<TalemarkClassification> Classify(string file, string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        return Classify(file, TaleSyntaxParser.Parse(text, cancellationToken), cancellationToken);
    }

    internal static IReadOnlyList<TalemarkClassification> Classify(string file, TaleFileSyntax syntax,
        CancellationToken cancellationToken)
    {
        var classifier = new Classifier(file, syntax, cancellationToken);
        return classifier.Run();
    }

    internal static void ClassifyExpressionSyntax(string text, Func<int, SourceLocation> locate,
        ICollection<TalemarkClassification> output, CancellationToken cancellationToken)
    {
        var tokens = Lexer.Tokenize(text);
        var previousWasHash = false;
        var work = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            if ((++work & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var token = tokens[index];
            if (token.Kind == TokenKind.End)
                continue;
            var kind = token.Kind switch
            {
                TokenKind.Number => TalemarkClassificationKind.Number,
                TokenKind.String => TalemarkClassificationKind.String,
                TokenKind.NamedPredicate => TalemarkClassificationKind.Function,
                TokenKind.Identifier => IdentifierKind(tokens, index, previousWasHash),
                TokenKind.Plus or TokenKind.Minus or TokenKind.Star or TokenKind.Slash or
                    TokenKind.Equal or TokenKind.NotEqual or TokenKind.Less or TokenKind.LessOrEqual or
                    TokenKind.Greater or TokenKind.GreaterOrEqual or TokenKind.Assign or
                    TokenKind.PlusAssign or TokenKind.MinusAssign =>
                    TalemarkClassificationKind.Operator,
                TokenKind.LeftParen or TokenKind.RightParen or TokenKind.Colon or TokenKind.Semicolon =>
                    TalemarkClassificationKind.Punctuation,
                TokenKind.Hash => TalemarkClassificationKind.Punctuation,
                _ => token.Length > 0 && char.IsLetter(token.Source[token.Offset])
                    ? TalemarkClassificationKind.Identifier
                    : TalemarkClassificationKind.Punctuation,
            };
            output.Add(new(new SourceRange(locate(token.Offset), locate(token.Offset + token.Length)), kind));
            previousWasHash = token.Kind == TokenKind.Hash;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static TalemarkClassificationKind IdentifierKind(TokenBuffer tokens, int index, bool previousWasHash)
    {
        if (previousWasHash)
            return TalemarkClassificationKind.Modifier;
        if (index + 1 < tokens.Count && tokens[index + 1].Kind == TokenKind.Colon)
            return TalemarkClassificationKind.Namespace;
        if (index > 0 && tokens[index - 1].Kind == TokenKind.Colon)
        {
            if (index + 1 < tokens.Count && tokens[index + 1].Kind == TokenKind.Colon)
                return TalemarkClassificationKind.Identifier;
            var prefixIndex = index - 2;
            var prefix = prefixIndex >= 0 ? tokens[prefixIndex].Value : "";
            return prefix is ExpressionVocabulary.Flag or ExpressionVocabulary.Var or
                ExpressionVocabulary.Faction or ExpressionVocabulary.Item or ExpressionVocabulary.Scene
                ? TalemarkClassificationKind.Identifier
                : TalemarkClassificationKind.Property;
        }

        var text = tokens[index].Value;
        var syntaxKind = AuthoringSyntaxVocabulary.ExpressionKind(text);
        if (syntaxKind == AuthoringTokenKind.Image)
            return TalemarkClassificationKind.Function;
        if (syntaxKind is AuthoringTokenKind.True or AuthoringTokenKind.False or
            AuthoringTokenKind.And or AuthoringTokenKind.Or or AuthoringTokenKind.Not or
            AuthoringTokenKind.Has or AuthoringTokenKind.Visited or AuthoringTokenKind.Set or
            AuthoringTokenKind.Clear or AuthoringTokenKind.Give or AuthoringTokenKind.Take or
            AuthoringTokenKind.SkillCheck or AuthoringTokenKind.If or AuthoringTokenKind.Else or
            AuthoringTokenKind.EndIf)
            return TalemarkClassificationKind.Keyword;
        return TalemarkClassificationKind.Identifier;
    }

    private sealed class Classifier(string file, TaleFileSyntax syntax, CancellationToken cancellationToken)
    {
        private readonly List<TalemarkClassification> _classifications = [];
        private int _work;

        public IReadOnlyList<TalemarkClassification> Run()
        {
            foreach (var line in syntax.Preamble)
            {
                CheckCancellation();
                if (line.Kind == TaleLineKind.Comment)
                    AddLine(line.Line + 1, line.Indent, line.Length - line.Indent,
                        TalemarkClassificationKind.Comment);
            }

            foreach (var scene in syntax.Scenes)
            {
                CheckCancellation();
                ClassifyHeader(scene);
                ClassifySceneLines(scene);
            }

            _classifications.Sort(static (left, right) =>
            {
                var line = left.Range.Start.Line.CompareTo(right.Range.Start.Line);
                if (line != 0) return line;
                var column = left.Range.Start.Column.CompareTo(right.Range.Start.Column);
                if (column != 0) return column;
                return left.Range.End.Column.CompareTo(right.Range.End.Column);
            });
            return Array.AsReadOnly(_classifications.ToArray());
        }

        private void ClassifyHeader(TaleSceneSyntax scene)
        {
            var line = syntax.Text.AsSpan(scene.Start, scene.HeaderLength);
            var marker = scene.HeaderIndent;
            Add(scene.Line + 1, marker, SceneVocabulary.SceneHeader.Length, TalemarkClassificationKind.Keyword);
            var idStart = marker + SceneVocabulary.SceneHeader.Length;
            while (idStart < line.Length && char.IsWhiteSpace(line[idStart]))
                idStart++;
            var idLength = scene.Id.Length;
            if (idLength > 0)
                Add(scene.Line + 1, idStart, idLength, TalemarkClassificationKind.SceneIdentifier,
                    TalemarkClassificationModifiers.Declaration);
        }

        private void ClassifySceneLines(TaleSceneSyntax scene)
        {
            var paragraph = new SourceText();

            void FlushParagraph()
            {
                if (paragraph.IsEmpty)
                    return;
                AddInline(paragraph);
                paragraph = new SourceText();
            }

            foreach (var line in scene.Lines)
            {
                CheckCancellation();
                switch (line.Kind)
                {
                    case TaleLineKind.Comment:
                        AddLine(scene.FileLine(line.Line) + 1, line.Indent, line.Length - line.Indent,
                            TalemarkClassificationKind.Comment);
                        continue;
                    case TaleLineKind.Blank:
                        FlushParagraph();
                        continue;
                    case TaleLineKind.Prose:
                        paragraph.AppendLine(syntax.Content(scene, line).ToString(),
                            At(scene, line, line.Indent));
                        continue;
                    case TaleLineKind.Metadata:
                    case TaleLineKind.MisplacedMetadata:
                        ClassifyMetadata(scene, line);
                        continue;
                }

                FlushParagraph();
                ClassifyLine(scene, line);
            }

            FlushParagraph();
        }

        private void ClassifyLine(TaleSceneSyntax scene, TaleLineSyntax line)
        {
            var raw = syntax.RawText(scene, line);
            switch (line.Kind)
            {
                case TaleLineKind.BlockIf:
                case TaleLineKind.BlockElse:
                case TaleLineKind.BlockEndIf:
                    ClassifyBlockLine(scene, line, raw);
                    break;
                case TaleLineKind.Speech:
                    ClassifySpeech(scene, line, raw);
                    break;
                case TaleLineKind.Effects:
                    AddLine(scene.FileLine(line.Line) + 1, line.Indent, 1, TalemarkClassificationKind.StructuralMarker);
                    ClassifyExpression(syntax.Value(scene, line), At(scene, line, scene.Part(line).ValueStart));
                    break;
                case TaleLineKind.Choice:
                    AddLine(scene.FileLine(line.Line) + 1, line.Indent, 1, TalemarkClassificationKind.StructuralMarker);
                    ClassifyClauses(scene, line, raw);
                    break;
                case TaleLineKind.Divert:
                    AddLine(scene.FileLine(line.Line) + 1, line.Indent, SceneVocabulary.Divert.Length,
                        TalemarkClassificationKind.Divert);
                    ClassifyDivertTarget(syntax.Value(scene, line), At(scene, line, scene.Part(line).ValueStart));
                    break;
                case TaleLineKind.SkillCheckBlock:
                    ClassifySkillCheckBlock(scene, line, raw);
                    break;
                case TaleLineKind.SuccessOutcome:
                case TaleLineKind.FailureOutcome:
                    ClassifyOutcome(scene, line, raw);
                    break;
            }
        }

        private void ClassifyMetadata(TaleSceneSyntax scene, TaleLineSyntax line)
        {
            var raw = syntax.RawText(scene, line);
            var marker = raw.IndexOf(SceneVocabulary.MetadataMarker);
            if (marker < 0)
                return;
            Add(scene.FileLine(line.Line) + 1, marker, 1, TalemarkClassificationKind.Punctuation);

            if (line.Kind == TaleLineKind.Metadata)
            {
                var part = scene.Part(line);
                Add(scene.FileLine(line.Line) + 1, part.HeadStart, part.HeadLength,
                    TalemarkClassificationKind.Keyword);
                if (line.MetadataKey is not { } key)
                    return;

                var value = syntax.Value(scene, line);
                var location = At(scene, line, part.ValueStart);
                switch (key)
                {
                    case SceneMetadataKey.Title:
                        if (!value.IsEmpty)
                            Add(location, location.Offset(value.Length), TalemarkClassificationKind.String);
                        break;
                    case SceneMetadataKey.Requires:
                        ClassifyRequirement(value.ToString(), location);
                        break;
                    case SceneMetadataKey.Priority:
                    case SceneMetadataKey.HubLimit:
                        ClassifyExpression(value, location);
                        break;
                    case SceneMetadataKey.Tags:
                        ClassifyTagList(value, location);
                        break;
                    case SceneMetadataKey.Hub:
                        ClassifyHubSelector(value, location);
                        break;
                }
                return;
            }

            var contentStart = line.Indent + 1;
            var contentEnd = line.Indent + line.ContentLength;
            var keyEnd = contentStart;
            while (keyEnd < contentEnd && !char.IsWhiteSpace(raw[keyEnd]))
                keyEnd++;
            if (keyEnd > contentStart)
                Add(scene.FileLine(line.Line) + 1, contentStart, keyEnd - contentStart,
                    TalemarkClassificationKind.Keyword);
        }

        private void ClassifyBlockLine(TaleSceneSyntax scene, TaleLineSyntax line, ReadOnlySpan<char> raw)
        {
            var contentStart = line.Indent;
            Add(scene.FileLine(line.Line) + 1, contentStart, 1, TalemarkClassificationKind.Punctuation);
            var part = scene.Part(line);
            Add(scene.FileLine(line.Line) + 1, part.HeadStart, part.HeadLength,
                TalemarkClassificationKind.Keyword);
            if (line.Kind == TaleLineKind.BlockIf)
                ClassifyExpression(syntax.Value(scene, line), At(scene, line, part.ValueStart));

            var close = raw.LastIndexOf(SceneVocabulary.InlineClose);
            if (close >= 0)
                Add(scene.FileLine(line.Line) + 1, close, 1, TalemarkClassificationKind.Punctuation);
        }

        private void ClassifySpeech(TaleSceneSyntax scene, TaleLineSyntax line, ReadOnlySpan<char> raw)
        {
            Add(scene.FileLine(line.Line) + 1, line.Indent, 1, TalemarkClassificationKind.StructuralMarker);
            var part = scene.Part(line);
            if (part.HeadLength > 0)
                Add(scene.FileLine(line.Line) + 1, part.HeadStart, part.HeadLength,
                    TalemarkClassificationKind.Identifier);
            var colon = raw[(part.HeadStart + part.HeadLength)..].IndexOf(ExpressionVocabulary.Separator);
            if (colon >= 0)
                Add(scene.FileLine(line.Line) + 1, part.HeadStart + part.HeadLength + colon, 1,
                    TalemarkClassificationKind.Punctuation);

            var value = syntax.Value(scene, line);
            AddInline(SourceText.Single(value.ToString(), At(scene, line, part.ValueStart)));
        }

        private void ClassifySkillCheckBlock(TaleSceneSyntax scene, TaleLineSyntax line, ReadOnlySpan<char> raw)
        {
            AddLine(scene.FileLine(line.Line) + 1, line.Indent, SceneVocabulary.SkillCheckFence.Length,
                TalemarkClassificationKind.StructuralMarker);
            if (!line.IsBareFence)
                ClassifyClauses(scene, line, raw);
        }

        private void ClassifyOutcome(TaleSceneSyntax scene, TaleLineSyntax line, ReadOnlySpan<char> raw)
        {
            var spelling = line.Kind == TaleLineKind.SuccessOutcome
                ? SceneVocabulary.SuccessOutcome
                : SceneVocabulary.FailureOutcome;
            var colon = spelling.IndexOf(ExpressionVocabulary.Separator);
            AddLine(scene.FileLine(line.Line) + 1, line.Indent, colon,
                TalemarkClassificationKind.Keyword);
            if (colon >= 0)
                AddLine(scene.FileLine(line.Line) + 1, line.Indent + colon, 1,
                    TalemarkClassificationKind.Punctuation);
            if (line.ClauseCount > 0)
                ClassifyClauses(scene, line, raw);
        }

        private void ClassifyClauses(TaleSceneSyntax scene, TaleLineSyntax line, ReadOnlySpan<char> raw)
        {
            foreach (var clause in scene.Clauses(line))
            {
                CheckCancellation();
                switch (clause.Kind)
                {
                    case TaleClauseKind.Label:
                    case TaleClauseKind.UnclosedLabel:
                        AddAt(scene, line, clause.Start, 1, TalemarkClassificationKind.Punctuation);
                        AddInline(SourceText.Single(
                            syntax.Value(scene, line, clause).ToString(),
                            At(scene, line, clause.ValueStart)));
                        if (clause.Kind == TaleClauseKind.Label)
                            AddAt(scene, line, clause.ValueStart + clause.ValueLength, 1,
                                TalemarkClassificationKind.Punctuation);
                        break;
                    case TaleClauseKind.UnclosedBrace:
                    case TaleClauseKind.UnclosedImage:
                        AddAt(scene, line, clause.Start, 1, TalemarkClassificationKind.Punctuation);
                        ClassifyExpression(syntax.Value(scene, line, clause), At(scene, line, clause.ValueStart));
                        break;
                    case TaleClauseKind.Requires:
                    case TaleClauseKind.UnclosedRequires:
                    case TaleClauseKind.InvalidCondition:
                        AddAt(scene, line, clause.Start, 1, TalemarkClassificationKind.Punctuation);
                        var requiresStart = clause.Start + 1;
                        if (clause.Kind is TaleClauseKind.Requires or TaleClauseKind.UnclosedRequires)
                        {
                            var rawText = raw[requiresStart..clause.ValueStart];
                            var word = rawText.IndexOf(SceneVocabulary.Requires, StringComparison.Ordinal);
                            if (word >= 0)
                                AddAt(scene, line, requiresStart + word, SceneVocabulary.Requires.Length,
                                    TalemarkClassificationKind.Keyword);
                        }
                        ClassifyRequirement(syntax.Value(scene, line, clause).ToString(),
                            At(scene, line, clause.ValueStart));
                        if (clause.Kind == TaleClauseKind.Requires)
                            AddAt(scene, line, clause.ValueStart + clause.ValueLength, 1,
                                TalemarkClassificationKind.Punctuation);
                        break;
                    case TaleClauseKind.Effects:
                    case TaleClauseKind.UnclosedEffects:
                        AddAt(scene, line, clause.Start, 1, TalemarkClassificationKind.Punctuation);
                        ClassifyExpression(syntax.Value(scene, line, clause), At(scene, line, clause.ValueStart));
                        if (clause.Kind == TaleClauseKind.Effects)
                            AddAt(scene, line, clause.ValueStart + clause.ValueLength, 1,
                                TalemarkClassificationKind.Punctuation);
                        break;
                    case TaleClauseKind.Divert:
                        AddAt(scene, line, clause.Start, SceneVocabulary.Divert.Length,
                            TalemarkClassificationKind.Divert);
                        ClassifyDivertTarget(syntax.Value(scene, line, clause),
                            At(scene, line, clause.ValueStart));
                        break;
                    case TaleClauseKind.SkillCheck:
                        ClassifySkillCheck(scene, line, clause);
                        break;
                    case TaleClauseKind.CheckWord:
                        ClassifyCheckWord(scene, line, clause);
                        break;
                    case TaleClauseKind.Difficulty:
                        ClassifyDifficulty(scene, line, clause);
                        break;
                }
            }
        }

        private void ClassifyRequirement(string value, SourceLocation location)
        {
            var parsed = RequirementClauseSyntax.Parse(value, cancellationToken);
            ClassifyExpression(value.AsSpan(0, parsed.ExpressionLength), location);
            foreach (var comma in parsed.CommaOffsets)
                Add(location.Offset(comma), location.Offset(comma + 1), TalemarkClassificationKind.Punctuation);
            foreach (var shown in parsed.ShownOffsets)
                Add(location.Offset(shown), location.Offset(shown + SceneVocabulary.Shown.Length),
                    TalemarkClassificationKind.Keyword);
            foreach (var slot in parsed.TextSlots)
            {
                Add(location.Offset(slot.Offset - 1), location.Offset(slot.Offset),
                    TalemarkClassificationKind.Punctuation);
                AddInline(SourceText.Single(value.Substring(slot.Offset, slot.Length),
                    location.Offset(slot.Offset)));
                Add(location.Offset(slot.Offset + slot.Length),
                    location.Offset(slot.Offset + slot.Length + 1), TalemarkClassificationKind.Punctuation);
            }
        }

        private void ClassifySkillCheck(TaleSceneSyntax scene, TaleLineSyntax line, TaleClause clause)
        {
            AddAt(scene, line, clause.Start, SceneVocabulary.SkillCheck.Length,
                TalemarkClassificationKind.Keyword);
        }

        private void ClassifyCheckWord(TaleSceneSyntax scene, TaleLineSyntax line, TaleClause clause)
        {
            var value = syntax.Value(scene, line, clause);
            var location = At(scene, line, clause.ValueStart);
            if (value.Length >= 2 && value[0] == SceneVocabulary.OpenBracket &&
                value[^1] == SceneVocabulary.CloseBracket)
            {
                Add(location, location.Offset(1), TalemarkClassificationKind.Punctuation);
                ClassifyIdentifier(value[1..^1].ToString(), location.Offset(1));
                Add(location.Offset(value.Length - 1), location.Offset(value.Length),
                    TalemarkClassificationKind.Punctuation);
                return;
            }

            ClassifyIdentifier(value.ToString(), location);
        }

        private void ClassifyDifficulty(TaleSceneSyntax scene, TaleLineSyntax line, TaleClause clause)
        {
            AddAt(scene, line, clause.Start, SceneVocabulary.Difficulty.Length, TalemarkClassificationKind.Keyword);
            ClassifyExpression(syntax.Value(scene, line, clause), At(scene, line, clause.ValueStart));
        }

        private void ClassifyTagList(ReadOnlySpan<char> value, SourceLocation location)
        {
            for (var index = 0; index < value.Length;)
            {
                if (char.IsWhiteSpace(value[index]))
                {
                    index++;
                    continue;
                }
                if (value[index] == ExpressionVocabulary.ArgumentSeparator)
                {
                    Add(location.Offset(index), location.Offset(index + 1), TalemarkClassificationKind.Punctuation);
                    index++;
                    continue;
                }
                var start = index;
                while (index < value.Length && !char.IsWhiteSpace(value[index]) &&
                       value[index] != ExpressionVocabulary.ArgumentSeparator)
                    index++;
                ClassifyIdentifier(value[start..index].ToString(), location.Offset(start));
            }
        }

        private void ClassifyHubSelector(ReadOnlySpan<char> value, SourceLocation location)
        {
            var start = 0;
            while (start < value.Length)
            {
                while (start < value.Length && char.IsWhiteSpace(value[start]))
                    start++;
                if (start == value.Length)
                    break;
                var end = start + 1;
                while (end < value.Length && !char.IsWhiteSpace(value[end]))
                    end++;
                var word = value[start..end].ToString();
                if (word is ExpressionVocabulary.And or ExpressionVocabulary.Or)
                    Add(location.Offset(start), location.Offset(end), TalemarkClassificationKind.Keyword);
                else
                    ClassifyIdentifier(word, location.Offset(start));
                start = end;
            }
        }

        private void ClassifyDivertTarget(ReadOnlySpan<char> target, SourceLocation location)
        {
            if (target.SequenceEqual(SceneVocabulary.End))
                Add(location, location.Offset(target.Length), TalemarkClassificationKind.Keyword);
            else if (!target.IsEmpty)
                Add(location, location.Offset(target.Length), TalemarkClassificationKind.SceneIdentifier);
        }

        private void ClassifyIdentifier(string value, SourceLocation location)
        {
            if (value.Length > 0)
                Add(location, location.Offset(value.Length), TalemarkClassificationKind.Identifier);
        }

        private void ClassifyExpression(ReadOnlySpan<char> text, SourceLocation location) =>
            ClassifyExpressionSyntax(text.ToString(), offset => location.Offset(offset), _classifications,
                cancellationToken);

        private void AddInline(SourceText source)
        {
            foreach (var classification in InlineParser.Classify(source, cancellationToken))
            {
                CheckCancellation();
                _classifications.Add(classification);
            }
        }

        private void AddAt(TaleSceneSyntax scene, TaleLineSyntax line, int column, int length,
            TalemarkClassificationKind kind, TalemarkClassificationModifiers modifiers =
                TalemarkClassificationModifiers.None)
        {
            if (length <= 0)
                return;
            Add(At(scene, line, column), At(scene, line, column + length), kind, modifiers);
        }

        private void AddLine(int line, int column, int length, TalemarkClassificationKind kind,
            TalemarkClassificationModifiers modifiers = TalemarkClassificationModifiers.None)
        {
            if (length <= 0)
                return;
            var start = new SourceLocation(file, line, column + 1);
            Add(start, start.Offset(length), kind, modifiers);
        }

        private void Add(int line, int column, int length, TalemarkClassificationKind kind,
            TalemarkClassificationModifiers modifiers = TalemarkClassificationModifiers.None) =>
            AddLine(line, column, length, kind, modifiers);

        private void Add(SourceLocation start, SourceLocation end, TalemarkClassificationKind kind,
            TalemarkClassificationModifiers modifiers = TalemarkClassificationModifiers.None) =>
            _classifications.Add(new(new SourceRange(start, end), kind, modifiers));

        private SourceLocation At(TaleSceneSyntax scene, TaleLineSyntax line, int column) =>
            new(file, scene.FileLine(line.Line) + 1, column + 1);

        private void CheckCancellation()
        {
            if ((++_work & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
