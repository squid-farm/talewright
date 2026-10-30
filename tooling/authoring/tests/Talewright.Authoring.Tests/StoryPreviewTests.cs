using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Authoring.Tests;

public sealed class StoryPreviewTests
{
    private const string Manifest = """
        game:
          id: preview
          title: Preview
          start: start
        characters:
          player: {first-name: Mara, last-name: Burns, pronouns: she}
        vars: {weather: "sunny", coins: 3}
        items: {rope: Rope}
        """;

    [Fact]
    public void The_authors_passage_has_exact_transformed_previews_matching_gameplay()
    {
        const string passage = """
            The clerk writes **{player:name#upper}**, then carefully corrects the entry to
            {player:name}. The margin says: "{player:pronoun#capitalize} signed
            {player:pronoun#possessive} own name; this copy is {player:pronoun#possessive("independent")}.
            Ask {player:pronoun#object} to check it {player:pronoun#reflexive}."
            """;
        var source = Source("=== start\n" + passage + "\n-> END");
        var analysis = StoryAnalyzer.Analyze(source);
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var previews = StoryPreview.Get(analysis, "main.tale");
        Assert.Equal(["MARA BURNS", "Mara Burns", "She", "her", "hers", "her", "herself"],
            previews.Select(preview => preview.Value));
        var story = Assert.IsType<Talewright.Talemark.Model.StoryDefinition>(StoryCompiler.Compile(source).Story);
        var state = GameState.CreateInitial(story.World, 0);
        var renderer = new TemplateRenderer();
        foreach (var (substitution, preview) in analysis.Substitutions.Zip(previews))
        {
            var rendered = Assert.IsType<RenderedText>(Assert.Single(renderer.RenderInlines([substitution.Template], state)));
            Assert.Equal(rendered.Text, preview.Value);
            Assert.Contains("(default)", preview.PlainText);
            Assert.Contains("not current gameplay state", preview.PlainText);
        }
        Assert.Equal(new PreviewStep("#upper", "MARA BURNS"), previews[0].Steps[1]);
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("friendly", "Friendly")]
    [InlineData("hostile", "Hostile")]
    [InlineData("true#upper", "TRUE")]
    [InlineData("friendly#lower", "friendly")]
    public void Constant_literals_preview_exact_rendered_values_and_hover(string expression, string expected)
    {
        var analysis = StoryAnalyzer.Analyze(Source("=== start\n{" + expression + "}\n-> END"));
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var substitution = Assert.Single(analysis.Substitutions);
        var preview = Assert.Single(StoryPreview.Get(analysis, "main.tale"));
        Assert.Equal(expected, preview.Value);
        Assert.Null(preview.UnavailableReason);
        var rendered = Assert.IsType<RenderedText>(Assert.Single(new TemplateRenderer().RenderInlines(
            [substitution.Template], GameState.CreateInitial(analysis.World!, 0))));
        Assert.Equal(rendered.Text, preview.Value);
        Assert.Equal($"\"{expected}\"", StoryPreview.HintLabel(preview.Value!));
        var hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, new("main.tale", 2, 2)));
        Assert.Contains($"Result: \"{expected}\" (default)", hover.PlainText);
        Assert.DoesNotContain("Preview unavailable", hover.PlainText);
    }

    [Theory]
    [InlineData("she", "has", "her")]
    [InlineData("they", "have", "them")]
    public void Pronoun_agreement_and_builtin_chains_use_engine_implementations(string pronoun, string verb, string obj)
    {
        var source = Source("=== start\n{player:pronoun#one-many(\"has\", \"have\")} {player:pronoun#object#upper#lower}",
            Manifest.Replace("pronouns: she", "pronouns: " + pronoun));
        var previews = StoryPreview.Get(StoryAnalyzer.Analyze(source), "main.tale");
        Assert.Equal([verb, obj], previews.Select(preview => preview.Value));
        Assert.Equal(3, previews[1].Steps.Count);
    }

    [Fact]
    public void Hints_have_utf16_ranges_after_closing_braces_in_multiline_styled_speech_and_choices()
    {
        const string tale = "=== start\r\n🦑 **{player:name}** {player:\r\npronoun#object}\r\n> player: {player:name}\r\n* [{player:name}] -> END";
        var analysis = StoryAnalyzer.Analyze(Source(tale));
        var previews = StoryPreview.Get(analysis, "main.tale");
        Assert.Equal(4, previews.Count);
        Assert.Equal(new SourceLocation("main.tale", 2, 6), previews[0].Range.Start);
        Assert.Equal(new SourceLocation("main.tale", 2, 19), previews[0].Range.End);
        Assert.Equal(new SourceLocation("main.tale", 3, 16), previews[1].Range.End);
        var lines = tale.ReplaceLineEndings("\n").Split('\n');
        foreach (var preview in previews)
            Assert.Equal('}', lines[preview.Range.End.Line - 1][preview.Range.End.Column - 2]);
    }

    [Fact]
    public void Unknown_incomplete_escaped_and_commented_substitutions_have_no_hints()
    {
        var analysis = StoryAnalyzer.Analyze(Source("""
            === start
            // {player:name}
            \{player:name}
            {missing:name} {player:unknown} {player:name#unknown}
            {player:name
            """));
        Assert.Empty(StoryPreview.Get(analysis, "main.tale"));
    }

    [Fact]
    public void Gameplay_values_and_implicit_initialization_are_not_guessed()
    {
        var analysis = StoryAnalyzer.Analyze(Source("""
            === start
            ~ var:implicit = "later"; var:weather = "rainy"
            {var:weather} {var:coins} {item:rope} {var:implicit} {player:hp}
            """));
        var previews = StoryPreview.Get(analysis, "main.tale");
        Assert.Equal(["sunny", "3", "Rope", null, null], previews.Select(preview => preview.Value));
        Assert.All(previews.Skip(3), preview => Assert.Contains("gameplay state", preview.UnavailableReason));
    }

    [Fact]
    public async Task Unsaved_supporting_character_changes_and_close_update_defaults_without_mutating_sources()
    {
        var source = Source("=== start\n{player:name} {player:pronoun#object}", Manifest[..Manifest.IndexOf("characters:", StringComparison.Ordinal)]);
        var files = source.Files.ToDictionary();
        files["characters/player.talewright.yaml"] = "characters:\n  player: {first-name: Mara, pronouns: she}";
        var workspace = new StoryWorkspace(new(files));
        await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.Equal("Mara", StoryPreview.Get(workspace.Current!, "main.tale")[0].Value);
        workspace.Open("characters/player.talewright.yaml", 1, "characters:\n  player: {first-name: Alex, pronouns: they}");
        await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.Equal(["Alex", "them"], StoryPreview.Get(workspace.Current!, "main.tale").Select(preview => preview.Value));
        workspace.Close("characters/player.talewright.yaml");
        await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.Equal(["Mara", "her"], StoryPreview.Get(workspace.Current!, "main.tale").Select(preview => preview.Value));
        Assert.Equal(source.Files["main.tale"], workspace.Current!.Source.Files["main.tale"]);
    }

    [Fact]
    public void Custom_modifiers_even_with_builtin_names_never_execute()
    {
        var calls = 0;
        var options = new CompilerOptions
        {
            Modifiers = TextModifierRegistry.CreateDefault().Register(new DelegateTextModifier("upper", (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("Must not execute");
            })),
        };
        var source = Source("=== start\n{player:name#upper} {player:name#lower}");
        var analysis = StoryAnalyzer.Analyze(source, options);
        var previews = StoryPreview.Get(analysis, "main.tale");
        Assert.Null(previews[0].Value);
        Assert.Contains("not executed", previews[0].UnavailableReason);
        Assert.Equal("mara burns", previews[1].Value);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Metadata_roundtrip_retains_only_proven_engine_builtins_and_old_metadata_stays_safe()
    {
        var source = Source("=== start\n{player:name#upper} {player:name#custom}");
        var options = new CompilerOptions
        {
            Modifiers = TextModifierRegistry.CreateDefault().Register(new DelegateTextModifier("custom", (_, _) =>
                throw new InvalidOperationException("Must not execute"))),
        };
        var metadata = AuthoringProjectMetadata.FromJson(AuthoringProjectMetadata.Export(options).ToJson());
        var previews = StoryPreview.Get(metadata.Analyze(source), "main.tale");
        Assert.Equal("MARA BURNS", previews[0].Value);
        Assert.Null(previews[1].Value);
        Assert.All(StoryPreview.Get((metadata with { BuiltInModifiers = [] }).Analyze(source), "main.tale"),
            preview => Assert.Null(preview.Value));
        Assert.Throws<InvalidDataException>(() => (metadata with { BuiltInModifiers = ["custom"] }).ToJson());
    }

    [Fact]
    public void Empty_whitespace_control_characters_and_long_results_are_visible_bounded_and_exact_on_hover()
    {
        foreach (var value in new[] { "", "  ", "a\nb\tc", new string('x', 100), new string('x', 35) + "🦑🦑" })
        {
            var hint = StoryPreview.HintLabel(value);
            Assert.InRange(hint.Length, 2, StoryPreview.MaximumHintLength);
            Assert.DoesNotContain('\n', hint);
            Assert.DoesNotContain('\t', hint);
            for (var index = 0; index < hint.Length; index++)
                if (char.IsHighSurrogate(hint[index]))
                    Assert.True(index + 1 < hint.Length && char.IsLowSurrogate(hint[index + 1]));
        }
        var analysis = StoryAnalyzer.Analyze(Source("=== start\n{var:weather}", Manifest.Replace("\"sunny\"", "\"\"")));
        var preview = Assert.Single(StoryPreview.Get(analysis, "main.tale"));
        Assert.Equal("", preview.Value);
        Assert.Equal("\"\"", StoryPreview.HintLabel(preview.Value!));
    }

    [Fact]
    public void Character_hover_is_a_default_card_and_expression_hover_keeps_existing_help()
    {
        var analysis = StoryAnalyzer.Analyze(Source("=== start\n> player: {player:pronoun#object#capitalize}"));
        var character = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, new("main.tale", 2, 4)));
        Assert.Contains("Name (default): \"Mara Burns\"", character.PlainText);
        Assert.Contains("Independent possessive: \"hers\"", character.PlainText);
        var hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, new("main.tale", 2, 34)));
        Assert.Contains("Result: \"Her\" (default)", hover.PlainText);
        Assert.Contains("#capitalize: \"Her\"", hover.PlainText);
        Assert.Contains("Declared at", character.PlainText);
    }

    private static StorySource Source(string tale, string manifest = Manifest) => new(new Dictionary<string, string>
    {
        [StorySource.ManifestFileName] = manifest, ["main.tale"] = tale,
    });
}
