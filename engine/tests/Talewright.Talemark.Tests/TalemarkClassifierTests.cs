using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Parsing;

namespace Talewright.Talemark.Tests;

public sealed class TalemarkClassifierTests
{
    [Fact]
    public void Classifies_documented_file_constructs_without_binding_story_symbols()
    {
        const string tale = """
            // preamble comment
            === entry
            @title The Door
            @requires not flag:ready or player:strength >= 10 and player:strength < 20 and scene:player:visits <= 10 and player has rope and @CanPickLock and true, shown "Unavailable: {var:count}"
            @tags crypt, hidden
            @hub crypt or hidden
            @priority 10
            @hub-limit 4
            @once
            // scene comment
            Narrative *emphasis* and **strong**.
            {if player visited entry}Here {player:first-name#upper} {player:pronoun#one-many("has", "have")}{else}{image("maps/old", "A {player:first-name} map")}{/if}
            > guide: Hello, {player:name}.
            ~ set flag:ready; var:count += 2; var:count = var:count + 1 * 2 - 3 / 4
            ~ take player key; clear flag:ready
            * [Open {item:key#the}] (requires player:strength > 0, shown "Need {var:count}") { give player key } -> unresolved-scene-id
            * -> END
            * [Climb] skill-check strength dc 12
              success: -> exit
              failure: -> exit
            ::: skill-check [player] strength dc 10
            success: { set flag:passed }
            failure: { clear flag:passed }
            :::
            === exit
            @once
            -> END
            """;

        var classifications = TalemarkClassifier.Classify("main.tale", tale);
        var lineCount = tale.Count(character => character == '\n') + 1;
        Assert.All(classifications, classification =>
            Assert.True(classification.Range.Start.Line >= 1 && classification.Range.End.Line >= 1 &&
                        classification.Range.End.Line <= lineCount,
                $"{classification.Kind}: {classification.Range}"));

        void Has(TalemarkClassificationKind kind, string text) =>
            Assert.Contains(classifications, classification =>
                classification.Kind == kind && TextOf(tale, classification) == text);

        Has(TalemarkClassificationKind.Comment, "// preamble comment");
        Has(TalemarkClassificationKind.Keyword, "===");
        Has(TalemarkClassificationKind.Keyword, "title");
        Has(TalemarkClassificationKind.Keyword, "requires");
        Has(TalemarkClassificationKind.Keyword, "shown");
        Has(TalemarkClassificationKind.Keyword, "tags");
        Has(TalemarkClassificationKind.Keyword, "hub");
        Has(TalemarkClassificationKind.Keyword, "priority");
        Has(TalemarkClassificationKind.Keyword, "hub-limit");
        Has(TalemarkClassificationKind.Keyword, "and");
        Has(TalemarkClassificationKind.Keyword, "or");
        Has(TalemarkClassificationKind.Keyword, "once");
        Has(TalemarkClassificationKind.Keyword, "if");
        Has(TalemarkClassificationKind.Keyword, "else");
        Has(TalemarkClassificationKind.Keyword, "/if");
        Has(TalemarkClassificationKind.Keyword, "visited");
        Has(TalemarkClassificationKind.Keyword, "has");
        Has(TalemarkClassificationKind.Keyword, "set");
        Has(TalemarkClassificationKind.Keyword, "give");
        Has(TalemarkClassificationKind.Keyword, "clear");
        Has(TalemarkClassificationKind.Keyword, "take");
        Has(TalemarkClassificationKind.Keyword, "skill-check");
        Has(TalemarkClassificationKind.Keyword, "dc");
        Has(TalemarkClassificationKind.Keyword, "success");
        Has(TalemarkClassificationKind.Keyword, "failure");
        Has(TalemarkClassificationKind.Keyword, "END");
        Has(TalemarkClassificationKind.StructuralMarker, ">");
        Has(TalemarkClassificationKind.StructuralMarker, "~");
        Has(TalemarkClassificationKind.StructuralMarker, "*");
        Has(TalemarkClassificationKind.Divert, "->");
        Has(TalemarkClassificationKind.Operator, "+=");
        Has(TalemarkClassificationKind.Operator, "+");
        Has(TalemarkClassificationKind.Operator, "-");
        Has(TalemarkClassificationKind.Operator, "*");
        Has(TalemarkClassificationKind.Operator, "/");
        Has(TalemarkClassificationKind.Operator, ">=");
        Has(TalemarkClassificationKind.Operator, "<=");
        Has(TalemarkClassificationKind.Namespace, "player");
        Has(TalemarkClassificationKind.Namespace, "scene");
        Has(TalemarkClassificationKind.Namespace, "flag");
        Has(TalemarkClassificationKind.Property, "strength");
        Has(TalemarkClassificationKind.Property, "first-name");
        Has(TalemarkClassificationKind.Property, "visits");
        Has(TalemarkClassificationKind.Function, "image");
        Has(TalemarkClassificationKind.Function, "@CanPickLock");
        Has(TalemarkClassificationKind.String, "\"maps/old\"");
        Has(TalemarkClassificationKind.Number, "10");
        Has(TalemarkClassificationKind.SceneIdentifier, "unresolved-scene-id");
        Has(TalemarkClassificationKind.Modifier, "upper");
        Has(TalemarkClassificationKind.Modifier, "the");
        Has(TalemarkClassificationKind.Modifier, "one-many");
        Has(TalemarkClassificationKind.Formatting, "*");
        Has(TalemarkClassificationKind.Punctuation, "{");

        Assert.Contains(classifications, classification =>
            classification.Kind == TalemarkClassificationKind.SceneIdentifier &&
            classification.Modifiers == TalemarkClassificationModifiers.Declaration &&
            TextOf(tale, classification) == "entry");
    }

