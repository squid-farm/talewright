using Talewright.Core.Rulesets;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Tale.Compilation;

namespace Talewright.Tale.Tests;

public sealed class StoryProjectValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"talewright-build-test-{Guid.NewGuid():N}");

    public StoryProjectValidatorTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "story.yaml"),
            "id: build\ntitle: Build\nstart: a\nruleset: {base: custom}\ncharacters:\n  player: {first-name: Author}\n");
        File.WriteAllText(Path.Combine(_root, "main.tale"),
            "=== a\n{player:first-name#greeting}\n* [Continue] (requires: @Ready) -> END\n");
    }

    private static CompilerOptions Options() => new()
    {
        CompatibilityId = "test-v1",
        Predicates = new NamedPredicateRegistry().Register("Ready", _ => true),
        Modifiers = TextModifierRegistry.CreateDefault().Register(new DelegateTextModifier("greeting", (text, _) => text)),
        Rulesets = new Dictionary<string, Func<Ruleset>> { ["custom"] = () => new RulesetBuilder("custom").Build() },
    };

    [Fact]
    public void Build_validation_uses_explicit_extensions()
    {
        using var output = new StringWriter();
        Assert.Equal(0, StoryProjectValidator.Validate(_root, Options(), output));
        Assert.Equal("", output.ToString());
        Assert.True(StoryCompiler.Compile(StorySource.FromDirectory(_root), Options()).Success);
        Assert.False(StoryCompiler.Compile(StorySource.FromDirectory(_root)).Success);
    }

    [Fact]
    public void Errors_use_absolute_files_and_msbuild_line_column_code()
    {
        File.WriteAllText(Path.Combine(_root, "main.tale"), "=== a\n-> missing\n");
        using var output = new StringWriter();
        Assert.Equal(1, StoryProjectValidator.Validate(_root, Options(), output));
        Assert.Contains($"{Path.Combine(_root, "main.tale")}(2,", output.ToString());
        Assert.Contains("): error TW2002:", output.ToString());
    }

    [Fact]
    public void Input_errors_are_explicit_build_errors()
    {
        using var output = new StringWriter();
        Assert.Equal(2, StoryProjectValidator.Validate(Path.Combine(_root, "missing"), Options(), output));
        Assert.Contains("error TWBUILD001:", output.ToString());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
