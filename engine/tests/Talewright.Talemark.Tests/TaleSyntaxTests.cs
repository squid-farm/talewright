using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.World;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Talemark.Parsing;
using Talewright.Talemark.Vocabulary;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class TaleSyntaxTests
{
    [Fact]
    public void Lines_are_classified_without_story_symbols()
    {
        const string text = """
            // file comment
            stray text
            === start
            @title Start
            // header comment

            @tags a, b
            @unknown value
            @once
            Prose line
            {if true}
            > player: Hi
            {else}
            ~ var:x = 1
            {/if}
            * [Go] -> next
            *emphasis* is prose
            -> next
            ::: check
            success: -> next
            failure: -> END
            :::
            success: stray
            === next
            -> END
            """;

        var syntax = TaleSyntaxParser.Parse(text);

        Assert.Equal([TaleLineKind.Comment, TaleLineKind.Prose], syntax.Preamble.Select(line => line.Kind));
        var preambleError = Assert.Single(syntax.Diagnostics);
        Assert.Equal(CompilerDiagnosticCodes.TextOutsideSceneOrNoFiles, preambleError.Code);
        Assert.Equal((1, 0), (preambleError.Line, preambleError.Column));

        var start = syntax.Scenes[0];
        Assert.Equal("start", start.Id);
        Assert.Equal(2, start.Line);
        Assert.Equal(
        [
            TaleLineKind.Metadata, TaleLineKind.Comment, TaleLineKind.Blank, TaleLineKind.Metadata,
            TaleLineKind.Metadata, TaleLineKind.MisplacedMetadata, TaleLineKind.Prose, TaleLineKind.BlockIf,
            TaleLineKind.Speech, TaleLineKind.BlockElse, TaleLineKind.Effects, TaleLineKind.BlockEndIf,
            TaleLineKind.Choice, TaleLineKind.Prose, TaleLineKind.Divert, TaleLineKind.SkillCheckBlock,
            TaleLineKind.SuccessOutcome, TaleLineKind.FailureOutcome, TaleLineKind.SkillCheckBlock,
            TaleLineKind.SuccessOutcome,
        ], start.Lines.Select(line => line.Kind));
        Assert.Equal([SceneMetadataKey.Title, SceneMetadataKey.Tags, null],
            start.Lines.Where(line => line.Kind == TaleLineKind.Metadata).Select(line => line.MetadataKey));
        Assert.Equal([false, true],
            start.Lines.Where(line => line.Kind == TaleLineKind.SkillCheckBlock).Select(line => line.IsBareFence));
        Assert.Empty(start.Diagnostics);
        Assert.Equal("next", syntax.Scenes[1].Id);
    }

    [Fact]
    public void Unknown_header_metadata_ends_the_header_so_later_known_keys_are_misplaced()
    {
        var syntax = TaleSyntaxParser.Parse("=== start\n@title A\n@nope\n@once\nText\n@tags x");

        Assert.Equal(
            [TaleLineKind.Metadata, TaleLineKind.Metadata, TaleLineKind.MisplacedMetadata, TaleLineKind.Prose,
                TaleLineKind.MisplacedMetadata],
            syntax.Scenes[0].Lines.Select(line => line.Kind));
        Assert.Null(syntax.Scenes[0].Lines[1].MetadataKey);
    }

    [Theory]
    [InlineData("=== ", "")]
    [InlineData("=== END", "END")]
    [InlineData("=== bad id", "bad id")]
    [InlineData("====x", "=x")]
    public void Malformed_scene_headers_still_own_their_lines(string header, string id)
    {
        var syntax = TaleSyntaxParser.Parse($"{header}\nText\n=== ok\nMore");

        Assert.Equal(id, syntax.Scenes[0].Id);
        Assert.Equal([TaleLineKind.Prose], syntax.Scenes[0].Lines.Select(line => line.Kind));
        var diagnostic = Assert.Single(syntax.Scenes[0].Diagnostics);
        Assert.Equal(CompilerDiagnosticCodes.InvalidSceneId, diagnostic.Code);
        Assert.Empty(syntax.Scenes[1].Diagnostics);
    }

    [Fact]
    public void Scene_boundaries_end_unclosed_blocks_and_partial_choices()
    {
        var syntax = TaleSyntaxParser.Parse("=== a\n* [Unclosed -> b\n::: check\nsuccess: -> b\n=== b\n:::\n-> END");

        Assert.Equal(
            [TaleLineKind.Choice, TaleLineKind.SkillCheckBlock, TaleLineKind.SuccessOutcome],
            syntax.Scenes[0].Lines.Select(line => line.Kind));
        Assert.Equal(4, syntax.Scenes[0].LineCount);
        Assert.Equal(
            [TaleLineKind.SkillCheckBlock, TaleLineKind.Divert],
            syntax.Scenes[1].Lines.Select(line => line.Kind));
    }

    [Fact]
    public void Scene_relative_lines_are_unchanged_when_an_earlier_scene_grows()
    {
        const string later = "=== b\n@title B\n\n  * [Go] -> END\n// note\n";
        var before = TaleSyntaxParser.Parse("=== a\nOne\n" + later);
        var after = TaleSyntaxParser.Parse("=== a\nOne\nTwo 😀\r\nThree\n\n" + later);

        var original = before.Scenes[1];
        var moved = after.Scenes[1];
        Assert.NotEqual(original.Start, moved.Start);
        Assert.NotEqual(original.Line, moved.Line);
        Assert.Equal(original.Lines, moved.Lines);
        Assert.Equal(original.LineCount, moved.LineCount);
        foreach (var line in moved.Lines)
            Assert.Equal(before.RawText(original, line).ToString(), after.RawText(moved, line).ToString());
    }

    [Fact]
    public void Every_line_span_resolves_to_its_original_utf16_text()
    {
        var text = "// intro\r\n  stray\t\r=== a\u2028\t@title  Tab\u00A0\n" +
                   "  \\* escaped \\{not code\\}\r\n" +
                   "> player: 𝔘nicode 😀 ok\u0085" +
                   "{if  true }\u2029" +
                   "\u00A0* [😀] -> END\u00A0\r\n" +
                   "{/if}\n" +
                   "=== b\n\n   \n";

        var syntax = TaleSyntaxParser.Parse(text);
        var expected = Regex.Split(text, "\r\n|\r|\n|\u0085|\u2028|\u2029");

        Assert.Equal(expected.Length, syntax.LineCount);
        var actual = syntax.Preamble.Select(line => (Line: line.Line, Raw: text.Substring(line.Start, line.Length),
                Content: (string?)syntax.Content(line).ToString()))
            .Concat(syntax.Scenes.SelectMany(scene =>
                new[] { (Line: scene.Line, Raw: text.Substring(scene.Start, scene.HeaderLength), Content: (string?)null) }
                    .Concat(scene.Lines.Select(line => (Line: scene.FileLine(line.Line),
                        Raw: syntax.RawText(scene, line).ToString(), Content: (string?)syntax.Content(scene, line).ToString())))))
            .ToList();

        Assert.Equal(Enumerable.Range(0, expected.Length), actual.Select(line => line.Line));
        foreach (var (line, raw, content) in actual)
        {
            Assert.Equal(expected[line], raw);
            if (content is not null)
                Assert.Equal(expected[line].Trim(), content);
        }
    }

    [Fact]
    public void Syntax_binds_to_different_symbol_tables_without_reparsing()
    {
        var syntax = TaleSyntaxParser.Parse("=== start\n-> later");
        var lines = syntax.Scenes[0].Lines;

        var missing = new DiagnosticBag();
        TaleParser.Parse("main.tale", syntax, Symbols(), missing);
        var present = new DiagnosticBag();
        var scenes = TaleParser.Parse("main.tale", syntax, Symbols(new SceneId("later")), present);

        Assert.Equal(CompilerDiagnosticCodes.UnknownScene, Assert.Single(missing.Items).Code);
        Assert.Empty(present.Items);
        var divert = Assert.IsType<DivertStep>(Assert.Single(scenes[0].Definition.Steps));
        Assert.Equal("step:2:1", divert.Id);
        Assert.Same(lines, syntax.Scenes[0].Lines);
    }

    [Fact]
    public void Blank_and_comment_lines_inside_suppressed_conditions_are_ignored()
    {
        var tale = "=== start\n" + string.Concat(Enumerable.Repeat("{if true}\n", 65)) +
            "\n// note\ninside\n\n" + string.Concat(Enumerable.Repeat("{/if}\n", 65)) + "-> END";

        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = TestStories.DefaultYaml,
            ["main.tale"] = tale,
        }));

        Assert.Single(result.Diagnostics, item => item.Code == CompilerDiagnosticCodes.NestingLimitExceeded);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == CompilerDiagnosticCodes.UnbalancedCondition);
    }

    [Fact]
    public void Syntax_shares_the_source_and_stays_small()
    {
        var files = RealisticStory.Create(new RealisticStoryProfile { Chapters = 20 }).ToSource().TaleFiles.ToList();
        var sourceChars = files.Sum(file => (long)file.Value.Length);
        Parse(files[0].Value);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var syntaxes = files.Select(file => Parse(file.Value)).ToList();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.All(files.Zip(syntaxes), pair => Assert.Same(pair.First.Value, pair.Second.Text));
        // Measured at about 2.2 allocated bytes per source char on the reference corpus; retained syntax is smaller.
        Assert.True(allocated <= sourceChars * 3, $"Syntax allocated {allocated:N0} bytes for {sourceChars:N0} chars.");
        Assert.True(Unsafe.SizeOf<TaleLineSyntax>() <= 24, $"TaleLineSyntax is {Unsafe.SizeOf<TaleLineSyntax>()} bytes.");
    }

    [Fact]
    public void Obsolete_syntax_is_collectible()
    {
        var reference = CreateUnreferencedSyntax();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(reference.TryGetTarget(out _));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<TaleFileSyntax> CreateUnreferencedSyntax() =>
        new(TaleSyntaxParser.Parse(new string('x', 10) + "\n=== a\n-> END"));

    private static TaleFileSyntax Parse(string text) => TaleSyntaxParser.Parse(text);

    private static StorySymbols Symbols(params SceneId[] scenes)
    {
        var options = CompilerOptions.Default;
        return new StorySymbols
        {
            World = new WorldDefinition(new RulesetBuilder("syntax").Build(), [], [], [], [],
                new Dictionary<VarId, int>()),
            Flags = new HashSet<FlagId>(),
            Variables = new HashSet<VarId>(),
            Scenes = scenes.ToHashSet(),
            Predicates = options.Predicates,
            Modifiers = options.Modifiers,
        };
    }
}
