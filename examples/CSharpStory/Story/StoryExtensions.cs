using Talewright.Core.Rulesets;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;

namespace CSharpStory;

public static class StoryExtensions
{
    public static CompilerOptions Create() => new()
    {
        CompatibilityId = "csharp-story-v1",
        Predicates = new NamedPredicateRegistry().Register("Ready", _ => true),
        Modifiers = TextModifierRegistry.CreateDefault()
            .Register(new DelegateTextModifier("greeting", (text, _) => $"Welcome, {text}")),
        Rulesets = new Dictionary<string, Func<Ruleset>>(CompilerOptions.DefaultRulesets())
        {
            ["custom"] = () => new RulesetBuilder("custom").Build(),
        },
    };
}
