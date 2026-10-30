using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Compilation;

namespace Talewright.Talemark.Tests;

public class ItemGrammarTests
{
    private const string Manifest = """
        game: {id: item-grammar, title: Item grammar, start: start}
        characters:
          player: {first-name: Ada, inventory: {pants: 1, sword: 5}}
        items:
          sword: sword
          amulet: {name: amulet}
          pants: {name: pants, name-agreement: plural}
          handcuffs: {name: handcuffs, name-agreement: plural}
          armor: {name: armor, uncountable: true}
          boots: {name: pair of boots}
        """;

    private static CompilationResult Compile(string text, string manifest = Manifest) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = manifest,
            ["main.tale"] = "=== start\n" + text + "\n-> END",
        }));

    [Theory]
    [InlineData("sword", "a sword/the sword/this sword/that sword/is/sword's")]
    [InlineData("amulet", "an amulet/the amulet/this amulet/that amulet/is/amulet's")]
    [InlineData("pants", "some pants/the pants/these pants/those pants/are/pants'")]
    [InlineData("handcuffs", "some handcuffs/the handcuffs/these handcuffs/those handcuffs/are/handcuffs'")]
    [InlineData("armor", "some armor/the armor/this armor/that armor/is/armor's")]
    [InlineData("boots", "a pair of boots/the pair of boots/this pair of boots/that pair of boots/is/pair of boots's")]
    public void Full_helper_table_uses_name_grammar_not_inventory(string item, string expected)
    {
        var result = Compile($$"""
            {item:{{item}}#a}/{item:{{item}}#the}/{item:{{item}}#this}/{item:{{item}}#that}/{item:{{item}}#one-many("is", "are")}/{item:{{item}}#possessive}
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var state = GameState.CreateInitial(story.World, 1);
        Assert.Equal(1, state.GetItemCount(EntityId.Player, new("pants")));
        Assert.Equal(5, state.GetItemCount(EntityId.Player, new("sword")));
        Assert.Equal(expected, new TemplateRenderer().Render(story.GetScene(new("start")).Body, state).ToPlainText());
    }

    [Fact]
    public void Original_item_metadata_survives_every_modifier_in_a_chain()
    {
        var result = Compile("""
            {item:pants#that#capitalize} {item:pants#upper#one-many("looks", "look")} expensive.
            {item:armor#upper#a#capitalize}/{item:pants#lower#this#capitalize}/{item:pants#the#one-many("is", "are")}/{item:handcuffs#upper#possessive}
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        Assert.Equal("Those pants look expensive. Some ARMOR/These pants/are/HANDCUFFS'",
            new TemplateRenderer().Render(story.GetScene(new("start")).Body, GameState.CreateInitial(story.World, 1)).ToPlainText());
        var plural = Assert.Single(story.World.Items.Values, item => item.Id == new ItemId("pants"));
        Assert.Equal(GrammaticalNumber.Plural, plural.NameAgreement);
        Assert.False(plural.Uncountable);
        foreach (var id in new[] { "sword", "amulet", "boots" })
        {
            Assert.Equal(GrammaticalNumber.Singular, story.World.Items[new(id)].NameAgreement);
            Assert.False(story.World.Items[new(id)].Uncountable);
        }
    }

    [Theory]
    [InlineData("name-agreement: many", "name-agreement")]
    [InlineData("name-agreement: true", "name-agreement")]
    [InlineData("name-agreement: [singular]", "name-agreement")]
    [InlineData("uncountable: sometimes", "uncountable")]
    [InlineData("uncountable: 1", "uncountable")]
    [InlineData("uncountable: {}", "uncountable")]
    public void Invalid_item_grammar_reports_standard_located_diagnostics(string setting, string field)
    {
        var result = Compile("Hello.", Manifest.Replace("name: amulet", "name: amulet, " + setting));
        Assert.False(result.Success);
        var error = Assert.Single(result.Errors, diagnostic => diagnostic.Code == "TW4003");
        Assert.Contains(field, error.Message);
        Assert.Equal(StorySource.ManifestFileName, error.Location.File);
        Assert.True(error.Location.Line > 1);
        Assert.True(error.Location.Column > 1);
    }

    [Theory]
    [InlineData("item:missing#one-many(\"is\", \"are\")", "TW2001")]
    [InlineData("item:pants#one-many(\"is\")", "TW2006")]
    [InlineData("item:pants#one-many(\"is\", \"are\", \"extra\")", "TW2006")]
    [InlineData("item:pants#that(\"extra\")", "TW2006")]
    [InlineData("var:word#one-many(\"is\", \"are\")", "TW2006")]
    public void Selectors_keep_the_existing_exact_two_string_contract(string expression, string code)
    {
        var result = Compile("~ var:word = item:pants\n{" + expression + "}");
        Assert.False(result.Success);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public void All_new_helpers_are_engine_owned_but_same_name_custom_delegates_are_not()
    {
        var modifiers = TextModifierRegistry.CreateDefault();
        foreach (var name in new[] { "a", "the", "this", "that", "possessive" })
        {
            Assert.True(modifiers.TryGet(name, out var modifier));
            Assert.True(TextModifierRegistry.IsBuiltIn(modifier));
            Assert.False(TextModifierRegistry.IsBuiltIn(new DelegateTextModifier(name, (text, _) => text)));
        }
        Assert.True(TextModifierRegistry.IsBuiltIn(new OneManyTextModifier("is", "are")));
    }
}
