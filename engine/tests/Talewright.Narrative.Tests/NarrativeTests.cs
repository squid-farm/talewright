using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Narrative.Grammar;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;

namespace Talewright.Narrative.Tests;

public class NarrativeTests
{
    private static readonly EntityId Guards = new("guards");
    private static readonly TextModifierRegistry Modifiers = TextModifierRegistry.CreateDefault();

    private static GameState State() => GameState.CreateInitial(
        new WorldDefinition(
            Ruleset.Empty,
            [
                new CharacterDefinition(EntityId.Player, "Mara", "Burns", Pronouns.She, new Dictionary<StatId, int>(), new Dictionary<ItemId, int>()),
                new CharacterDefinition(Guards, "the guards", "", Pronouns.They, new Dictionary<StatId, int>(), new Dictionary<ItemId, int>(), NameAgreement: GrammaticalNumber.Plural),
            ],
            [], [], [new FlagId("lit")], new Dictionary<VarId, int>()),
        1);

    private static ITextModifier Mod(string name) => Modifiers.TryGet(name, out var m) ? m : throw new KeyNotFoundException(name);

    private static string RenderInline(params TemplateInline[] inlines) =>
        RenderedInline.ToPlainText(new TemplateRenderer().RenderInlines(inlines, State()));

    [Theory]
    [InlineData("Mara", false, "Mara's")]
    [InlineData("Burns", false, "Burns's")]
    [InlineData("the guards", true, "the guards'")]
    [InlineData("HANDCUFFS", true, "HANDCUFFS'")]
    [InlineData("ARMOR", false, "ARMOR's")]
    [InlineData("the children", true, "the children's")]
    public void Possessives_follow_chicago_style(string word, bool plural, string expected)
    {
        Assert.Equal(expected, EnglishGrammar.Instance.Possessive(word, plural));
    }

    [Theory]
    [InlineData("relic", "a relic")]
    [InlineData("ancient door", "an ancient door")]
    [InlineData("hour", "an hour")]
    [InlineData("unicorn", "a unicorn")]
    public void Indefinite_articles(string phrase, string expected)
    {
        Assert.Equal(expected, EnglishGrammar.Instance.WithIndefiniteArticle(phrase));
    }

    [Fact]
    public void Substitutions_apply_modifier_chain()
    {
        var text = RenderInline(
            new SubstitutionTemplate(new NameRef(EntityId.Player, NamePart.First), [Mod("possessive")]),
            new TextTemplate(" and "),
            new SubstitutionTemplate(new NameRef(EntityId.Player, NamePart.Last), [Mod("possessive")]),
            new TextTemplate(" and "),
            new SubstitutionTemplate(new NameRef(Guards, NamePart.First), [Mod("possessive"), Mod("capitalize")], IsPlural: true),
            new TextTemplate(". "),
            new SubstitutionTemplate(new PronounRef(EntityId.Player, PronounForm.Subject), [Mod("capitalize")]),
            new TextTemplate(" smiles."));

        Assert.Equal("Mara's and Burns's and The guards'. She smiles.", text);
    }

    [Fact]
    public void CSharp_item_references_keep_original_grammar_after_custom_transformations()
    {
        var world = State().World;
        var item = new ItemDefinition(new("supplies"), "supplies", new Dictionary<StatId, int>(),
            NameAgreement: GrammaticalNumber.Plural, Uncountable: true);
        var state = GameState.CreateInitial(new WorldDefinition(world.Ruleset, world.Characters.Values,
            [item], [], [], new Dictionary<VarId, int>()), 1);
        var renamed = new DelegateTextModifier("rename", (_, context) =>
        {
            Assert.True(context.IsPlural);
            Assert.True(context.IsUncountable);
            return "goods";
        });
        var template = new SubstitutionTemplate(new ItemNameRef(item.Id),
            [renamed, Mod("a"), Mod("capitalize")]);
        var rendered = new TemplateRenderer().RenderInlines([template], state);
        Assert.Equal("Some goods", RenderedInline.ToPlainText(rendered));
        Assert.Equal("are", RenderedInline.ToPlainText(new TemplateRenderer().RenderInlines(
            [template with { Modifiers = [renamed, new OneManyTextModifier("is", "are")] }], state)));
    }

    [Fact]
    public void Inline_conditionals_collapse_extra_whitespace()
    {
        var text = RenderInline(
            new TextTemplate("The torch "),
            new ConditionalInlineTemplate(new FlagRef(new FlagId("lit")), [new TextTemplate("blazes")], []),
            new TextTemplate(" sits there."));

        Assert.Equal("The torch sits there.", text);
    }

    [Fact]
    public void Blocks_render_styles_speech_and_conditionals()
    {
        var document = new TemplateRenderer().Render(
        [
            new ParagraphTemplate([new TextTemplate("Cold "), new StyledTemplate(TextStyle.Strong, [new TextTemplate("air "), new StyledTemplate(TextStyle.Emphasis, [new TextTemplate("rushes")])])]),
            new ConditionalBlockTemplate(new FlagRef(new FlagId("lit")), [new ParagraphTemplate([new TextTemplate("Light!")])], [new ParagraphTemplate([new TextTemplate("Darkness.")])]),
            new SpeechTemplate(EntityId.Player, [new TextTemplate("Hello.")]),
        ], State());

        Assert.Equal("Cold air rushes\n\nDarkness.\n\nMara: Hello.", document.ToPlainText());
        var first = Assert.IsType<RenderedParagraph>(document.Blocks[0]);
        Assert.Equal(
            [new RenderedText("Cold ", TextStyle.None), new RenderedText("air ", TextStyle.Strong), new RenderedText("rushes", TextStyle.Strong | TextStyle.Emphasis)],
            first.Inlines);
    }

    [Fact]
    public void Media_preserves_effective_alt_order_and_inherited_styles_without_io()
    {
        var rendered = new TemplateRenderer().RenderInlines(
        [
            new TextTemplate("Before "),
            new StyledTemplate(TextStyle.Strong, [
                new StyledTemplate(TextStyle.Emphasis | TextStyle.Code, [
                    new ConditionalInlineTemplate(new BoolLiteral(true),
                        [new MediaTemplate("images/map.png", "A map")], []),
                ]),
            ]),
            new TextTemplate(" after."),
        ], State());
        Assert.Equal(new RenderedText("Before ", TextStyle.None), rendered[0]);
        Assert.Equal(new RenderedMedia("images/map.png", "A map")
        {
            Style = TextStyle.Strong | TextStyle.Emphasis | TextStyle.Code,
        }, rendered[1]);
        Assert.Equal(new RenderedText(" after.", TextStyle.None), rendered[2]);
    }

    [Fact]
    public void Empty_paragraphs_are_dropped()
    {
        var document = new TemplateRenderer().Render(
            [new ParagraphTemplate([new ConditionalInlineTemplate(new BoolLiteral(false), [new TextTemplate("hidden")], [])])],
            State());

        Assert.Empty(document.Blocks);
    }
}
