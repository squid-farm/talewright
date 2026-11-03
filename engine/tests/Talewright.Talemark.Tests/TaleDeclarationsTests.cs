using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Parsing;

namespace Talewright.Talemark.Tests;

public sealed class TaleDeclarationsTests
{
    [Fact]
    public void Effects_are_collected_in_source_order_from_every_line_the_binder_binds()
    {
        var syntax = TaleSyntaxParser.Parse("""
            === first
            ~ set flag:line
            * [Go] { set flag:choice } { set flag:duplicate } -> second
            * [Drop] { set flag:dropped } next
            * [Climb] skill-check athletics dc 5
              success: { set flag:won } -> END
              failure: -> END
            Prose ends the check.
            success: { set flag:stray }
            === second
            ::: skill-check dex dc 5
            success: -> END
            failure: { var:misses += 1 }
            :::
            -> END
            """);
        var scenes = new List<(string, SourceLocation)>();
        var effects = new List<(string, SourceLocation)>();

        TaleDeclarations.Collect("main.tale", syntax, (id, at) => scenes.Add((id.ToString(), at)),
            (text, at) => effects.Add((text.Trim(), at)));

        Assert.Equal([("first", new SourceLocation("main.tale", 1, 1)), ("second", new SourceLocation("main.tale", 10, 1))],
            scenes);
        Assert.Equal(
        [
            ("set flag:line", new SourceLocation("main.tale", 2, 3)),
            ("set flag:choice", new SourceLocation("main.tale", 3, 9)),
            ("set flag:duplicate", new SourceLocation("main.tale", 3, 29)),
            ("set flag:dropped", new SourceLocation("main.tale", 4, 11)),
            ("set flag:won", new SourceLocation("main.tale", 6, 13)),
            ("var:misses += 1", new SourceLocation("main.tale", 13, 11)),
        ], effects);
    }

    [Fact]
    public void Suppressed_lines_declare_nothing()
    {
        var nested = string.Concat(Enumerable.Repeat("{if true}\n", TaleSyntaxParser.MaximumBlockConditionDepth + 1));
        var syntax = TaleSyntaxParser.Parse($"=== first\n{nested}~ set flag:hidden\n* [Go] {{ set flag:hidden }} -> END\n");
        var effects = new List<string>();

        TaleDeclarations.Collect("main.tale", syntax, (_, _) => { }, (text, _) => effects.Add(text));

        Assert.Empty(effects);
    }

    [Fact]
    public void Scene_only_collection_skips_effects()
    {
        var syntax = TaleSyntaxParser.Parse("=== first\n~ set flag:x\n=== second\n-> END");
        var scenes = new List<string>();

        TaleDeclarations.Collect("main.tale", syntax, (id, _) => scenes.Add(id.ToString()), null);

        Assert.Equal(["first", "second"], scenes);
    }
}