    [Fact]
    public void Classifies_block_conditionals_on_separate_lines()
    {
        const string tale = """
            === start
            {if player visited entry}
            First path.
            {else}
            Other path.
            {/if}
            """;

        var classifications = TalemarkClassifier.Classify("main.tale", tale);

        void Has(TalemarkClassificationKind kind, string text, int line, int column) =>
            Assert.Contains(classifications, classification =>
                classification.Kind == kind &&
                TextOf(tale, classification) == text &&
                classification.Range.Start.Line == line &&
                classification.Range.Start.Column == column &&
                classification.Range.End.Column == column + text.Length);

        Has(TalemarkClassificationKind.Punctuation, "{", 2, 1);
        Has(TalemarkClassificationKind.Keyword, "if", 2, 2);
        Has(TalemarkClassificationKind.Identifier, "player", 2, 5);
        Has(TalemarkClassificationKind.Keyword, "visited", 2, 12);
        Has(TalemarkClassificationKind.Identifier, "entry", 2, 20);
        Has(TalemarkClassificationKind.Punctuation, "}", 2, 25);
        Has(TalemarkClassificationKind.Punctuation, "{", 4, 1);
        Has(TalemarkClassificationKind.Keyword, "else", 4, 2);
        Has(TalemarkClassificationKind.Punctuation, "}", 4, 6);
        Has(TalemarkClassificationKind.Punctuation, "{", 6, 1);
        Has(TalemarkClassificationKind.Keyword, "/if", 6, 2);
        Has(TalemarkClassificationKind.Punctuation, "}", 6, 5);
    }

    [Fact]
    public void Formatting_ranges_match_the_inline_renderers_pairing_rules()
    {
        const string tale = """
            === start
            word*inner*word
            **bold**text
            __snake__case__
            *a **b* c**
            """;

        var formatting = TalemarkClassifier.Classify("main.tale", tale)
            .Where(classification => classification.Kind == TalemarkClassificationKind.Formatting)
            .ToArray();

        Assert.Equal(["__", "__", "**", "**"], formatting.Select(classification => TextOf(tale, classification)));
        Assert.DoesNotContain(formatting, classification => classification.Range.Start.Line is 2 or 3);
    }

