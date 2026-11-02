using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Parsing;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Tests;

public sealed class ExpressionVocabularyTests
{
    [Fact]
    public void Every_typed_operator_and_property_has_one_spelling_that_maps_back()
    {
        foreach (var op in Enum.GetValues<BinaryOp>())
            Assert.True(ExpressionVocabulary.TryGetSpelling(op, out _), $"{op} has no spelling.");
        foreach (var op in Enum.GetValues<UnaryOp>())
            Assert.True(ExpressionVocabulary.TryGetSpelling(op, out _), $"{op} has no spelling.");
        foreach (var op in Enum.GetValues<AssignOp>())
            Assert.NotEmpty(ExpressionVocabulary.Spelling(op));
        foreach (var part in Enum.GetValues<NamePart>())
        {
            Assert.True(ExpressionVocabulary.TryGetSpelling(part, out var spelling), $"{part} has no spelling.");
            Assert.True(ExpressionVocabulary.TryGetNamePart(spelling, out var parsed));
            Assert.Equal(part, parsed);
        }

        foreach (var form in Enum.GetValues<PronounForm>())
        {
            var found = ExpressionVocabulary.TryGetPronounSpelling(form, out var spelling)
                ? ExpressionVocabulary.TryGetPronounForm(spelling, out var parsed) && parsed == form
                : ExpressionVocabulary.TryGetPossessiveSpelling(form, out spelling) &&
                  ExpressionVocabulary.TryGetPossessiveForm(spelling, out parsed) && parsed == form;
            Assert.True(found, $"{form} has no spelling that maps back to it.");
        }
    }

    [Fact]
    public void Every_lexer_symbol_tokenizes_as_its_declared_kind()
    {
        foreach (var (spelling, kind) in ExpressionVocabulary.Symbols)
        {
            var tokens = Lexer.Tokenize(spelling);
            Assert.Equal(kind, tokens[0].Kind);
            Assert.Equal(spelling.Length, tokens[0].Length);
        }
    }

    [Fact]
    public void Reference_namespaces_classify_once_from_their_spelling()
    {
        Assert.Equal(ReferenceNamespace.Faction, ExpressionVocabulary.Namespace("faction"));
        Assert.Equal(ReferenceNamespace.Item, ExpressionVocabulary.Namespace("item"));
        Assert.Equal(ReferenceNamespace.Flag, ExpressionVocabulary.Namespace("flag"));
        Assert.Equal(ReferenceNamespace.Variable, ExpressionVocabulary.Namespace("var"));
        Assert.Equal(ReferenceNamespace.Character, ExpressionVocabulary.Namespace("player"));
    }
}