    [Fact]
    public void Expression_members_are_distinguished_from_owner_names()
    {
        const string tale = """
            === start
            @requires player:name == "Ada" and var:name == "Ada" and flag:first-name and var:and == "word"
            """;

        var classifications = TalemarkClassifier.Classify("main.tale", tale);

        void Has(TalemarkClassificationKind kind, string text) =>
            Assert.Contains(classifications, classification =>
                classification.Kind == kind && TextOf(tale, classification) == text);

        Has(TalemarkClassificationKind.Property, "name");
        Has(TalemarkClassificationKind.Identifier, "name");
        Has(TalemarkClassificationKind.Identifier, "first-name");
        Has(TalemarkClassificationKind.Keyword, "and");
        Has(TalemarkClassificationKind.Identifier, "and");
        Assert.DoesNotContain(classifications, classification =>
            classification.Kind == TalemarkClassificationKind.Property &&
            TextOf(tale, classification) == "first-name");
    }

    [Fact]
    public void Image_alt_text_classification_maps_back_to_authored_source()
    {
        const string tale = """
            === start
            A {image("map", "A {player:first-name} \*literal")} waits.
            """;

        var classifications = TalemarkClassifier.Classify("main.tale", tale);

        var property = Assert.Single(classifications, classification =>
            classification.Kind == TalemarkClassificationKind.Property &&
            TextOf(tale, classification) == "first-name");
        var escape = Assert.Single(classifications, classification =>
            classification.Kind == TalemarkClassificationKind.Escape &&
            TextOf(tale, classification) == @"\*");
        Assert.Equal(2, property.Range.Start.Line);
        Assert.Equal(2, escape.Range.Start.Line);
    }

    [Fact]
    public void Story_analysis_exposes_classifications_from_its_parsed_syntax()
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = """
                game: {id: classifier, title: Classifier, start: start}
                characters: {player: {first-name: Ada}}
                """,
            ["main.tale"] = "=== start\n* -> END\n",
        });

        var analysis = StoryAnalyzer.Analyze(source);

        Assert.Contains(analysis.Classifications, classification =>
            classification.Kind == TalemarkClassificationKind.SceneIdentifier &&
            classification.Modifiers == TalemarkClassificationModifiers.Declaration &&
            classification.Range.Start.File == "main.tale");
    }

    [Theory]
    [InlineData("game: {id: classifier, title: Classifier, start: start}")]
    [InlineData(null)]
    [InlineData("game: [")]
    public void Analysis_classifications_are_cached_from_the_parsed_snapshot(string? manifest)
    {
        const string tale = "=== start\n* -> END\n";
        var files = new Dictionary<string, string> { ["main.tale"] = tale };
        if (manifest is not null)
            files[StorySource.ManifestFileName] = manifest;
        using var cancellation = new CancellationTokenSource();
        var parses = 0;
        var analysis = StoryAnalyzer.Analyze(new StorySource(files), null, cancellation.Token,
            (_, text, token) =>
            {
                parses++;
                return TaleSyntaxParser.Parse(text, token);
            });
        cancellation.Cancel();
        var annotated = analysis.WithDiagnostic(new(DiagnosticSeverity.Warning, "HOST001", "Host warning.",
            SourceLocation.None));

        Assert.Equal(TalemarkClassifier.Classify("main.tale", tale), analysis.Classifications);
        Assert.Same(analysis.Classifications, analysis.Classifications);
        Assert.Same(analysis.Classifications, annotated.Classifications);
        Assert.Equal(1, parses);
    }

    private static string TextOf(string source, TalemarkClassification classification)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < source.Length; index++)
            if (source[index] == '\n')
                starts.Add(index + 1);
        var start = starts[classification.Range.Start.Line - 1] + classification.Range.Start.Column - 1;
        var end = starts[classification.Range.End.Line - 1] + classification.Range.End.Column - 1;
        return source[start..end];
    }
}
